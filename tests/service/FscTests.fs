
#if INTERACTIVE
#r "../../bin/v4.5/FSharp.Compiler.Service.dll"
#r "../../packages/NUnit/lib/nunit.framework.dll"
#load "FsUnit.fs"
#load "Common.fs"
#else
module FSharp.Compiler.Service.Tests.FscTests
#endif


open System
open System.Diagnostics
open System.IO

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler.SimpleSourceCodeServices
open FSharp.Compiler.Service.Tests
open FSharp.Compiler.Service.Tests.Common

open NUnit.Framework

exception 
   VerificationException of (*assembly:*)string * (*errorCode:*)int * (*output:*)string
   with override e.Message = sprintf "Verification of '%s' failed with code %d, message <<<%s>>>" e.Data0 e.Data1 e.Data2

exception 
   CompilationError of (*assembly:*)string * (*errorCode:*)int * (*info:*)FSharpErrorInfo []
   with override e.Message = sprintf "Compilation of '%s' failed with code %d (%A)" e.Data0 e.Data1 e.Data2

let runningOnMono = try System.Type.GetType("Mono.Runtime") <> null with e->  false        
let pdbExtension isDll = (if runningOnMono then (if isDll then ".dll.mdb" else ".exe.mdb") else ".pdb")

type PEVerifier () =

    static let expectedExitCode = 0
    static let runsOnMono = try System.Type.GetType("Mono.Runtime") <> null with _ -> false

    let verifierInfo =
        if runsOnMono then
            Some ("pedump", "--verify all")
        else
            let rec tryFindFile (fileName : string) (dir : DirectoryInfo) =
                let file = Path.Combine(dir.FullName, fileName)
                if File.Exists file then Some file
                else
                    dir.GetDirectories() 
                    |> Array.sortBy(fun d -> d.Name)
                    |> Array.filter(fun d -> 
                        match d.Name with 
                        // skip old SDK directories
                        | "v6.0" | "v6.0A" | "v7.0" | "v7.0A" | "v7.1" | "v7.1A" -> false
                        | _ -> true)
                    |> Array.rev // order by descending -- get latest version
                    |> Array.tryPick (tryFindFile fileName)

            let tryGetSdkDir (progFiles : Environment.SpecialFolder) =
                let progFilesFolder = Environment.GetFolderPath(progFiles)
                //let dI = DirectoryInfo(Path.Combine(progFilesFolder, "Microsoft SDKs", "Windows"))
                let dI = DirectoryInfo(Path.Combine(Path.Combine(progFilesFolder, "Microsoft SDKs"), "Windows"))
                if dI.Exists then Some dI
                else None

//            match Array.tryPick tryGetSdkDir [| Environment.SpecialFolder.ProgramFilesX86; Environment.SpecialFolder.ProgramFiles  |] with
            match Array.tryPick tryGetSdkDir [| Environment.SpecialFolder.ProgramFiles  |] with
            | None -> None
            | Some sdkDir ->
                match tryFindFile "peverify.exe" sdkDir with
                | None -> None
                | Some pe -> Some (pe, "/UNIQUE /IL /NOLOGO")

    static let execute (fileName : string, arguments : string) =
        printfn "executing '%s' with arguments %s" fileName arguments
        let psi = new ProcessStartInfo(fileName, arguments)
        psi.UseShellExecute <- false
        psi.ErrorDialog <- false
        psi.CreateNoWindow <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true

        use proc = Process.Start(psi)
        let stdOut = proc.StandardOutput.ReadToEnd()
        let stdErr = proc.StandardError.ReadToEnd()
        while not proc.HasExited do ()
        proc.ExitCode, stdOut, stdErr

    member __.Verify(assemblyPath : string) =
        match verifierInfo with
        | Some (verifierPath, switches) -> 
            let id,stdOut,stdErr = execute(verifierPath, sprintf "%s \"%s\"" switches assemblyPath)
//            if id = expectedExitCode && String.IsNullOrWhiteSpace stdErr then ()
            if id = expectedExitCode && String.IsNullOrEmpty stdErr then ()
            else
                printfn "Verification failure, stdout: <<<%s>>>" stdOut
                printfn "Verification failure, stderr: <<<%s>>>" stdErr
                raise <| VerificationException(assemblyPath, id, stdOut + "\n" + stdErr)
        | None -> 
           printfn "Skipping verification part of test because verifier not found"
            


type DebugMode =
    | Off
    | PdbOnly
    | Full

let checker = FSharpChecker.Create()
let compiler = new SimpleSourceCodeServices()

/// Ensures the default FSharp.Core referenced by the F# compiler service (if none is 
/// provided explicitly) is available in the output directory.
let ensureDefaultFSharpCoreAvailable tmpDir  =
    // FSharp.Compiler.Service references FSharp.Core 4.3.0.0 by default.  That's wrong? But the output won't verify
    // or run on a system without FSharp.Core 4.3.0.0 in the GAC or in the same directory, or with a binding redirect in place.
    // 
    // So just copy the FSharp.Core 4.3.0.0 to the tmp directory. Only need to do this on Windows.
    if System.Environment.OSVersion.Platform = System.PlatformID.Win32NT then // file references only valid on Windows 
        File.Copy(fsCore4300(), Path.Combine(tmpDir, Path.GetFileName(fsCore4300())), overwrite = true)

let compile isDll debugMode (assemblyName : string) (code : string) (dependencies : string list) =
    let tmp = Path.Combine(Path.GetTempPath(),"test"+string(hash (isDll,debugMode,assemblyName,code,dependencies)))
    try Directory.CreateDirectory(tmp) |> ignore with _ -> ()
    let sourceFile = Path.Combine(tmp, assemblyName + ".fs")
    let outFile = Path.Combine(tmp, assemblyName + if isDll then ".dll" else ".exe")
    let pdbFile = Path.Combine(tmp, assemblyName + pdbExtension isDll)
    do File.WriteAllText(sourceFile, code)
    let args =
        [|
            // fsc parser skips the first argument by default;
            // perhaps this shouldn't happen in library code.
            yield "fsc.exe"

            if isDll then yield "--target:library"

            match debugMode with
            | Off -> () // might need to include some switches here
            | PdbOnly ->
                yield "--debug:pdbonly"
                if not runningOnMono then  // on Mono, the debug file name is not configurable
                    yield sprintf "--pdb:%s" pdbFile
            | Full ->
                yield "--debug:full"
                if not runningOnMono then // on Mono, the debug file name is not configurable
                    yield sprintf "--pdb:%s" pdbFile

            for d in dependencies do
                yield sprintf "-r:%s" d

            yield sprintf "--out:%s" outFile

            yield sourceFile
        |]

    ensureDefaultFSharpCoreAvailable tmp
        
    printfn "args: %A" args
    let errorInfo, id = compiler.Compile args
    for err in errorInfo do 
       printfn "error: %A" err
    if id <> 0 then raise <| CompilationError(assemblyName, id, errorInfo)
    Assert.AreEqual (errorInfo.Length, 0)
    outFile

//sizeof<nativeint>
let compileAndVerify isDll debugMode assemblyName code dependencies =
    let verifier = new PEVerifier ()
    let outFile = compile isDll debugMode assemblyName code dependencies 
    verifier.Verify outFile
    outFile

let parseSourceCode (name : string, code : string) =
    let location = Path.Combine(Path.GetTempPath(),"test"+string(hash (name, code)))
    try Directory.CreateDirectory(location) |> ignore with _ -> ()

    let projPath = Path.Combine(location, name + ".fsproj")
    let filePath = Path.Combine(location, name + ".fs")
    let dllPath = Path.Combine(location, name + ".dll")
    let args = Common.mkProjectCommandLineArgs(dllPath, [filePath])
    let options = checker.GetProjectOptionsFromCommandLineArgs(projPath, args)
    let parseResults = checker.ParseFileInProject(filePath, code, options) |> Async.RunSynchronously
    parseResults.ParseTree |> Option.toList


let compileAndVerifyAst (name : string, ast : Ast.ParsedInput list, references : string list) =
    let outDir = Path.Combine(Path.GetTempPath(),"test"+string(hash (name, references)))
    try Directory.CreateDirectory(outDir) |> ignore with _ -> ()

    let outFile = Path.Combine(outDir, name + ".dll")

    ensureDefaultFSharpCoreAvailable outDir

    let errors, id = compiler.Compile(ast, name, outFile, references, executable = false)
    for err in errors do printfn "error: %A" err
    Assert.AreEqual (errors.Length, 0)
    if id <> 0 then raise <| CompilationError(name, id, errors)

    // copy local explicit references for verification
    for ref in references do 
        let name = Path.GetFileName ref
        File.Copy(ref, Path.Combine(outDir, name), overwrite = true)

    let verifier = new PEVerifier()

    verifier.Verify outFile

[<Test>]
let ``1. PEVerifier sanity check`` () =
    let verifier = new PEVerifier()

    let fscorlib = typeof<int option>.Assembly
    verifier.Verify fscorlib.Location

    let nonAssembly = Path.Combine(Directory.GetCurrentDirectory(), typeof<PEVerifier>.Assembly.GetName().Name + ".pdb")
    Assert.Throws<VerificationException>(fun () -> verifier.Verify nonAssembly |> ignore) |> ignore


[<Test>]
let ``2. Simple FSC library test`` () =
    let code = """
module Foo

    let f x = (x,x)

    type Foo = class end

    exception E of int * string

    printfn "done!" // make the code have some initialization effect
"""

    compileAndVerify true PdbOnly "Foo" code [] |> ignore

[<Test>]
let ``3. Simple FSC executable test`` () =
    let code = """
module Bar

    [<EntryPoint>]
    let main _ = printfn "Hello, World!" ; 42

"""
    let outFile = compileAndVerify false PdbOnly "Bar" code []

    use proc = Process.Start(outFile, "")
    proc.WaitForExit()
    Assert.AreEqual(proc.ExitCode, 42)



[<Test>]
let ``4. Compile from simple AST`` () =
    let code = """
module Foo

    let f x = (x,x)

    type Foo = class end

    exception E of int * string

    printfn "done!" // make the code have some initialization effect
"""
    let ast = parseSourceCode("foo", code)
    compileAndVerifyAst("foo", ast, [])

[<Test>]
let ``5. Compile from AST with explicit assembly reference`` () =
    let code = """
module Bar

    open Microsoft.FSharp.Compiler.SourceCodeServices

    let f x = (x,x)

    type Bar = class end

    exception E of int * string

    // depends on FSharp.Compiler.Service
    // note : mono's pedump fails if this is a value; will not verify type initializer for module
    let checker () = FSharpChecker.Create()

    printfn "done!" // make the code have some initialization effect
"""
    let serviceAssembly = typeof<FSharpChecker>.Assembly.Location
    let ast = parseSourceCode("bar", code)
    compileAndVerifyAst("bar", ast, [serviceAssembly])


[<Test>]
let ``Check line nos are indexed by 1`` () =
    let code = """
module Bar
    let doStuff a b =
            a + b

    let sum = doStuff "1" 2

"""    
    try
        compile false PdbOnly "Bar" code [] |> ignore
    with
    | :? CompilationError as exn  ->
            Assert.AreEqual(6,exn.Data2.[0].StartLineAlternate)
            Assert.True(exn.Data2.[0].ToString().Contains("Bar.fs (6,27)-(6,28)"))
    | _  -> failwith "No compilation error"

[<Test>]
let ``Check cols are indexed by 1`` () =
    let code = "let x = 1 + a"

    try
        compile false PdbOnly "Foo" code [] |> ignore
    with
    | :? CompilationError as exn  ->
            Assert.True(exn.Data2.[0].ToString().Contains("Foo.fs (1,13)-(1,14)"))
    | _  -> failwith "No compilation error"



#if STRESS
// For this stress test the aim is to check if we have a memory leak

module StressTest1 = 
    open Microsoft.FSharp.Compiler.SimpleSourceCodeServices
    open System.IO

    [<Test>]
    let ``stress test repeated in-memory compilation``() =
      for i = 1 to 500 do
        printfn "stress test iteration %d" i
        let code = """
module M

type C() = 
    member x.P = 1

let x = 3 + 4
"""

        compile true PdbOnly "Foo" code [] |> ignore

#endif

(*

[<Test>]
let ``Check read of mscorlib`` () =
    let options = Microsoft.FSharp.Compiler.AbstractIL.ILBinaryReader.mkDefault  Microsoft.FSharp.Compiler.AbstractIL.IL.EcmaILGlobals
    let options = { options with optimizeForMemory=true}
    let reader = Microsoft.FSharp.Compiler.AbstractIL.ILBinaryReader.OpenILModuleReaderAfterReadingAllBytes "C:\\Program Files (x86)\\Reference Assemblies\\Microsoft\\Framework\\.NETFramework\\v4.5\\mscorlib.dll" options
    let greg = reader.ILModuleDef.TypeDefs.FindByName "System.Globalization.GregorianCalendar"
    for attr in greg.CustomAttrs.AsList do 
        printfn "%A" attr.Method

*)


  