/// Test module for the pretty printer module.
module Starling.Tests.Pretty

open NUnit.Framework
open Starling
open Starling.Core.Var
open Starling.Lang.AST
open Starling.Lang.AST.Pretty

/// Tests for the pretty printer.
type PrettyTests() = 
    
    /// Test cases for printExpression.
    static member Exprs = 
        [ TestCaseData(Int 5L).Returns("5")
          TestCaseData(Bop(Div, Int 6L, LV(LVIdent "bar"))).Returns("(6 / bar)")
          
          TestCaseData(Bop(Mul, Bop(Add, Int 1L, Int 2L), Int 3L)).Returns("((1 + 2) * 3)") ]
        |> List.map (fun d -> d.SetName(sprintf "Print expression %A" d.ExpectedResult))
    
    [<TestCaseSource("Exprs")>]
    /// Tests whether printExpression behaves itself.
    member x.``printExpression correctly prints expressions`` expr = 
        expr
        |> printExpression
        |> Core.Pretty.print
