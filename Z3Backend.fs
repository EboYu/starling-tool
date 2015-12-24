﻿/// The Z3 backend driver.
module Starling.Z3.Backend

open Chessie.ErrorHandling
open Starling

(*
 * Request and response types
 *)

/// Type of requests to the Z3 backend.
type Request = 
    /// Only translate the term views; return `Output.Translate`.
    | Translate
    /// Translate and combine term Z3 expressions; return `Output.Combine`.
    | Combine
    /// Translate, combine, and run term Z3 expressions; return `Output.Sat`.
    | Sat

/// Type of responses from the Starling frontend.
[<NoComparison>]
type Response =
    /// Output of the term translation step only.
    | Translate of Starling.Model.ZTerm list
    /// Output of the final Z3 terms only.
    | Combine of Microsoft.Z3.BoolExpr list
    /// Output of satisfiability reports for the Z3 terms.
    | Sat of Microsoft.Z3.Status list

(*
 * Error types
 *)

/// Type of errors generated by the Starling frontend.
type Error = 
    /// A parse error occurred, details of which are enclosed in string form.
    | Parse of string
    /// A modeller error occurred, given as a `ModelError`.
    | Model of Errors.Lang.Modeller.ModelError

(*
 * Pretty-printing
 *)

/// Pretty-prints a response.
let printResponse = 
    function 
    | Response.Translate t -> Starling.Pretty.Misc.printZTerms t
    | Response.Combine z -> Starling.Pretty.Misc.printZ3Exps z
    | Response.Sat s -> Starling.Pretty.Misc.printSats s

(*
 * Driver functions
 *)

/// Shorthand for the parser stage of the frontend pipeline.
let translate = Translator.reifyZ3
/// Shorthand for the collation stage of the frontend pipeline.
let combine = Translator.combineTerms
/// Shorthand for the modelling stage of the frontend pipeline.
let sat m = Run.run m

/// Runs the Starling Z3 backend.
/// Takes three arguments: the first is the Z3 model; the second is the
/// `Response` telling the backend what to output; and the third is the list of
/// reified terms to process with Z3.
let run model =
    function
    | Request.Translate -> translate model >> Response.Translate
    | Request.Combine -> translate model >> combine model >> Response.Combine
    | Request.Sat -> translate model >> combine model >> sat model >> Response.Sat