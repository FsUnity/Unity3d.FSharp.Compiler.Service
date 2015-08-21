﻿// Open the namespace with InteractiveChecker type
open System
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices

// Create an interactive checker instance (ignore notifications)
let checker = FSharpChecker.Create()

let parseWithTypeInfo (file, input) = 
    let checkOptions = checker.GetProjectOptionsFromScript(file, input) |> Async.RunSynchronously
    let untypedRes = checker.ParseFileInProject(file, input, checkOptions) |> Async.RunSynchronously
    
    match checker.CheckFileInProject(untypedRes, file, 0, input, checkOptions) |> Async.RunSynchronously with 
    | FSharpCheckFileAnswer.Succeeded(res) -> untypedRes, res
    | res -> failwithf "Parsing did not finish... (%A)" res

// ----------------------------------------------------------------------------
// Example
// ----------------------------------------------------------------------------

let input = 
  """
  let foo() = 
    let msg = "Hello world"
    if true then 
      printfn "%s" msg.
  """
let inputLines = input.Split('\n')
let file = "/home/user/Test.fsx"

let identTokenTag = FSharpTokenTag.Identifier
let untyped, parsed = parseWithTypeInfo (file, input)
// Get tool tip at the specified location
let tip = parsed.GetToolTipTextAlternate(2, 7, inputLines.[1], [ "foo" ], identTokenTag)

printfn "%A" tip

// Get declarations (autocomplete) for a location
let decls = 
    parsed.GetDeclarationListInfo(Some untyped, 5, 23, inputLines.[4], [], "msg") 
    |> Async.RunSynchronously

for item in decls.Items do
    printfn " - %s" item.Name
