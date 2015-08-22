// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.FSharp.Compiler

module internal MSBuildResolver = 

    open System
    open System.IO
    open System.Reflection
    open Microsoft.Build.Tasks
    open Microsoft.Build.Utilities
    open Microsoft.Build.Framework
    open Microsoft.Build.BuildEngine
    open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library 

    exception ResolutionFailure
    
    /// Describes the location where the reference was found.
    type ResolvedFrom =
        | AssemblyFolders
        | AssemblyFoldersEx
        | TargetFrameworkDirectory
        | RawFileName
        | GlobalAssemblyCache
        | Path of string
        | Unknown
            
    /// Indicates whether the resolve should follow compile-time rules or runtime rules.                      
    type ResolutionEnvironment = 
        | CompileTimeLike 
        | RuntimeLike 
        | DesigntimeLike
    
#if NO_MSBUILD_REFERENCE_RESOLUTION
    let HighestInstalledNetFrameworkVersionMajorMinor() =
        4,"v5.0"
#else

    /// Information about a resolved file.
    type ResolvedFile = 
        { /// Item specification
          itemSpec:string
          /// Location that the assembly was resolved from
          resolvedFrom:ResolvedFrom
          /// The long fusion name of the assembly
          fusionName:string
          /// The version of the assembly (like 4.0.0.0)
          version:string
          /// The name of the redist the assembly was found in
          redist:string        
          /// Round-tripped baggage string
          baggage:string
        }

        override this.ToString() = sprintf "ResolvedFile(%s)" this.itemSpec
    
    /// Reference resolution results. All paths are fully qualified.
    type ResolutionResults = 
        { /// Paths to primary references
          resolvedFiles:ResolvedFile[]
          /// Paths to dependencies
          referenceDependencyPaths:string[]
          /// Paths to related files (like .xml and .pdb)
          relatedPaths:string[]
          /// Paths to satellite assemblies used for localization.
          referenceSatellitePaths:string[]
          /// Additional files required to support multi-file assemblies.
          referenceScatterPaths:string[]
          /// Paths to files that reference resolution recommend be copied to the local directory
          referenceCopyLocalPaths:string[]
          /// Binding redirects that reference resolution recommends for the app.config file.
          suggestedBindingRedirects:string[] 
        }

        static member Empty = 
            { resolvedFiles = [| |]
              referenceDependencyPaths = [| |]
              relatedPaths = [| |]
              referenceSatellitePaths = [| |]
              referenceScatterPaths = [| |]
              referenceCopyLocalPaths = [| |]
              suggestedBindingRedirects = [| |] }


    /// Get the Reference Assemblies directory for the .NET Framework on Window
    let DotNetFrameworkReferenceAssembliesRootDirectoryOnWindows = 
        // Note that ProgramFilesX86 is correct for both x86 and x64 architectures (the reference assemblies are always in the 32-bit location, which is PF(x86) on an x64 machine)
        let PF = 
            match Environment.GetEnvironmentVariable("ProgramFiles(x86)") with
            | null -> Environment.GetEnvironmentVariable("ProgramFiles")  // if PFx86 is null, then we are 32-bit and just get PF
            | s -> s 
        PF + @"\Reference Assemblies\Microsoft\Framework\.NETFramework"


    /// When targeting .NET 2.0-3.5 on Windows, we expand the {WindowsFramework} and {ReferenceAssemblies} paths manually
    let internal ReplaceVariablesForLegacyFxOnWindows(dirs: string list) =
        let windowsFramework = Environment.GetEnvironmentVariable("windir")+ @"\Microsoft.NET\Framework"
        let referenceAssemblies = DotNetFrameworkReferenceAssembliesRootDirectoryOnWindows
        dirs |> List.map(fun d -> d.Replace("{WindowsFramework}",windowsFramework).Replace("{ReferenceAssemblies}",referenceAssemblies))

    
    // ATTENTION!: the following code needs to be updated every time we are switching to the new MSBuild version because new .NET framework version was released
    // 1. List of frameworks
    // 2. DeriveTargetFrameworkDirectoriesFor45Plus
    // 3. HighestInstalledNetFrameworkVersionMajorMinor
    // 4. GetPathToDotNetFrameworkImlpementationAssemblies
    [<Literal>]    
    let private Net10 = "v1.0"

    [<Literal>]    
    let private Net11 = "v1.1"

    [<Literal>]    
    let private Net20 = "v2.0"

    [<Literal>]    
    let private Net30 = "v3.0"

    [<Literal>]    
    let private Net35 = "v3.5"

    [<Literal>]    
    let private Net40 = "v4.0"

    [<Literal>]    
    let private Net45 = "v4.5"

    [<Literal>]    
    let private Net451 = "v4.5.1"

    /// The list of supported .NET Framework version numbers, using the monikers of the Reference Assemblies folder.
    let SupportedNetFrameworkVersions = set [ Net20; Net30; Net35; Net40; Net45; Net451; (*SL only*) "v5.0" ]
    
#if CROSS_PLATFORM_COMPILER
    // Mono doesn't have GetPathToDotNetFramework. In this case we simply don't search this extra directory.
    // When the x-plat compiler is run on Mono this is ok since implementation assembly folder is the same as the target framework folder.
    // When the x-plat compiler is run on Windows/.NET this will curently cause slightly divergent behaviour.
    let GetPathToDotNetFrameworkImlpementationAssemblies _v = []
#else
    /// Get the path to the .NET Framework implementation assemblies by using ToolLocationHelper.GetPathToDotNetFramework.
    /// This is only used to specify the "last resort" path for assembly resolution.
    let GetPathToDotNetFrameworkImlpementationAssemblies(v) =
#if FX_ATLEAST_45
        let v =
            match v with
            | Net11 ->  Some TargetDotNetFrameworkVersion.Version11
            | Net20 ->  Some TargetDotNetFrameworkVersion.Version20
            | Net30 ->  Some TargetDotNetFrameworkVersion.Version30
            | Net35 ->  Some TargetDotNetFrameworkVersion.Version35
            | Net40 ->  Some TargetDotNetFrameworkVersion.Version40
            | Net45 ->  Some TargetDotNetFrameworkVersion.Version45
            | Net451 -> Some TargetDotNetFrameworkVersion.Version451
            | _ -> assert false; None
        match v with
        | Some v -> 
            match ToolLocationHelper.GetPathToDotNetFramework v with
            | null -> []
            | x -> [x]
        | _ -> []
#else
        // FX_ATLEAST_45 is not defined for step when we build compiler with proto compiler.
        ignore v
        []
#endif        
#endif        


#if CROSS_PLATFORM_COMPILER
    // ToolLocationHelper.GetPathToDotNetFrameworkReferenceAssemblies is not available on Mono.
    // We currently use the old values that the F# 2.0 compiler assumed. 
    // When the x-plat compiler is run on Mono this is ok since the asemblies are all in the framework folder
    // When the x-plat compiler is run on Windows/.NET this will curently cause slightly divergent behaviour this directory 
    // may not be the same as the Microsoft compiler in all cases.
    let GetPathToDotNetFrameworkReferenceAssembliesFor40Plus(version) = 
        match version with 
        | Net40 -> ReplaceVariablesForLegacyFxOnWindows([@"{ReferenceAssemblies}\v4.0"])  
        | Net45 -> ReplaceVariablesForLegacyFxOnWindows([@"{ReferenceAssemblies}\v4.5"])         
        | Net451 -> ReplaceVariablesForLegacyFxOnWindows([@"{ReferenceAssemblies}\v4.5"])         
        | _ -> []
#else

    let GetPathToDotNetFrameworkReferenceAssembliesFor40Plus(version) = 
#if FX_ATLEAST_45
        // starting with .Net 4.0, the runtime dirs (WindowsFramework) are never used by MSBuild RAR
        let v =
            match version with
            | Net40 -> Some TargetDotNetFrameworkVersion.Version40
            | Net45 -> Some TargetDotNetFrameworkVersion.Version45
            | Net451 -> Some TargetDotNetFrameworkVersion.Version451
            | _ -> assert false; None // unknown version - some parts in the code are not synced
        match v with
        | Some v -> 
            match ToolLocationHelper.GetPathToDotNetFrameworkReferenceAssemblies v with
            | null -> []
            | x -> [x]
        | None -> []        
#else
        // FX_ATLEAST_45 is not defined for step when we build compiler with proto compiler.
        ignore version
        []
#endif
#endif

#if CROSS_PLATFORM_COMPILER
    let HighestInstalledNetFrameworkVersionMajorMinor() =
       // Mono doesn't have GetPathToDotNetFramework
        4, Net40
#else
    /// Use MSBuild to determine the version of the highest installed framework.
    let HighestInstalledNetFrameworkVersionMajorMinor() =
        if box (ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version451)) <> null then 4, Net451
        elif box (ToolLocationHelper.GetPathToDotNetFramework(TargetDotNetFrameworkVersion.Version45)) <> null then 4, Net45
        else 4, Net40 // version is 4.0 assumed since this code is running.
#endif

    /// Derive the target framework directories.        
    let DeriveTargetFrameworkDirectories (targetFrameworkVersion:string, logMessage) =

        let targetFrameworkVersion =
            if not(targetFrameworkVersion.StartsWith("v",StringComparison.Ordinal)) then "v"+targetFrameworkVersion
            else targetFrameworkVersion

        let result =
            if targetFrameworkVersion.StartsWith(Net10, StringComparison.Ordinal) then ReplaceVariablesForLegacyFxOnWindows([@"{WindowsFramework}\v1.0.3705"])
            elif targetFrameworkVersion.StartsWith(Net11, StringComparison.Ordinal) then ReplaceVariablesForLegacyFxOnWindows([@"{WindowsFramework}\v1.1.4322"])
            elif targetFrameworkVersion.StartsWith(Net20, StringComparison.Ordinal) then ReplaceVariablesForLegacyFxOnWindows([@"{WindowsFramework}\v2.0.50727"])
            elif targetFrameworkVersion.StartsWith(Net30, StringComparison.Ordinal) then ReplaceVariablesForLegacyFxOnWindows([@"{ReferenceAssemblies}\v3.0"; @"{WindowsFramework}\v3.0"; @"{WindowsFramework}\v2.0.50727"])
            elif targetFrameworkVersion.StartsWith(Net35, StringComparison.Ordinal) then ReplaceVariablesForLegacyFxOnWindows([@"{ReferenceAssemblies}\v3.5"; @"{WindowsFramework}\v3.5"; @"{ReferenceAssemblies}\v3.0"; @"{WindowsFramework}\v3.0"; @"{WindowsFramework}\v2.0.50727"])
            else GetPathToDotNetFrameworkReferenceAssembliesFor40Plus(targetFrameworkVersion)

        let result = result |> Array.ofList                
        logMessage (sprintf "Derived target framework directories for version %s are: %s" targetFrameworkVersion (String.Join(",", result)))                
        result    
 
    /// Decode the ResolvedFrom code from MSBuild.
    let DecodeResolvedFrom(resolvedFrom:string) : ResolvedFrom = 
        match resolvedFrom with
        | "{RawFileName}" -> RawFileName
        | "{GAC}" -> GlobalAssemblyCache
        | "{TargetFrameworkDirectory}" -> TargetFrameworkDirectory
        | "{AssemblyFolders}" -> AssemblyFolders
        | r when r.Length >= 10 &&  "{Registry:" = r.Substring(0,10) -> AssemblyFoldersEx
        | r -> ResolvedFrom.Path r
        

    /// Perform assembly resolution by instantiating the ResolveAssemblyReference task directly from the MSBuild SDK.
    let ResolveCore(resolutionEnvironment: ResolutionEnvironment,
                    references:(string*(*baggage*)string)[], 
                    targetFrameworkVersion: string, 
                    targetFrameworkDirectories: string list,
                    targetProcessorArchitecture: string,                
                    outputDirectory: string, 
                    fsharpCoreExplicitDirOrFSharpBinariesDir: string,
                    explicitIncludeDirs: string list,
                    implicitIncludeDir: string,
                    frameworkRegistryBase: string, 
                    assemblyFoldersSuffix: string, 
                    assemblyFoldersConditions: string, 
                    allowRawFileName: bool,
                    logMessage: (string -> unit), 
                    logWarning: (string -> string -> unit), 
                    logError: (string -> string -> unit)) =

        if Array.isEmpty references then ResolutionResults.Empty else

        let backgroundException = ref false
        
        let protect f = 
            if not !backgroundException then 
                try f() 
                with _ -> backgroundException := true
                
        let engine = 
            { new IBuildEngine with 
                    member __.BuildProjectFile(projectFileName, targetNames, globalProperties, targetOutputs) = true
                    member __.LogCustomEvent(e) = protect (fun () -> logMessage e.Message)
                    member __.LogErrorEvent(e) = protect (fun () -> logError e.Code e.Message)
                    member __.LogMessageEvent(e) = protect (fun () -> logMessage e.Message)
                    member __.LogWarningEvent(e) = protect (fun () -> logWarning e.Code e.Message)
                    member __.ColumnNumberOfTaskNode = 1
                    member __.LineNumberOfTaskNode = 1
                    member __.ContinueOnError = true
                    member __.ProjectFileOfTaskNode = "" }
                    
        // Derive the target framework directory if none was supplied.
        let targetFrameworkDirectories =
            if targetFrameworkDirectories=[] then DeriveTargetFrameworkDirectories(targetFrameworkVersion, logMessage) 
            else targetFrameworkDirectories |> Array.ofList
            
        // Filter for null and zero length
        let references = references |> Array.filter(fst >> String.IsNullOrEmpty >> not) 

        // Determine the set of search paths for the resolution
        let searchPaths = 

            let explicitIncludeDirs = explicitIncludeDirs |> List.filter(String.IsNullOrEmpty >> not)

            let rawFileNamePath = if allowRawFileName then ["{RawFileName}"] else []

            let registry = sprintf "{Registry:%s,%s,%s%s}" frameworkRegistryBase targetFrameworkVersion assemblyFoldersSuffix assemblyFoldersConditions

            [|  match resolutionEnvironment with
                | DesigntimeLike
                | RuntimeLike ->
                    logMessage("Using scripting resolution precedence.")                      
                    // These are search paths for runtime-like or scripting resolution. GAC searching is present.
                    yield! rawFileNamePath    // Quick-resolve straight to filename first 
                    yield! explicitIncludeDirs     // From -I, #I
                    yield implicitIncludeDir   // Usually the project directory
                    yield fsharpCoreExplicitDirOrFSharpBinariesDir    // Location of explicit reference to FSharp.Core, otherwise location of fsc.exe
                    yield "{TargetFrameworkDirectory}"
                    yield registry
                    yield "{AssemblyFolders}"
                    yield "{GAC}"

                | CompileTimeLike -> 
                    logMessage("Using compilation resolution precedence.")                      
                    // These are search paths for compile-like resolution. GAC searching is not present.
                    yield "{TargetFrameworkDirectory}"
                    yield! rawFileNamePath        // Quick-resolve straight to filename first
                    yield! explicitIncludeDirs     // From -I, #I
                    yield implicitIncludeDir   // Usually the project directory
                    yield fsharpCoreExplicitDirOrFSharpBinariesDir    // Location of explicit reference to FSharp.Core, otherwise location of fsc.exe
                    yield registry
                    yield "{AssemblyFolders}"
                    yield outputDirectory
                    yield "{GAC}"
                    // use path to implementation assemblies as the last resort
                    yield! GetPathToDotNetFrameworkImlpementationAssemblies targetFrameworkVersion 
             |]    

        let assemblies = 
            [| for (referenceName,baggage) in references -> 
                    let item = new Microsoft.Build.Utilities.TaskItem(referenceName)
                    item.SetMetadata("Baggage", baggage)
                    item:>ITaskItem |]

        let rar = 
            ResolveAssemblyReference(BuildEngine=engine, TargetFrameworkDirectories=targetFrameworkDirectories,
                                     FindRelatedFiles=false, FindDependencies=false, FindSatellites=false, 
                                     FindSerializationAssemblies=false, Assemblies=assemblies, 
                                     SearchPaths=searchPaths, 
                                     AllowedAssemblyExtensions= [| ".dll" ; ".exe" |])
#if BUILDING_WITH_LKG
        ignore targetProcessorArchitecture
#else       
        rar.TargetProcessorArchitecture <- targetProcessorArchitecture
        let targetedRuntimeVersionValue = typeof<obj>.Assembly.ImageRuntimeVersion
#if CROSS_PLATFORM_COMPILER 
        // The properties TargetedRuntimeVersion and CopyLocalDependenciesWhenParentReferenceInGac 
        // are not available to the cross-platform compiler since they are Windows only (not defined in the Mono  
        // 4.0 XBuild support). So we only set them if available (to avoid a compile-time dependency). 
        let runningOnMono = try System.Type.GetType("Mono.Runtime") <> null with e-> false         
        if not runningOnMono then  
            typeof<ResolveAssemblyReference>.InvokeMember("TargetedRuntimeVersion",(BindingFlags.Instance ||| BindingFlags.SetProperty ||| BindingFlags.Public),null,rar,[| box targetedRuntimeVersionValue |])  |> ignore 
            typeof<ResolveAssemblyReference>.InvokeMember("CopyLocalDependenciesWhenParentReferenceInGac",(BindingFlags.Instance ||| BindingFlags.SetProperty ||| BindingFlags.Public),null,rar,[| box true |])  |> ignore 
#else
        rar.TargetedRuntimeVersion <- targetedRuntimeVersionValue
        rar.CopyLocalDependenciesWhenParentReferenceInGac <- true
#endif
#endif

        let succeeded = rar.Execute()
        
        if not succeeded then 
            raise ResolutionFailure

        let resolvedFiles = 
            [| for p in rar.ResolvedFiles -> 
                { itemSpec = p.ItemSpec
                  resolvedFrom = DecodeResolvedFrom(p.GetMetadata("ResolvedFrom"))
                  fusionName = p.GetMetadata("FusionName")
                  version = p.GetMetadata("Version")
                  redist = p.GetMetadata("Redist") 
                  baggage = p.GetMetadata("Baggage") } |]

        { resolvedFiles = resolvedFiles
          referenceDependencyPaths = [| for p in rar.ResolvedDependencyFiles -> p.ItemSpec |]
          relatedPaths = [| for p in rar.RelatedFiles -> p.ItemSpec |]
          referenceSatellitePaths = [| for p in rar.SatelliteFiles -> p.ItemSpec |]
          referenceScatterPaths = [| for p in rar.ScatterFiles -> p.ItemSpec |]
          referenceCopyLocalPaths = [| for p in rar.CopyLocalFiles -> p.ItemSpec |]
          suggestedBindingRedirects = [| for p in rar.SuggestedRedirects -> p.ItemSpec |] }

    
    
    /// Perform the resolution on rooted and unrooted paths, and then combine the results.
    let Resolve(resolutionEnvironment, references, targetFrameworkVersion, targetFrameworkDirectories, targetProcessorArchitecture,                
                outputDirectory, fsharpCoreExplicitDirOrFSharpBinariesDir, explicitIncludeDirs, implicitIncludeDir, frameworkRegistryBase, 
                assemblyFoldersSuffix, assemblyFoldersConditions, logMessage, logWarning, logError) =

        // The {RawFileName} target is 'dangerous', in the sense that is uses Directory.GetCurrentDirectory() to resolve unrooted file paths.
        // It is unreliable to use this mutable global state inside Visual Studio.  As a result, we partition all references into a "rooted" set
        // (which contains e.g. C:\MyDir\MyAssem.dll) and "unrooted" (everything else).  We only allow "rooted" to use {RawFileName}.  Note that
        // unrooted may still find 'local' assemblies by virtue of the fact that "implicitIncludeDir" is one of the places searched during 
        // assembly resolution.
        let references = 
            [| for ((file,baggage) as data) in references -> 
                    // However, MSBuild will not resolve 'relative' paths, even when e.g. implicitIncludeDir is part of the search.  As a result,
                    // if we have an unrooted path+filename, we'll assume this is relative to the project directory and root it.
                    if FileSystem.IsPathRootedShim(file) then
                        data  // fine, e.g. "C:\Dir\foo.dll"
                    elif not(file.Contains("\\") || file.Contains("/")) then
                        data  // fine, e.g. "System.Transactions.dll"
                    else
                        // we have a 'relative path', e.g. "bin/Debug/foo.exe" or "..\Yadda\bar.dll"
                        // turn it into an absolute path based at implicitIncludeDir
                        (Path.Combine(implicitIncludeDir, file), baggage) |]

        let rooted, unrooted = references |> Array.partition (fst >> FileSystem.IsPathRootedShim)

        let rootedResults = ResolveCore(resolutionEnvironment, rooted,  targetFrameworkVersion, targetFrameworkDirectories, targetProcessorArchitecture, outputDirectory, fsharpCoreExplicitDirOrFSharpBinariesDir, explicitIncludeDirs, implicitIncludeDir, frameworkRegistryBase, assemblyFoldersSuffix, assemblyFoldersConditions, true, logMessage, logWarning, logError)

        let unrootedResults = ResolveCore(resolutionEnvironment, unrooted,  targetFrameworkVersion, targetFrameworkDirectories, targetProcessorArchitecture, outputDirectory, fsharpCoreExplicitDirOrFSharpBinariesDir, explicitIncludeDirs, implicitIncludeDir, frameworkRegistryBase, assemblyFoldersSuffix, assemblyFoldersConditions, false, logMessage, logWarning, logError)

        // now unify the two sets of results
        {
            resolvedFiles = Array.concat [| rootedResults.resolvedFiles; unrootedResults.resolvedFiles |]
            referenceDependencyPaths = set rootedResults.referenceDependencyPaths |> Set.union (set unrootedResults.referenceDependencyPaths) |> Set.toArray 
            relatedPaths = set rootedResults.relatedPaths |> Set.union (set unrootedResults.relatedPaths) |> Set.toArray 
            referenceSatellitePaths = set rootedResults.referenceSatellitePaths |> Set.union (set unrootedResults.referenceSatellitePaths) |> Set.toArray 
            referenceScatterPaths = set rootedResults.referenceScatterPaths |> Set.union (set unrootedResults.referenceScatterPaths) |> Set.toArray 
            referenceCopyLocalPaths = set rootedResults.referenceCopyLocalPaths |> Set.union (set unrootedResults.referenceCopyLocalPaths) |> Set.toArray 
            suggestedBindingRedirects = set rootedResults.suggestedBindingRedirects |> Set.union (set unrootedResults.suggestedBindingRedirects) |> Set.toArray 
        }

#endif
