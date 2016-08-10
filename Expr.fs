/// <summary>
///     Utilities and types for working with expressions.
/// </summary>
module Starling.Core.Expr

open Starling.Utils
open Starling.Core.TypeSystem


/// <summary>
///     Expression types.
/// </summary>
[<AutoOpen>]
module Types =
    /// <summary>
    ///     An expression of arbitrary type.
    /// </summary>
    /// <typeparam name="var">
    ///     The type of variables in the expression.
    /// </typeparam>
    
    type Expr<'var> = Typed<IntExpr<'var>, BoolExpr<'var>>

    /// <summary>
    ///     An integral expression.
    /// </summary>
    /// <typeparam name="var">
    ///     The type of variables in the expression.
    /// </typeparam>
    and IntExpr<'var> =
        | AVar of 'var
        | AInt of int64
        | AAdd of IntExpr<'var> list
        | ASub of IntExpr<'var> list
        | AMul of IntExpr<'var> list
        | ADiv of IntExpr<'var> * IntExpr<'var>
        override this.ToString () = sprintf "%A" this

    /// <summary>
    ///     A Boolean expression.
    /// </summary>
    /// <typeparam name="var">
    ///     The type of variables in the expression.
    /// </typeparam>
    and BoolExpr<'var> =
        | BVar of 'var
        | BTrue
        | BFalse
        | BAnd of BoolExpr<'var> list
        | BOr of BoolExpr<'var> list
        | BImplies of BoolExpr<'var> * BoolExpr<'var>
        | BEq of Expr<'var> * Expr<'var>
        | BGt of IntExpr<'var> * IntExpr<'var>
        | BGe of IntExpr<'var> * IntExpr<'var>
        | BLe of IntExpr<'var> * IntExpr<'var>
        | BLt of IntExpr<'var> * IntExpr<'var>
        | BNot of BoolExpr<'var>
        override this.ToString () = sprintf "%A" this

    /// Type for fresh variable generators.
    type FreshGen = bigint ref


/// <summary>
///     Pretty printers for expressions.
///
///     <para>
///         These are deliberately made to look like the Z3 equivalent.
///     </para>
/// </summary>
module Pretty =
    open Starling.Core.Pretty

    let svexpr op pxs x =
      let mapped = Seq.map pxs x
      let sep = ivsep mapped in 
      let head = HSep([(String "("); (String op)], Nop) 
      vsep [head; sep; (String ")")] 

    /// Creates an S-expression from an operator string, operand print function, and
    /// sequence of operands.
    let sexpr op pxs =
        Seq.map pxs
        >> scons (String op)
        >> hsep
        >> parened

    /// Pretty-prints an arithmetic expression.
    let rec printIntExpr pVar =
        function
        | AVar c -> pVar c
        | AInt i -> i |> sprintf "%i" |> String
        | AAdd xs -> sexpr "+" (printIntExpr pVar) xs
        | ASub xs -> sexpr "-" (printIntExpr pVar) xs
        | AMul xs -> sexpr "*" (printIntExpr pVar) xs
        | ADiv (x, y) -> sexpr "/" (printIntExpr pVar) [x; y]

    /// Pretty-prints a Boolean expression.
    and printBoolExpr pVar =
        function
        | BVar c -> pVar c
        | BTrue -> String "true"
        | BFalse -> String "false"
        | BAnd xs -> svexpr "and" (printBoolExpr pVar) xs
        | BOr xs -> svexpr "or" (printBoolExpr pVar) xs
        | BImplies (x, y) -> svexpr "=>" (printBoolExpr pVar) [x; y]
        | BEq (x, y) -> sexpr "=" (printExpr pVar) [x; y]
        | BGt (x, y) -> sexpr ">" (printIntExpr pVar) [x; y]
        | BGe (x, y) -> sexpr ">=" (printIntExpr pVar) [x; y]
        | BLe (x, y) -> sexpr "<=" (printIntExpr pVar) [x; y]
        | BLt (x, y) -> sexpr "<" (printIntExpr pVar) [x; y]
        | BNot x -> sexpr "not" (printBoolExpr pVar) [x]

    /// Pretty-prints an expression.
    and printExpr pVar =
        function
        | Int a -> printIntExpr pVar a
        | Bool b -> printBoolExpr pVar b


/// Partial pattern that matches a Boolean equality on arithmetic expressions.
let (|BAEq|_|) =
    function
    | BEq (Int x, Int y) -> Some (x, y)
    | _ -> None

/// Partial pattern that matches a Boolean equality on Boolean expressions.
let (|BBEq|_|) =
    function
    | BEq (Bool x, Bool y) -> Some (x, y)
    | _ -> None

/// Define when two Boolean expressions are trivially equal 
/// Eg: (= a b)  is equivalent ot (=b a)
let rec eqBoolExpr (e1: BoolExpr<'var>) (e2:BoolExpr<'var>) : bool   = 
  match e1, e2 with 
  | BEq (a1,a2), BEq (b1,b2) -> 
     ((a1=a2 && b1=b2) || (a1=b2 && b1=a2))
  | BNot a, BNot b -> eqBoolExpr a b 
  | _ -> false 

/// Remove duplicate boolean expressions 
/// TODO(@septract) This is stupid, should do it more cleverly 
let rec remExprDup beq (xs: List<BoolExpr<'var>>) : List<BoolExpr<'var>> =   
  match xs with 
  | (x::xs) -> 
      let xs2 = remExprDup beq xs in 
      if (List.exists (beq x) xs) then xs2 else x::xs2
  | x -> x 


/// Recursively simplify a formula
/// Note: this does _not_ simplify variables.
let rec simpRec beq (ax : BoolExpr<'var>) : BoolExpr<'var> =
    match ax with
    | BNot (x) ->
        match simpRec beq x with
        | BTrue      -> BFalse
        | BFalse     -> BTrue
        | BNot x     -> x
        | BGt (x, y) -> BLe (x, y)
        | BGe (x, y) -> BLt (x, y)
        | BLe (x, y) -> BGt (x, y)
        | BLt (x, y) -> BGe (x, y)
        //Following, all come from DeMorgan
        | BAnd xs        -> simpRec beq (BOr (List.map BNot xs))
        | BOr xs         -> simpRec beq (BAnd (List.map BNot xs))
        | BImplies (x,y) -> simpRec beq (BAnd [x; BNot y])
        | y          -> BNot y
    // x = x is always true.
    | BEq (x, y) when x = y -> BTrue
    // As are x >= x, and x <= x.
    | BGe (x, y)
    | BLe (x, y) when x = y -> BTrue
    | BImplies (x, y) ->
        match simpRec beq x, simpRec beq y with
        | BFalse, _
        | _, BTrue      -> BTrue
        | BTrue, y      -> y
        | x, BFalse     -> simpRec beq (BNot x)
        | x, y          -> BImplies(x,y)
    | BOr xs ->
        match foldFastTerm
                (fun s x ->
                  match simpRec beq x with
                  | BTrue  -> None
                  | BFalse -> Some s
                  | BOr ys -> Some (ys @ s)
                  | y      -> Some (y :: s)
                )
                []
                xs with
        | Some xs -> 
           match remExprDup beq xs with 
           | []  -> BFalse
           | [x] -> x
           | xs  -> BOr (List.rev xs)
        | None     -> BTrue
    // An and is always true if everything in it is always true.
    | BAnd xs ->
        match foldFastTerm
                (fun s x ->
                  match simpRec beq x with
                  | BFalse  -> None
                  | BTrue   -> Some s
                  | BAnd ys -> Some (ys @ s)
                  | y       -> Some (y :: s)
                )
                []
                xs with 
        | Some xs -> 
           match remExprDup beq xs with 
           | []  -> BTrue
           | [x] -> x
           | xs  -> BAnd (List.rev xs)
        | None     -> BFalse
    // A Boolean equality between two contradictions or tautologies is always true.
    | BBEq (x, y)  ->
        match simpRec beq x, simpRec beq y with
        | BFalse, BFalse
        | BTrue, BTrue      -> BTrue
        | BTrue, BFalse
        | BFalse, BTrue     -> BFalse
        // A Boolean equality between something and True reduces to that something.
        | x, BTrue          -> x
        | BTrue, x          -> x
        | x, BFalse         -> simpRec beq (BNot x)
        | BFalse, x         -> simpRec beq (BNot x)
        | x, y              -> BEq(Bool x, Bool y)
    | x -> x

let simp (ax : BoolExpr<'var>) : BoolExpr<'var> = 
  simpRec eqBoolExpr ax

/// Returns true if the expression is definitely false.
/// This is sound, but not complete.
let isFalse expr =
    match (simp expr) with
    | BFalse -> true
    | _      -> false

let isTrue expr =
    match (simp expr) with
    | BTrue -> true
    | _     -> false

/// Converts a typed string to an expression.
let mkVarExp marker (ts : CTyped<string>) =
    match ts with
    | Int s -> s |> marker |> AVar |> Int
    | Bool s -> s |> marker |> BVar |> Bool

/// Converts a VarMap to a sequence of expressions.
let varMapToExprs marker vm =
    vm |> Map.toSeq |> Seq.map (fun (name, ty) -> mkVarExp marker (withType ty name))

(* The following are just curried versions of the usual constructors. *)

/// Curried wrapper over BGt.
let mkGt a b = BGt (a, b)
/// Curried wrapper over BGe.
let mkGe a b = BGe (a, b)
/// Curried wrapper over BLt.
let mkLt a b = BLt (a, b)
/// Curried wrapper over BLe.
let mkLe a b = BLe (a, b)

/// Curried wrapper over BEq.
let mkEq a b = BEq (a, b)

/// Makes an arithmetic equality.
let iEq a b = BEq (Int a, Int b)

/// Makes a Boolean equality.
let bEq a b = BEq (Bool a, Bool b)

/// Curried wrapper over ADiv.
let mkDiv a b = ADiv (a, b)

/// Slightly optimised version of ctx.MkAnd.
/// Returns true for the empty array, and x for the singleton set {x}.
let mkAnd xs = simp (BAnd xs)

/// Slightly optimised version of ctx.MkOr.
/// Returns false for the empty set, and x for the singleton set {x}.
let mkOr xs = simp (BOr xs)

/// Makes an And from a pair of two expressions.
let mkAnd2 l r = mkAnd [l ; r]

/// Makes an Or from a pair of two expressions.
let mkOr2 l r = mkOr [l ; r]

/// Symbolically inverts a Boolean expression.
let mkNot x = simp (BNot x)

/// Makes not-equals.
let mkNeq l r = mkEq l r |> mkNot

/// Makes an implication from a pair of two expressions.
let mkImplies l r = BImplies (l, r) |> simp

/// Makes an Add out of a pair of two expressions.
let mkAdd2 l r = AAdd [ l; r ]
/// Makes a Sub out of a pair of two expressions.
let mkSub2 l r = ASub [ l; r ]
/// Makes a Mul out of a pair of two expressions.
let mkMul2 l r = AMul [ l; r ]


(*
 * Fresh variable generation
 *)

/// Creates a new fresh generator.
let freshGen () = ref 0I

/// Takes a fresh number out of the generator.
/// This method is NOT thread-safe.
let getFresh fg =
    let result = !fg
    fg := !fg + 1I
    result

(*
 * Active patterns
 *)

/// Categorises integral expressions into simple or compound.
let (|SimpleInt|CompoundInt|) =
    function
    | AVar _ | AInt _ -> SimpleInt
    | _ -> CompoundInt

/// Categorises Boolean expressions into simple or compound.
let (|SimpleBool|CompoundBool|) =
    function
    | BVar _ | BTrue | BFalse -> SimpleBool
    | _ -> CompoundBool

/// Categorises expressions into simple or compound.
let (|SimpleExpr|CompoundExpr|) =
    function
    | Bool (SimpleBool) -> SimpleExpr
    | Int (SimpleInt) -> SimpleExpr
    | _ -> CompoundExpr

/// <summary>
///     Tests for <c>Expr</c>.
/// </summary>
module Tests =
    open NUnit.Framework

    /// <summary>
    ///     NUnit tests for <c>Expr</c>.
    /// </summary>
    type NUnit () =
        /// Test cases for testing simple/compound arithmetic classification.
        static member IntSimpleCompound =
            [ TestCaseData(AInt 1L)
                .Returns(false)
                .SetName("Classify '1' as simple")
              TestCaseData(AAdd [AInt 1L; AInt 2L])
                .Returns(true)
                .SetName("Classify '1+2' as compound")
              TestCaseData(ASub [AAdd [AInt 1L; AInt 2L]; AInt 3L])
                .Returns(true)
                .SetName("Classify '(1+2)-3' as compound")
              TestCaseData(AVar "foo")
                .Returns(false)
                .SetName("Classify 'foo' as simple")
              TestCaseData(AMul [AVar "foo"; AVar "bar"])
                .Returns(true)
                .SetName("Classify 'foo * bar' as compound") ]

        /// Tests whether the simple/compound arithmetic patterns work correctly
        [<TestCaseSource("IntSimpleCompound")>]
        member x.``SimpleInt and CompoundInt classify properly`` e =
            match e with
            | SimpleInt -> false
            | CompoundInt -> true
