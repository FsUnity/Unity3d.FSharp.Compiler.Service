// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

/// Solves constraints using a mutable constraint-solver state
module internal Microsoft.FSharp.Compiler.ConstraintSolver

open Internal.Utilities
open Internal.Utilities.Collections
open Microsoft.FSharp.Compiler.AbstractIL 
open Microsoft.FSharp.Compiler.AbstractIL.Internal 
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library 
open Microsoft.FSharp.Compiler 
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.ErrorLogger
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.Import
open Microsoft.FSharp.Compiler.Tastops
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.TcGlobals
open Microsoft.FSharp.Compiler.Infos

/// Create a type variable representing the use of a "_" in F# code
val NewAnonTypar : TyparKind * range * TyparRigidity * TyparStaticReq * TyparDynamicReq -> Typar

/// Create an inference type variable 
val NewInferenceType : unit -> TType

/// Create an inference type variable representing an error condition when checking an expression
val NewErrorType : unit -> TType

/// Create an inference type variable representing an error condition when checking a measure
val NewErrorMeasure : unit -> MeasureExpr

/// Create a list of inference type variables, one for each element in the input list
val NewInferenceTypes : 'a list -> TType list

/// Given a set of formal type parameters and their constraints, make new inference type variables for
/// each and ensure that the constraints on the new type variables are adjusted to refer to these.
val FreshenAndFixupTypars : range -> TyparRigidity -> Typars -> TType list -> Typars -> Typars * TyparInst * TType list

val FreshenTypeInst : range -> Typars -> Typars * TyparInst * TType list

val FreshenTypars : range -> Typars -> TType list

val FreshenMethInfo : range -> MethInfo -> TType list

exception ConstraintSolverTupleDiffLengths              of DisplayEnv * TType list * TType list * range * range
exception ConstraintSolverInfiniteTypes                 of DisplayEnv * TType * TType * range * range
exception ConstraintSolverTypesNotInEqualityRelation    of DisplayEnv * TType * TType * range * range
exception ConstraintSolverTypesNotInSubsumptionRelation of DisplayEnv * TType * TType * range * range
exception ConstraintSolverMissingConstraint             of DisplayEnv * Typar * TyparConstraint * range * range
exception ConstraintSolverError                         of string * range * range
exception ConstraintSolverRelatedInformation            of string option * range * exn
exception ErrorFromApplyingDefault                      of TcGlobals * DisplayEnv * Typar * TType * exn * range
exception ErrorFromAddingTypeEquation                   of TcGlobals * DisplayEnv * TType * TType * exn * range
exception ErrorsFromAddingSubsumptionConstraint         of TcGlobals * DisplayEnv * TType * TType * exn * range
exception ErrorFromAddingConstraint                     of DisplayEnv * exn * range
exception UnresolvedConversionOperator                  of DisplayEnv * TType * TType * range
exception PossibleOverload                              of DisplayEnv * string * exn * range
exception UnresolvedOverloading                         of DisplayEnv * exn list * string * range
exception NonRigidTypar                                 of DisplayEnv * string option * range * TType * TType * range

/// A function that denotes captured tcVal, Used in constraint solver and elsewhere to get appropriate expressions for a ValRef.
type TcValF = (ValRef -> ValUseFlag -> TType list -> range -> Expr * TType)

[<Sealed>]
type ConstraintSolverState =
    static member New: TcGlobals * Import.ImportMap * InfoReader * TcValF -> ConstraintSolverState

type ConstraintSolverEnv 

val BakedInTraitConstraintNames : string list

val MakeConstraintSolverEnv : ConstraintSolverState -> range -> DisplayEnv -> ConstraintSolverEnv

type Trace = Trace of (unit -> unit) list ref

type OptionalTrace =
  | NoTrace
  | WithTrace of Trace

val SimplifyMeasuresInTypeScheme             : TcGlobals -> bool -> Typars -> TType -> TyparConstraint list -> Typars
val SolveTyparEqualsTyp                      : ConstraintSolverEnv -> int -> range -> OptionalTrace -> TType -> TType -> OperationResult<unit>
val SolveTypEqualsTypKeepAbbrevs             : ConstraintSolverEnv -> int -> range -> OptionalTrace -> TType -> TType -> OperationResult<unit>
val CanonicalizeRelevantMemberConstraints    : ConstraintSolverEnv -> int -> OptionalTrace -> Typars -> OperationResult<unit>
val ResolveOverloading                       : ConstraintSolverEnv -> OptionalTrace -> string -> ndeep: int -> bool -> int * int -> AccessorDomain -> TypeRelations.CalledMeth<Expr> list ->  bool -> TType option -> TypeRelations.CalledMeth<Expr> option * OperationResult<unit>
val UnifyUniqueOverloading                   : ConstraintSolverEnv -> int * int -> string -> AccessorDomain -> TypeRelations.CalledMeth<SynExpr> list -> TType -> OperationResult<bool> 
val EliminateConstraintsForGeneralizedTypars : ConstraintSolverEnv -> OptionalTrace -> Typars -> unit 

val CheckDeclaredTypars                       : DisplayEnv -> ConstraintSolverState -> range -> Typars -> Typars -> unit 

val AddConstraint                             : ConstraintSolverEnv -> int -> Range.range -> OptionalTrace -> Typar -> TyparConstraint -> OperationResult<unit>
val AddCxTypeEqualsType                       : DisplayEnv -> ConstraintSolverState -> range -> TType -> TType -> unit
val AddCxTypeEqualsTypeUndoIfFailed           : DisplayEnv -> ConstraintSolverState -> range -> TType -> TType -> bool
val AddCxTypeEqualsTypeMatchingOnlyUndoIfFailed : DisplayEnv -> ConstraintSolverState -> range -> TType -> TType -> bool
val AddCxTypeMustSubsumeType                  : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> TType -> unit
val AddCxTypeMustSubsumeTypeUndoIfFailed      : DisplayEnv -> ConstraintSolverState -> range -> TType -> TType -> bool
val AddCxTypeMustSubsumeTypeMatchingOnlyUndoIfFailed : DisplayEnv -> ConstraintSolverState -> range -> TType -> TType -> bool
val AddCxMethodConstraint                     : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TraitConstraintInfo -> unit
val AddCxTypeMustSupportNull                  : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> unit
val AddCxTypeMustSupportComparison            : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> unit
val AddCxTypeMustSupportEquality              : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> unit
val AddCxTypeMustSupportDefaultCtor           : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> unit
val AddCxTypeIsReferenceType                  : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> unit
val AddCxTypeIsValueType                      : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> unit
val AddCxTypeIsUnmanaged                      : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> unit
val AddCxTypeIsEnum                           : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> TType -> unit
val AddCxTypeIsDelegate                       : DisplayEnv -> ConstraintSolverState -> range -> OptionalTrace -> TType -> TType -> TType -> unit

val CodegenWitnessThatTypSupportsTraitConstraint : TcValF -> TcGlobals -> ImportMap -> range -> TraitConstraintInfo -> Expr list -> OperationResult<Expr option>

val ChooseTyparSolutionAndSolve : ConstraintSolverState -> DisplayEnv -> Typar -> unit

val IsApplicableMethApprox : TcGlobals -> ImportMap -> range -> MethInfo -> TType -> bool
