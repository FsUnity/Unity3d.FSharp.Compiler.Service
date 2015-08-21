// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

module Microsoft.FSharp.Compiler.Interactive.Shell

#nowarn "55"

[<assembly: System.Runtime.InteropServices.ComVisible(false)>]
[<assembly: System.CLSCompliant(true)>]  
do()

open Internal.Utilities

module Tc = Microsoft.FSharp.Compiler.TypeChecker

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.Runtime.InteropServices
open System.Runtime.CompilerServices
open System.IO
open System.Text
open System.Threading
open System.Reflection

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.AbstractIL
open Microsoft.FSharp.Compiler.AbstractIL.IL
open Microsoft.FSharp.Compiler.AbstractIL.Internal
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library
open Microsoft.FSharp.Compiler.AbstractIL.Extensions.ILX
open Microsoft.FSharp.Compiler.AbstractIL.ILRuntimeWriter 
open Microsoft.FSharp.Compiler.Lib
open Microsoft.FSharp.Compiler.CompileOptions
open Microsoft.FSharp.Compiler.AbstractIL.Diagnostics
open Microsoft.FSharp.Compiler.AbstractIL.IL
open Microsoft.FSharp.Compiler.IlxGen
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.ErrorLogger
open Microsoft.FSharp.Compiler.TypeChecker
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.Infos
open Microsoft.FSharp.Compiler.Tastops
open Microsoft.FSharp.Compiler.Optimizer
open Microsoft.FSharp.Compiler.TcGlobals
open Microsoft.FSharp.Compiler.CompileOps
open Microsoft.FSharp.Compiler.Lexhelp
open Microsoft.FSharp.Compiler.Layout
open Microsoft.FSharp.Compiler.NameResolution
open Microsoft.FSharp.Compiler.PostTypeCheckSemanticChecks
open Microsoft.FSharp.Compiler.SourceCodeServices

open Internal.Utilities.Collections
open Internal.Utilities.StructuredFormat

//----------------------------------------------------------------------------
// For the FSI as a service methods...
//----------------------------------------------------------------------------

type FsiValue(reflectionValue:obj, reflectionType:Type, fsharpType:FSharpType) = 
  member x.ReflectionValue = reflectionValue
  member x.ReflectionType = reflectionType
  member x.FSharpType = fsharpType

//----------------------------------------------------------------------------
// Hardbinding dependencies should we NGEN fsi.exe
//----------------------------------------------------------------------------

open System.Runtime.CompilerServices
[<Dependency("FSharp.Compiler",LoadHint.Always)>] do ()
[<Dependency("FSharp.Core",LoadHint.Always)>] do ()

[<AutoOpen>]
// Extension methods to enable functionality on .Net 3.5
module internal Extensions =
    open System.Runtime.InteropServices
    open Microsoft.FSharp.NativeInterop

    #if WIN32
    [<DllImport("KERNEL32.dll",SetLastError=true)  >]
    extern bool DoesWin32MethodExist(string moduleName, string methodName)

    [<DllImport("KERNEL32.dll",SetLastError=true, CallingConvention=CallingConvention.Winapi)  >]
    extern [<MarshalAs(UnmanagedType.Bool)>]
        bool IsWow64Process([<In>] IntPtr hSourceProcessHandle   ,
                            [<Out;MarshalAs(UnmanagedType.Bool)>] bool   isWow64)

    [<DllImport("KERNEL32.dll", CharSet=CharSet.Auto, SetLastError=true)>]
    extern IntPtr GetCurrentProcess() 
    #endif

    type Environment with
        static member Is64BitProcess 
            with get() =
                #if WIN32
                    false
                #else 
                    true
                #endif
        static member Is64BitOperatingSystem
            with get()  =
                #if WIN32
                //let is64bitProcess = IntPtr.Size = 8
                let mutable isWow64 = false                
                if DoesWin32MethodExist("Win32Native.KERNEL32","IsWow64Process") then
                    IsWow64Process(GetCurrentProcess(),isWow64)
                else
                    false
                #else
                    true
                #endif
                    
                
                    
                
                   
//                Microsoft.Win32.Registry.LocalMachine.
//                    Win32Native.DoesWin32MethodExist(Win32Native.KERNEL32, "IsWow64Process")
//                    Win32Native.IsWow64Process (Win32Native.GetCurrentProcess(), out isWow64)
//                

//public static bool Is64BitOperatingSystem {
//            [System.Security.SecuritySafeCritical]
//            get {
//                #if WIN32                    
//                    bool isWow64; // WinXP SP2+ and Win2k3 SP1+
//                    return Win32Native.DoesWin32MethodExist(Win32Native.KERNEL32, "IsWow64Process")
//                        && Win32Native.IsWow64Process(Win32Native.GetCurrentProcess(), out isWow64)
//                        && isWow64;
//                #else
//                    // 64-bit programs run only on 64-bit
//                    //<STRIP>This will have to change for Mac if we add this API to Silverlight</STRIP>
//                    return true;
//                #endif
//            }
//        }




module internal Utilities = 
    type IAnyToLayoutCall = 
        abstract AnyToLayout : FormatOptions * obj -> Internal.Utilities.StructuredFormat.Layout
        abstract FsiAnyToLayout : FormatOptions * obj -> Internal.Utilities.StructuredFormat.Layout

    type private AnyToLayoutSpecialization<'T>() = 
        interface IAnyToLayoutCall with
            member this.AnyToLayout(options, o : obj) = Internal.Utilities.StructuredFormat.Display.any_to_layout options (Unchecked.unbox o : 'T)
            member this.FsiAnyToLayout(options, o : obj) = Internal.Utilities.StructuredFormat.Display.fsi_any_to_layout options (Unchecked.unbox o : 'T)
    
    let getAnyToLayoutCall ty = 
        let specialized = typedefof<AnyToLayoutSpecialization<_>>.MakeGenericType [| ty |]
        Activator.CreateInstance(specialized) :?> IAnyToLayoutCall

    let callStaticMethod (ty:Type) name args =
        ty.InvokeMember(name, (BindingFlags.InvokeMethod ||| BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic), null, null, Array.ofList args,Globalization.CultureInfo.InvariantCulture)

    let ignoreAllErrors f = try f() with _ -> ()



let referencedAssemblies = Dictionary<string, DateTime>()

//----------------------------------------------------------------------------
// Timing support
//----------------------------------------------------------------------------

[<AutoSerializable(false)>]
type internal FsiTimeReporter(outWriter: TextWriter) =
    let stopwatch = new System.Diagnostics.Stopwatch()
    let ptime = System.Diagnostics.Process.GetCurrentProcess()
    let numGC = System.GC.MaxGeneration
    member tr.TimeOp(f) =
        let startTotal = ptime.TotalProcessorTime
        let startGC = [| for i in 0 .. numGC -> System.GC.CollectionCount(i) |]
        stopwatch.Reset()
        stopwatch.Start()
        let res = f ()
        stopwatch.Stop()
        let total = ptime.TotalProcessorTime - startTotal
        let spanGC = [ for i in 0 .. numGC-> System.GC.CollectionCount(i) - startGC.[i] ]
        let elapsed = stopwatch.Elapsed 
        fprintfn outWriter "%s" (FSIstrings.SR.fsiTimeInfoMainString((sprintf "%02d:%02d:%02d.%03d" (int elapsed.TotalHours) elapsed.Minutes elapsed.Seconds elapsed.Milliseconds),(sprintf "%02d:%02d:%02d.%03d" (int total.TotalHours) total.Minutes total.Seconds total.Milliseconds),(String.concat ", " (List.mapi (sprintf "%s%d: %d" (FSIstrings.SR.fsiTimeInfoGCGenerationLabelSomeShorthandForTheWordGeneration())) spanGC))))
        res

    member tr.TimeOpIf flag f = if flag then tr.TimeOp f else f ()


type internal FsiValuePrinterMode = 
    | PrintExpr 
    | PrintDecl

type EvaluationEventArgs(fsivalue : FsiValue option, symbolUse : FSharpSymbolUse, decl: FSharpImplementationFileDeclaration) =
    inherit EventArgs()
    member x.Name = symbolUse.Symbol.DisplayName
    member x.FsiValue = fsivalue
    member x.SymbolUse = symbolUse
    member x.Symbol = symbolUse.Symbol
    member x.ImplementationDeclaration = decl

[<AbstractClass>]
type public FsiEvaluationSessionHostConfig () = 
    let evaluationEvent = new Event<EvaluationEventArgs> () 
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract FormatProvider: System.IFormatProvider  
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract FloatingPointFormat: string 
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract AddedPrinters : Choice<(System.Type * (obj -> string)), (System.Type * (obj -> obj))>  list
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract ShowDeclarationValues: bool  
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract ShowIEnumerable: bool  
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract ShowProperties : bool  
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract PrintSize : int  
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract PrintDepth : int  
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract PrintWidth : int
    /// Called by the evaluation session to ask the host for parameters to format text for output
    abstract PrintLength : int

    /// The evaluation session calls this to report the preferred view of the command line arguments after 
    /// stripping things like "/use:file.fsx", "-r:Foo.dll" etc.
    abstract ReportUserCommandLineArgs : string [] -> unit


    /// The evaluation session calls this to ask the host for the special console reader. 
    /// Returning 'Some' indicates a console is to be used, so some special rules apply.
    ///
    /// A "console" gets used if 
    ///     --readline- is specified (the default on Windows + .NET); and 
    ///     not --fsi-server (which should always be combined with --readline-); and 
    ///     OptionalConsoleReadLine() returns a Some
    ///
    /// "Peekahead" occurs if --peekahead- is not specified (i.e. it is the default):
    ///     - If a console is being used then 
    ///         - a prompt is printed early 
    ///         - a background thread is created 
    ///         - the OptionalConsoleReadLine() callback is used to read the first line
    ///     - Otherwise call inReader.Peek()
    ///
    /// Further lines are read as follows:
    ///     - If a console is being used then use OptionalConsoleReadLine()
    ///     - Otherwise use inReader.ReadLine()

    abstract OptionalConsoleReadLine : (unit -> string) option 

    /// The evaluation session calls this at an appropriate point in the startup phase if the --fsi-server parameter was given
    abstract StartServer : fsiServerName:string -> unit
    
    /// Called by the evaluation session to ask the host to enter a dispatch loop like Application.Run().
    /// Only called if --gui option is used (which is the default).
    /// Gets called towards the end of startup and every time a ThreadAbort escaped to the backup driver loop.
    /// Return true if a 'restart' is required, which is a bit meaningless.
    abstract EventLoopRun : unit -> bool

    /// Request that the given operation be run synchronously on the event loop.
    abstract EventLoopInvoke : codeToRun: (unit -> 'T) -> 'T

    /// Schedule a restart for the event loop.
    abstract EventLoopScheduleRestart : unit -> unit

    /// Implicitly reference FSharp.Compiler.Interactive.Settings.dll
    abstract UseFsiAuxLib : bool

    /// Hook for listening for evaluation bindings
    member x.OnEvaluation = evaluationEvent.Publish
    member internal x.TriggerEvaluation (value, symbolUse, decl) =
        evaluationEvent.Trigger (EvaluationEventArgs (value, symbolUse, decl) )

/// Used to print value signatures along with their values, according to the current
/// set of pretty printers installed in the system, and default printing rules.
type internal FsiValuePrinter(fsi: FsiEvaluationSessionHostConfig, ilGlobals, generateDebugInfo, resolvePath, outWriter) = 

    /// This printer is used by F# Interactive if no other printers apply.
    let DefaultPrintingIntercept (ienv: Internal.Utilities.StructuredFormat.IEnvironment) (obj:obj) = 
       match obj with 
       | null -> None 
       | :? System.Collections.IDictionary as ie ->
          let it = ie.GetEnumerator() 
          try 
              let itemLs = 
                  Internal.Utilities.StructuredFormat.LayoutOps.unfoldL // the function to layout each object in the unfold
                          (fun obj -> ienv.GetLayout obj) 
                          // the function to call at each step of the unfold
                          (fun () -> 
                              if it.MoveNext() then 
                                 Some((it.Key, it.Value),()) 
                              else None) () 
                          // the maximum length
                          (1+fsi.PrintLength/3) 
              let makeListL itemLs =
                (leftL "[") ^^
                sepListL (rightL ";") itemLs ^^
                (rightL "]")
              Some(wordL "dict" --- makeListL itemLs)
          finally
             match it with 
             | :? System.IDisposable as d -> d.Dispose()
             | _ -> ()
             
       | _ -> None 


    /// Get the print options used when formatting output using the structured printer.
    member __.GetFsiPrintOptions() = 
        { Internal.Utilities.StructuredFormat.FormatOptions.Default with 
              FormatProvider = fsi.FormatProvider;
              PrintIntercepts = 
                  // The fsi object supports the addition of two kinds of printers, one which converts to a string
                  // and one which converts to another object that is recursively formatted.
                  // The internal AddedPrinters reports these to FSI.EXE and we pick them up here to produce a layout
                  [ for x in fsi.AddedPrinters do 
                         match x with 
                         | Choice1Of2 (aty: System.Type, printer) -> 
                                yield (fun _ienv (obj:obj) ->
                                   match obj with 
                                   | null -> None 
                                   | _ when aty.IsAssignableFrom(obj.GetType())  ->  
                                       match printer obj with 
                                       | null -> None
                                       | s -> Some (wordL s) 
                                   | _ -> None)
                                   
                         | Choice2Of2 (aty: System.Type, converter) -> 
                                yield (fun ienv (obj:obj) ->
                                   match obj with 
                                   | null -> None 
                                   | _ when aty.IsAssignableFrom(obj.GetType())  -> 
                                       match converter obj with 
                                       | null -> None
                                       | res -> Some (ienv.GetLayout res)
                                   | _ -> None)
                    yield DefaultPrintingIntercept];
              FloatingPointFormat = fsi.FloatingPointFormat;
              PrintWidth = fsi.PrintWidth; 
              PrintDepth = fsi.PrintDepth; 
              PrintLength = fsi.PrintLength;
              PrintSize = fsi.PrintSize;
              ShowProperties = fsi.ShowProperties;
              ShowIEnumerable = fsi.ShowIEnumerable; }

    /// Get the evaluation context used when inverting the storage mapping of the ILRuntimeWriter.
    member __.GetEvaluationContext emEnv = 
        let cenv = { ilg = ilGlobals ; generatePdb = generateDebugInfo; resolvePath=resolvePath }
        { LookupFieldRef = ILRuntimeWriter.LookupFieldRef emEnv >> Option.get
          LookupMethodRef = ILRuntimeWriter.LookupMethodRef emEnv >> Option.get
          LookupTypeRef = ILRuntimeWriter.LookupTypeRef cenv emEnv 
          LookupType = ILRuntimeWriter.LookupType cenv emEnv }

    /// Generate a layout for an actual F# value, where we know the value has the given static type.
    member __.PrintValue (printMode, opts:FormatOptions, x:obj, ty:System.Type) = 
        // We do a dynamic invoke of any_to_layout with the right System.Type parameter for the static type of the saved value.
        // In principle this helps any_to_layout do the right thing as it descends through terms. In practice it means
        // it at least does the right thing for top level 'null' list and option values (but not for nested ones).
        //
        // The static type was saved into the location used by RuntimeHelpers.GetSavedItType when RuntimeHelpers.SaveIt was called.
        // RuntimeHelpers.SaveIt has type ('a -> unit), and fetches the System.Type for 'a by using a typeof<'a> call.
        // The funny thing here is that you might think that the driver (this file) knows more about the static types
        // than the compiled code does. But it doesn't! In particular, it's not that easy to get a System.Type value based on the
        // static type information we do have: we have no direct way to bind a F# TAST type or even an AbstractIL type to 
        // a System.Type value (I guess that functionality should be in ilreflect.fs).
        //
        // This will be more significant when we print values other then 'it'
        //
        try 
            let anyToLayoutCall = Utilities.getAnyToLayoutCall ty
            match printMode with
              | PrintDecl ->
                  // When printing rhs of fsi declarations, use "fsi_any_to_layout".
                  // This will suppress some less informative values, by returning an empty layout. [fix 4343].
                  anyToLayoutCall.FsiAnyToLayout(opts, x)
              | PrintExpr -> 
                  anyToLayoutCall.AnyToLayout(opts, x)
        with 
        | :? ThreadAbortException -> Layout.wordL ""
        | e ->
#if DEBUG
          printf "\n\nPrintValue: x = %+A and ty=%s\n" x (ty.FullName)
#endif
          printf "%s" (FSIstrings.SR.fsiExceptionDuringPrettyPrinting(e.ToString())); 
          Layout.wordL ""
            
    /// Display the signature of an F# value declaration, along with its actual value.
    member valuePrinter.InvokeDeclLayout (emEnv, ilxGenerator: IlxAssemblyGenerator, v:Val) =
        // Implemented via a lookup from v to a concrete (System.Object,System.Type).
        // This (obj,objTy) pair can then be fed to the fsi value printer.
        // Note: The value may be (null:Object).
        // Note: A System.Type allows the value printer guide printing of nulls, e.g. as None or [].
        //-------
        // IlxGen knows what the v:Val was converted to w.r.t. AbsIL datastructures.
        // Ilreflect knows what the AbsIL was generated to.
        // Combining these allows for obtaining the (obj,objTy) by reflection where possible.
        // This assumes the v:Val was given appropriate storage, e.g. StaticField.
        if fsi.ShowDeclarationValues then 
            // Adjust "opts" for printing for "declared-values":
            // - No sequences, because they may have effects or time cost.
            // - No properties, since they may have unexpected effects.
            // - Limit strings to roughly one line, since huge strings (e.g. 1 million chars without \n are slow in vfsi).
            // - Limit PrintSize which is a count on nodes.
            let declaredValueReductionFactor = 10 (* reduce PrintSize for declared values, e.g. see less of large terms *)
            let opts   = valuePrinter.GetFsiPrintOptions()
            let opts   = {opts with ShowProperties  = false // properties off, motivated by Form props 
                                    ShowIEnumerable = false // seq off, motivated by db query concerns 
                                    StringLimit = max 0 (opts.PrintWidth-4) // 4 allows for an indent of 2 and 2 quotes (rough) 
                                    PrintSize = opts.PrintSize / declaredValueReductionFactor } // print less 
            let res    = 
                try  ilxGenerator.LookupGeneratedValue (valuePrinter.GetEvaluationContext emEnv, v)
                with e -> 
                    assert false
#if DEBUG
                    //fprintfn fsiConsoleOutput.Out "lookGenerateVal: failed on v=%+A v.Name=%s" v v.LogicalName
#endif
                    None // lookup may fail 
            match res with
              | None             -> None
              | Some (obj,objTy) -> 
                  let lay = valuePrinter.PrintValue (FsiValuePrinterMode.PrintDecl, opts, obj, objTy)
                  if isEmptyL lay then None else Some lay // suppress empty layout 
                                    
        else
            None
    
    /// Fetch the saved value of an expression out of the 'it' register and show it.
    member valuePrinter.InvokeExprPrinter (denv, emEnv, ilxGenerator: IlxAssemblyGenerator, vref) = 
        let opts        = valuePrinter.GetFsiPrintOptions()
        let res    = ilxGenerator.LookupGeneratedValue (valuePrinter.GetEvaluationContext emEnv, vref)
        let rhsL = 
            match res with
                | None             -> None
                | Some (obj,objTy) -> 
                    let lay = valuePrinter.PrintValue (FsiValuePrinterMode.PrintExpr, opts, obj, objTy)
                    if isEmptyL lay then None else Some lay // suppress empty layout 
        let denv = { denv with suppressMutableKeyword = true } // suppress 'mutable' in 'val mutable it = ...'
        let fullL = if isNone rhsL || isEmptyL rhsL.Value then
                      NicePrint.layoutValOrMember denv vref (* the rhs was suppressed by the printer, so no value to print *)
                    else
                      (NicePrint.layoutValOrMember denv vref ++ wordL "=") --- rhsL.Value
        Internal.Utilities.StructuredFormat.Display.output_layout opts outWriter fullL;  
        outWriter.WriteLine()
    


/// Used to make a copy of input in order to include the input when displaying the error text.
type internal FsiStdinSyphon(errorWriter: TextWriter) = 
    let syphonText = new StringBuilder()

    /// Clears the syphon text
    member x.Reset () = 
        syphonText.Clear() |> ignore

    /// Adds a new line to the syphon text
    member x.Add (str:string) = 
        syphonText.Append str |> ignore  

    /// Gets the indicated line in the syphon text
    member x.GetLine filename i =
        if filename <> Lexhelp.stdinMockFilename then 
            "" 
        else
            let text = syphonText.ToString()
            // In Visual Studio, when sending a block of text, it  prefixes  with '# <line> "filename"\n'
            // and postfixes with '# 1 "stdin"\n'. To first, get errors filename context,
            // and second to get them back into stdin context (no position stack...).
            // To find an error line, trim upto the last stdinReset string the syphoned text.
            //printf "PrePrune:-->%s<--\n\n" text;
            let rec prune (text:string) =
                let stdinReset = "# 1 \"stdin\"\n"
                let idx = text.IndexOf(stdinReset,StringComparison.Ordinal)
                if idx <> -1 then
                    prune (text.Substring(idx + stdinReset.Length))
                else
                    text
           
            let text = prune text
            let lines = text.Split '\n'
            if 0 < i && i <= lines.Length then lines.[i-1] else ""

    /// Display the given error.
    member syphon.PrintError (tcConfig:TcConfigBuilder, isWarn, err) = 
        Utilities.ignoreAllErrors (fun () -> 
            DoWithErrorColor isWarn  (fun () ->
                errorWriter.WriteLine();
                writeViaBufferWithEnvironmentNewLines errorWriter (OutputErrorOrWarningContext "  " syphon.GetLine) err; 
                writeViaBufferWithEnvironmentNewLines errorWriter (OutputErrorOrWarning (tcConfig.implicitIncludeDir,tcConfig.showFullPaths,tcConfig.flatErrors,tcConfig.errorStyle,false))  err;
                errorWriter.WriteLine()
                errorWriter.Flush()))


   
/// Encapsulates functions used to write to outWriter and errorWriter
type internal FsiConsoleOutput(tcConfigB, outWriter:TextWriter, errorWriter:TextWriter) = 

    let nullOut = new StreamWriter(Stream.Null) :> TextWriter
    let fprintfnn (os: TextWriter) fmt  = Printf.kfprintf (fun _ -> os.WriteLine(); os.WriteLine()) os fmt   
    /// uprintf to write usual responses to stdout (suppressed by --quiet), with various pre/post newlines
    member out.uprintf    fmt = fprintf   (if tcConfigB.noFeedback then nullOut else outWriter) fmt 
    member out.uprintfn   fmt = fprintfn  (if tcConfigB.noFeedback then nullOut else outWriter) fmt
    member out.uprintfnn  fmt = fprintfnn (if tcConfigB.noFeedback then nullOut else outWriter) fmt
    member out.uprintnf   fmt = out.uprintfn ""; out.uprintf   fmt
    member out.uprintnfn  fmt = out.uprintfn ""; out.uprintfn  fmt
    member out.uprintnfnn fmt = out.uprintfn ""; out.uprintfnn fmt
      
    member out.Out = outWriter
    member out.Error = errorWriter


/// This ErrorLogger reports all warnings, but raises StopProcessing on first error or early exit
type internal ErrorLoggerThatStopsOnFirstError(tcConfigB:TcConfigBuilder, fsiStdinSyphon:FsiStdinSyphon, fsiConsoleOutput: FsiConsoleOutput) = 
    inherit ErrorLogger("ErrorLoggerThatStopsOnFirstError")
    let mutable errors = 0 
    member x.SetError() = 
        errors <- 1
    member x.ErrorSinkHelper(err) = 
        fsiStdinSyphon.PrintError(tcConfigB,false,err)
        errors <- errors + 1
        if tcConfigB.abortOnError then exit 1 (* non-zero exit code *)
        // STOP ON FIRST ERROR (AVOIDS PARSER ERROR RECOVERY)
        raise (StopProcessing (sprintf "%A" err))
    
    member x.CheckForErrors() = (errors > 0)
    member x.ResetErrorCount() = (errors <- 0)
    
    override x.WarnSinkImpl(err) = 
        DoWithErrorColor true (fun () -> 
            if ReportWarningAsError (tcConfigB.globalWarnLevel, tcConfigB.specificWarnOff, tcConfigB.specificWarnOn, tcConfigB.specificWarnAsError, tcConfigB.specificWarnAsWarn, tcConfigB.globalWarnAsError) err then 
                x.ErrorSinkHelper err 
            elif ReportWarning (tcConfigB.globalWarnLevel, tcConfigB.specificWarnOff, tcConfigB.specificWarnOn) err then 
                fsiConsoleOutput.Error.WriteLine()
                writeViaBufferWithEnvironmentNewLines fsiConsoleOutput.Error (OutputErrorOrWarningContext "  " fsiStdinSyphon.GetLine) err
                writeViaBufferWithEnvironmentNewLines fsiConsoleOutput.Error (OutputErrorOrWarning (tcConfigB.implicitIncludeDir,tcConfigB.showFullPaths,tcConfigB.flatErrors,tcConfigB.errorStyle,true)) err
                fsiConsoleOutput.Error.WriteLine())

    override x.ErrorSinkImpl err = x.ErrorSinkHelper err
    override x.ErrorCount = errors

    /// A helper function to check if its time to abort
    member x.AbortOnError() = 
        if errors > 0 then 
            fprintf fsiConsoleOutput.Error "%s" (FSIstrings.SR.stoppedDueToError())
            fsiConsoleOutput.Error.Flush()
            raise (StopProcessing "")

/// Get the directory name from a string, with some defaults if it doesn't have one
let internal directoryName (s:string) = 
    if s = "" then "."
    else 
        match Path.GetDirectoryName s with 
        | null -> if FileSystem.IsPathRootedShim s then s else "."
        | res -> if res = "" then "." else res




//----------------------------------------------------------------------------
// cmd line - state for options
//----------------------------------------------------------------------------

/// Process the command line options 
type internal FsiCommandLineOptions(fsi: FsiEvaluationSessionHostConfig, argv: string[], tcConfigB, fsiConsoleOutput: FsiConsoleOutput) = 
    let mutable enableConsoleKeyProcessing = 
       // Mono on Win32 doesn't implement correct console processing
       not (runningOnMono && System.Environment.OSVersion.Platform = System.PlatformID.Win32NT) 
// In the cross-platform edition of F#, 'gui' support is currently off by default
#if CROSS_PLATFORM_COMPILER
    let mutable gui        = false // override via "--gui", off by default
#else
    let mutable gui        = true // override via "--gui", on by default
#endif
#if DEBUG
    let mutable showILCode = false // show modul il code 
#endif
    let mutable showTypes  = true  // show types after each interaction?
    let mutable fsiServerName = ""
    let mutable interact = true
    let mutable explicitArgs = []

    let mutable inputFilesAcc   = []  

    let mutable fsiServerInputCodePage = None
    let mutable fsiServerOutputCodePage = None
    let mutable fsiLCID = None

    // internal options  
    let mutable peekAheadOnConsoleToPermitTyping = true   

    let isInteractiveServer() = fsiServerName <> ""  
    let recordExplicitArg arg = explicitArgs <- explicitArgs @ [arg]

    let executableFileName = 
        lazy 
            match tcConfigB.exename with
            | Some s -> s
            | None -> 
            let currentProcess = System.Diagnostics.Process.GetCurrentProcess()
            Path.GetFileName(currentProcess.MainModule.FileName)


    // Additional fsi options are list below.
    // In the "--help", these options can be printed either before (fsiUsagePrefix) or after (fsiUsageSuffix) the core options.

    let displayHelpFsi tcConfigB (blocks:CompilerOptionBlock list) =
        DisplayBannerText tcConfigB;
        fprintfn fsiConsoleOutput.Out ""
        fprintfn fsiConsoleOutput.Out "%s" (FSIstrings.SR.fsiUsage(executableFileName.Value))
        PrintCompilerOptionBlocks blocks
        exit 0

    // option tags
    let tagFile        = "<file>"
    let tagNone        = ""
  
    /// These options preceed the FsiCoreCompilerOptions in the help blocks
    let fsiUsagePrefix tcConfigB =
      [PublicOptions(FSIstrings.SR.fsiInputFiles(),
        [CompilerOption("use",tagFile, OptionString (fun s -> inputFilesAcc <- inputFilesAcc @ [(s,true)]), None,
                                 Some (FSIstrings.SR.fsiUse()));
         CompilerOption("load",tagFile, OptionString (fun s -> inputFilesAcc <- inputFilesAcc @ [(s,false)]), None,
                                 Some (FSIstrings.SR.fsiLoad()));
        ]);
       PublicOptions(FSIstrings.SR.fsiCodeGeneration(),[]);
       PublicOptions(FSIstrings.SR.fsiErrorsAndWarnings(),[]);
       PublicOptions(FSIstrings.SR.fsiLanguage(),[]);
       PublicOptions(FSIstrings.SR.fsiMiscellaneous(),[]);
       PublicOptions(FSIstrings.SR.fsiAdvanced(),[]);
       PrivateOptions(
        [// Make internal fsi-server* options. Do not print in the help. They are used by VFSI. 
         CompilerOption("fsi-server","", OptionString (fun s -> fsiServerName <- s), None, None); // "FSI server mode on given named channel");
         CompilerOption("fsi-server-input-codepage","",OptionInt (fun n -> fsiServerInputCodePage <- Some(n)), None, None); // " Set the input codepage for the console"); 
         CompilerOption("fsi-server-output-codepage","",OptionInt (fun n -> fsiServerOutputCodePage <- Some(n)), None, None); // " Set the output codepage for the console"); 
         CompilerOption("fsi-server-no-unicode","", OptionUnit (fun () -> fsiServerOutputCodePage <- None;  fsiServerInputCodePage <- None), None, None); // "Do not set the codepages for the console");
         CompilerOption("fsi-server-lcid","", OptionInt (fun n -> fsiLCID <- Some(n)), None, None); // "LCID from Visual Studio"

         // We do not want to print the "script.fsx arg2..." as part of the options 
         CompilerOption("script.fsx arg1 arg2 ...","",
                                 OptionGeneral((fun args -> args.Length > 0 && IsScript args.[0]),
                                               (fun args -> let scriptFile = args.[0]
                                                            let scriptArgs = List.tail args
                                                            inputFilesAcc <- inputFilesAcc @ [(scriptFile,true)]   (* record script.fsx for evaluation *)
                                                            List.iter recordExplicitArg scriptArgs            (* record rest of line as explicit arguments *)
                                                            tcConfigB.noFeedback <- true                      (* "quiet", no banners responses etc *)
                                                            interact <- false                                 (* --exec, exit after eval *)
                                                            [] (* no arguments passed on, all consumed here *)

                                               )),None,None); // "Run script.fsx with the follow command line arguments: arg1 arg2 ...");
        ]);
       PrivateOptions(
        [
         // Private options, related to diagnostics around console probing 
         CompilerOption("peekahead","", OptionSwitch (fun flag -> peekAheadOnConsoleToPermitTyping <- flag=OptionSwitch.On), None, None); // "Probe to see if Console looks functional");

         // Disables interaction (to be used by libraries embedding FSI only!)
         CompilerOption("noninteractive","", OptionUnit (fun () -> interact <-  false), None, None);     

        ])
      ]

    /// These options follow the FsiCoreCompilerOptions in the help blocks
    let fsiUsageSuffix tcConfigB =
      [PublicOptions(FSComp.SR.optsHelpBannerInputFiles(),
        [CompilerOption("--","", OptionRest recordExplicitArg, None,
                                 Some (FSIstrings.SR.fsiRemaining()));
        ]);
       PublicOptions(FSComp.SR.optsHelpBannerMisc(),    
        [   CompilerOption("help", tagNone,                      
                                 OptionHelp (fun blocks -> displayHelpFsi tcConfigB blocks),None,
                                 Some (FSIstrings.SR.fsiHelp()))
        ]);
       PrivateOptions(
        [   CompilerOption("?"        , tagNone, OptionHelp (fun blocks -> displayHelpFsi tcConfigB blocks), None, None); // "Short form of --help");
            CompilerOption("help"     , tagNone, OptionHelp (fun blocks -> displayHelpFsi tcConfigB blocks), None, None); // "Short form of --help");
            CompilerOption("full-help", tagNone, OptionHelp (fun blocks -> displayHelpFsi tcConfigB blocks), None, None); // "Short form of --help");
        ]);
       PublicOptions(FSComp.SR.optsHelpBannerAdvanced(),
        [CompilerOption("exec",                 "", OptionUnit (fun () -> interact <- false), None, Some (FSIstrings.SR.fsiExec()));
         CompilerOption("gui",                  tagNone, OptionSwitch(fun flag -> gui <- (flag = OptionSwitch.On)),None,Some (FSIstrings.SR.fsiGui()));
         CompilerOption("quiet",                "", OptionUnit (fun () -> tcConfigB.noFeedback <- true), None,Some (FSIstrings.SR.fsiQuiet()));     
         (* Renamed --readline and --no-readline to --tabcompletion:+|- *)
         CompilerOption("readline",             tagNone, OptionSwitch(fun flag -> enableConsoleKeyProcessing <- (flag = OptionSwitch.On)),           None, Some(FSIstrings.SR.fsiReadline()));
         CompilerOption("quotations-debug",     tagNone, OptionSwitch(fun switch -> tcConfigB.emitDebugInfoInQuotations <- switch = OptionSwitch.On),None, Some(FSIstrings.SR.fsiEmitDebugInfoInQuotations()));
         CompilerOption("shadowcopyreferences", tagNone, OptionSwitch(fun flag -> tcConfigB.shadowCopyReferences <- flag = OptionSwitch.On),         None, Some(FSIstrings.SR.shadowCopyReferences()));
        ]);
      ]


    /// Process command line, flags and collect filenames.
    /// The ParseCompilerOptions function calls imperative function to process "real" args 
    /// Rather than start processing, just collect names, then process them. 
    let sourceFiles = 
        let collect name = 
            let fsx = CompileOps.IsScript name
            inputFilesAcc <- inputFilesAcc @ [(name,fsx)] // O(n^2), but n small...
        try 
           let fsiCompilerOptions = fsiUsagePrefix tcConfigB @ GetCoreFsiCompilerOptions tcConfigB @ fsiUsageSuffix tcConfigB
           let abbrevArgs = GetAbbrevFlagSet tcConfigB false
           ParseCompilerOptions (collect, fsiCompilerOptions, List.tail (PostProcessCompilerArgs abbrevArgs argv))
        with e ->
            stopProcessingRecovery e range0; failwithf "Error creating evaluation session: %A" e
        inputFilesAcc

#if LIMITED_CONSOLE
#else
    do 
        if tcConfigB.utf8output then
            let prev = Console.OutputEncoding
            Console.OutputEncoding <- System.Text.Encoding.UTF8
            System.AppDomain.CurrentDomain.ProcessExit.Add(fun _ -> Console.OutputEncoding <- prev)
#endif

    do 
        let firstArg = 
            match sourceFiles with 
            | [] -> argv.[0] 
            | _  -> fst (List.head (List.rev sourceFiles) )
        let args = Array.ofList (firstArg :: explicitArgs) 
        fsi.ReportUserCommandLineArgs args


    //----------------------------------------------------------------------------
    // Banner
    //----------------------------------------------------------------------------

    member __.ShowBanner() =
        fsiConsoleOutput.uprintnfn "%s" (tcConfigB.productNameForBannerText)
        fsiConsoleOutput.uprintfnn "%s" (FSComp.SR.optsCopyright())
        fsiConsoleOutput.uprintfn  "%s" (FSIstrings.SR.fsiBanner3())
     
    member __.ShowHelp() =
        let helpLine = sprintf "%s --help" (Path.GetFileNameWithoutExtension executableFileName.Value)

        fsiConsoleOutput.uprintfn  ""
        fsiConsoleOutput.uprintfnn "%s" (FSIstrings.SR.fsiIntroTextHeader1directives());
        fsiConsoleOutput.uprintfn  "    #r \"file.dll\";;        %s" (FSIstrings.SR.fsiIntroTextHashrInfo());
        fsiConsoleOutput.uprintfn  "    #I \"path\";;            %s" (FSIstrings.SR.fsiIntroTextHashIInfo());
        fsiConsoleOutput.uprintfn  "    #load \"file.fs\" ...;;  %s" (FSIstrings.SR.fsiIntroTextHashloadInfo());
        fsiConsoleOutput.uprintfn  "    #time [\"on\"|\"off\"];;   %s" (FSIstrings.SR.fsiIntroTextHashtimeInfo());
        fsiConsoleOutput.uprintfn  "    #help;;                %s" (FSIstrings.SR.fsiIntroTextHashhelpInfo());
        fsiConsoleOutput.uprintfn  "    #quit;;                %s" (FSIstrings.SR.fsiIntroTextHashquitInfo()); (* last thing you want to do, last thing in the list - stands out more *)
        fsiConsoleOutput.uprintfn  "";
        fsiConsoleOutput.uprintfnn "%s" (FSIstrings.SR.fsiIntroTextHeader2commandLine());
        fsiConsoleOutput.uprintfn  "%s" (FSIstrings.SR.fsiIntroTextHeader3(helpLine));
        fsiConsoleOutput.uprintfn  "";
        fsiConsoleOutput.uprintfn "";

#if DEBUG
    member __.ShowILCode with get() = showILCode and set v = showILCode <- v
#endif
    member __.ShowTypes with get() = showTypes and set v = showTypes <- v
    member __.FsiServerName = fsiServerName
    member __.FsiServerInputCodePage = fsiServerInputCodePage
    member __.FsiServerOutputCodePage = fsiServerOutputCodePage
    member __.FsiLCID with get() = fsiLCID and set v = fsiLCID <- v
    member __.IsInteractiveServer = isInteractiveServer()
    member __.EnableConsoleKeyProcessing = enableConsoleKeyProcessing

    member __.Interact = interact
    member __.PeekAheadOnConsoleToPermitTyping = peekAheadOnConsoleToPermitTyping
    member __.SourceFiles = sourceFiles
    member __.Gui = gui

/// Set the current ui culture for the current thread.
let internal SetCurrentUICultureForThread (lcid : int option) =
    let culture = Thread.CurrentThread.CurrentUICulture
    match lcid with
    | Some n -> Thread.CurrentThread.CurrentUICulture <- new CultureInfo(n)
    | None -> ()
    { new IDisposable with member x.Dispose() = Thread.CurrentThread.CurrentUICulture <- culture }


//----------------------------------------------------------------------------
// Reporting - warnings, errors
//----------------------------------------------------------------------------

let internal InstallErrorLoggingOnThisThread errorLogger =
    if !progress then dprintfn "Installing logger on id=%d name=%s" Thread.CurrentThread.ManagedThreadId Thread.CurrentThread.Name
    SetThreadErrorLoggerNoUnwind(errorLogger)
    SetThreadBuildPhaseNoUnwind(BuildPhase.Interactive)

/// Set the input/output encoding. The use of a thread is due to a known bug on 
/// on Vista where calls to Console.InputEncoding can block the process.
let internal SetServerCodePages(fsiOptions: FsiCommandLineOptions) =     
#if LIMITED_CONSOLE
    ignore fsiOptions
#else     
    match fsiOptions.FsiServerInputCodePage, fsiOptions.FsiServerOutputCodePage with 
    | None,None -> ()
    | inputCodePageOpt,outputCodePageOpt -> 
        let successful = ref false 
        Async.Start (async { do match inputCodePageOpt with 
                                | None -> () 
                                | Some(n:int) ->
                                      let encoding = System.Text.Encoding.GetEncoding(n) 
                                      // Note this modifies the real honest-to-goodness settings for the current shell.
                                      // and the modifiations hang around even after the process has exited.
                                      Console.InputEncoding <- encoding
                             do match outputCodePageOpt with 
                                | None -> () 
                                | Some(n:int) -> 
                                      let encoding = System.Text.Encoding.GetEncoding n
                                      // Note this modifies the real honest-to-goodness settings for the current shell.
                                      // and the modifiations hang around even after the process has exited.
                                      Console.OutputEncoding <- encoding
                             do successful := true  });
        for pause in [10;50;100;1000;2000;10000] do 
            if not !successful then 
                Thread.Sleep(pause);
#if LOGGING_GUI
        if not !successful then 
            System.Windows.Forms.MessageBox.Show(FSIstrings.SR.fsiConsoleProblem()) |> ignore
#endif
#endif



//----------------------------------------------------------------------------
// Prompt printing
//----------------------------------------------------------------------------

type internal FsiConsolePrompt(fsiOptions: FsiCommandLineOptions, fsiConsoleOutput: FsiConsoleOutput) =

    // A prompt gets "printed ahead" at start up. Tells users to start type while initialisation completes.
    // A prompt can be skipped by "silent directives", e.g. ones sent to FSI by VS.
    let mutable dropPrompt = 0
    // NOTE: SERVER-PROMPT is not user displayed, rather it's a prefix that code elsewhere 
    // uses to identify the prompt, see vs\FsPkgs\FSharp.VS.FSI\fsiSessionToolWindow.fs
    let prompt = if fsiOptions.IsInteractiveServer then "SERVER-PROMPT>\n" else "> "  

    member __.Print()      = if dropPrompt = 0 then fsiConsoleOutput.uprintf "%s" prompt else dropPrompt <- dropPrompt - 1
    member __.PrintAhead() = dropPrompt <- dropPrompt + 1; fsiConsoleOutput.uprintf "%s" prompt
    member __.SkipNext()   = dropPrompt <- dropPrompt + 1    
    member __.FsiOptions = fsiOptions



//----------------------------------------------------------------------------
// Startup processing
//----------------------------------------------------------------------------
type internal FsiConsoleInput(fsi: FsiEvaluationSessionHostConfig, fsiOptions: FsiCommandLineOptions, inReader: TextReader, outWriter: TextWriter) =

    let consoleOpt =
        // The "console.fs" code does a limited form of "TAB-completion".
        // Currently, it turns on if it looks like we have a console.
        if fsiOptions.EnableConsoleKeyProcessing then
            fsi.OptionalConsoleReadLine
        else
            None

    // When VFSI is running, there should be no "console", and in particular the console.fs readline code should not to run.
    do  if fsiOptions.IsInteractiveServer then assert(consoleOpt.IsNone)

    /// This threading event gets set after the first-line-reader has finished its work
    let consoleReaderStartupDone = new ManualResetEvent(false)

    /// When using a key-reading console this holds the first line after it is read
    let mutable firstLine = None

    /// Peek on the standard input so that the user can type into it from a console window.
    do if fsiOptions.Interact then
         if fsiOptions.PeekAheadOnConsoleToPermitTyping then 
          (new Thread(fun () -> 
              match consoleOpt with 
              | Some console when fsiOptions.EnableConsoleKeyProcessing && not fsiOptions.IsInteractiveServer ->
                  if isNil fsiOptions.SourceFiles then 
                      if !progress then fprintfn outWriter "first-line-reader-thread reading first line...";
                      firstLine <- Some(console()); 
                      if !progress then fprintfn outWriter "first-line-reader-thread got first line = %A..." firstLine;
                  consoleReaderStartupDone.Set() |> ignore 
                  if !progress then fprintfn outWriter "first-line-reader-thread has set signal and exited." ;
              | _ -> 
                  ignore(inReader.Peek());
                  consoleReaderStartupDone.Set() |> ignore 
            )).Start()
         else
           if !progress then fprintfn outWriter "first-line-reader-thread not in use."
           consoleReaderStartupDone.Set() |> ignore

    /// Try to get the first line, if we snarfed it while probing.
    member __.TryGetFirstLine() = let r = firstLine in firstLine <- None; r

    /// Try to get the console, if it appears operational.
    member __.TryGetConsole() = consoleOpt

    member __.In = inReader

    member __.WaitForInitialConsoleInput() = WaitHandle.WaitAll [| consoleReaderStartupDone  |] |> ignore;
    

//----------------------------------------------------------------------------
// FsiDynamicCompilerState
//----------------------------------------------------------------------------

type internal FsiInteractionStepStatus = 
    | CtrlC 
    | EndOfFile 
    | Completed of option<FsiValue>
    | CompletedWithReportedError of exn

[<AutoSerializable(false)>]
[<NoEquality; NoComparison>]
type internal FsiDynamicCompilerState =
    { optEnv    : Optimizer.IncrementalOptimizationEnv
      emEnv     : ILRuntimeWriter.emEnv
      tcGlobals : TcGlobals
      tcState   : TcState 
      tcImports   : TcImports
      ilxGenerator : IlxGen.IlxAssemblyGenerator
      // Why is this not in FsiOptions?
      timing    : bool
      debugBreak : bool }

let internal WithImplicitHome (tcConfigB, dir) f = 
    let old = tcConfigB.implicitIncludeDir 
    tcConfigB.implicitIncludeDir <- dir;
    try f() 
    finally tcConfigB.implicitIncludeDir <- old



/// Encapsulates the coordination of the typechecking, optimization and code generation
/// components of the F# compiler for interactively executed fragments of code.
///
/// A single instance of this object is created per interactive session.
type internal FsiDynamicCompiler
                       (fsi: FsiEvaluationSessionHostConfig,
                        timeReporter : FsiTimeReporter, 
                        tcConfigB, 
                        tcLockObject : obj, 
                        errorLogger: ErrorLoggerThatStopsOnFirstError, 
                        outWriter: TextWriter,
                        tcImports: TcImports, 
                        tcGlobals: TcGlobals, 
                        ilGlobals: ILGlobals, 
                        fsiOptions : FsiCommandLineOptions,
                        fsiConsoleOutput : FsiConsoleOutput,
                        fsiCollectible: bool,
                        niceNameGen,
                        resolvePath) = 

    let outfile = "TMPFSCI.exe"
    let assemblyName = "FSI-ASSEMBLY"

    let mutable fragmentId = 0
    let mutable prevIt : ValRef option = None

    let generateDebugInfo = tcConfigB.debuginfo

    let valuePrinter = FsiValuePrinter(fsi, ilGlobals, generateDebugInfo, resolvePath, outWriter)

    let assemblyBuilder,moduleBuilder = ILRuntimeWriter.mkDynamicAssemblyAndModule (assemblyName, tcConfigB.optSettings.localOpt(), generateDebugInfo, fsiCollectible)

    let rangeStdin = rangeN Lexhelp.stdinMockFilename 0

    let _writer = moduleBuilder.GetSymWriter()

    let infoReader = InfoReader(tcGlobals,tcImports.GetImportMap())    

    /// Add attributes 
    let CreateModuleFragment (tcConfigB, assemblyName, codegenResults) =
        if !progress then fprintfn fsiConsoleOutput.Out "Creating main module...";
        let mainModule = mkILSimpleModule assemblyName (GetGeneratedILModuleName tcConfigB.target assemblyName) (tcConfigB.target = Dll) tcConfigB.subsystemVersion tcConfigB.useHighEntropyVA (mkILTypeDefs codegenResults.ilTypeDefs) None None 0x0 (mkILExportedTypes []) ""
        { mainModule 
          with Manifest = 
                (let man = mainModule.ManifestOfAssembly
                 Some { man with  CustomAttrs = mkILCustomAttrs codegenResults.ilAssemAttrs }); }

    let ProcessInputs(istate: FsiDynamicCompilerState, inputs: ParsedInput list, showTypes: bool, isIncrementalFragment: bool, isInteractiveItExpr: bool, prefixPath: LongIdent) =
        let optEnv    = istate.optEnv
        let emEnv     = istate.emEnv
        let tcState   = istate.tcState
        let ilxGenerator = istate.ilxGenerator
        let tcConfig = TcConfig.Create(tcConfigB,validate=false)

        // Typecheck. The lock stops the type checker running at the same time as the 
        // server intellisense implementation (which is currently incomplete and #if disabled)
        let (tcState:TcState),topCustomAttrs,declaredImpls,tcEnvAtEndOfLastInput =
            lock tcLockObject (fun _ -> TypeCheckClosedInputSet(errorLogger.CheckForErrors,tcConfig,tcImports,tcGlobals, Some prefixPath,tcState,inputs))

#if DEBUG
        // Logging/debugging
        if tcConfig.printAst then
            let (TAssembly(declaredImpls)) = declaredImpls
            for input in declaredImpls do 
                fprintfn fsiConsoleOutput.Out "AST:" 
                fprintfn fsiConsoleOutput.Out "%+A" input
#endif

        errorLogger.AbortOnError();
         
        let importMap = tcImports.GetImportMap()

        // optimize: note we collect the incremental optimization environment 
        let optimizedImpls, _optData, optEnv = ApplyAllOptimizations (tcConfig, tcGlobals, (LightweightTcValForUsingInBuildMethodCall tcGlobals), outfile, importMap, isIncrementalFragment, optEnv, tcState.Ccu, declaredImpls)
        errorLogger.AbortOnError();
            
        let fragName = textOfLid prefixPath 
        let codegenResults = GenerateIlxCode (IlReflectBackend, isInteractiveItExpr, runningOnMono, tcConfig, topCustomAttrs, optimizedImpls, fragName, true, ilxGenerator)
        errorLogger.AbortOnError();

        // Each input is like a small separately compiled extension to a single source file. 
        // The incremental extension to the environment is dictated by the "signature" of the values as they come out 
        // of the type checker. Hence we add the declaredImpls (unoptimized) to the environment, rather than the 
        // optimizedImpls. 
        ilxGenerator.AddIncrementalLocalAssemblyFragment (isIncrementalFragment, fragName, declaredImpls)

        ReportTime tcConfig "TAST -> ILX";
        errorLogger.AbortOnError();
            
        ReportTime tcConfig "Linking";
        let ilxMainModule = CreateModuleFragment (tcConfigB, assemblyName, codegenResults)

        errorLogger.AbortOnError();
            
        ReportTime tcConfig "ILX -> IL (Unions)"; 
        let ilxMainModule = EraseUnions.ConvModule ilGlobals ilxMainModule
        ReportTime tcConfig "ILX -> IL (Funcs)"; 
        let ilxMainModule = EraseClosures.ConvModule ilGlobals ilxMainModule 

        errorLogger.AbortOnError();   
              
        ReportTime tcConfig "Assembly refs Normalised"; 
        let mainmod3 = Morphs.morphILScopeRefsInILModuleMemoized ilGlobals (NormalizeAssemblyRefs tcImports) ilxMainModule
        errorLogger.AbortOnError();

#if DEBUG
        if fsiOptions.ShowILCode then 
            fsiConsoleOutput.uprintnfn "--------------------";
            ILAsciiWriter.output_module outWriter mainmod3;
            fsiConsoleOutput.uprintnfn "--------------------"
#else
        ignore(fsiOptions)
#endif

        ReportTime tcConfig "Reflection.Emit";
        let emEnv,execs = ILRuntimeWriter.emitModuleFragment(ilGlobals, emEnv, assemblyBuilder, moduleBuilder, mainmod3, generateDebugInfo, resolvePath)

        errorLogger.AbortOnError();

        // Explicitly register the resources with the QuotationPickler module 
        // We would save them as resources into the dynamic assembly but there is missing 
        // functionality System.Reflection for dynamic modules that means they can't be read back out 
#if COMPILER_SERVICE_ASSUMES_FSHARP_CORE_4_4_0_0
        let cenv = { ilg = ilGlobals ; generatePdb = generateDebugInfo; resolvePath=resolvePath }
        for (referencedTypeDefs, bytes) in codegenResults.quotationResourceInfo do 
            let referencedTypes = 
                [| for tref in referencedTypeDefs do 
                      yield ILRuntimeWriter.LookupTypeRef cenv emEnv tref  |]
            Microsoft.FSharp.Quotations.Expr.RegisterReflectedDefinitions (assemblyBuilder, fragName, bytes, referencedTypes);
#else
        for (_referencedTypeDefs, bytes) in codegenResults.quotationResourceInfo do 
            Microsoft.FSharp.Quotations.Expr.RegisterReflectedDefinitions (assemblyBuilder, fragName, bytes);
#endif            
            

        ReportTime tcConfig "Run Bindings";
        timeReporter.TimeOpIf istate.timing (fun () -> 
          execs |> List.iter (fun exec -> 
            match exec() with 
            | Some err ->         
                fprintfn fsiConsoleOutput.Error "%s" (err.ToString())
                errorLogger.SetError()
                errorLogger.AbortOnError()

            | None -> ())) ;

        errorLogger.AbortOnError();

        // Echo the decls (reach inside wrapping)
        // This code occurs AFTER the execution of the declarations.
        // So stored values will have been initialised, modified etc.
        if showTypes && not tcConfig.noFeedback then  
            let denv = tcState.TcEnvFromImpls.DisplayEnv
            let denv = 
                if isIncrementalFragment then
                  // Extend denv with a (Val -> layout option) function for printing of val bindings.
                  {denv with generatedValueLayout = (fun v -> valuePrinter.InvokeDeclLayout (emEnv, ilxGenerator, v)) }
                else
                  // With #load items, the vals in the inferred signature do not tie up with those generated. Disable printing.
                  denv 

            // 'Open' the path for the fragment we just compiled for any future printing.
            let denv = denv.AddOpenPath (pathOfLid prefixPath) 

            let (TAssembly(declaredImpls)) = declaredImpls
            for (TImplFile(_qname,_,mexpr,_,_)) in declaredImpls do
                let responseL = NicePrint.layoutInferredSigOfModuleExpr false denv infoReader AccessibleFromSomewhere rangeStdin mexpr 
                if not (Layout.isEmptyL responseL) then      
                    fsiConsoleOutput.uprintfn "";
                    let opts = valuePrinter.GetFsiPrintOptions()
                    let responseL = Internal.Utilities.StructuredFormat.Display.squash_layout opts responseL
                    Layout.renderL (Layout.channelR outWriter) responseL |> ignore
                    fsiConsoleOutput.uprintfnn ""

        // Build the new incremental state.
        let istate = {istate with  optEnv    = optEnv;
                                   emEnv     = emEnv;
                                   ilxGenerator = ilxGenerator;
                                   tcState   = tcState  }
        
        // Return the new state and the environment at the end of the last input, ready for further inputs.
        (istate,tcEnvAtEndOfLastInput,declaredImpls)

    let nextFragmentId() = fragmentId <- fragmentId + 1; fragmentId

    let mkFragmentPath  i = 
        // NOTE: this text shows in exn traces and type names. Make it clear and fixed width 
        [mkSynId rangeStdin (FsiDynamicModulePrefix + sprintf "%04d" i)]

    member __.DynamicAssemblyName = assemblyName
    member __.DynamicAssembly = (assemblyBuilder :> Assembly)

    member __.EvalParsedSourceFiles (istate, inputs) =
        let i = nextFragmentId()
        let prefix = mkFragmentPath i 
        // Ensure the path includes the qualifying name 
        let inputs = inputs |> List.map (PrependPathToInput prefix) 
        let istate,_,_ = ProcessInputs (istate, inputs, true, false, false, prefix)
        istate

    /// Evaluate the given definitions and produce a new interactive state.
    member __.EvalParsedDefinitions (istate, showTypes, isInteractiveItExpr, defs: SynModuleDecls) =
        let filename = Lexhelp.stdinMockFilename
        let i = nextFragmentId()
        let prefix = mkFragmentPath i
        let prefixPath = pathOfLid prefix
        let impl = SynModuleOrNamespace(prefix,(* isModule: *) true,defs,PreXmlDoc.Empty,[],None,rangeStdin)
        let input = ParsedInput.ImplFile(ParsedImplFileInput(filename,true, ComputeQualifiedNameOfFileFromUniquePath (rangeStdin,prefixPath),[],[],[impl],true (* isLastCompiland *) ))
        let istate,tcEnvAtEndOfLastInput,declaredImpls = ProcessInputs (istate, [input], showTypes, true, isInteractiveItExpr, prefix)
        let tcState = istate.tcState 
        let newState = { istate with tcState = tcState.NextStateAfterIncrementalFragment(tcEnvAtEndOfLastInput) }

        // Find all new declarations the EvaluationListener
        begin
            let (TAssembly(mimpls)) = declaredImpls
            let contents = FSharpAssemblyContents(tcGlobals, tcState.Ccu, tcImports, mimpls)
            let contentFile = contents.ImplementationFiles.[0]
            // Skip the "FSI_NNNN"
            match contentFile.Declarations with 
            | [FSharpImplementationFileDeclaration.Entity (_eFakeModule,modDecls) ] -> 
                for decl in modDecls do 
                    match decl with 
                    | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (v,_,_) ->
                        // Report a top-level function or value definition
                      if v.IsModuleValueOrMember && not v.IsMember then 
                        let fsiValueOpt = 
                            match v.Item with 
                            | Item.Value vref ->
                                let optValue = newState.ilxGenerator.LookupGeneratedValue(valuePrinter.GetEvaluationContext(newState.emEnv), vref.Deref)
                                match optValue with
                                | Some (res, typ) -> Some(FsiValue(res, typ, FSharpType(tcGlobals, newState.tcState.Ccu, newState.tcImports, vref.Type)))
                                | None -> None 
                            | _ -> None

                        let symbol = FSharpSymbol.Create(newState.tcGlobals, newState.tcState.Ccu, newState.tcImports, v.Item)
                        let symbolUse = FSharpSymbolUse(tcGlobals, newState.tcState.TcEnvFromImpls.DisplayEnv, symbol, ItemOccurence.Binding, v.DeclarationLocation)
                        fsi.TriggerEvaluation (fsiValueOpt, symbolUse, decl)
                    | FSharpImplementationFileDeclaration.Entity (e,_) ->
                        // Report a top-level module or namespace definition
                        let symbol = FSharpSymbol.Create(newState.tcGlobals, newState.tcState.Ccu, newState.tcImports, e.Item)
                        let symbolUse = FSharpSymbolUse(tcGlobals, newState.tcState.TcEnvFromImpls.DisplayEnv, symbol, ItemOccurence.Binding, e.DeclarationLocation)
                        fsi.TriggerEvaluation (None, symbolUse, decl)
                    | FSharpImplementationFileDeclaration.InitAction _ ->
                        // Top level 'do' bindings are not reported as incremental declarations
                        ()
            | _ -> ()
        end

        newState
      
     
    /// Evaluate the given expression and produce a new interactive state.
    member fsiDynamicCompiler.EvalParsedExpression (istate, expr: SynExpr) =
        let tcConfig = TcConfig.Create (tcConfigB, validate=false)
        let itName = "it" 

        // Construct the code that saves the 'it' value into the 'SaveIt' register.
        let defs = fsiDynamicCompiler.BuildItBinding expr

        // Evaluate the overall definitions.
        let istate = fsiDynamicCompiler.EvalParsedDefinitions (istate, false, true, defs)
        // Snarf the type for 'it' via the binding
        match istate.tcState.TcEnvFromImpls.NameEnv.FindUnqualifiedItem itName with 
        | NameResolution.Item.Value vref -> 
             if not tcConfig.noFeedback then 
                 valuePrinter.InvokeExprPrinter (istate.tcState.TcEnvFromImpls.DisplayEnv, istate.emEnv, istate.ilxGenerator, vref.Deref)
             
             /// Clear the value held in the previous "it" binding, if any, as long as it has never been referenced.
             match prevIt with
             | Some prevVal when not prevVal.Deref.HasBeenReferenced -> 
                 istate.ilxGenerator.ClearGeneratedValue (valuePrinter.GetEvaluationContext istate.emEnv, prevVal.Deref)
             | _ -> ()
             prevIt <- Some vref

             //
             let optValue = istate.ilxGenerator.LookupGeneratedValue(valuePrinter.GetEvaluationContext(istate.emEnv), vref.Deref);
             match optValue with
             | Some (res, typ) -> istate, Completed(Some(FsiValue(res, typ, FSharpType(tcGlobals, istate.tcState.Ccu, istate.tcImports, vref.Type))))
             | _ -> istate, Completed None

        // Return the interactive state.
        | _ -> istate, Completed None

    // Construct the code that saves the 'it' value into the 'SaveIt' register.
    member __.BuildItBinding (expr: SynExpr) =
        let m = expr.Range
        let itName = "it" 

        let itID  = mkSynId m itName
        //let itExp = SynExpr.Ident itID
        let mkBind pat expr = Binding (None, DoBinding, false, (*mutable*)false, [], PreXmlDoc.Empty, SynInfo.emptySynValData, pat, None, expr, m, NoSequencePointAtInvisibleBinding)
        let bindingA = mkBind (mkSynPatVar None itID) expr (* let it = <expr> *)  // NOTE: the generalizability of 'expr' must not be damaged, e.g. this can't be an application 
        //let saverPath  = ["Microsoft";"FSharp";"Compiler";"Interactive";"RuntimeHelpers";"SaveIt"]
        //let dots = List.replicate (saverPath.Length - 1) m
        //let bindingB = mkBind (SynPat.Wild m) (SynExpr.App(ExprAtomicFlag.NonAtomic, false, SynExpr.LongIdent(false, LongIdentWithDots(List.map (mkSynId m) saverPath,dots),None,m), itExp,m)) (* let _  = saverPath it *)
        let defA = SynModuleDecl.Let (false, [bindingA], m)
        //let defB = SynModuleDecl.Let (false, [bindingB], m)
        
        [defA (* ; defB *) ]

    // construct an invisible call to Debugger.Break(), in the specified range
    member __.CreateDebuggerBreak (m : range) =
        let breakPath = ["System";"Diagnostics";"Debugger";"Break"]
        let dots = List.replicate (breakPath.Length - 1) m
        let methCall = SynExpr.LongIdent(false, LongIdentWithDots(List.map (mkSynId m) breakPath, dots), None, m)
        let args = SynExpr.Const(SynConst.Unit, m)
        let breakStatement = SynExpr.App(ExprAtomicFlag.Atomic, false, methCall, args, m)
        SynModuleDecl.DoExpr(SequencePointInfoForBinding.NoSequencePointAtDoBinding, breakStatement, m)

    member __.EvalRequireReference istate m path = 
        if FileSystem.IsInvalidPathShim(path) then
            error(Error(FSIstrings.SR.fsiInvalidAssembly(path),m))
        // Check the file can be resolved before calling requireDLLReference 
        let resolutions = tcImports.ResolveAssemblyReference(AssemblyReference(m,path, None),ResolveAssemblyReferenceMode.ReportErrors)
        tcConfigB.AddReferencedAssemblyByPath(m,path)
        let tcState = istate.tcState 
        let tcEnv,(_dllinfos,ccuinfos) = 
            try
                RequireDLL tcImports tcState.TcEnvFromImpls m path 
            with e ->
                tcConfigB.RemoveReferencedAssemblyByPath(m,path)
                reraise()
        let optEnv = List.fold (AddExternalCcuToOpimizationEnv tcGlobals) istate.optEnv ccuinfos
        istate.ilxGenerator.AddExternalCcus (ccuinfos |> List.map (fun ccuinfo -> ccuinfo.FSharpViewOfMetadata)) 
        resolutions,
        { istate with tcState = tcState.NextStateAfterIncrementalFragment(tcEnv); optEnv = optEnv }

    member fsiDynamicCompiler.ProcessMetaCommandsFromInputAsInteractiveCommands istate sourceFile inp =
        WithImplicitHome
           (tcConfigB, directoryName sourceFile) 
           (fun () ->
               ProcessMetaCommandsFromInput 
                   ((fun st (m,nm) -> tcConfigB.TurnWarningOff(m,nm); st),
                    (fun st (m,nm) -> snd (fsiDynamicCompiler.EvalRequireReference st m nm)),
                    (fun _ _ -> ()))  
                   tcConfigB 
                   inp 
                   (Path.GetDirectoryName sourceFile)
                   istate)
      
    member fsiDynamicCompiler.EvalSourceFiles(istate, m, sourceFiles, lexResourceManager) =
        let tcConfig = TcConfig.Create(tcConfigB,validate=false)
        match sourceFiles with 
        | [] -> istate
        | _ -> 
          // use a set of source files as though they were command line inputs
          let sourceFiles = sourceFiles |> List.map (fun nm -> tcConfig.ResolveSourceFile(m,nm,tcConfig.implicitIncludeDir),m) 
         
          // Close the #load graph on each file and gather the inputs from the scripts.
          let closure = LoadClosure.ComputeClosureOfSourceFiles(TcConfig.Create(tcConfigB,validate=false),sourceFiles,CodeContext.Evaluation,lexResourceManager=lexResourceManager,useDefaultScriptingReferences=true)
          
          // Intent "[Loading %s]\n" (String.concat "\n     and " sourceFiles)
          fsiConsoleOutput.uprintf "[%s " (FSIstrings.SR.fsiLoadingFilesPrefixText())
          closure.Inputs  |> List.iteri (fun i (sourceFile,_) -> 
              if i=0 then fsiConsoleOutput.uprintf  "%s" sourceFile
              else fsiConsoleOutput.uprintnf " %s %s" (FSIstrings.SR.fsiLoadingFilesPrefixText()) sourceFile)
          fsiConsoleOutput.uprintfn "]"

          // Play errors and warnings from closures of the surface (root) script files.
          closure.RootErrors |> List.iter errorSink
          closure.RootWarnings |> List.iter warnSink
                
          // Non-scripts will not have been parsed during #load closure so parse them now
          let sourceFiles,inputs = 
              closure.Inputs  
              |> List.map (fun (filename, input)-> 
                    let parsedInput = 
                        match input with 
                        | None -> ParseOneInputFile(tcConfig,lexResourceManager,["INTERACTIVE"],filename,true,errorLogger,(*retryLocked*)false)
                        | _-> input
                    filename, parsedInput)
              |> List.unzip
          
          errorLogger.AbortOnError();
          if inputs |> List.exists isNone then failwith "parse error";
          let inputs = List.map Option.get inputs 
          let istate = List.fold2 fsiDynamicCompiler.ProcessMetaCommandsFromInputAsInteractiveCommands istate sourceFiles inputs
          fsiDynamicCompiler.EvalParsedSourceFiles (istate, inputs)

    
    member __.GetInitialInteractiveState () =
        let tcConfig = TcConfig.Create(tcConfigB,validate=false)
        let optEnv0 = GetInitialOptimizationEnv (tcImports, tcGlobals)
        let emEnv = ILRuntimeWriter.emEnv0
        let tcEnv = GetInitialTcEnv (None, rangeStdin, tcConfig, tcImports, tcGlobals)
        let ccuName = assemblyName 

        let tcState = GetInitialTcState (rangeStdin, ccuName, tcConfig, tcGlobals, tcImports, niceNameGen, tcEnv)

        let ilxGenerator = CreateIlxAssemblyGenerator(tcConfig,tcImports,tcGlobals, (LightweightTcValForUsingInBuildMethodCall tcGlobals), tcState.Ccu )
        {optEnv    = optEnv0
         emEnv     = emEnv
         tcGlobals = tcGlobals
         tcState   = tcState
         tcImports = tcImports
         ilxGenerator = ilxGenerator
         timing    = false
         debugBreak = false
        } 

    member __.CurrentPartialAssemblySignature(istate) = 
        FSharpAssemblySignature(istate.tcGlobals, istate.tcState.Ccu, istate.tcImports, istate.tcState.PartialAssemblySignature)


//----------------------------------------------------------------------------
// ctrl-c handling
//----------------------------------------------------------------------------

module internal NativeMethods = 

    type ControlEventHandler = delegate of int -> bool

    [<DllImport("kernel32.dll")>]
    extern bool SetConsoleCtrlHandler(ControlEventHandler _callback,bool _add)

// One strange case: when a TAE happens a strange thing 
// occurs the next read from stdin always returns
// 0 bytes, i.e. the channel will look as if it has been closed.  So we check
// for this condition explicitly.  We also recreate the lexbuf whenever CtrlC kicks.
type internal FsiInterruptStdinState = 
    | StdinEOFPermittedBecauseCtrlCRecentlyPressed 
    | StdinNormal

type internal FsiInterruptControllerState =  
    | InterruptCanRaiseException 
    | InterruptIgnored 

type internal FsiInterruptControllerKillerThreadRequest =  
    | ThreadAbortRequest 
    | NoRequest 
    | ExitRequest 
    | PrintInterruptRequest

type internal FsiInterruptController(fsiOptions : FsiCommandLineOptions, 
                                     fsiConsoleOutput: FsiConsoleOutput) = 

    let mutable stdinInterruptState = StdinNormal
    let CTRL_C = 0 
    let mutable interruptAllowed = InterruptIgnored
    let mutable killThreadRequest = NoRequest
    let mutable ctrlEventHandlers = [] : NativeMethods.ControlEventHandler list 
    let mutable ctrlEventActions  = [] : (unit -> unit) list 
    let mutable exitViaKillThread = false

    let mutable posixReinstate = (fun () -> ())

    member __.Exit() = 
        if exitViaKillThread then 
            killThreadRequest <- ExitRequest
            Thread.Sleep(1000)
        exit 0

    member __.FsiInterruptStdinState with get () = stdinInterruptState and set v = stdinInterruptState <- v

    member __.ClearInterruptRequest() = killThreadRequest <- NoRequest
    
    member __.InterruptAllowed with set v = interruptAllowed <- v
    
    member __.Interrupt() = ctrlEventActions |> List.iter (fun act -> act())
    
    member __.EventHandlers = ctrlEventHandlers

    // REVIEW: streamline all this code to use the same code on Windows and Posix.   
    member controller.InstallKillThread(threadToKill:Thread, pauseMilliseconds:int) = 
#if DYNAMIC_CODE_EMITS_INTERRUPT_CHECKS
        let action() =
            Microsoft.FSharp.Silverlight.InterruptThread(threadToKill.ManagedThreadId)

        ctrlEventActions  <- action           :: ctrlEventActions;
#else
        if !progress then fprintfn fsiConsoleOutput.Out "installing CtrlC handler"
        // WINDOWS TECHNIQUE: .NET has more safe points, and you can do more when a safe point. 
        // Hence we actually start up the killer thread within the handler. 
        try 
            let raiseCtrlC() = 
                use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
                fprintf fsiConsoleOutput.Error "%s" (FSIstrings.SR.fsiInterrupt())
                stdinInterruptState <- StdinEOFPermittedBecauseCtrlCRecentlyPressed
                if (interruptAllowed = InterruptCanRaiseException) then 
                    killThreadRequest <- ThreadAbortRequest
                    let killerThread = 
                        new Thread(new ThreadStart(fun () ->
                            use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
                            // sleep long enough to allow ControlEventHandler handler on main thread to return 
                            // Also sleep to give computations a bit of time to terminate 
                            Thread.Sleep(pauseMilliseconds)
                            if (killThreadRequest = ThreadAbortRequest) then 
                                if !progress then fsiConsoleOutput.uprintnfn "%s" (FSIstrings.SR.fsiAbortingMainThread())  
                                killThreadRequest <- NoRequest
                                threadToKill.Abort()
                            ()),Name="ControlCAbortThread") 
                    killerThread.IsBackground <- true
                    killerThread.Start() 
        
            let ctrlEventHandler = new NativeMethods.ControlEventHandler(fun i ->  if i = CTRL_C then (raiseCtrlC(); true) else false ) 
            ctrlEventHandlers <- ctrlEventHandler :: ctrlEventHandlers
            ctrlEventActions  <- raiseCtrlC       :: ctrlEventActions
            let _resultOK = NativeMethods.SetConsoleCtrlHandler(ctrlEventHandler,true)
            exitViaKillThread <- false // don't exit via kill thread
        with e -> 
            if !progress then fprintfn fsiConsoleOutput.Error "Failed to install ctrl-c handler using Windows technique - trying to install one using Unix signal handling...";
            // UNIX TECHNIQUE: We start up a killer thread, and it watches the mutable reference location.    
            // We can't have a dependency on Mono DLLs (indeed we don't even have them!)
            // So SOFT BIND the following code:
            // Mono.Unix.Native.Stdlib.signal(Mono.Unix.Native.Signum.SIGINT,new Mono.Unix.Native.SignalHandler(fun n -> PosixSignalProcessor.PosixInvoke(n))) |> ignore;
            match (try Choice1Of2(Assembly.Load("Mono.Posix, Version=2.0.0.0, Culture=neutral, PublicKeyToken=0738eb9f132ed756")) with e -> Choice2Of2 e) with 
            | Choice1Of2(monoPosix) -> 
              try
                if !progress then fprintfn fsiConsoleOutput.Error "loading type Mono.Unix.Native.Stdlib..."
                let monoUnixStdlib = monoPosix.GetType("Mono.Unix.Native.Stdlib") 
                if !progress then fprintfn fsiConsoleOutput.Error "loading type Mono.Unix.Native.SignalHandler..."
                let monoUnixSignalHandler = monoPosix.GetType("Mono.Unix.Native.SignalHandler") 
                if !progress then fprintfn fsiConsoleOutput.Error "creating delegate..."
                controller.PosixInvoke(-1)
                let monoHandler = System.Delegate.CreateDelegate(monoUnixSignalHandler,controller,"PosixInvoke") 
                if !progress then fprintfn fsiConsoleOutput.Error "registering signal handler..."
                let monoSignalNumber = System.Enum.Parse(monoPosix.GetType("Mono.Unix.Native.Signum"),"SIGINT")
                let register () = Utilities.callStaticMethod monoUnixStdlib "signal" [ monoSignalNumber; box monoHandler ]  |> ignore 
                posixReinstate <- register
                register()
                let killerThread = 
                    new Thread(new ThreadStart(fun () ->
                        use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
                        while true do 
                            //fprintf fsiConsoleOutput.Error "\n- kill thread loop...\n"; errorWriter.Flush();  
                            Thread.Sleep(pauseMilliseconds*2)
                            match killThreadRequest with 
                            | PrintInterruptRequest -> 
                                fprintf fsiConsoleOutput.Error "%s" (FSIstrings.SR.fsiInterrupt()); fsiConsoleOutput.Error.Flush()  
                                killThreadRequest <- NoRequest
                            | ThreadAbortRequest -> 
                                fprintf fsiConsoleOutput.Error  "%s" (FSIstrings.SR.fsiInterrupt()); fsiConsoleOutput.Error.Flush()  
                                if !progress then fsiConsoleOutput.uprintnfn "%s" (FSIstrings.SR.fsiAbortingMainThread())
                                killThreadRequest <- NoRequest
                                threadToKill.Abort()
                            | ExitRequest -> 
                                // Mono has some wierd behaviour where it blocks on exit
                                // once CtrlC has ever been pressed.  Who knows why?  Perhaps something
                                // to do with having a signal handler installed, but it only happens _after_
                                // at least one CtrLC has been pressed.  Maybe raising a ThreadAbort causes
                                // exiting to have problems.
                                //
                                // Anyway, we make "#q" work this case by setting ExitRequest and brutally calling
                                // the process-wide 'exit'
                                fprintf fsiConsoleOutput.Error  "%s" (FSIstrings.SR.fsiExit()); fsiConsoleOutput.Error.Flush()  
                                Utilities.callStaticMethod monoUnixStdlib "exit" [ box 0 ] |> ignore
                            | _ ->  ()
                        done),Name="ControlCAbortAlternativeThread") 
                killerThread.IsBackground <- true
                killerThread.Start()
                // exit via kill thread to workaround block-on-exit bugs with Mono once a CtrlC has been pressed
                exitViaKillThread <- true 
              with e -> 
                fprintf fsiConsoleOutput.Error  "%s" (FSIstrings.SR.fsiCouldNotInstallCtrlCHandler(e.Message))
                exitViaKillThread <- false
            | Choice2Of2 e ->
              fprintf fsiConsoleOutput.Error  "%s" (FSIstrings.SR.fsiCouldNotInstallCtrlCHandler(e.Message))
              exitViaKillThread <- false  


    member x.PosixInvoke(n:int) = 
         // we run this code once with n = -1 to make sure it is JITted before execution begins
         // since we are not allowed to JIT a signal handler.  THis also ensures the "PosixInvoke"
         // method is not eliminated by dead-code elimination
         if n >= 0 then 
             posixReinstate()
             stdinInterruptState <- StdinEOFPermittedBecauseCtrlCRecentlyPressed
             killThreadRequest <- if (interruptAllowed = InterruptCanRaiseException) then ThreadAbortRequest else PrintInterruptRequest

#endif

//----------------------------------------------------------------------------
// assembly finder
//----------------------------------------------------------------------------

#nowarn "40"

// From http://msdn.microsoft.com/en-us/library/ff527268.aspx
// What the Event Handler Does
//
// The handler for the AssemblyResolve event receives the display name of the assembly to 
// be loaded, in the ResolveEventArgs.Name property. If the handler does not recognize the 
// assembly name, it returns null (Nothing in Visual Basic, nullptr in Visual C++). 
//
// - If the handler recognizes the assembly name, it can load and return an assembly that 
//   satisfies the request. The following list describes some sample scenarios. 
//
// - If the handler knows the location of a version of the assembly, it can load the assembly by 
//   using the Assembly.LoadFrom or Assembly.LoadFile method, and can return the loaded assembly if successful. 
//
// - If the handler has access to a database of assemblies stored as byte arrays, it can load a byte array by 
//   using one of the Assembly.Load method overloads that take a byte array. 
//
// - The handler can generate a dynamic assembly and return it.
// 
// It is the responsibility of the event handler to return a suitable assembly. The handler can parse the display 
// name of the requested assembly by passing the ResolveEventArgs.Name property value to the AssemblyName(String) 
// constructor. Beginning with the .NET Framework version 4, the handler can use the ResolveEventArgs.RequestingAssembly 
// property to determine whether the current request is a dependency of another assembly. This information can help 
// identify an assembly that will satisfy the dependency.
// 
// The event handler can return a different version of the assembly than the version that was requested. 
// 
// In most cases, the assembly that is returned by the handler appears in the load context, regardless of the context 
// the handler loads it into. For example, if the handler uses the Assembly.LoadFrom method to load an assembly into 
// the load-from context, the assembly appears in the load context when the handler returns it. However, in the following 
// case the assembly appears without context when the handler returns it:
// 
// - The handler loads an assembly without context.
// - The ResolveEventArgs.RequestingAssembly property is not null.
// - The requesting assembly (that is, the assembly that is returned by the ResolveEventArgs.RequestingAssembly property) 
//   was loaded without context. 
// 
// For information about contexts, see the Assembly.LoadFrom(String) method overload.

module internal MagicAssemblyResolution =
    // FxCop identifies Assembly.LoadFrom.
    [<CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2001:AvoidCallingProblematicMethods", MessageId="System.Reflection.Assembly.UnsafeLoadFrom")>]
    let private assemblyLoadFrom (path:string) = 

    // See bug 5501 for details on decision to use UnsafeLoadFrom here.
    // Summary:
    //  It is an explicit user trust decision to load an assembly with #r. Scripts are not run automatically (for example, by double-clicking in explorer).
    //  We considered setting loadFromRemoteSources in fsi.exe.config but this would transitively confer unsafe loading to the code in the referenced 
    //  assemblies. Better to let those assemblies decide for themselves which is safer.
#if FX_ATLEAST_40
        Assembly.UnsafeLoadFrom(path)
#else
        Assembly.LoadFrom(path)
#endif
    let ResolveAssembly(m,tcConfigB, tcImports: TcImports, fsiDynamicCompiler: FsiDynamicCompiler, fsiConsoleOutput: FsiConsoleOutput, fullAssemName:string) = 
           try 
               // Grab the name of the assembly
               let tcConfig = TcConfig.Create(tcConfigB,validate=false)
               let simpleAssemName = fullAssemName.Split([| ',' |]).[0]          
               if !progress then fsiConsoleOutput.uprintfn "ATTEMPT MAGIC LOAD ON ASSEMBLY, simpleAssemName = %s" simpleAssemName // "Attempting to load a dynamically required assembly in response to an AssemblyResolve event by using known static assembly references..." 
               
               // Special case: Mono Windows Forms attempts to load an assembly called something like "Windows.Forms.resources"
               // We can't resolve this, so don't try.
               // REVIEW: Suggest 4481, delete this special case.
               if simpleAssemName.EndsWith(".resources",StringComparison.OrdinalIgnoreCase) || 
                    // See F# 1.0 Product Studio bug 1171
                    simpleAssemName.EndsWith(".XmlSerializers",StringComparison.OrdinalIgnoreCase) || 
                    (runningOnMono && simpleAssemName = "UIAutomationWinforms") then null else

               // Special case: Is this the global unique dynamic assembly for FSI code? In this case just
               // return the dynamic assembly itself.       
               if fsiDynamicCompiler.DynamicAssemblyName = simpleAssemName then fsiDynamicCompiler.DynamicAssembly else

               // Otherwise continue
               let assemblyReferenceTextDll = (simpleAssemName + ".dll") 
               let assemblyReferenceTextExe = (simpleAssemName + ".exe") 
               let overallSearchResult =           
                   // OK, try to resolve as a .dll
                   let searchResult = tcImports.TryResolveAssemblyReference (AssemblyReference(m,assemblyReferenceTextDll,None),ResolveAssemblyReferenceMode.Speculative)

                   match searchResult with
                   | OkResult (warns,[r]) -> OkResult (warns, Choice1Of2 r.resolvedPath)
                   | _ -> 

                   // OK, try to resolve as a .exe
                   let searchResult = tcImports.TryResolveAssemblyReference (AssemblyReference(m,assemblyReferenceTextExe,None),ResolveAssemblyReferenceMode.Speculative)

                   match searchResult with
                   | OkResult (warns, [r]) -> OkResult (warns, Choice1Of2 r.resolvedPath)
                   | _ -> 

                   if !progress then fsiConsoleOutput.uprintfn "ATTEMPT LOAD, assemblyReferenceTextDll = %s" assemblyReferenceTextDll
                   /// Take a look through the files quoted, perhaps with explicit paths
                   let searchResult = 
                       (tcConfig.referencedDLLs 
                            |> List.tryPick (fun assemblyReference -> 
                             if !progress then fsiConsoleOutput.uprintfn "ATTEMPT MAGIC LOAD ON FILE, referencedDLL = %s" assemblyReference.Text
                             if System.String.Compare(Filename.fileNameOfPath assemblyReference.Text, assemblyReferenceTextDll,StringComparison.OrdinalIgnoreCase) = 0 ||
                                System.String.Compare(Filename.fileNameOfPath assemblyReference.Text, assemblyReferenceTextExe,StringComparison.OrdinalIgnoreCase) = 0 then
                                 Some(tcImports.TryResolveAssemblyReference(assemblyReference,ResolveAssemblyReferenceMode.Speculative))
                             else None ))

                   match searchResult with
                   | Some (OkResult (warns,[r])) -> OkResult (warns, Choice1Of2 r.resolvedPath)
                   | _ -> 

#if EXTENSIONTYPING
                   match tcImports.TryFindProviderGeneratedAssemblyByName(simpleAssemName) with
                   | Some(assembly) -> OkResult([],Choice2Of2 assembly)
                   | None -> 
#endif
                   
                   // As a last resort, try to find the reference without an extension
                   match tcImports.TryFindExistingFullyQualifiedPathFromAssemblyRef(ILAssemblyRef.Create(simpleAssemName,None,None,false,None,None)) with
                   | Some(resolvedPath) -> 
                       OkResult([],Choice1Of2 resolvedPath)
                   | None -> 
                   
                   ErrorResult([],Failure (FSIstrings.SR.fsiFailedToResolveAssembly(simpleAssemName)))
                           
               match overallSearchResult with 
               | ErrorResult _ -> null
               | OkResult _ -> 
                   let res = CommitOperationResult overallSearchResult
                   match res with 
                   | Choice1Of2 assemblyName -> 
                       if simpleAssemName <> "Mono.Posix" then fsiConsoleOutput.uprintfn "%s" (FSIstrings.SR.fsiBindingSessionTo(assemblyName))
                       assemblyLoadFrom assemblyName
                   | Choice2Of2 assembly -> 
                       assembly
                   
           with e -> 
               stopProcessingRecovery e range0
               null

    let Install(tcConfigB, tcImports: TcImports, fsiDynamicCompiler: FsiDynamicCompiler, fsiConsoleOutput: FsiConsoleOutput) = 

        let rangeStdin = rangeN Lexhelp.stdinMockFilename 0

        let handler = new ResolveEventHandler(fun _ args -> 
            ResolveAssembly (rangeStdin, tcConfigB, tcImports, fsiDynamicCompiler, fsiConsoleOutput, args.Name))
        
        AppDomain.CurrentDomain.add_AssemblyResolve(handler)

        { new System.IDisposable  with 
             member x.Dispose() = AppDomain.CurrentDomain.remove_AssemblyResolve(handler) }

//----------------------------------------------------------------------------
// Reading stdin 
//----------------------------------------------------------------------------

type internal FsiStdinLexerProvider
                          (tcConfigB, fsiStdinSyphon, 
                           fsiConsoleInput : FsiConsoleInput, 
                           fsiConsoleOutput : FsiConsoleOutput, 
                           fsiOptions : FsiCommandLineOptions,
                           lexResourceManager : LexResourceManager,
                           errorLogger) = 

    // #light is the default for FSI
    let interactiveInputLightSyntaxStatus = 
        let initialLightSyntaxStatus = tcConfigB.light <> Some false
        LightSyntaxStatus (initialLightSyntaxStatus, false (* no warnings *))

    let LexbufFromLineReader (fsiStdinSyphon: FsiStdinSyphon) readf = 
        UnicodeLexing.FunctionAsLexbuf 
          (fun (buf: char[], start, len) -> 
            //fprintf fsiConsoleOutput.Out "Calling ReadLine\n"
            let inputOption = try Some(readf()) with :? EndOfStreamException -> None
            inputOption |> Option.iter (fun t -> fsiStdinSyphon.Add (t + "\n"))
            match inputOption with 
            |  Some(null) | None -> 
                 if !progress then fprintfn fsiConsoleOutput.Out "End of file from TextReader.ReadLine"
                 0
            | Some (input:string) ->
                let input  = input + "\n" 
                let ninput = input.Length 
                if ninput > len then fprintf fsiConsoleOutput.Error  "%s" (FSIstrings.SR.fsiLineTooLong())
                let ntrimmed = min len ninput 
                for i = 0 to ntrimmed-1 do
                    buf.[i+start] <- input.[i]
                ntrimmed
        )

    //----------------------------------------------------------------------------
    // Reading stdin as a lex stream
    //----------------------------------------------------------------------------

    let removeZeroCharsFromString (str:string) = (* bug://4466 *)
        if str<>null && str.Contains("\000") then
          System.String(str |> Seq.filter (fun c -> c<>'\000') |> Seq.toArray)
        else
          str

    let CreateLexerForLexBuffer (sourceFileName, lexbuf) =

        Lexhelp.resetLexbufPos sourceFileName lexbuf
        let skip = true  // don't report whitespace from lexer 
        let defines = "INTERACTIVE"::tcConfigB.conditionalCompilationDefines
        let lexargs = mkLexargs (sourceFileName,defines, interactiveInputLightSyntaxStatus, lexResourceManager, ref [], errorLogger) 
        let tokenizer = LexFilter.LexFilter(interactiveInputLightSyntaxStatus, tcConfigB.compilingFslib, Lexer.token lexargs skip, lexbuf)
        tokenizer


    // Create a new lexer to read stdin 
    member __.CreateStdinLexer () =
        let lexbuf = 
            match fsiConsoleInput.TryGetConsole() with 
            | Some console when fsiOptions.EnableConsoleKeyProcessing && not fsiOptions.IsInteractiveServer -> 
                LexbufFromLineReader fsiStdinSyphon (fun () -> 
                    match fsiConsoleInput.TryGetFirstLine() with 
                    | Some firstLine -> firstLine
                    | None -> console())
            | _ -> 
                LexbufFromLineReader fsiStdinSyphon (fun () -> fsiConsoleInput.In.ReadLine() |> removeZeroCharsFromString)

        fsiStdinSyphon.Reset()
        CreateLexerForLexBuffer (Lexhelp.stdinMockFilename, lexbuf)

    // Create a new lexer to read an "included" script file
    member __.CreateIncludedScriptLexer sourceFileName =
        let lexbuf = UnicodeLexing.UnicodeFileAsLexbuf(sourceFileName,tcConfigB.inputCodePage,(*retryLocked*)false)  
        CreateLexerForLexBuffer (sourceFileName, lexbuf)

    // Create a new lexer to read a string
    member this.CreateStringLexer (sourceFileName, source) =
        let lexbuf = UnicodeLexing.StringAsLexbuf(source)  
        CreateLexerForLexBuffer (sourceFileName, lexbuf)

    member __.ConsoleInput = fsiConsoleInput

    member __.CreateBufferLexer (sourceFileName, lexbuf) = CreateLexerForLexBuffer (sourceFileName, lexbuf)


//----------------------------------------------------------------------------
// Process one parsed interaction.  This runs on the GUI thread.
// It might be simpler if it ran on the parser thread.
//----------------------------------------------------------------------------

type internal FsiInteractionProcessor
                            (fsi: FsiEvaluationSessionHostConfig, 
                             tcConfigB, 
                             errorLogger : ErrorLoggerThatStopsOnFirstError, 
                             fsiOptions: FsiCommandLineOptions,
                             fsiDynamicCompiler: FsiDynamicCompiler,
                             fsiConsolePrompt : FsiConsolePrompt,
                             fsiConsoleOutput : FsiConsoleOutput,
                             fsiInterruptController : FsiInterruptController,
                             fsiStdinLexerProvider : FsiStdinLexerProvider,
                             lexResourceManager : LexResourceManager,
                             initialInteractiveState) = 

    let mutable currState = initialInteractiveState
    let event = Event<unit>()
    let setCurrState s = currState <- s; event.Trigger()
    //let mutable queueAgent = None

    let runCodeOnEventLoop f istate = 
        try 
            fsi.EventLoopInvoke (fun () -> 
                // FSI error logging on switched to thread
                InstallErrorLoggingOnThisThread errorLogger
                use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
                f istate) 
        with _ -> 
            (istate,Completed None)
                              
    let InteractiveCatch (f:_ -> _ * FsiInteractionStepStatus)  istate = 
        try
            // reset error count 
            errorLogger.ResetErrorCount()  
            f istate
        with  e ->
            stopProcessingRecovery e range0
            istate,CompletedWithReportedError(e)


    let rangeStdin = rangeN Lexhelp.stdinMockFilename 0

    let ChangeDirectory (path:string) m =
        let tcConfig = TcConfig.Create(tcConfigB,validate=false)
        let path = tcConfig.MakePathAbsolute path 
        if Directory.Exists(path) then 
            tcConfigB.implicitIncludeDir <- path
        else
            error(Error(FSIstrings.SR.fsiDirectoryDoesNotExist(path),m))


    /// Parse one interaction. Called on the parser thread.
    let ParseInteraction (tokenizer:LexFilter.LexFilter) =   
        let lastToken = ref Parser.ELSE // Any token besides SEMICOLON_SEMICOLON will do for initial value 
        try 
            if !progress then fprintfn fsiConsoleOutput.Out "In ParseInteraction..."

            let input = 
                Lexhelp.reusingLexbufForParsing tokenizer.LexBuffer (fun () -> 
                    let lexerWhichSavesLastToken lexbuf = 
                        let tok = tokenizer.Lexer lexbuf
                        lastToken := tok
                        tok                        
                    Parser.interaction lexerWhichSavesLastToken tokenizer.LexBuffer)
            Some input
        with e ->
            // On error, consume tokens until to ;; or EOF.
            // Caveat: Unless the error parse ended on ;; - so check the lastToken returned by the lexer function.
            // Caveat: What if this was a look-ahead? That's fine! Since we need to skip to the ;; anyway.     
            if (match !lastToken with Parser.SEMICOLON_SEMICOLON -> false | _ -> true) then
                let mutable tok = Parser.ELSE (* <-- any token <> SEMICOLON_SEMICOLON will do *)
                while (match tok with  Parser.SEMICOLON_SEMICOLON -> false | _ -> true) 
                      && not tokenizer.LexBuffer.IsPastEndOfStream do
                    tok <- tokenizer.Lexer tokenizer.LexBuffer

            stopProcessingRecovery e range0    
            None

    /// Execute a single parsed interaction. Called on the GUI/execute/main thread.
    let ExecInteraction (tcConfig:TcConfig, istate, action:ParsedFsiInteraction) =
        istate |> InteractiveCatch (fun istate -> 
            match action with 
            | IDefns ([  ],_) ->
                istate,Completed None
            | IDefns ([  SynModuleDecl.DoExpr(_,expr,_)],_) ->
                fsiDynamicCompiler.EvalParsedExpression(istate, expr)
            | IDefns (defs,_) -> 
                fsiDynamicCompiler.EvalParsedDefinitions (istate, true, false, defs),Completed None

            | IHash (ParsedHashDirective("load",sourceFiles,m),_) -> 
                fsiDynamicCompiler.EvalSourceFiles (istate, m, sourceFiles, lexResourceManager),Completed None

            | IHash (ParsedHashDirective(("reference" | "r"),[path],m),_) -> 
                let resolutions,istate = fsiDynamicCompiler.EvalRequireReference istate m path 
                resolutions |> List.iter (fun ar -> 
                    let format = 
                        if tcConfig.shadowCopyReferences then
                            let resolvedPath = ar.resolvedPath.ToUpperInvariant()
                            let fileTime = File.GetLastWriteTimeUtc(resolvedPath)
                            match referencedAssemblies.TryGetValue(resolvedPath) with
                            | false, _ -> 
                                referencedAssemblies.Add(resolvedPath, fileTime)
                                FSIstrings.SR.fsiDidAHashr(ar.resolvedPath)
                            | true, time when time <> fileTime ->
                                FSIstrings.SR.fsiDidAHashrWithStaleWarning(ar.resolvedPath)
                            | _ ->
                                FSIstrings.SR.fsiDidAHashr(ar.resolvedPath)
                        else
                            FSIstrings.SR.fsiDidAHashrWithLockWarning(ar.resolvedPath)
                    fsiConsoleOutput.uprintnfnn "%s" format)
                istate,Completed None

            | IHash (ParsedHashDirective("I",[path],m),_) -> 
                tcConfigB.AddIncludePath (m,path, tcConfig.implicitIncludeDir)
                fsiConsoleOutput.uprintnfnn "%s" (FSIstrings.SR.fsiDidAHashI(tcConfig.MakePathAbsolute path))
                istate,Completed None

            | IHash (ParsedHashDirective("cd",[path],m),_) ->
                ChangeDirectory path m
                istate,Completed None

            | IHash (ParsedHashDirective("silentCd",[path],m),_) ->
                ChangeDirectory path m
                fsiConsolePrompt.SkipNext() (* "silent" directive *)
                istate,Completed None                  
                               
            | IHash (ParsedHashDirective("dbgbreak",[],_),_) -> 
                {istate with debugBreak = true},Completed None

            | IHash (ParsedHashDirective("time",[],_),_) -> 
                if istate.timing then
                    fsiConsoleOutput.uprintnfnn "%s" (FSIstrings.SR.fsiTurnedTimingOff())
                else
                    fsiConsoleOutput.uprintnfnn "%s" (FSIstrings.SR.fsiTurnedTimingOn())
                {istate with timing = not istate.timing},Completed None

            | IHash (ParsedHashDirective("time",[("on" | "off") as v],_),_) -> 
                if v <> "on" then
                    fsiConsoleOutput.uprintnfnn "%s" (FSIstrings.SR.fsiTurnedTimingOff())
                else
                    fsiConsoleOutput.uprintnfnn "%s" (FSIstrings.SR.fsiTurnedTimingOn())
                {istate with timing = (v = "on")},Completed None

            | IHash (ParsedHashDirective("nowarn",numbers,m),_) -> 
                List.iter (fun (d:string) -> tcConfigB.TurnWarningOff(m,d)) numbers
                istate,Completed None

            | IHash (ParsedHashDirective("terms",[],_),_) -> 
                tcConfigB.showTerms <- not tcConfig.showTerms
                istate,Completed None

            | IHash (ParsedHashDirective("types",[],_),_) -> 
                fsiOptions.ShowTypes <- not fsiOptions.ShowTypes
                istate,Completed None

    #if DEBUG
            | IHash (ParsedHashDirective("ilcode",[],_m),_) -> 
                fsiOptions.ShowILCode <- not fsiOptions.ShowILCode; 
                istate,Completed None

            | IHash (ParsedHashDirective("info",[],_m),_) -> 
                PrintOptionInfo tcConfigB
                istate,Completed None         
    #endif

            | IHash (ParsedHashDirective(("q" | "quit"),[],_),_) -> 
                fsiInterruptController.Exit()

            | IHash (ParsedHashDirective("help",[],_),_) ->
                fsiOptions.ShowHelp()
                istate,Completed None

            | IHash (ParsedHashDirective(c,arg,_),_) -> 
                fsiConsoleOutput.uprintfn "%s" (FSIstrings.SR.fsiInvalidDirective(c, String.concat " " arg))  // REVIEW: uprintnfnn - like other directives above
                istate,Completed None  (* REVIEW: cont = CompletedWithReportedError *)
        )

    /// Execute a single parsed interaction which may contain multiple items to be executed
    /// independently, because some are #directives. Called on the GUI/execute/main thread.
    /// 
    /// #directive comes through with other definitions as a SynModuleDecl.HashDirective.
    /// We split these out for individual processing.
    let rec execParsedInteractions (tcConfig, istate, action) (lastResult:option<FsiInteractionStepStatus>)  =
        let action,nextAction,istate = 
            match action with
            | None                                      -> None  ,None,istate
            | Some (IHash _)                            -> action,None,istate
            | Some (IDefns ([],_))                      -> None  ,None,istate
            | Some (IDefns (SynModuleDecl.HashDirective(hash,mh)::defs,m)) -> 
                Some (IHash(hash,mh)),Some (IDefns(defs,m)),istate

            | Some (IDefns (defs,m))                    -> 
                let isDefHash = function SynModuleDecl.HashDirective(_,_) -> true | _ -> false
                let isBreakable def = 
                    // only add automatic debugger breaks before 'let' or 'do' expressions with sequence points
                    match def with
                    | SynModuleDecl.DoExpr (SequencePointInfoForBinding.SequencePointAtBinding _, _, _)
                    | SynModuleDecl.Let (_, SynBinding.Binding(_, _, _, _, _, _, _, _ ,_ ,_ ,_ , SequencePointInfoForBinding.SequencePointAtBinding _) :: _, _) -> true
                    | _ -> false
                let defsA = Seq.takeWhile (isDefHash >> not) defs |> Seq.toList
                let defsB = Seq.skipWhile (isDefHash >> not) defs |> Seq.toList

                // If user is debugging their script interactively, inject call
                // to Debugger.Break() at the first "breakable" line.
                // Update istate so that more Break() calls aren't injected when recursing
                let defsA,istate =
                    if istate.debugBreak then
                        let preBreak = Seq.takeWhile (isBreakable >> not) defsA |> Seq.toList
                        let postBreak = Seq.skipWhile (isBreakable >> not) defsA |> Seq.toList
                        match postBreak with
                        | h :: _ -> preBreak @ (fsiDynamicCompiler.CreateDebuggerBreak(h.Range) :: postBreak), { istate with debugBreak = false }
                        | _ -> defsA, istate
                    else defsA,istate

                // When the last declaration has a shape of DoExp (i.e., non-binding), 
                // transform it to a shape of "let it = <exp>", so we can refer it.
                let defsA = if defsA.Length <= 1 || defsB.Length > 0 then  defsA else
                            match List.headAndTail (List.rev defsA) with
                            | SynModuleDecl.DoExpr(_,exp,_), rest -> (rest |> List.rev) @ (fsiDynamicCompiler.BuildItBinding exp)
                            | _ -> defsA

                Some (IDefns(defsA,m)),Some (IDefns(defsB,m)),istate

        match action, lastResult with
          | None, Some prev -> assert(nextAction.IsNone); istate, prev
          | None,_ -> assert(nextAction.IsNone); istate, Completed None
          | Some action, _ ->
              let istate,cont = ExecInteraction (tcConfig, istate, action)
              match cont with
                | Completed _                  -> execParsedInteractions (tcConfig, istate, nextAction) (Some cont)
                | CompletedWithReportedError e -> istate,CompletedWithReportedError e             (* drop nextAction on error *)
                | EndOfFile                    -> istate,defaultArg lastResult (Completed None)   (* drop nextAction on EOF *)
                | CtrlC                        -> istate,CtrlC                                    (* drop nextAction on CtrlC *)

    /// Execute a single parsed interaction on the parser/execute thread.
    let mainThreadProcessAction action istate =         
        try 
            let tcConfig = TcConfig.Create(tcConfigB,validate=false)
#if DYNAMIC_CODE_EMITS_INTERRUPT_CHECKS
            Microsoft.FSharp.Silverlight.ResumeThread(Threading.Thread.CurrentThread.ManagedThreadId)
            action tcConfig istate
        with
        | :? ThreadAbortException ->
           (istate,CtrlC)
        |  e ->
           stopProcessingRecovery e range0;
           istate,CompletedWithReportedError e
#else                                   
            if !progress then fprintfn fsiConsoleOutput.Out "In mainThreadProcessAction...";                  
            fsiInterruptController.InterruptAllowed <- InterruptCanRaiseException;
            let res = action tcConfig istate
            fsiInterruptController.ClearInterruptRequest()
            fsiInterruptController.InterruptAllowed <- InterruptIgnored;
            res
        with
        | :? ThreadAbortException ->
           fsiInterruptController.ClearInterruptRequest()
           fsiInterruptController.InterruptAllowed <- InterruptIgnored;
           (try Thread.ResetAbort() with _ -> ());
           (istate,CtrlC)
        |  e ->
           fsiInterruptController.ClearInterruptRequest()
           fsiInterruptController.InterruptAllowed <- InterruptIgnored;
           stopProcessingRecovery e range0;
           istate, CompletedWithReportedError e
#endif

    let mainThreadProcessParsedInteractions (action, istate) = 
      istate |> mainThreadProcessAction (fun tcConfig istate ->
        execParsedInteractions (tcConfig, istate, action) None)

    let parseExpression (tokenizer:LexFilter.LexFilter) =
        reusingLexbufForParsing tokenizer.LexBuffer (fun () ->
            Parser.typedSeqExprEOF tokenizer.Lexer tokenizer.LexBuffer)
  
//    let parseType (tokenizer:LexFilter.LexFilter) =
//        reusingLexbufForParsing tokenizer.LexBuffer (fun () ->
//            Parser.typEOF tokenizer.Lexer tokenizer.LexBuffer)
  
    let mainThreadProcessParsedExpression (expr, istate) = 
      istate |> InteractiveCatch (fun istate ->
        istate |> mainThreadProcessAction (fun _tcConfig istate ->
          fsiDynamicCompiler.EvalParsedExpression(istate, expr)  )) 

    let commitResult (istate, result) =
        match result with
        | FsiInteractionStepStatus.CtrlC -> raise (OperationCanceledException())
        | FsiInteractionStepStatus.EndOfFile -> failwith "End of input"
        | FsiInteractionStepStatus.Completed res -> 
            setCurrState istate
            res
        | FsiInteractionStepStatus.CompletedWithReportedError e -> 
            raise (System.Exception("Evaluation failed", e))

    /// Parse then process one parsed interaction.  
    ///
    /// During normal execution, this initially runs on the parser
    /// thread, then calls runCodeOnMainThread when it has completed 
    /// parsing and needs to typecheck and execute a definition. This blocks the parser thread
    /// until execution has competed on the GUI thread.
    ///
    /// During processing of startup scripts, this runs on the main thread.
    ///
    /// This is blocking: it reads until one chunk of input have been received, unless IsPastEndOfStream is true
    member __.ParseAndExecOneSetOfInteractionsFromLexbuf (runCodeOnMainThread, istate:FsiDynamicCompilerState, tokenizer:LexFilter.LexFilter) =

        if tokenizer.LexBuffer.IsPastEndOfStream then 
            let stepStatus = 
                if fsiInterruptController.FsiInterruptStdinState = StdinEOFPermittedBecauseCtrlCRecentlyPressed then 
                    fsiInterruptController.FsiInterruptStdinState <- StdinNormal; 
                    CtrlC
                else 
                    EndOfFile
            istate,stepStatus

        else 

            fsiConsolePrompt.Print();
            istate |> InteractiveCatch (fun istate -> 
                if !progress then fprintfn fsiConsoleOutput.Out "entering ParseInteraction...";

                // Parse the interaction. When FSI.EXE is waiting for input from the console the 
                // parser thread is blocked somewhere deep this call. 
                let action  = ParseInteraction tokenizer

                if !progress then fprintfn fsiConsoleOutput.Out "returned from ParseInteraction...calling runCodeOnMainThread...";

                // After we've unblocked and got something to run we switch 
                // over to the run-thread (e.g. the GUI thread) 
                let res = istate  |> runCodeOnMainThread (fun istate -> mainThreadProcessParsedInteractions (action, istate)) 

                if !progress then fprintfn fsiConsoleOutput.Out "Just called runCodeOnMainThread, res = %O..." res;
                res)
        
    member __.CurrentState = currState

    /// Perform an "include" on a script file (i.e. a script file specified on the command line)
    member processor.EvalIncludedScript (istate, sourceFile, m) =
        let tcConfig = TcConfig.Create(tcConfigB, validate=false)
        // Resolve the filename to an absolute filename
        let sourceFile = tcConfig.ResolveSourceFile(m,sourceFile,tcConfig.implicitIncludeDir) 
        // During the processing of the file, further filenames are 
        // resolved relative to the home directory of the loaded file.
        WithImplicitHome (tcConfigB, directoryName sourceFile)  (fun () ->
              // An included script file may contain maybe several interaction blocks.
              // We repeatedly parse and process these, until an error occurs.
                let tokenizer = fsiStdinLexerProvider.CreateIncludedScriptLexer sourceFile
                let rec run istate =
                    let istate,cont = processor.ParseAndExecOneSetOfInteractionsFromLexbuf ((fun f istate -> f istate), istate, tokenizer)
                    match cont with Completed _ -> run istate | _ -> istate,cont 

                let istate,cont = run istate 

                match cont with
                | Completed _ -> failwith "EvalIncludedScript: Completed expected to have relooped"
                | CompletedWithReportedError e -> istate,CompletedWithReportedError e
                | EndOfFile -> istate,Completed None// here file-EOF is normal, continue required 
                | CtrlC     -> istate,CtrlC
          )


    /// Load the source files, one by one. Called on the main thread.
    member processor.EvalIncludedScripts (istate, sourceFiles) =
      match sourceFiles with
        | [] -> istate
        | sourceFile :: moreSourceFiles ->
            // Catch errors on a per-file basis, so results/bindings from pre-error files can be kept.
            let istate,cont = InteractiveCatch (fun istate -> processor.EvalIncludedScript (istate, sourceFile, rangeStdin)) istate
            match cont with
              | Completed _                -> processor.EvalIncludedScripts (istate, moreSourceFiles)
              | CompletedWithReportedError _ -> istate // do not process any more files              
              | CtrlC                      -> istate // do not process any more files 
              | EndOfFile                  -> assert false; istate // This is unexpected. EndOfFile is replaced by Completed in the called function 


    member processor.LoadInitialFiles () =
        /// Consume initial source files in chunks of scripts or non-scripts
        let rec consume istate sourceFiles =
            match sourceFiles with
            | [] -> istate
            | (_,isScript1) :: _ -> 
                let sourceFiles,rest = List.takeUntil (fun (_,isScript2) -> isScript1 <> isScript2) sourceFiles 
                let sourceFiles = List.map fst sourceFiles 
                let istate = 
                    if isScript1 then 
                        processor.EvalIncludedScripts (istate, sourceFiles)
                    else 
                        istate |> InteractiveCatch (fun istate -> fsiDynamicCompiler.EvalSourceFiles(istate, rangeStdin, sourceFiles, lexResourceManager), Completed None) |> fst 
                consume istate rest 

        setCurrState (consume currState fsiOptions.SourceFiles)

        if nonNil fsiOptions.SourceFiles then 
            fsiConsolePrompt.PrintAhead(); // Seems required. I expected this could be deleted. Why not?

    /// Send a dummy interaction through F# Interactive, to ensure all the most common code generation paths are 
    /// JIT'ed and ready for use.
    member __.LoadDummyInteraction() =
        setCurrState (currState |> InteractiveCatch (fun istate ->  fsiDynamicCompiler.EvalParsedDefinitions (istate, true, false, []), Completed None) |> fst)
        
    member __.EvalInteraction(sourceText) =
        use _unwind1 = ErrorLogger.PushThreadBuildPhaseUntilUnwind(ErrorLogger.BuildPhase.Interactive)
        use _unwind2 = ErrorLogger.PushErrorLoggerPhaseUntilUnwind(fun _ -> errorLogger)
        use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
        let lexbuf = UnicodeLexing.StringAsLexbuf(sourceText)
        let tokenizer = fsiStdinLexerProvider.CreateBufferLexer("input.fsx", lexbuf)
        currState 
        |> InteractiveCatch(fun istate ->
            let expr = ParseInteraction tokenizer
            mainThreadProcessParsedInteractions (expr, istate) )
        |> commitResult
        |> ignore

    member this.EvalScript(scriptPath) =
        // Todo: this runs the script as expected but errors are displayed one line to far in debugger
        let sourceText = sprintf "#load @\"%s\" " scriptPath
        this.EvalInteraction sourceText

    member __.EvalExpression(sourceText) =
        use _unwind1 = ErrorLogger.PushThreadBuildPhaseUntilUnwind(ErrorLogger.BuildPhase.Interactive)
        use _unwind2 = ErrorLogger.PushErrorLoggerPhaseUntilUnwind(fun _ -> errorLogger)
        use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
        let lexbuf = UnicodeLexing.StringAsLexbuf(sourceText)
        let tokenizer = fsiStdinLexerProvider.CreateBufferLexer("input.fsx", lexbuf)
        currState 
        |> InteractiveCatch(fun istate ->
            let expr = parseExpression tokenizer 
            let m = expr.Range
            // Make this into "(); expr" to suppress generalization and compilation-as-function
            let exprWithSeq = SynExpr.Sequential(SequencePointInfoForSeq.SuppressSequencePointOnStmtOfSequential,true,SynExpr.Const(SynConst.Unit,m.StartRange), expr, m)
            mainThreadProcessParsedExpression (exprWithSeq, istate))
        |> commitResult

    member __.PartialAssemblySignatureUpdated = event.Publish

    /// Start the background thread used to read the input reader and/or console
    ///
    /// This is the main stdin loop, running on the stdinReaderThread.
    /// 
    // We run the actual computations for each action on the main GUI thread by using
    // mainForm.Invoke to pipe a message back through the form's main event loop. (The message 
    // is a delegate to execute on the main Thread)
    //
    member processor.StartStdinReadAndProcessThread () = 

      if !progress then fprintfn fsiConsoleOutput.Out "creating stdinReaderThread";

      let stdinReaderThread = 
        new Thread(new ThreadStart(fun () ->
            InstallErrorLoggingOnThisThread errorLogger // FSI error logging on stdinReaderThread, e.g. parse errors.
            use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
            try 
                try 
                  let initialTokenizer = fsiStdinLexerProvider.CreateStdinLexer()
                  if !progress then fprintfn fsiConsoleOutput.Out "READER: stdin thread started...";

                  // Delay until we've peeked the input or read the entire first line
                  fsiStdinLexerProvider.ConsoleInput.WaitForInitialConsoleInput()
                  
                  if !progress then fprintfn fsiConsoleOutput.Out "READER: stdin thread got first line...";

                  // Keep going until EndOfFile on the inReader or console
                  let rec loop currTokenizer = 

                      let istateNew,contNew = 
                          processor.ParseAndExecOneSetOfInteractionsFromLexbuf (runCodeOnEventLoop, currState, currTokenizer)   

                      setCurrState istateNew

                      match contNew with 
                      | EndOfFile -> ()
                      | CtrlC -> loop (fsiStdinLexerProvider.CreateStdinLexer())   // After each interrupt, restart to a brand new tokenizer
                      | CompletedWithReportedError _ 
                      | Completed _ -> loop currTokenizer

                  loop initialTokenizer


                  if !progress then fprintfn fsiConsoleOutput.Out "- READER: Exiting stdinReaderThread";  

                with e -> stopProcessingRecovery e range0;

            finally 
                if !progress then fprintfn fsiConsoleOutput.Out "- READER: Exiting process because of failure/exit on  stdinReaderThread";  
                // REVIEW: On some flavors of Mono, calling exit may freeze the process if we're using the WinForms event handler
                // Basically, on Mono 2.6.3, the GUI thread may be left dangling on exit.  At that point:
                //   -- System.Environment.Exit will cause the process to stop responding
                //   -- Calling Application.Exit() will leave the GUI thread up and running, creating a Zombie process
                //   -- Calling Abort() on the Main thread or the GUI thread will have no effect, and the process will remain unresponsive
                // Also, even the the GUI thread is up and running, the WinForms event loop will be listed as closed
                // In this case, killing the process is harmless, since we've already cleaned up after ourselves and FSI is responding
                // to an error.  (CTRL-C is handled elsewhere.) 
                // We'll only do this if we're running on Mono, "--gui" is specified and our input is piped in from stdin, so it's still
                // fairly constrained.
                if runningOnMono && fsiOptions.Gui then
                    System.Environment.ExitCode <- 1
                    Process.GetCurrentProcess().Kill()
                else
                    exit 1

        ),Name="StdinReaderThread")

      if !progress then fprintfn fsiConsoleOutput.Out "MAIN: starting stdin thread..."
      stdinReaderThread.Start()

    member __.CompletionsForPartialLID (istate, prefix:string) =
        let lid,stem =
            if prefix.IndexOf(".",StringComparison.Ordinal) >= 0 then
                let parts = prefix.Split('.')
                let n = parts.Length
                Array.sub parts 0 (n-1) |> Array.toList,parts.[n-1]
            else
                [],prefix   

        let tcState = istate.tcState
        let amap = istate.tcImports.GetImportMap()
        let infoReader = new Infos.InfoReader(istate.tcGlobals,amap)
        let ncenv = new NameResolver(istate.tcGlobals,amap,infoReader,FakeInstantiationGenerator)
        let ad = tcState.TcEnvFromImpls.AccessRights
        let nenv = tcState.TcEnvFromImpls.NameEnv

        let nItems = NameResolution.ResolvePartialLongIdent ncenv nenv (ConstraintSolver.IsApplicableMethApprox istate.tcGlobals amap rangeStdin) rangeStdin ad lid false
        let names  = nItems |> List.map (fun d -> d.DisplayName) 
        let names  = names |> List.filter (fun name -> name.StartsWith(stem,StringComparison.Ordinal)) 
        names

    member __.ParseAndCheckInteraction (checker, istate, text:string) =
        let tcConfig = TcConfig.Create(tcConfigB,validate=false)

        let loadClosure = None
        let fsiInteractiveChecker = FsiInteractiveChecker(checker, tcConfig, istate.tcGlobals, istate.tcImports, istate.tcState, loadClosure)
        fsiInteractiveChecker.ParseAndCheckInteraction(text)


//----------------------------------------------------------------------------
// Server mode:
//----------------------------------------------------------------------------

let internal SpawnThread name f =
    let th = new Thread(new ThreadStart(f),Name=name)
    th.IsBackground <- true;
    th.Start()

let internal SpawnInteractiveServer 
                           (fsi: FsiEvaluationSessionHostConfig,
                            fsiOptions : FsiCommandLineOptions, 
                            fsiConsoleOutput:  FsiConsoleOutput) =   
    //printf "Spawning fsi server on channel '%s'" !fsiServerName;
    SpawnThread "ServerThread" (fun () ->
         use _scope = SetCurrentUICultureForThread fsiOptions.FsiLCID
         try
             fsi.StartServer(fsiOptions.FsiServerName)
         with e ->
             fprintfn fsiConsoleOutput.Error "%s" (FSIstrings.SR.fsiExceptionRaisedStartingServer(e.ToString())))

/// Repeatedly drive the event loop (e.g. Application.Run()) but catching ThreadAbortException and re-running.
///
/// This gives us a last chance to catch an abort on the main execution thread.
let internal DriveFsiEventLoop (fsi: FsiEvaluationSessionHostConfig, fsiConsoleOutput: FsiConsoleOutput) = 
    let rec runLoop() = 
        if !progress then fprintfn fsiConsoleOutput.Out "GUI thread runLoop";
        let restart = 
            try 
              // BLOCKING POINT: The GUI Thread spends most (all) of its time this event loop
              if !progress then fprintfn fsiConsoleOutput.Out "MAIN:  entering event loop...";
              fsi.EventLoopRun()
            with
            |  :? ThreadAbortException ->
              // If this TAE handler kicks it's almost certainly too late to save the
              // state of the process - the state of the message loop may have been corrupted 
              fsiConsoleOutput.uprintnfn "%s" (FSIstrings.SR.fsiUnexpectedThreadAbortException());  
              (try Thread.ResetAbort() with _ -> ());
              true
              // Try again, just case we can restart
            | e -> 
              stopProcessingRecovery e range0;
              true
              // Try again, just case we can restart
        if !progress then fprintfn fsiConsoleOutput.Out "MAIN:  exited event loop...";
        if restart then runLoop() 

    runLoop();

/// The primary type, representing a full F# Interactive session, reading from the given
/// text input, writing to the given text output and error writers.
type FsiEvaluationSession (fsi: FsiEvaluationSessionHostConfig, argv:string[], inReader:TextReader, outWriter:TextWriter, errorWriter: TextWriter, fsiCollectible: bool) = 
#if DYNAMIC_CODE_REWRITES_CONSOLE_WRITE
    do
        Microsoft.FSharp.Core.Printf.setWriter outWriter
        Microsoft.FSharp.Core.Printf.setError errorWriter
#endif
    do if not runningOnMono then Lib.UnmanagedProcessExecutionOptions.EnableHeapTerminationOnCorruption() (* SDL recommendation *)
    // See Bug 735819 
    let lcidFromCodePage = 
#if LIMITED_CONSOLE
#else
        if (Console.OutputEncoding.CodePage <> 65001) &&
           (Console.OutputEncoding.CodePage <> Thread.CurrentThread.CurrentUICulture.TextInfo.OEMCodePage) &&
           (Console.OutputEncoding.CodePage <> Thread.CurrentThread.CurrentUICulture.TextInfo.ANSICodePage) then
                Thread.CurrentThread.CurrentUICulture <- new CultureInfo("en-US")
                Some 1033
        else
#endif        
            None

    let timeReporter = FsiTimeReporter(outWriter)

    //----------------------------------------------------------------------------
    // Console coloring
    //----------------------------------------------------------------------------

    // Testing shows "console coloring" is broken on some Mono configurations (e.g. Mono 2.4 Suse LiveCD).
    // To support fsi usage, the console coloring is switched off by default on Mono.
    do if runningOnMono then enableConsoleColoring <- false 

    do SetUninitializedErrorLoggerFallback AssertFalseErrorLogger
    

    //----------------------------------------------------------------------------
    // tcConfig - build the initial config
    //----------------------------------------------------------------------------

#if SILVERLIGHT
    let defaultFSharpBinariesDir = "."
    let currentDirectory = "."
#else    
    let defaultFSharpBinariesDir = System.AppDomain.CurrentDomain.BaseDirectory
    let currentDirectory = Directory.GetCurrentDirectory()
#endif

    let tcConfigB = 
        TcConfigBuilder.CreateNew(defaultFSharpBinariesDir, 
                                  true, // long running: optimizeForMemory 
                                  currentDirectory,isInteractive=true, 
                                  isInvalidationSupported=false)
    let tcConfigP = TcConfigProvider.BasedOnMutableBuilder(tcConfigB)
    do tcConfigB.resolutionEnvironment <- MSBuildResolver.RuntimeLike // See Bug 3608
    do tcConfigB.useFsiAuxLib <- fsi.UseFsiAuxLib

    // Preset: --optimize+ -g --tailcalls+ (see 4505)
    do SetOptimizeSwitch tcConfigB OptionSwitch.On
    do SetDebugSwitch    tcConfigB (Some "pdbonly") OptionSwitch.On
    do SetTailcallSwitch tcConfigB OptionSwitch.On    

#if FX_ATLEAST_40
    // set platform depending on whether the current process is a 64-bit process.
    // BUG 429882 : FsiAnyCPU.exe issues warnings (x64 v MSIL) when referencing 64-bit assemblies
    do tcConfigB.platform <- if System.Environment.Is64BitProcess then Some AMD64 else Some X86
#endif

    let fsiStdinSyphon = new FsiStdinSyphon(errorWriter)
    let fsiConsoleOutput = FsiConsoleOutput(tcConfigB, outWriter, errorWriter)

    let errorLogger = ErrorLoggerThatStopsOnFirstError(tcConfigB, fsiStdinSyphon, fsiConsoleOutput)

    do InstallErrorLoggingOnThisThread errorLogger // FSI error logging on main thread.

    let updateBannerText() =
      tcConfigB.productNameForBannerText <- FSIstrings.SR.fsiProductName(FSharpEnvironment.DotNetBuildString)
  
    do updateBannerText() // setting the correct banner so that 'fsi -?' display the right thing

    let fsiOptions       = FsiCommandLineOptions(fsi, argv, tcConfigB, fsiConsoleOutput)
    let fsiConsolePrompt = FsiConsolePrompt(fsiOptions, fsiConsoleOutput)

    // Check if we have a codepage from the console
    do
      match fsiOptions.FsiLCID with
      | Some _ -> ()
      | None -> tcConfigB.lcid <- lcidFromCodePage

    // Set the ui culture
    do 
      match fsiOptions.FsiLCID with
      | Some(n) -> Thread.CurrentThread.CurrentUICulture <- new CultureInfo(n)
      | None -> ()

    do 
      try 
          SetServerCodePages fsiOptions 
      with e -> 
          warning(e)

    do 
      updateBannerText() // resetting banner text after parsing options

      if tcConfigB.showBanner then 
          fsiOptions.ShowBanner()

    do fsiConsoleOutput.uprintfn ""

    // When no source files to load, print ahead prompt here 
    do if isNil  fsiOptions.SourceFiles then 
        fsiConsolePrompt.PrintAhead()       


    let fsiConsoleInput = FsiConsoleInput(fsi, fsiOptions, inReader, outWriter)

    let (tcGlobals,frameworkTcImports,nonFrameworkResolutions,unresolvedReferences) = 
        try 
            let tcConfig = tcConfigP.Get()
            IncrementalFSharpBuild.GetFrameworkTcImports tcConfig
        with e -> 
            stopProcessingRecovery e range0; failwithf "Error creating evaluation session: %A" e

    let tcImports =  
      try 
          TcImports.BuildNonFrameworkTcImports(None, tcConfigP,tcGlobals,frameworkTcImports,nonFrameworkResolutions,unresolvedReferences)
      with e -> 
          stopProcessingRecovery e range0; failwithf "Error creating evaluation session: %A" e

    let ilGlobals  = tcGlobals.ilg

    let niceNameGen = NiceNameGenerator() 

    // Share intern'd strings across all lexing/parsing
    let lexResourceManager = new Lexhelp.LexResourceManager() 

    /// The lock stops the type checker running at the same time as the server intellisense implementation.
    let tcLockObject = box 7 // any new object will do
    
    let resolveType (aref: ILAssemblyRef) = 
#if EXTENSIONTYPING
        match tcImports.TryFindProviderGeneratedAssemblyByName aref.Name with
        | Some assembly -> Some (Choice2Of2 assembly)
        | None -> 
#endif
        match tcImports.TryFindExistingFullyQualifiedPathFromAssemblyRef aref with
        | Some resolvedPath -> Some (Choice1Of2 resolvedPath)
        | None -> None
          
       
    let fsiDynamicCompiler = FsiDynamicCompiler(fsi, timeReporter, tcConfigB, tcLockObject, errorLogger, outWriter, tcImports, tcGlobals, ilGlobals, fsiOptions, fsiConsoleOutput, fsiCollectible, niceNameGen, resolveType) 
    
    let fsiInterruptController = FsiInterruptController(fsiOptions, fsiConsoleOutput) 
    
    let uninstallMagicAssemblyResolution = MagicAssemblyResolution.Install(tcConfigB, tcImports, fsiDynamicCompiler, fsiConsoleOutput)
    
    /// This reference cell holds the most recent interactive state 
    let initialInteractiveState = fsiDynamicCompiler.GetInitialInteractiveState ()
      
    let fsiStdinLexerProvider = FsiStdinLexerProvider(tcConfigB, fsiStdinSyphon, fsiConsoleInput, fsiConsoleOutput, fsiOptions, lexResourceManager, errorLogger)

    let fsiInteractionProcessor = FsiInteractionProcessor(fsi, tcConfigB, errorLogger, fsiOptions, fsiDynamicCompiler, fsiConsolePrompt, fsiConsoleOutput, fsiInterruptController, fsiStdinLexerProvider, lexResourceManager, initialInteractiveState) 

    /// The single, global interactive checker that can be safely used in conjunction with other operations
    /// on the FsiEvaluationSession.  
    let checker = InteractiveChecker.Create()

    interface IDisposable with 
        member x.Dispose() = 
            (tcImports :> IDisposable).Dispose()
            uninstallMagicAssemblyResolution.Dispose()

    /// Load the dummy interaction, load the initial files, and,
    /// if interacting, start the background thread to read the standard input.
    member x.Interrupt() = fsiInterruptController.Interrupt()

    /// A host calls this to get the completions for a long identifier, e.g. in the console
    member x.GetCompletions(longIdent) = 
        fsiInteractionProcessor.CompletionsForPartialLID (fsiInteractionProcessor.CurrentState, longIdent)  |> Seq.ofList

    member x.ParseAndCheckInteraction(code) = 
        fsiInteractionProcessor.ParseAndCheckInteraction (checker.ReactorOps, fsiInteractionProcessor.CurrentState, code)  

    member x.CurrentPartialAssemblySignature = 
        fsiDynamicCompiler.CurrentPartialAssemblySignature (fsiInteractionProcessor.CurrentState)  

    member x.DynamicAssembly = 
        fsiDynamicCompiler.DynamicAssembly
    /// A host calls this to determine if the --gui parameter is active
    member x.IsGui = fsiOptions.Gui 

    /// A host calls this to get the active language ID if provided by fsi-server-lcid
    member x.LCID = fsiOptions.FsiLCID

    /// A host calls this to report an unhandled exception in a standard way, e.g. an exception on the GUI thread gets printed to stderr
    member x.ReportUnhandledException exn = x.ReportUnhandledExceptionSafe true exn

    member x.ReportUnhandledExceptionSafe isFromThreadException (exn:exn) = 
             fsi.EventLoopInvoke (
                fun () ->          
                    fprintfn fsiConsoleOutput.Error "%s" (exn.ToString())
                    errorLogger.SetError()
                    try 
                        errorLogger.AbortOnError() 
                    with StopProcessing _ -> 
                        // BUG 664864: Watson Clr20r3 across buckets with: Application FSIAnyCPU.exe from Dev11 RTM; Exception AE251Y0L0P2WC0QSWDZ0E2IDRYQTDSVB; FSIANYCPU.NI.EXE!Microsoft.FSharp.Compiler.Interactive.Shell+threadException
                        // reason: some window that use System.Windows.Forms.DataVisualization types (possible FSCharts) was created in FSI.
                        // at some moment one chart has raised InvalidArgumentException from OnPaint, this exception was intercepted by the code in higher layer and 
                        // passed to Application.OnThreadException. FSI has already attached its own ThreadException handler, inside it will log the original error
                        // and then raise StopProcessing exception to unwind the stack (and possibly shut down current Application) and get to DriveFsiEventLoop.
                        // DriveFsiEventLoop handles StopProcessing by suppressing it and restarting event loop from the beginning.
                        // This schema works almost always except when FSI is started as 64 bit process (FsiAnyCpu) on Windows 7.

                        // http://msdn.microsoft.com/en-us/library/windows/desktop/ms633573(v=vs.85).aspx
                        // Remarks:
                        // If your application runs on a 32-bit version of Windows operating system, uncaught exceptions from the callback 
                        // will be passed onto higher-level exception handlers of your application when available. 
                        // The system then calls the unhandled exception filter to handle the exception prior to terminating the process. 
                        // If the PCA is enabled, it will offer to fix the problem the next time you run the application.
                        // However, if your application runs on a 64-bit version of Windows operating system or WOW64, 
                        // you should be aware that a 64-bit operating system handles uncaught exceptions differently based on its 64-bit processor architecture, 
                        // exception architecture, and calling convention. 
                        // The following table summarizes all possible ways that a 64-bit Windows operating system or WOW64 handles uncaught exceptions.
                        // 1. The system suppresses any uncaught exceptions.
                        // 2. The system first terminates the process, and then the Program Compatibility Assistant (PCA) offers to fix it the next time 
                        // you run the application. You can disable the PCA mitigation by adding a Compatibility section to the application manifest.
                        // 3. The system calls the exception filters but suppresses any uncaught exceptions when it leaves the callback scope, 
                        // without invoking the associated handlers.
                        // Behavior type 2 only applies to the 64-bit version of the Windows 7 operating system.
                        
                        // NOTE: tests on Win8 box showed that 64 bit version of the Windows 8 always apply type 2 behavior

                        // Effectively this means that when StopProcessing exception is raised from ThreadException callback - it won't be intercepted in DriveFsiEventLoop.
                        // Instead it will be interpreted as unhandled exception and crash the whole process.

                        // FIX: detect if current process in 64 bit running on Windows 7 or Windows 8 and if yes - swallow the StopProcessing and ScheduleRestart instead.
                        // Visible behavior should not be different, previosuly exception unwinds the stack and aborts currently running Application.
                        // After that it will be intercepted and suppressed in DriveFsiEventLoop.
                        // Now we explicitly shut down Application so after execution of callback will be completed the control flow 
                        // will also go out of WinFormsEventLoop.Run and again get to DriveFsiEventLoop => restart the loop. I'd like the fix to be  as conservative as possible
                        // so we use special case for problematic case instead of just always scheduling restart.

                        // http://msdn.microsoft.com/en-us/library/windows/desktop/ms724832(v=vs.85).aspx
                        let os = Environment.OSVersion
                        // Win7 6.1
                        let isWindows7 = os.Version.Major = 6 && os.Version.Minor = 1
                        // Win8 6.2
                        let isWindows8Plus = os.Version >= Version(6, 2, 0, 0)
                        if isFromThreadException && ((isWindows7 && Environment.Is64BitProcess) || (Environment.Is64BitOperatingSystem && isWindows8Plus))
#if DEBUG
                            // for debug purposes
                            && Environment.GetEnvironmentVariable("FSI_SCHEDULE_RESTART_WITH_ERRORS") = null
#endif
                        then
                            fsi.EventLoopScheduleRestart()
                        else
                            reraise()
                )

    member x.PartialAssemblySignatureUpdated = fsiInteractionProcessor.PartialAssemblySignatureUpdated

    member x.InteractiveChecker = checker

    member x.EvalExpression(sourceText) = 
      fsiInteractionProcessor.EvalExpression(sourceText)

    member x.EvalInteraction(sourceText) : unit =
      fsiInteractionProcessor.EvalInteraction(sourceText)

    member x.EvalScript(scriptPath) : unit =
      fsiInteractionProcessor.EvalScript(scriptPath)

    /// Performs these steps:
    ///    - Load the dummy interaction, if any
    ///    - Set up exception handling, if any
    ///    - Load the initial files, if any
    ///    - Start the background thread to read the standard input, if any
    ///    - Sit in the GUI event loop indefinitely, if needed
    ///
    /// This method only returns after "exit". The method repeatedly calls the event loop and
    /// the thread may be subject to Thread.Abort() signals if Interrupt() is used, giving rise 
    /// to internal ThreadAbortExceptions.
    ///
    /// A background thread is started by this thread to read from the inReader and/or console reader.

    [<CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2004:RemoveCallsToGCKeepAlive")>]
    member x.Run() = 
        progress := condition "FSHARP_INTERACTIVE_PROGRESS"
    
        if not runningOnMono && fsiOptions.IsInteractiveServer then 
            SpawnInteractiveServer (fsi, fsiOptions, fsiConsoleOutput)

        use unwindBuildPhase = PushThreadBuildPhaseUntilUnwind (BuildPhase.Interactive)

        if fsiOptions.Interact then 
            // page in the type check env 
            fsiInteractionProcessor.LoadDummyInteraction()
            if !progress then fprintfn fsiConsoleOutput.Out "MAIN: InstallKillThread!";
            
            // Compute how long to pause before a ThreadAbort is actually executed.
            // A somewhat arbitrary choice.
            let pauseMilliseconds = (if fsiOptions.Gui then 400 else 100)

            // Request that ThreadAbort interrupts be performed on this (current) thread
            fsiInterruptController.InstallKillThread(Thread.CurrentThread, pauseMilliseconds) 
            if !progress then fprintfn fsiConsoleOutput.Out "MAIN: got initial state, creating form";

            // Route background exceptions to the exception handlers
            AppDomain.CurrentDomain.UnhandledException.Add (fun args -> 
                match args.ExceptionObject with 
                | :? System.Exception as err -> x.ReportUnhandledExceptionSafe false err 
                | _ -> ())

            fsiInteractionProcessor.LoadInitialFiles()

            fsiInteractionProcessor.StartStdinReadAndProcessThread()            

            DriveFsiEventLoop (fsi, fsiConsoleOutput )

        else // not interact
            if !progress then fprintfn fsiConsoleOutput.Out "Run: not interact, loading intitial files..."
            fsiInteractionProcessor.LoadInitialFiles()
            if !progress then fprintfn fsiConsoleOutput.Out "Run: done..."
            exit (min errorLogger.ErrorCount 1)

        // The Ctrl-C exception handler that we've passed to native code has
        // to be explicitly kept alive.
        GC.KeepAlive fsiInterruptController.EventHandlers


    new (fsiConfig, argv, inReader, outWriter, errorWriter) = 
        new FsiEvaluationSession (fsiConfig, argv, inReader, outWriter, errorWriter, fsiCollectible=false)

    static member Create(fsiConfig, argv, inReader, outWriter, errorWriter, ?collectible) = 
        new FsiEvaluationSession(fsiConfig, argv, inReader, outWriter, errorWriter, defaultArg collectible false)

    static member GetDefaultConfiguration(fsiObj:obj) =  FsiEvaluationSession.GetDefaultConfiguration(fsiObj, true)
    static member GetDefaultConfiguration(fsiObj:obj, useFsiAuxLib) = 
    
        let rec tryFindMember (name : string) (memberType : MemberTypes) (declaringType : Type) =
            match declaringType.GetMember(name, memberType, BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic) with
            | [||] -> declaringType.GetInterfaces() |> Array.tryPick (tryFindMember name memberType)
            | [|m|] -> Some m
            | _ -> raise <| new AmbiguousMatchException(sprintf "Ambiguous match for member '%s'" name)

        let getInstanceProperty (obj:obj) (nm:string) =
            let p = (tryFindMember nm MemberTypes.Property <| obj.GetType()).Value :?> PropertyInfo
            p.GetValue(obj, [||]) |> unbox

        let setInstanceProperty (obj:obj) (nm:string) (v:obj) =
            let p = (tryFindMember nm MemberTypes.Property <| obj.GetType()).Value :?> PropertyInfo
            p.SetValue(obj, v, [||]) |> unbox

        let callInstanceMethod0 (obj:obj) (typeArgs : Type []) (nm:string) =
            let m = (tryFindMember nm MemberTypes.Method <| obj.GetType()).Value :?> MethodInfo
            let m = match typeArgs with [||] -> m | _ -> m.MakeGenericMethod(typeArgs)
            m.Invoke(obj, [||]) |> unbox

        let callInstanceMethod1 (obj:obj) (typeArgs : Type []) (nm:string) (v:obj) =
            let m = (tryFindMember nm MemberTypes.Method <| obj.GetType()).Value :?> MethodInfo
            let m = match typeArgs with [||] -> m | _ -> m.MakeGenericMethod(typeArgs)
            m.Invoke(obj, [|v|]) |> unbox

        // We want to avoid modifying FSharp.Compiler.Interactive.Settings to avoid republishing that DLL.
        // So we access these via reflection
        { // Connect the configuration through to the 'fsi' object from FSharp.Compiler.Interactive.Settings
            new FsiEvaluationSessionHostConfig () with 
              member __.FormatProvider = getInstanceProperty fsiObj "FormatProvider"
              member __.FloatingPointFormat = getInstanceProperty fsiObj "FloatingPointFormat"
              member __.AddedPrinters = getInstanceProperty fsiObj "AddedPrinters"
              member __.ShowDeclarationValues = getInstanceProperty fsiObj "ShowDeclarationValues"
              member __.ShowIEnumerable = getInstanceProperty fsiObj "ShowIEnumerable"
              member __.ShowProperties = getInstanceProperty fsiObj "ShowProperties"
              member __.PrintSize = getInstanceProperty fsiObj "PrintSize"
              member __.PrintDepth = getInstanceProperty fsiObj "PrintDepth"
              member __.PrintWidth = getInstanceProperty fsiObj "PrintWidth"
              member __.PrintLength = getInstanceProperty fsiObj "PrintLength"
              member __.ReportUserCommandLineArgs args = setInstanceProperty fsiObj "CommandLineArgs" args
              member __.StartServer(fsiServerName) =  failwith "--fsi-server not implemented in the default configuration"
              member __.EventLoopRun() = callInstanceMethod0 (getInstanceProperty fsiObj "EventLoop") [||] "Run"   
              member __.EventLoopInvoke(f : unit -> 'T) =  callInstanceMethod1 (getInstanceProperty fsiObj "EventLoop") [|typeof<'T>|] "Invoke" f
              member __.EventLoopScheduleRestart() = callInstanceMethod0 (getInstanceProperty fsiObj "EventLoop") [||] "ScheduleRestart"
              member __.UseFsiAuxLib = useFsiAuxLib
              member __.OptionalConsoleReadLine = None }


//-------------------------------------------------------------------------------
// If no "fsi" object for the configuration is specified, make the default
// configuration one which stores the settings in-process 

module Settings = 
    type IEventLoop =
        abstract Run : unit -> bool
        abstract Invoke : (unit -> 'T) -> 'T 
        abstract ScheduleRestart : unit -> unit
    
    // An implementation of IEventLoop suitable for the command-line console
    [<AutoSerializable(false)>]
    type internal SimpleEventLoop() = 
        let runSignal = new AutoResetEvent(false)
        let exitSignal = new AutoResetEvent(false)
        let doneSignal = new AutoResetEvent(false)
        let mutable queue = ([] : (unit -> obj) list)
        let mutable result = (None : obj option)
        let setSignal(signal : AutoResetEvent) = while not (signal.Set()) do Thread.Sleep(1); done
        let waitSignal signal = WaitHandle.WaitAll([| (signal :> WaitHandle) |]) |> ignore
        let waitSignal2 signal1 signal2 = 
            WaitHandle.WaitAny([| (signal1 :> WaitHandle); (signal2 :> WaitHandle) |])
        let mutable running = false
        let mutable restart = false
        interface IEventLoop with 
             member x.Run() =  
                 running <- true;
                 let rec run() = 
                     match waitSignal2 runSignal exitSignal with 
                     | 0 -> 
                         queue |> List.iter (fun f -> result <- try Some(f()) with _ -> None); 
                         setSignal doneSignal;
                         run()
                     | 1 -> 
                         running <- false;
                         restart
                     | _ -> run()
                 run();
             member x.Invoke(f : unit -> 'T) : 'T  = 
                 queue <- [f >> box];
                 setSignal runSignal;
                 waitSignal doneSignal
                 result.Value |> unbox
             member x.ScheduleRestart() = 
                 if running then 
                     restart <- true;
                     setSignal exitSignal
        interface System.IDisposable with 
             member x.Dispose() =
                         runSignal.Close();
                         exitSignal.Close();
                         doneSignal.Close();
                     


    [<Sealed>]
    type InteractiveSettings()  = 
        let mutable evLoop = (new SimpleEventLoop() :> IEventLoop)
        let mutable showIDictionary = true
        let mutable showDeclarationValues = true
#if SILVERLIGHT
        let mutable args : string[] = [| |]
#else
        let mutable args = Environment.GetCommandLineArgs()
#endif
        let mutable fpfmt = "g10"
        let mutable fp = (CultureInfo.InvariantCulture :> System.IFormatProvider)
        let mutable printWidth = 78
        let mutable printDepth = 100
        let mutable printLength = 100
        let mutable printSize = 10000
        let mutable showIEnumerable = true
        let mutable showProperties = true
        let mutable addedPrinters = []

        member self.FloatingPointFormat with get() = fpfmt and set v = fpfmt <- v
        member self.FormatProvider with get() = fp and set v = fp <- v
        member self.PrintWidth  with get() = printWidth and set v = printWidth <- v
        member self.PrintDepth  with get() = printDepth and set v = printDepth <- v
        member self.PrintLength  with get() = printLength and set v = printLength <- v
        member self.PrintSize  with get() = printSize and set v = printSize <- v
        member self.ShowDeclarationValues with get() = showDeclarationValues and set v = showDeclarationValues <- v
        member self.ShowProperties  with get() = showProperties and set v = showProperties <- v
        member self.ShowIEnumerable with get() = showIEnumerable and set v = showIEnumerable <- v
        member self.ShowIDictionary with get() = showIDictionary and set v = showIDictionary <- v
        member self.AddedPrinters with get() = addedPrinters and set v = addedPrinters <- v
        member self.CommandLineArgs with get() = args  and set v  = args <- v
        member self.AddPrinter(printer : 'T -> string) =
          addedPrinters <- Choice1Of2 (typeof<'T>, (fun (x:obj) -> printer (unbox x))) :: addedPrinters

        member self.EventLoop
           with get () = evLoop
           and set (x:IEventLoop)  = evLoop.ScheduleRestart(); evLoop <- x

        member self.AddPrintTransformer(printer : 'T -> obj) =
          addedPrinters <- Choice2Of2 (typeof<'T>, (fun (x:obj) -> printer (unbox x))) :: addedPrinters
    
    let fsi = InteractiveSettings()

type FsiEvaluationSession with 
    static member GetDefaultConfiguration() = 
        FsiEvaluationSession.GetDefaultConfiguration(Settings.fsi, false)

/// Defines a read-only input stream used to feed content to the hosted F# Interactive dynamic compiler.
[<AllowNullLiteral>]
type CompilerInputStream() = 
    inherit Stream()
    // Duration (in milliseconds) of the pause in the loop of waitForAtLeastOneByte. 
    let pauseDuration = 100

    // Queue of characters waiting to be read.
    let readQueue = new Queue<byte>()

    let  waitForAtLeastOneByte(count : int) =
        let rec loop() = 
            let attempt = 
                lock readQueue (fun () ->
                    let n = readQueue.Count
                    if (n >= 1) then 
                        let lengthToRead = if (n < count) then n else count
                        let ret = Array.zeroCreate lengthToRead
                        for i in 0 .. lengthToRead - 1 do
                            ret.[i] <- readQueue.Dequeue()
                        Some ret
                    else 
                        None)
            match attempt with 
            | None -> System.Threading.Thread.Sleep(pauseDuration); loop()
            | Some res -> res
        loop() 

    override x.CanRead = true 
    override x.CanWrite = false
    override x.CanSeek = false
    override x.Position with get() = raise (NotSupportedException()) and set _v = raise (NotSupportedException())
    override x.Length = raise (NotSupportedException()) 
    override x.Flush() = ()
    override x.Seek(_offset, _origin) = raise (NotSupportedException()) 
    override x.SetLength(_value) = raise (NotSupportedException()) 
    override x.Write(_buffer, _offset, _count) = raise (NotSupportedException("Cannot write to input stream")) 
    override x.Read(buffer, offset, count) = 
        let bytes = waitForAtLeastOneByte count
        Array.Copy(bytes, 0, buffer, offset, bytes.Length)
        bytes.Length

    /// Feeds content into the stream.
    member x.Add(str:string) =
        if (System.String.IsNullOrEmpty(str)) then () else

        lock readQueue (fun () -> 
            let bytes = System.Text.Encoding.UTF8.GetBytes(str)
            for i in 0 .. bytes.Length - 1 do
                readQueue.Enqueue(bytes.[i]))



/// Defines a write-only stream used to capture output of the hosted F# Interactive dynamic compiler.
[<AllowNullLiteral>]
type CompilerOutputStream()  =
    inherit Stream()
    // Queue of characters waiting to be read.
    let contentQueue = new Queue<byte>()
    let nyi() = raise (NotSupportedException())

    override x.CanRead = false
    override x.CanWrite = true
    override x.CanSeek = false
    override x.Position with get() = nyi() and set _v = nyi()
    override x.Length = nyi() 
    override x.Flush() = ()
    override x.Seek(_offset, _origin) = nyi() 
    override x.SetLength(_value) = nyi() 
    override x.Read(_buffer, _offset, _count) = raise (NotSupportedException("Cannot write to input stream")) 
    override x.Write(buffer, offset, count) = 
        let stop = offset + count
        if (stop > buffer.Length) then raise (ArgumentException("offset,count"))

        lock contentQueue (fun () -> 
            for i in offset .. stop - 1 do
                contentQueue.Enqueue(buffer.[i]))

    member x.Read() = 
        lock contentQueue (fun () -> 
            let n = contentQueue.Count
            if (n > 0) then 
                let bytes = Array.zeroCreate n
                for i in 0 .. n-1 do 
                    bytes.[i] <- contentQueue.Dequeue()   

                System.Text.Encoding.UTF8.GetString(bytes, 0, n)
            else
                "")

