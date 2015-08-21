// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

/// Implements a set of checks on the TAST for a file that can only be performed after type inference
/// is complete.
module internal Microsoft.FSharp.Compiler.PostTypeCheckSemanticChecks

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.TcGlobals

val testFlagMemberBody : bool ref
val CheckTopImpl : TcGlobals * Import.ImportMap * bool * Infos.InfoReader * Tast.CompilationPath list * Tast.CcuThunk * Tastops.DisplayEnv * Tast.ModuleOrNamespaceExprWithSig * Tast.Attribs * bool -> bool
