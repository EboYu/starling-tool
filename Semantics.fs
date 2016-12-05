/// <summary>
///   Module containing the semantic instantiator.
///
///   <para>
///     The semantic instantiator converts the commands in a model's
///     axioms into Boolean expressions, by instantiating them in
///     accordance with the model's semantic definitions.
///   </para>
///   <para>
///     It also ensures variables not mentioned in a command's semantic
///     definition are preserved in the resulting expression.
///   </para>
/// </summary>
module Starling.Semantics

open Chessie.ErrorHandling
open Starling.Collections
open Starling.Core.TypeSystem
open Starling.Core.Command
open Starling.Core.Command.Compose
open Starling.Core.GuardedView
open Starling.Core.Expr
open Starling.Core.Var
open Starling.Core.Symbolic
open Starling.Core.Symbolic.Traversal
open Starling.Core.Model
open Starling.Core.Traversal


/// <summary>
///     Types used in the Semantics stage.
/// </summary>
[<AutoOpen>]
module Types =
    /// Type of errors relating to semantics instantiation.
    type Error =
        /// There was an error instantiating a semantic definition.
        | Instantiate of prim: PrimCommand
                       * error: Error
        /// A primitive has a missing semantic definition.
        | MissingDef of prim: PrimCommand
        /// Got unexpected number of arguments
        | CountMismatch of expected: int * actual: int
        | TypeMismatch of param: TypedVar * atype: Type
        /// <summary>
        ///     The semantics of a command is ill-formed.
        /// </summary>
        | BadSemantics of why : string
        /// <summary>
        ///     We tried to substitute parameters, but one parameter was free
        ///     (not bound to an expression) somehow.
        /// </summary>
        | FreeVarInSub of param: TypedVar
        /// <summary>
        ///     An error occurred during traversal.
        ///     This error may contain nested semantics errors!
        /// </summary>
        | Traversal of TraversalError<Error>


/// <summary>
///     Pretty printers used in the Semantics stage.
/// </summary>
module Pretty =
    open Starling.Core.Pretty
    open Starling.Core.TypeSystem.Pretty
    open Starling.Core.Var.Pretty
    open Starling.Core.Command.Pretty
    open Starling.Core.Symbolic.Pretty
    open Starling.Core.Model.Pretty

    /// Pretty-prints semantics errors.
    let rec printSemanticsError =
        function
        | Instantiate (prim, error) ->
          colonSep
              [ fmt "couldn't instantiate primitive '{0}'"
                    [ printPrimCommand prim ]
                printSemanticsError error ]
        | MissingDef prim ->
            fmt "primitive '{0}' has no semantic definition"
                [ printPrimCommand prim ]
        | TypeMismatch (par, atype) ->
            fmt "parameter '{0}' conflicts with argument of type '{1}'"
                [ printTypedVar par; printType atype ]
        | CountMismatch (fn, dn) ->
            fmt "view usage has {0} parameter(s), but its definition has {1}"
                [ fn |> sprintf "%d" |> String; dn |> sprintf "%d" |> String ]
        | BadSemantics why ->
            errorStr "internal semantics error:" <+> errorStr why
        | FreeVarInSub var ->
            // TODO(CaptainHayashi): this is a bit shoddy.
            error
                (hsep
                    [ String "parameter '"
                      printTypedVar var
                      String "' has no substitution" ])
        | Traversal err ->
            Starling.Core.Traversal.Pretty.printTraversalError printSemanticsError err

/// <summary>
///     A write record for an variable.
///
///     <para>
///         Write records are used to build frames, by calculating which bits
///         of an variable have been modified by a command.
/// </summary>
type Write =
    /// <summary>The entire lvalue has been written to or havoc'd.</summary>
    | Entire of newVal : Expr<Sym<Var>> option
    /// <summary>
    ///     Only certain parts of the lvalue have been written to,
    ///     and their recursive write records are enclosed.
    /// </summary>
    | Indices of Map<IntExpr<Sym<Var>>, Write>
    override this.ToString () = sprintf "%A" this

/// <summary>
///     Records a write into a write map.
/// </summary>
/// <param name="var">The variable being written to.</param>
/// <param name="idxPath">
///     The path of indexes from the variable being written to to the variable.
///     For example, [3; x; 1+i] would represent a write to A[3][x][1+i].
/// </param>
/// <param name="value">The value written to the eventual destination.</param>
/// <param name="map">The write map being extended.</param>
/// <returns>The extended write map.</returns>
let markWrite (var : TypedVar) (idxPath : IntExpr<Sym<Var>> list)
  (value : Expr<Sym<Var>> option)
  (map : Map<TypedVar, Write>)
  : Map<TypedVar, Write> =
    (* First, consider what it means to add an index write to an index write
       map. *)
    let rec markWriteIdx
      (idx : IntExpr<Sym<Var>>)
      (idxPathRest : IntExpr<Sym<Var>> list)
      (imap : Map<IntExpr<Sym<Var>>, Write>) =
        // Find out if we've already written to this index.
        let idxRec = imap.TryFind idx
        let imapLessIdx =
            maybe imap (fun _ -> imap.Remove idx) (imap.TryFind idx)

        let idxRec' =
            match idxPathRest with
            | [] ->
                (* If there is no subscript, then we must be writing to this
                   entire index, so mark it as Entire... if it isn't already
                   written to. *)
                match idxRec with
                | Some _ -> failwith "markWriteIdx: tried to write twice with empty path"
                | None -> Entire value
            | x::xs ->
                match idxRec with
                | Some (Entire _) -> failwith "markWriteIdx: tried to write twice with nonempty path"
                | Some (Indices imap) -> markWriteIdx x xs imap
                | None -> markWriteIdx x xs Map.empty

        Indices (Map.add idx idxRec' imapLessIdx)


    // Now we can define the top-level.

    let varRec = map.TryFind var
    let mapLessVar = maybe map (fun _ -> map.Remove var) (map.TryFind var)

    let varRec' =
        match idxPath with
        | [] ->
            (* If there is no subscript, then we must be writing to this entire
               variable, so mark it as Entire... if it isn't already written to. *)
            match varRec with
            | Some _ -> failwith "markWrite: tried to write twice with empty path"
            | None -> Entire value
        | (x::xs) ->
            match varRec with
            | Some (Entire _) -> failwith "markWrite: tried to write twice with nonempty path"
            | Some (Indices imap) -> markWriteIdx x xs imap
            | None -> markWriteIdx x xs Map.empty

    Map.add var varRec' mapLessVar

/// <summary>
///     Tries to extract the variable and index path from a lvalue.
/// </summary>
let varAndIdxPath (expr : Expr<Sym<Var>>)
  : (TypedVar * IntExpr<Sym<Var>> list) option =
    // TODO(CaptainHayashi): proper doc comment.
    // TODO(CaptainHayashi): merge with type lookup stuff in Modeller?
    // TODO(CaptainHayashi): error perhaps if given a non-lvalue

    let rec getInBool bx path =
        match bx with
        | BVar (Reg v) -> Some (Bool v, path)
        // Symbols are not lvalues, so we can't process them.
        | BIdx (e, l, a, i) -> getInArray e l a (i::path)
        | _ -> None
    and getInInt ix path =
        match ix with
        | IVar (Reg v) -> Some (Int v, path)
        // Symbols are not lvalues, so we can't process them.
        | IIdx (e, l, a, i) -> getInArray e l a (i::path)
        | _ -> None
    and getInArray eltype length ax path =
        match ax with
        | AVar (Reg v) -> Some (Array (eltype, length, v), path)
        // Symbols are not lvalues, so we can't process them.
        | AIdx (e, l, a, i) -> getInArray e l a (i::path)
        | _ -> None

    match expr with
    | Int ix -> getInInt ix []
    | Bool bx -> getInBool bx []
    | Array (eltype, length, ax) -> getInArray eltype length ax []

/// <summary>
///     Generates a write record map for a given assignment list.
/// </summary>
/// <param name="assigns">The assignment list to investigate.</param>
/// <returns>The write map for that microcode list.</returns>
let makeWriteMap (assigns : (Expr<Sym<Var>> * Expr<Sym<Var>> option) list)
  : Map<TypedVar, Write> =
    let addToWriteMap map (lv, rv) =
        // TODO(CaptainHayashi): complain if lv isn't a lvalue?
        maybe map (fun (var, idx) -> markWrite var idx rv map) (varAndIdxPath lv)
    List.fold addToWriteMap Map.empty assigns

/// <summary>
///     Partitions a list of microcode instructions.
/// </summary>
/// <param name="instrs">The instructions to partition.</param>
/// <returns>
///     A triple containing a list of assignments, a list of assumptions,
///     and a list of (unpartitioned) microcode branches.
/// </returns>
let partitionMicrocode (instrs : Microcode<Expr<Sym<Var>>, Sym<Var>> list)
  : ((Expr<Sym<Var>> * Expr<Sym<Var>> option) list
     * BoolExpr<Sym<Var>> list
     * (BoolExpr<Sym<Var>>
        * Microcode<Expr<Sym<Var>>, Sym<Var>> list
        * Microcode<Expr<Sym<Var>>, Sym<Var>> list) list) =
    let partitionStep (assigns, assumes, branches) instr =
        match instr with
        | Assign (l, r) -> ((l, r)::assigns, assumes, branches)
        | Assume s -> (assigns, s::assumes, branches)
        | Branch (i, t, e) -> (assigns, assumes, (i, t, e)::branches)
    List.fold partitionStep ([], [], []) instrs

/// <summary>
///     Generates a well-typed expression for a subscript of a given array.
/// </summary>
/// <param name="eltype">The type of elements in the array.</param>
/// <param name="length">The length of the array.</param>
/// <param name="array">The array to subscript.</param>
/// <param name="idx">The index to subscript by.</param>
/// <returns>A well-typed <see cref="Expr"/> capturing the subscript.</returns>
let mkIdx (eltype : Type) (length : int option) (arr : ArrayExpr<Sym<Var>>)
  (idx : IntExpr<Sym<Var>>)
  : Expr<Sym<Var>> =
    let record = (eltype, length, arr, idx)

    match eltype with
    | Type.Int () -> Expr.Int (IIdx record)
    | Type.Bool () -> Expr.Bool (BIdx record)
    | Type.Array (eltype', length', ()) -> Expr.Array (eltype', length', AIdx record)

/// <summary>
///     Normalises a list of assignments such that they represent
///     entire-variable assignments.
///     <para>
///         This converts array-subscript assignments into assignments of
///         arrays to array updates.
///         This allows the framing logic to frame on a per-variable basis
///         in the presence of arrays.
///     </para>
/// </summary>
/// <param name="assigns">The assignments to normalise.</param>
/// <returns>
///     The assignments in entire-variable form, in arbitrary order.
/// </returns>
let normaliseAssigns (assigns : (Expr<Sym<Var>> * Expr<Sym<Var>> option) list)
  : Result<(TypedVar * Expr<Sym<Var>> option) list, Error> =
    // First, we convert the assigns to a write map.
    let wmap = makeWriteMap assigns
    (* Then, each item in the write map represents an assignment.
       We need to convert each write map entry into an array update or a
       direct value. *)
    let rec translateRhs lhs (value : Write) =
        match value with
        | Entire v -> ok v
        | Indices ixmap ->
            // TODO(CaptainHayashi): proper errors.
            let addUpdate
              (index : IntExpr<Sym<Var>>, value : Write) (lhs' : Expr<Sym<Var>> option)
              : Result<Expr<Sym<Var>> option, Error> =
                (* TODO(CaptainHayashi): currently, if an array update havocs,
                   any future updates also havoc.  This perhaps throws too much
                   information away! *)
                match lhs' with
                | None -> ok None
                | Some (Array (eltype, length, alhs)) ->
                    (* Need to translate any further subscripts inside value.
                       But, to do that, we need to know what the LHS of those
                       subscripts is! *)
                    let vlhs = mkIdx eltype length alhs index
                    let vrhsResult = translateRhs vlhs value
                    lift
                        (Option.map
                            (fun vrhs ->
                                 Expr.Array
                                    (eltype, length,
                                     AUpd (eltype, length, alhs, index, vrhs))))
                        vrhsResult
                | _ -> fail (BadSemantics "tried to index into a non-array")
            seqBind addUpdate (Some lhs) (Map.toSeq ixmap)

    let translateAssign (lhs : TypedVar, rhs) =
        // lhs is a typed variable here, but must be an expression for the above
        let lhsE = mkVarExp (mapCTyped Reg lhs)
        lift (mkPair lhs) (translateRhs lhsE rhs)

    collect (Seq.map translateAssign (Map.toSeq wmap))

/// <summary>
///     Normalises a microcode listing.
/// </summary>
/// <param name="instrs">The set of instructions to normalise.</param>
/// <returns>On success, the normalised listing (in arbitrary order).</returns>
let rec normaliseMicrocode
  (instrs : Microcode<Expr<Sym<Var>>, Sym<Var>> list)
  : Result<Microcode<TypedVar, Sym<Var>> list, Error> =
    let assigns, assumes, branches = partitionMicrocode instrs

    let normaliseBranch (i, t, e) =
        let t'Result = normaliseMicrocode t
        let e'Result = normaliseMicrocode e
        lift2 (fun t' e' -> (i, t', e')) t'Result e'Result

    let branches'Result = collect (Seq.map normaliseBranch branches)
    let assigns'Result = normaliseAssigns assigns

    lift2
        (fun branches' assigns' ->
            List.concat
                [ List.map Assign assigns'
                  List.map Assume assumes
                  List.map Branch branches' ])
        branches'Result
        assigns'Result

let primParamSubFun
  (cmd : PrimCommand)
  (sem : PrimSemantics)
  : Traversal<TypedVar, Expr<Sym<Var>>, Error, unit> =

    let fpars = List.append cmd.Args cmd.Results
    let dpars = sem.Args @ sem.Results

    let pmap =
        Map.ofSeq (Seq.map2 (fun par up -> valueOf par, up) dpars fpars)

    ignoreContext
        (function
         | WithType (var, vtype) as v ->
            match pmap.TryFind var with
            | Some tvar ->
                if vtype = typeOf tvar
                then ok tvar
                else fail (Inner (TypeMismatch (v, typeOf tvar)))
            | None -> fail (Inner (FreeVarInSub v)))

let checkParamCountPrim (prim : PrimCommand) (def : PrimSemantics) : Result<PrimSemantics, Error> =
    let fn = List.length prim.Args
    let dn = List.length def.Args
    if fn = dn then ok def else fail (CountMismatch (fn, dn))

let lookupPrim (prim : PrimCommand) (map : PrimSemanticsMap) : Result<PrimSemantics, Error>  =
    maybe
        (fail (MissingDef prim))
        (checkParamCountPrim prim)
        (map.TryFind prim.Name)

let checkParamTypesPrim (prim : PrimCommand) (sem : PrimSemantics) : Result<PrimSemantics, Error> =
    List.map2
        (fun fp dp ->
            if typesCompatible (typeOf fp) (typeOf dp)
            then ok ()
            else fail (TypeMismatch (dp, typeOf fp)))
        prim.Args
        sem.Args
    |> collect
    |> lift (fun _ -> sem)

/// <summary>
///     Lifts lvalue and rvalue traversals onto a microcode instruction.
/// </summary>
/// <param name="ltrav">The lvalue traversal to lift onto microcode.</param>
/// <param name="rtrav">The rvalue traversal to lift onto microcode.</param>
/// <typeparam name="L">The type of input lvalues.</typeparam>
/// <typeparam name="RV">The type of input rvalue variables.</typeparam>
/// <typeparam name="LO">The type of output lvalue.</typeparam>
/// <typeparam name="RVO">The type of output rvalue variables.</typeparam>
/// <typeparam name="Var">The type of context variables.</typeparam>
/// <returns>
///     A traversal that visits all of the lvalues and rvalues in a microcode
///     instruction, applying the given traversals to each.
/// </returns>
let traverseMicrocode
  (ltrav : Traversal<'L, 'LO, Error, 'Var>)
  (rtrav : Traversal<Expr<'RV>, Expr<'RVO>, Error, 'Var>)
  : Traversal<Microcode<'L, 'RV>,
              Microcode<'LO, 'RVO>, Error, 'Var> =
    let brtrav = traverseBoolAsExpr rtrav

    let rec tm ctx mc =
        let tml = tchainL tm id

        match mc with
        | Assign (lv, Some rv) ->
            tchain2 ltrav rtrav (pairMap id Some >> Assign) ctx (lv, rv)
        | Assign (lv, None) ->
            tchain ltrav (flip mkPair None >> Assign) ctx lv
        | Assume assumption -> tchain brtrav Assume ctx assumption
        | Branch (i, t, e) -> tchain3 brtrav tml tml Branch ctx (i, t, e)
    tm

/// <summary>
///     Lifts a parameter instantiation traversal onto a microcode instruction.
/// </summary>
/// <param name="trav">The traversal to lift onto microcode.</param>
/// <typeparam name="Var">The type of context variables.</typeparam>
/// <returns>
///     A traversal that visits all of the lvalues and rvalues in a microcode
///     instruction.
/// </returns>
let tliftToMicrocode
  (trav : Traversal<TypedVar, Expr<Sym<Var>>, Error, 'Var>)
  : Traversal<Microcode<TypedVar, Var>,
              Microcode<Expr<Sym<Var>>, Sym<Var>>, Error, 'Var> =
    traverseMicrocode trav (tliftToExprSrc trav)

/// <summary>
///     Traversal that marks a microcode instruction with its pre- and
///     post-state.
/// </summary>
let rec markMicrocode
  (postMark : Var -> MarkedVar)
  (preStates : Map<TypedVar, MarkedVar>)
  : Traversal<Microcode<TypedVar, Sym<Var>>,
              Microcode<CTyped<MarkedVar>, Sym<MarkedVar>>,
              Error, unit> =
    // Define marker functions for lvalues and rvalues...
    let lf var = ok (postMark var)
    let rf var =
        match preStates.TryFind var with
         // TODO(CaptainHayashi): proper error
         | None -> fail (Inner (BadSemantics "somehow referenced variable not in scope"))
         | Some mv -> ok (withType (typeOf var) (Reg mv))

    // ...then use them in a traversal.
    let lt = tliftOverCTyped (ignoreContext lf)
    let rt = tliftToExprSrc (tliftToTypedSymVarSrc (tliftToExprDest (ignoreContext rf)))

    traverseMicrocode lt rt

/// <summary>
///     Updates a map from variables to their last marker with the assignments
///     in a microcode listing.
/// </summary>
let rec updateState
  (state : Map<TypedVar, MarkedVar>)
  (listing : Microcode<CTyped<MarkedVar>, Sym<MarkedVar>> list)
  : Map<TypedVar, MarkedVar> =
    let updateOne (s : Map<TypedVar, MarkedVar>) m =
        // TODO(CaptainHayashi): de-duplicate this
        match m with
        | Assign (lv, rv) ->
            // Assumption: this is monotone, eg. rv >= s.[lv]
            // TODO(CaptainHayashi): check this?
            match (valueOf lv) with
            | Before l | After l | Intermediate (_, l) | Goal (_, l) ->
                s.Add(withType (typeOf lv) l, valueOf lv)
        | Assume _ -> s
        | Branch (i, t, e) ->
            updateState (updateState s t) e
    List.fold updateOne state listing

/// <summary>
///     Converts a microcode instruction set into a two-state Boolean predicate.
/// </summary>
let rec markedMicrocodeToBool
  (instrs : Microcode<CTyped<MarkedVar>, Sym<MarkedVar>> list)
  : BoolExpr<Sym<MarkedVar>> =
    let translateInstr instr =
        match instr with
        // Havoc
        | Assign (x, None) -> BTrue
        // Deterministic assignment
        | Assign (x, Some y) -> mkEq (mkVarExp (mapCTyped Reg x)) y
        | Assume x -> x
        | Branch (i, t, e) ->
            let tX = markedMicrocodeToBool t
            let eX = markedMicrocodeToBool e
            mkAnd2 (mkImplies i tX) (mkImplies (mkNot i) eX)
    mkAnd (List.map translateInstr instrs)

/// <summary>
///     Generates a frame from a state assignment map.
/// </summary>
let makeFrame (states : Map<TypedVar, MarkedVar>) : BoolExpr<Sym<MarkedVar>> =
    let maybeFrame (var, state) =
        match state with
        // If the variable was last assigned an After, it needs no framing.
        | After _ -> None
        // Otherwise, we need to bind After to its last assigned state.
        | _ ->
            Some
                (mkEq
                    (mkVarExp (mapCTyped (After >> Reg) var))
                    (mkVarExp (withType (typeOf var) (Reg state))))
    mkAnd (List.choose maybeFrame (Map.toList states))

/// <summary>
///     Converts a microcode routine into a two-state Boolean predicate.
/// </summary>
let microcodeRoutineToBool
  (vars : TypedVar list)
  (routine : Microcode<Expr<Sym<Var>>, Sym<Var>> list list)
  : Result<BoolExpr<Sym<MarkedVar>>, Error> =
    (* Each item in 'routine' represents a stage in the sequential composition
       of microcode listings.  Each stage has a corresponding variable state:
       the first is Intermediate 0, the second Intermediate 1, and so on until
       the last stage assigns to After.

       To begin translation, we annotate each stage with the corresponding
       state marker. *)
    let decideMarker index stage =
        (stage,
         if index = routine.Length - 1
         then After
         else (curry Intermediate (bigint index)))
    let markedStages = Seq.mapi decideMarker routine

    (* Throughout the translation, we keep a record of the last variable state
       that was assigned to for each variable.  To begin with, each variable
       is assigned its own pre-state. *)
    let initialState =
        Map.ofSeq (Seq.map (fun var -> (var, Before (valueOf var))) vars)

    (* The main process is a fold over all of the individual stages.
       For each, we normalise the listing to assign whole variables instead of
       lvalues, then translate the lvalues to the last assigned state of their
       variable and rvalues to the expected assigned state of this stage.
       Finally, we repopulate the state with the new assignments.

       This way, 'state' always tells us which values were assigned in the last
       stage, several stages ago, or not at all. *)
    let listingToBool (listing, marker) (state, xs) =
        (* First, normalise the listing.
           This ensures only whole variables are written to, which allows us to
           track the assignment later. *)
        let normalisedR = normaliseMicrocode listing

        (* Next, make the microcode state-aware.
           This means that each lvalue is translated with this
           stage's marker, and each rvalue is translated according to the state
           map. *)
        let stateAwareR =
            let makeAware normalised =
                mapMessages Traversal
                    (mapTraversal (tchainL (markMicrocode marker state) id)
                        normalised)
            bind makeAware normalisedR

        (* Finally, we need to repopulate the table with all assignments made
           in this command, and actually translate the listing to a Boolean. *)
        lift
            (fun stateAware ->
                (updateState state stateAware, markedMicrocodeToBool stateAware :: xs))
            stateAwareR
    let processedR = seqBind listingToBool (initialState, []) markedStages
    (* Finally, decide the frame and conjoin it with the listings.
       The frame is (x!after = x!z) where x!z is the last assignment of x and
       z is not after. *)
    lift (fun (assigns, bools) -> mkAnd (makeFrame assigns :: bools))
        processedR 

/// <summary>
///     Converts a primitive command to its representation as a disjoint
///     parallel composition of microcode instructions.
/// </summary>
/// <param name="semantics">The map from command to microcode schemata.</param>
/// <param name="prim">The primitive command to instantiate.</param>
/// <returns>
///     If the instantiation succeeded, the resulting list of parallel-composed
///     <see cref="Microcode"/> instructions.
/// </returns>
let instantiateToMicrocode
  (semantics : PrimSemanticsMap)
  (prim : PrimCommand)
  : Result<Microcode<Expr<Sym<Var>>, Sym<Var>> list, Error> =
    let primDefR = lookupPrim prim semantics
    let typeCheckedDefR = bind (checkParamTypesPrim prim) primDefR

    let instantiate (s : PrimSemantics) =
        let subInMCode =
                tchainL (tliftToMicrocode (primParamSubFun prim s)) id
        mapMessages Traversal (mapTraversal subInMCode s.Body)

    bind instantiate typeCheckedDefR

/// <summary>
///     Translates a command to a multi-state Boolean expression.
/// </summary>
/// <param name="semantics">The map from command to microcode schemata.</param>
/// <param name="svars">The shared variable environment.</param>
/// <param name="tvars">The thread-local variable environment.</param>
/// <param name="cmd">The command to instantiate.</param>
/// <returns>
///     If the instantiation succeeded, the resulting Boolean expression.
/// </returns>
let semanticsOfCommand
  (semantics : PrimSemanticsMap)
  (svars : VarMap)
  (tvars : VarMap)
  (cmd : Command) : Result<CommandSemantics<SMBoolExpr>, Error> =
    // First, get the microcode representation of each part of the command.
    let microcodeR = collect (Seq.map (instantiateToMicrocode semantics) cmd)

    (* Then, translate the microcode to a framed Boolean expression.
       This requires us to provide all variables in the environment for framing
       purposes. *)
    let vars =
        List.ofSeq
            (Seq.append
                (VarMap.toTypedVarSeq svars)
                (VarMap.toTypedVarSeq tvars))
    let semanticsR = bind (microcodeRoutineToBool vars) microcodeR

    // Finally, collect all of these results into a CommandSemantics record.
    lift (fun semantics -> { Cmd = cmd; Semantics = semantics }) semanticsR

open Starling.Core.Axiom.Types
/// Translate a model over Prims to a model over semantic expressions.
let translate
  (model : Model<GoalAxiom<Command>, 'viewdef>)
  : Result<Model<GoalAxiom<CommandSemantics<SMBoolExpr>>, 'viewdef>, Error> =
    let modelSemantics = semanticsOfCommand model.Semantics model.SharedVars model.ThreadVars
    let replaceCmd ga c = { Goal = ga.Goal; Axiom = {Pre = ga.Axiom.Pre; Post = ga.Axiom.Post; Cmd = c }}
    let transSem ga = bind (replaceCmd ga >> ok) (modelSemantics ga.Axiom.Cmd)
    tryMapAxioms transSem model
