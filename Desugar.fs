/// <summary>
///     Module for performing desugaring operations on a collated AST.
/// </summary>
module Starling.Lang.Desugar

open Chessie.ErrorHandling

open Starling.Collections
open Starling.Utils
open Starling.Core.TypeSystem
open Starling.Core.View
open Starling.Core.Var
open Starling.Core.Expr
open Starling.Core.GuardedView
open Starling.Lang.AST
open Starling.Lang.Collator


/// <summary>
///     A partly modelled view prototype, whose parameters use Starling's type
///     system rather than the language's.
/// </summary>
type DesugaredViewProto = GeneralViewProto<TypedVar>

/// <summary>
///     A desugared view atom, ready for modelling.
/// </summary>
type DesugaredFunc = Func<AST.Types.Expression>

/// <summary>
///     A desugared guarded func, ready for modelling.
/// </summary>
type DesugaredGFunc = AST.Types.Expression * DesugaredFunc

/// <summary>
///     A desugared guarded view, ready for modelling.
/// </summary>
type DesugaredGView = DesugaredGFunc list


/// <summary>
///     The set of new views and constraints generated by the view
///     desugaring.
/// </summary>
type DesugarContext =
    { /// <summary>The list of shared variables in the program.</summary>
      SharedVars : (TypeLiteral * string) list
      /// <summary>The list of thread variables in the program.</summary>
      ThreadVars : (TypeLiteral * string) list
      /// <summary>The name of the local lift view, if any.</summary>
      LocalLiftView : string option
      /// <summary>The list of fresh views generated.</summary>
      GeneratedProtos : Set<ViewProto>
      /// <summary>The list of views already present in the system.</summary>
      ExistingProtos : Set<ViewProto> 
      /// <summary>The name of the 'ok' Boolean, if any.</summary>
      OkayBool : string option
    }

/// <summary>
///     An atomic command with errors and asserts desugared,
///     and missing branches inserted.
/// </summary>
type DesugaredAtomic =
    | DAPrim of Prim
    | DACond of
        cond : Expression
        * trueBranch : DesugaredAtomic list
        * falseBranch : DesugaredAtomic list

/// <summary>
///     A block whose missing views have been filled up.
/// </summary>
type FullBlock<'view, 'cmd> =
    { /// <summary> The precondition of the block.</summary>
      Pre : 'view
      /// <summary>
      ///     The commands in the block, and their subsequent views.
      /// </summary>
      Cmds : ('cmd * 'view) list }

/// <summary>A non-view command with FullBlocks.</summary>
type FullCommand' =
    /// A set of sequentially composed primitives.
    | FPrim of PrimSet<DesugaredAtomic>
    /// An if-then-else statement, with optional else.
    | FIf of ifCond : Expression
          * thenBlock : FullBlock<ViewExpr<DesugaredGView>, FullCommand>
          * elseBlock : FullBlock<ViewExpr<DesugaredGView>, FullCommand> option
    /// A while loop.
    | FWhile of Expression * FullBlock<ViewExpr<DesugaredGView>, FullCommand>
    /// A do-while loop.
    | FDoWhile of FullBlock<ViewExpr<DesugaredGView>, FullCommand>
               * Expression // do { b } while (e)
    /// A list of parallel-composed blocks.
    | FBlocks of FullBlock<ViewExpr<DesugaredGView>, FullCommand> list
and FullCommand = Node<FullCommand'>

module Pretty =
    open Starling.Core.Pretty
    open Starling.Core.View.Pretty
    open Starling.Lang.AST.Pretty

    /// <summary>
    ///     Pretty-prints desugared atomic actions.
    /// </summary>
    /// <param name="a">The <see cref="DesugaredAtomic'"/> to print.</param>
    /// <returns>
    ///     A <see cref="Doc"/> representing <paramref name="a"/>.
    /// </returns>
    let rec printDesugaredAtomic (a : DesugaredAtomic) : Doc =
        match a with
        | DAPrim p -> printPrim p
        | DACond (cond = c; trueBranch = t; falseBranch = f) ->
            printITE printDesugaredAtomic c t (Some f)

    /// <summary>
    ///     Prints a <see cref="FullCommand'"/>.
    /// </summary>
    /// <param name="pView">Pretty-printer for views.</param>
    /// <param name="pCmd">Pretty-printer for commands.</param>
    /// <param name="fb">The <see cref="FullBlock'"/> to print.</param>
    /// <typeparam name="View">Type of views in the block.</typeparam>
    /// <typeparam name="Cmd">Type of commands in the block.</typeparam>
    /// <returns>
    ///     The <see cref="Doc"/> representing <paramref name="fc"/>.
    /// </returns>
    let printFullBlock (pView : 'View -> Doc) (pCmd : 'Cmd -> Doc)
      (fb : FullBlock<'View, 'Cmd>) : Doc =
        let printStep (c, v) = vsep [ Indent (pCmd c); pView v ]
        let indocs = pView fb.Pre :: List.map printStep fb.Cmds
        braced (ivsep indocs)

    /// <summary>
    ///     Prints a <see cref="DesugaredGView"/>.
    /// </summary>
    /// <param name="v">The view to print.</param>
    /// <returns>
    ///     The <see cref="Doc"/> representing <paramref name="v"/>.
    /// </returns>
    let printDesugaredGView (v : DesugaredGView) : Doc =
        let pv (g, b) =
            String "if"
            <+> parened (printExpression g)
            <+> braced (func b.Name (List.map printExpression b.Params))
        hsepStr " * " (List.map pv v)

    /// <summary>
    ///     Prints a <see cref="FullCommand'"/>.
    /// </summary>
    /// <param name="fc">The <see cref="FullCommand'"/> to print.</param>
    /// <returns>
    ///     The <see cref="Doc"/> representing <paramref name="fc"/>.
    /// </returns>
    let rec printFullCommand' (fc : FullCommand') : Doc =
        // TODO(CaptainHayashi): dedupe with PrintCommand'.
        match fc with
        (* The trick here is to make Prim [] appear as ;, but
           Prim [x; y; z] appear as x; y; z;, and to do the same with
           atomic lists. *)
        | FPrim { PreLocals = ps; Atomics = ts; PostLocals = qs } ->
            seq { yield! Seq.map printPrim ps
                  yield (ts
                         |> Seq.map printDesugaredAtomic
                         |> semiSep |> withSemi |> braced |> angled)
                  yield! Seq.map printPrim qs }
            |> semiSep |> withSemi
        | FIf(c, t, fo) ->
            hsep [ "if" |> String |> syntax
                   c |> printExpression |> parened
                   t |> printFullBlock (printViewExpr printDesugaredGView) printFullCommand
                   (maybe Nop
                        (fun f ->
                            hsep
                                [ "else" |> String |> syntax
                                  printFullBlock (printViewExpr printDesugaredGView) printFullCommand f ])
                        fo) ]
        | FWhile(c, b) ->
            hsep [ "while" |> String |> syntax
                   parened (printExpression c)
                   b |> printFullBlock (printViewExpr printDesugaredGView) printFullCommand ]
        | FDoWhile(b, c) ->
            hsep [ "do" |> String |> syntax
                   printFullBlock (printViewExpr printDesugaredGView) printFullCommand b
                   "while" |> String |> syntax
                   parened (printExpression c) ]
            |> withSemi
        | FBlocks bs ->
            bs
            |> List.map (printFullBlock (printViewExpr printDesugaredGView) printFullCommand)
            |> hsepStr "||"
    /// <summary>
    ///     Prints a <see cref="FullCommand"/>.
    /// </summary>
    /// <param name="fc">The <see cref="FullCommand"/> to print.</param>
    /// <returns>
    ///     The <see cref="Doc"/> representing <paramref name="fc"/>.
    /// </returns>
    and printFullCommand (fc : FullCommand) : Doc = printFullCommand' fc.Node

let protoName (p : GeneralViewProto<'P>) : string =
    // TODO(MattWindsor91): doc comment.
    match p with
    | NoIterator (f, _) -> f.Name
    | WithIterator f -> f.Name

module private Generators =
    /// <summary>
    ///     Given a set of existing names and a prefix, generate a fresh name
    ///     not contained in that set.
    ///     <para>
    ///        This has worst-case time O(n), where n is the number of elements
    ///        in <paramref name="existing"/>.
    ///     </para>
    /// </summary>
    /// <param name="existing">The set of existing names.</param>
    /// <param name="prefix">The prefix to use when generating names.</param>
    /// <returns>
    ///     A name containing <paramref name="prefix"/> and not contained in
    ///     <paramref name="existing"/>.
    /// </returns>
    let genName (existing : Set<string>) (prefix : string) : string =
        (* Keep spinning a number up until we get to a fresh name.
           Inefficient, but simple. *)
        let rec tryGenName (k : bigint) : string =
            let name = sprintf "__%s_%A" prefix k
            if existing.Contains name then tryGenName (k + 1I) else name
        tryGenName 0I

    /// <summary>
    ///     Generates a fresh view with a given name prefix and parameter list.
    ///     Inserts that view into the given context.
    ///     <para>
    ///         The view is guaranteed to have a name that does not clash with
    ///         an generated or existing view.
    ///     </para>
    /// </summary>
    /// <param name="prefix">The prefix to use when generating the name.</param>
    /// <param name="pars">The parameters to use for the view prototype.</param>
    /// <param name="ctx">The <see cref="DesugarContext"/> to extend.</param>
    /// <returns>
    ///     A pair of the context updated with the new view, and its name.
    /// </returns>
    let genView (prefix : string) (pars : Param list) (ctx : DesugarContext)
      : DesugarContext * string =
        let vnames =
            // Can't union-map because the proto types are different.
            Set.union
                (Set.map protoName ctx.ExistingProtos)
                (Set.map protoName ctx.GeneratedProtos)

        let newName = genName vnames prefix
        let newProto = NoIterator ({ Name = newName; Params = pars }, false)
        let ctx' = { ctx with GeneratedProtos = ctx.GeneratedProtos.Add newProto }
        (ctx', newName)

    /// <summary>
    ///     Generates the lifter view in a context, if it does not exist.
    /// </summary>
    /// <param name="ctx">The current desugaring context.</param>
    /// <returns>
    ///     <paramref name="ctx"/> updated to contain a lifter if it didn't
    ///     already, and the lifter's name.  The lifter always takes one
    ///     parameter, `bool x`.
    /// </returns>
    let genLifter (ctx : DesugarContext) : DesugarContext * string =
        match ctx.LocalLiftView with
        | Some n -> (ctx, n)
        | None ->
            (* We need to generate the view, then set the context to use it as
               the lifter.  The lifter has one parameter: the lifted Boolean. *)
            let ctxR, n =
                genView "lift" [ { ParamName = "x"; ParamType = TBool } ] ctx
            ({ ctxR with LocalLiftView = Some n }, n)
    
    /// <summary>
    ///     Generates the okay variable in a context, if it does not exist.
    ///     <para>
    ///         The okay variable is used when a program contains an assertion
    ///         or error command, and is used to represent the failure of the
    ///         program when an error occurs.
    ///     </para> 
    /// </summary>
    /// <param name="ctx">The current desugaring context.</param>
    /// <returns>
    ///     <paramref name="ctx"/> updated to contain an okay variable if it
    ///     didn't already, and the variable's name.  The variable is always of
    ///     type `bool`.
    /// </returns>
    /// <remarks>
    ///     It is currently the modeller's responsibility to constrain on the
    ///     okay variable.  This function does add the okay variable to the
    ///     list of shared variables, though.
    /// </remarks>
    let genOkay (ctx : DesugarContext) : DesugarContext * string =
        match ctx.OkayBool with
        | Some n -> (ctx, n)
        | None ->
            let vars =
                Set.ofSeq
                    (Seq.map snd
                        (Seq.append ctx.SharedVars ctx.ThreadVars))
            let n = genName vars "ok"
            ({ ctx with
                OkayBool = Some n
                SharedVars = (TBool, n) :: ctx.SharedVars }, n)


/// <summary>
///     Performs desugaring operations on a view, possibly creating new
///     view prototypes.
/// </summary>
/// <param name="ctx">The current desugaring context.</param>
/// <param name="view">The view to be converted.</param>
/// <returns>
///     A pair of the new context and desugared view.
/// </returns>
let desugarView
  (ctx : DesugarContext)
  (view : AST.Types.View)
  : DesugarContext * DesugaredGView =
    let rec desugarIn suffix c v =
        match v with
        | Unit -> (c, [])
        | Falsehood ->
            // Treat {| false |} as {| local { false } |} for simplicity.
            desugarIn suffix c (Local (freshNode False))
        | Local e ->
            (* Treat {| local { x } |} as {| lift(x) |} for simplicity.
               Generate lift if it doesn't exist. *)
            let (c', liftName) = Generators.genLifter c
            desugarIn suffix c' (Func { Name = liftName; Params = [e] })
        | Func v -> (c, [ (suffix, v) ])
        | Join (x, y) -> desugarJoin c suffix x suffix y
        | View.If (i, t, eo) ->
            // Empty elses are equivalent to the unit.
            let e = withDefault Unit eo

            let addSuff x =
                match suffix.Node with
                | True -> x
                | _ -> freshNode (BopExpr (And, suffix, x))

            // ITE is just a join with different suffixes.
            desugarJoin c
                (addSuff i) t
                (addSuff (freshNode (UopExpr (Neg, i)))) e
    and desugarJoin c suffx x suffy y =
        (* It doesn't really matter in which order we do these, as long as
            they get the right guard and thread the context through. *)
        let cx, xv = desugarIn suffx c  x
        let cy, yv = desugarIn suffy cx y

        (cy, List.append xv yv)


    // TODO(MattWindsor91): woefully inefficient?
    desugarIn (freshNode True) ctx view

/// <summary>
///     Converts a possibly-unknown marked view into one over known views,
///     desugaring the inner view if possible.
/// </summary>
/// <param name="ctx">The current desugaring context.</param>
/// <param name="marked">The view to be converted.</param>
/// <returns>
///     A pair of the desugared view and the view prototypes generated
///     inside it.
/// </returns>
let desugarMarkedView (ctx : DesugarContext) (marked : Marked<View>)
  : DesugarContext * ViewExpr<DesugaredGView> =
    match marked with
    | Unmarked v -> pairMap id Mandatory (desugarView ctx v)
    | Questioned v -> pairMap id Mandatory (desugarView ctx v)
    | Unknown ->
        (* We assume that the UnknownViewParams are named to correspond to
           thread-local variables. *)
        let tvars = ctx.ThreadVars
        let texprs = List.map (snd >> Identifier >> freshNode) tvars
        let tpars =
            List.map (fun (t, n) -> { ParamName = n; ParamType = t })
                tvars
    
        let ctx', vname = Generators.genView "unknown" tpars ctx
        (ctx', Advisory [ (freshNode True, func vname texprs ) ])

/// <summary>
///     Desugars an atomic command.
/// </summary>
/// <param name="ctx">The current desugaring context.</param>
/// <param name="a">The <see cref="Atomic"/> to desugar.</param>
/// <returns>The resulting <see cref="DesugaredAtomic"/>.</returns>
let rec desugarAtomic (ctx : DesugarContext) (a : Atomic)
  : DesugarContext * DesugaredAtomic =
    match a.Node with
    | AAssert k ->
        (* assert(x) is lowered into 'ok = x'.
           Generate the variable 'ok' if it doesn't exist yet. *)
        let ctx', ok = Generators.genOkay ctx

        let assignOk =
            freshNode
                (Fetch
                    (freshNode (Identifier ok),
                    k,
                    Direct))

        (ctx', DAPrim assignOk)
    | AError ->
        (* error is lowered into 'assert(false)' and then
           re-lowered. *)
        desugarAtomic ctx (freshNode (AAssert (freshNode False)))
    | APrim p ->
        // Primitives are carried over unharmed.
        (ctx, DAPrim p)
    | ACond (cond, trueBranch, falseBranchO) ->
        (* Desugaring distributes over ACond.
           We desugar a missing false branch into an empty one. *)
        let falseBranch = withDefault [] falseBranchO

        let ctxT, trueBranch' = mapAccumL desugarAtomic ctx trueBranch
        let ctx', falseBranch' = mapAccumL desugarAtomic ctxT falseBranch

        (ctx', DACond (cond, trueBranch', falseBranch'))

/// <summary>
///     Desugars a primitive set.
/// </summary>
/// <param name="ctx">The current desugaring context.</param>
/// <param name="ps">The <see cref="PrimSet"/> to desugar.</param>
/// <returns>The desugared <see cref="PrimSet"/>.</returns>
let desugarPrimSet (ctx : DesugarContext) (ps : PrimSet<Atomic>)
  : DesugarContext * PrimSet<DesugaredAtomic> =
    let ctx', ats = mapAccumL desugarAtomic ctx ps.Atomics

    (ctx',
     { PreLocals = ps.PreLocals
       Atomics = ats
       PostLocals = ps.PostLocals } )

/// <summary>
///     Performs desugaring on a command.
/// </summary>
/// <param name="ctx">The current desugaring context.</param>
/// <param name="cmd">The command whose views are to be converted.</param>
/// <returns>
///     A pair of the new desugar context and desugared command.
/// </returns>
let rec desugarCommand (ctx : DesugarContext) (cmd : Command)
  : DesugarContext * FullCommand =
    let ctx', cmd' =
        match cmd.Node with
        | ViewExpr v -> failwith "should have been handled at block level"
        | If (e, t, fo) ->
            let (tc, t') = desugarBlock ctx t
            let (fc, f') =
                match fo with
                | None -> tc, None
                | Some f -> pairMap id Some (desugarBlock tc f)
            let ast = FIf (e, t', f')
            (fc, ast)
        | While (e, b) ->
            let (ctx', b') = desugarBlock ctx b
            (ctx', FWhile (e, b'))
        | DoWhile (b, e) ->
            let (ctx', b') = desugarBlock ctx b
            (ctx', FDoWhile (b', e))
        | Blocks bs ->
            let (ctx', bs') = mapAccumL desugarBlock ctx bs
            (ctx', FBlocks bs')
        | Prim ps ->
            let ctx', ps' = desugarPrimSet ctx ps
            (ctx', FPrim ps')
    (ctx', cmd |=> cmd')

/// <summary>
///     Converts a block whose views can be unknown into a block over known
///     views.
/// </summary>
/// <param name="ctx">The current desugaring context.</param>
/// <param name="block">
///     The block whose views are to be converted.
/// </param>
/// <returns>
///     A pair of the desugared block and the <see cref="DesugarContext"/>
///     containing newly generated views and constraints arising from the
///     desugar.
/// </returns>
and desugarBlock (ctx : DesugarContext) (block : Command list)
  : DesugarContext * FullBlock<ViewExpr<DesugaredGView>, FullCommand> = 
    (* Block desugaring happens in two stages.
       - First, we fill in every gap where a view should be, but isn't, with
         an unknown view.
       - Next, we desugar the resulting fully specified block. *)

    // Add an Unknown view to the start of a block without one.
    let cap l =
        match l with
        | { Node = ViewExpr v } :: _ -> (l, v)
        | _ -> (freshNode (ViewExpr Unknown) :: l, Unknown)

    (* If the first item isn't a view, we have to synthesise a block
       precondition. *)
    let (blockP, pre) = cap block

    let skip () =
        freshNode (Prim { PreLocals = []; Atomics = []; PostLocals = [] })

    (* If the last item isn't a view, we have to synthesise a block
       postcondition.
       (TODO(CaptainHayashi): do this efficiently) *)
    let blockPQ = List.rev (fst (cap (List.rev blockP)))

    (* If there is only one item in the block, then by the above it must be
       a view, so we can skip processing commands. *)
    let cmds =
        match blockPQ with
        | [x] -> []
        | _ ->
        (* Next, we have to slide down the entire block pairwise.
           1. If we see ({| view |}, {| view |}), insert a skip between them.
           2. If we see (cmd, {| view |}), add it directly to the full block;
           3. If we see ({| view |}, cmd), ignore it.  Either the view is the
              precondition at the start, which is accounted for, or it was just
              added through rule 1. and can be ignored;
           3. If we see (cmd, cmd), add (cmd, {| ? |}) to the full block.
              We'll add the next command on the next pass. *)
        let blockPairs = Seq.windowed 2 blockPQ

        let fillBlock bsf pair =
            match pair with
            | [| { Node = ViewExpr x }; { Node = ViewExpr y } |] -> (skip (), x) :: bsf
            | [| cx                   ; { Node = ViewExpr y } |] -> (cx, y) :: bsf
            | [| { Node = ViewExpr x }; cx                    |] -> bsf
            | [| cx                   ; _                     |] -> (cx, Unknown) :: bsf
            | x -> failwith (sprintf "unexpected window in fillBlock: %A" x)

        // The above built the block backwards, so reverse it.
        List.rev (Seq.fold fillBlock [] blockPairs)

    // Now we can desugar each view in the block contents.
    let desugarViewedCommand c (cmd, post) =
        let cc, cmd' = desugarCommand c cmd
        let cp, post' = desugarMarkedView cc post
        (cp, (cmd', post'))

    let pc, pre' = desugarMarkedView ctx pre
    let ctx', cmds' = mapAccumL desugarViewedCommand pc cmds

    let block' = { Pre = pre' ; Cmds = cmds' }
    (ctx', block')

/// <summary>
///     Creates an initial desugaring context.
/// </summary>
/// <param name="tvars">The list of thread-local variables.</param>
/// <param name="vprotos">The sequence of existing view prototypes.</param>
/// <returns>An initial <see cref="DesugarContext"/>.</returns>
let initialContext
  (svars : (TypeLiteral * string) seq)
  (tvars : (TypeLiteral * string) seq)
  (vprotos : ViewProto seq)
  : DesugarContext =
    { SharedVars = List.ofSeq svars
      ThreadVars = List.ofSeq tvars
      LocalLiftView = None 
      OkayBool = None
      GeneratedProtos = Set.empty
      ExistingProtos = Set.ofSeq vprotos }

/// <summary>
///     Converts a sequence of methods whose views can be unknown into
///     a sequence of methods over known views.
///
///     <para>
///         This effectively replaces every view <c>{| ? |}</c> with
///         a view <c>{| n(locals) |}</c>, where <c>n</c> is fresh,
///         and then adds <c>n</c> to the view prototypes considered by
///         the constraint searcher.
///     </para>
///     <para>
///         It also collapses <c>{| false |}</c>,
///         <c>{| locals {...} |}</c>, and <c>{| if v { ... } |}</c>
///         into guarded views.
///     </para>
/// </summary>
/// <param name="tvars">
///     The <c>VarMap</c> of thread-local variables.
/// </param>
/// <param name="methods">
///     The methods to convert, as a map from names to bodies.
/// </param>
/// <returns>
///     A pair of desugared methods and the <see cref="DesugarContext"/>
///     containing newly generated views and constraints arising from the
///     desugar.
/// </returns>
let desugar
  (collated : CollatedScript)
  : (DesugarContext * Map<string, FullBlock<ViewExpr<DesugaredGView>, FullCommand>>) =
    let ctx =
        initialContext collated.SharedVars collated.ThreadVars collated.VProtos
    
    let ms = Map.toList collated.Methods
    let (ctx', ms') =
        mapAccumL
            (fun c (n, b) ->
                 let c', b' = desugarBlock c b
                 (c', (n, b')))
            ctx
            ms
    (ctx', Map.ofSeq ms')


/// <summary>
///     Tests for <c>ViewDesugar</c>.
/// </summary>
module Tests =
    open NUnit.Framework
    open Starling.Utils.Testing

    let normalCtx : DesugarContext =
        let svars = [ (TInt, "serving"); (TInt, "ticket") ]
        let tvars = [ (TInt, "s"); (TInt, "t") ]
        let vprotos = Seq.empty
        initialContext svars tvars vprotos

    let dupeCtx : DesugarContext =
        let svars =
            [ (TInt, "__ok_0")
              (TBool, "__ok_1")
              (TInt, "serving")
              (TInt, "ticket") ]
        let tvars = [ (TInt, "s"); (TInt, "t") ]
        let vprotos = Seq.empty
        initialContext svars tvars vprotos

    module DesugarMarkedView =
        let check
          (expectedCtx : DesugarContext)
          (expectedView : ViewExpr<DesugaredGView>)
          (ctx : DesugarContext)
          (ast : Marked<View>)
          : unit =
            let got = desugarMarkedView ctx ast
            assertEqual (expectedCtx, expectedView) got

        [<Test>]
        let ``desugaring an unknown view creates a fresh view`` () : unit =
            let nfunc =  
                NoIterator
                    (Func = 
                        func "__unknown_0"
                            [ { ParamName = "s"; ParamType = TInt }
                              { ParamName = "t"; ParamType = TInt } ],
                     IsAnonymous = false)

            check
                { normalCtx with GeneratedProtos = Set.singleton nfunc }
                (Advisory
                    [ (freshNode True,
                       func "__unknown_0"
                        [ freshNode (Identifier "s")
                          freshNode (Identifier "t") ] ) ] )
                normalCtx
                Unknown

    /// <summary>Tests for the atomic command desugarer.</summary>
    module DesugarAtomic =
        let check
          (expectedCtx : DesugarContext)
          (expectedAtom : DesugaredAtomic)
          (ctx : DesugarContext)
          (ast : Atomic)
          : unit =
            let got = desugarAtomic ctx ast
            assertEqual (expectedCtx, expectedAtom) got

        [<Test>]
        let ``Desugar assert into an assignment to the okay variable`` () =
            check
                { normalCtx with
                    OkayBool = Some "__ok_0"
                    SharedVars = (TBool, "__ok_0") :: normalCtx.SharedVars }
                (DAPrim
                    (freshNode
                        (Fetch
                            (freshNode (Identifier "__ok_0"),
                             freshNode (Identifier "foobar"),
                             Direct))))
                normalCtx
                (freshNode
                    (AAssert (freshNode (Identifier "foobar"))))

        [<Test>]
        let ``Desugar error into a false assignment to the okay variable`` () =
            check
                { normalCtx with
                    OkayBool = Some "__ok_0"
                    SharedVars = (TBool, "__ok_0") :: normalCtx.SharedVars }
                (DAPrim
                    (freshNode
                        (Fetch
                            (freshNode (Identifier "__ok_0"),
                             freshNode False,
                             Direct))))
                normalCtx
                (freshNode AError)

        [<Test>]
        let ``Desugar assert properly when normal okay is taken`` () =
            check
                { dupeCtx with
                    OkayBool = Some "__ok_2"
                    SharedVars = (TBool, "__ok_2") :: dupeCtx.SharedVars }
                (DAPrim
                    (freshNode
                        (Fetch
                            (freshNode (Identifier "__ok_2"),
                             freshNode (Identifier "foobar"),
                             Direct))))
                dupeCtx
                (freshNode
                    (AAssert (freshNode (Identifier "foobar"))))

        [<Test>]
        let ``Desugar error properly when normal okay is taken`` () =
            check
                { dupeCtx with
                    OkayBool = Some "__ok_2"
                    SharedVars = (TBool, "__ok_2") :: dupeCtx.SharedVars }
                (DAPrim
                    (freshNode
                        (Fetch
                            (freshNode (Identifier "__ok_2"),
                             freshNode False,
                             Direct))))
                dupeCtx
                (freshNode AError)


    /// <summary>Tests for the single-view desugarer.</summary>
    module DesugarView =
        let check
          (expectedCtx : DesugarContext)
          (expectedView : DesugaredGView)
          (ctx : DesugarContext)
          (ast : View)
          : unit =
            let got = desugarView ctx ast
            assertEqual (expectedCtx, expectedView) got
        
        [<Test>]
        let ``Desugar the empty view into an empty view`` () =
            check normalCtx [] normalCtx Unit

        [<Test>]
        let ``Desugar a local expression`` () =
            check
                { normalCtx with
                    LocalLiftView = Some "__lift_0"
                    GeneratedProtos =
                        Set.ofList
                            [ (NoIterator
                                    (Func = func "__lift_0" [ { ParamName = "x"; ParamType = TBool } ],
                                    IsAnonymous = false)) ] }
                [ (freshNode True, func "__lift_0" [ freshNode (Identifier "bar") ]) ]
                normalCtx
                (Local (freshNode (Identifier "bar")))

        [<Test>]
        let ``Desugar a falsehood`` () =
            check
                { normalCtx with
                    LocalLiftView = Some "__lift_0"
                    GeneratedProtos =
                        Set.ofList
                            [ (NoIterator
                                    (Func = func "__lift_0" [ { ParamName = "x"; ParamType = TBool } ],
                                    IsAnonymous = false)) ] }
                [ (freshNode True, func "__lift_0" [ freshNode (False) ]) ]
                normalCtx
                Falsehood



        [<Test>]
        let ``Desugar a flat join`` () =
            check
                normalCtx
                [ (freshNode True, func "foo" [ freshNode (Identifier "bar") ])
                  (freshNode True, func "bar" [ freshNode (Identifier "baz") ]) ] 
                normalCtx
                (Join
                    (Func (afunc "foo" [ freshNode (Identifier "bar") ]),
                     Func (afunc "bar" [ freshNode (Identifier "baz") ])))

        [<Test>]
        let ``Desugar a single conditional`` () =
            check
                normalCtx
                [ (freshNode (Identifier "s"),
                   func "foo" [ freshNode (Identifier "bar") ] )
                  (freshNode (UopExpr (Neg, freshNode (Identifier "s"))),
                   func "bar" [ freshNode (Identifier "baz") ] ) ]
                normalCtx
                (View.If
                    (freshNode (Identifier "s"),
                     Func (afunc "foo" [ freshNode (Identifier "bar") ] ),
                     Some (Func (afunc "bar" [ freshNode (Identifier "baz") ] ))))

        [<Test>]
        let ``Desugar a single conditional with no else`` () =
            check
                normalCtx
                [ (freshNode (Identifier "s"),
                   func "foo" [ freshNode (Identifier "bar") ] ) ]
                normalCtx
                (View.If
                    (freshNode (Identifier "s"),
                     Func (afunc "foo" [ freshNode (Identifier "bar") ] ),
                     None))


        [<Test>]
        let ``Convert a complex-nested CondView-list to a GuarView-list with complex guards`` () =
            check
                normalCtx
                [ (freshNode
                    (BopExpr
                        (And,
                         freshNode (Identifier "s"),
                         freshNode (Identifier "t"))),
                   func "foo" [ freshNode (Identifier "bar") ] )
                  (freshNode
                     (BopExpr
                         (And,
                          freshNode (Identifier "s"),
                          freshNode (Identifier "t"))),
                   func "bar" [ freshNode (Identifier "baz") ] )
                  (freshNode
                     (BopExpr
                         (And,
                          freshNode (Identifier "s"),
                          freshNode
                             (UopExpr (Neg, (freshNode (Identifier "t")))))),
                   func "fizz" [ freshNode (Identifier "buzz") ] )
                  (freshNode (Identifier "s"),
                   func "in" [ freshNode (Identifier "out") ] )
                  (freshNode
                     (UopExpr (Neg, freshNode (Identifier "s"))),
                   func "ding" [ freshNode (Identifier "dong") ] ) ]
                normalCtx
                (View.If
                    (freshNode (Identifier "s"),
                     Join
                        (View.If
                            (freshNode (Identifier "t"),
                             Join
                                (Func (afunc "foo" [ freshNode (Identifier "bar") ] ),
                                 Func (afunc "bar" [ freshNode (Identifier "baz") ] )),
                             Some (Func (afunc "fizz" [ freshNode (Identifier "buzz") ] ))),
                         Func (afunc "in" [ freshNode (Identifier "out") ])),
                     Some (Func (afunc "ding" [ freshNode (Identifier "dong") ]))))