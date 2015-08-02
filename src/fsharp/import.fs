// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

module internal Microsoft.FSharp.Compiler.Import

#nowarn "44" // This construct is deprecated. please use List.item

open System.Reflection
open System.Collections.Generic
open Internal.Utilities
open Microsoft.FSharp.Compiler.AbstractIL.IL
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library
open Microsoft.FSharp.Compiler 

open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.Tastops
open Microsoft.FSharp.Compiler.AbstractIL.IL
open Microsoft.FSharp.Compiler.Env
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.ErrorLogger
//#if EXTENSIONTYPING
//open Microsoft.FSharp.Compiler.ExtensionTyping
//#endif

/// Represents an interface to some of the functionality of TcImports, for loading assemblies 
/// and accessing information about generated provided assemblies.
type AssemblyLoader = 

    /// Resolve an Abstract IL assembly reference to a Ccu
    abstract LoadAssembly : range * ILAssemblyRef -> CcuResolutionResult
//#if EXTENSIONTYPING
//
//    /// Get a flag indicating if an assembly is a provided assembly, plus the
//    /// table of information recording remappings from type names in the provided assembly to type
//    /// names in the statically linked, embedded assembly.
//    abstract GetProvidedAssemblyInfo : range * Tainted<ProvidedAssembly> -> bool * ProvidedAssemblyStaticLinkingMap option
//
//    /// Record a root for a [<Generate>] type to help guide static linking & type relocation
//    abstract RecordGeneratedTypeRoot : ProviderGeneratedType -> unit
//#endif
//        


//-------------------------------------------------------------------------
// Import an IL types as F# types.
//------------------------------------------------------------------------- 

/// Represents a context used by the import routines that convert AbstractIL types and provided 
/// types to F# internal compiler data structures. 
///
/// Also caches the conversion of AbstractIL ILTypeRef nodes, based on hashes of these.
///
/// There is normally only one ImportMap for any assembly compilation, though additional instances can be created
/// using tcImports.GetImportMap() if needed, and it is not harmful if multiple instances are used. The object 
/// serves as an interface through to the tables stored in the primary TcImports structures defined in build.fs. 
[<Sealed>]
type ImportMap(g:TcGlobals,assemblyLoader:AssemblyLoader) =
    let typeRefToTyconRefCache = new System.Collections.Generic.Dictionary<ILTypeRef,TyconRef>()
    member this.g = g
    member this.assemblyLoader = assemblyLoader
    member this.ILTypeRefToTyconRefCache = typeRefToTyconRefCache

let CanImportILScopeRef (env:ImportMap) m scoref = 
    match scoref with 
    | ILScopeRef.Local    -> true
    | ILScopeRef.Module _ -> true
    | ILScopeRef.Assembly assref -> 
        match env.assemblyLoader.LoadAssembly (m,assref) with
        | UnresolvedCcu _ ->  false
        | ResolvedCcu _ -> true


/// Import a reference to a type definition, given the AbstractIL data for the type reference
let ImportTypeRefData (env:ImportMap) m (scoref,path,typeName) = 
    let ccu =  
        match scoref with 
        | ILScopeRef.Local    -> error(InternalError("ImportILTypeRef: unexpected local scope",m))
        | ILScopeRef.Module _ -> error(InternalError("ImportILTypeRef: reference found to a type in an auxiliary module",m))
        | ILScopeRef.Assembly assref -> env.assemblyLoader.LoadAssembly (m,assref)  // NOTE: only assemblyLoader callsite

    // Do a dereference of a fake tcref for the type just to check it exists in the target assembly and to find
    // the corresponding Tycon.
    let ccu = 
        match ccu with
        | ResolvedCcu ccu->ccu
        | UnresolvedCcu ccuName -> 
            error (Error(FSComp.SR.impTypeRequiredUnavailable(typeName, ccuName),m))
    let fakeTyconRef = mkNonLocalTyconRef (mkNonLocalEntityRef ccu path) typeName
    let tycon = 
        try   
            fakeTyconRef.Deref
        with _ ->
            error (Error(FSComp.SR.impReferencedTypeCouldNotBeFoundInAssembly(String.concat "." (Array.append path  [| typeName |]), ccu.AssemblyName),m))
//#if EXTENSIONTYPING
//    // Validate (once because of caching)
//    match tycon.TypeReprInfo with
//    | TProvidedTypeExtensionPoint info ->
//            //printfn "ImportTypeRefData: validating type: typeLogicalName = %A" typeName
//            ExtensionTyping.ValidateProvidedTypeAfterStaticInstantiation(m,info.ProvidedType,path,typeName)
//    | _ -> 
//            ()
//#endif
    match tryRescopeEntity ccu tycon with 
    | None -> error (Error(FSComp.SR.impImportedAssemblyUsesNotPublicType(String.concat "." (Array.toList path@[typeName])),m));
    | Some tcref -> tcref
    

/// Import a reference to a type definition, given an AbstractIL ILTypeRef, without caching
//
// Note, the type names that flow to the point include the "mangled" type names used for static parameters for provided types.
// For example, 
//       Foo.Bar,"1.0"
// This is because ImportProvidedType goes via Abstract IL type references. 
let ImportILTypeRefUncached (env:ImportMap) m (tref:ILTypeRef) = 
    let path,typeName = 
        match tref.Enclosing with 
        | [] -> 
            splitILTypeNameWithPossibleStaticArguments tref.Name
        | h :: t -> 
            let nsp,tname = splitILTypeNameWithPossibleStaticArguments h
            // Note, subsequent type names do not need to be split, only the first
            [| yield! nsp; yield tname; yield! t |], tref.Name

    ImportTypeRefData (env:ImportMap) m (tref.Scope,path,typeName)

    
/// Import a reference to a type definition, given an AbstractIL ILTypeRef, with caching
let ImportILTypeRef (env:ImportMap) m (tref:ILTypeRef) =
    if env.ILTypeRefToTyconRefCache.ContainsKey(tref) then
        env.ILTypeRefToTyconRefCache.[tref]
    else 
        let tcref = ImportILTypeRefUncached  env m tref
        env.ILTypeRefToTyconRefCache.[tref] <- tcref
        tcref

/// Import a reference to a type definition, given an AbstractIL ILTypeRef, with caching
let CanImportILTypeRef (env:ImportMap) m (tref:ILTypeRef) =
    env.ILTypeRefToTyconRefCache.ContainsKey(tref) || CanImportILScopeRef env m tref.Scope

/// Import a type, given an AbstractIL ILTypeRef and an F# type instantiation.
/// 
/// Prefer the F# abbreviation for some built-in types, e.g. 'string' rather than 
/// 'System.String', since we prefer the F# abbreviation to the .NET equivalents. 
let ImportTyconRefApp (env:ImportMap) tcref tyargs = 
    match env.g.better_tcref_map tcref tyargs with 
    | Some res -> res
    | None -> TType_app (tcref,tyargs) 

/// Import an IL type as an F# type.
let rec ImportILType (env:ImportMap) m tinst typ =  
    match typ with
    | ILType.Void -> 
        env.g.unit_ty

    | ILType.Array(bounds,ty) -> 
        let n = bounds.Rank
        let elementType = ImportILType env m tinst ty
        mkArrayTy env.g n elementType m

    | ILType.Boxed  tspec | ILType.Value tspec ->
        let tcref = ImportILTypeRef env m tspec.TypeRef 
        let inst = tspec.GenericArgs |> ILList.toList |> List.map (ImportILType env m tinst) 
        ImportTyconRefApp env tcref inst

    | ILType.Byref ty -> mkByrefTy env.g (ImportILType env m tinst ty)
    | ILType.Ptr ty  -> mkNativePtrType env.g (ImportILType env m tinst ty)
    | ILType.FunctionPointer _ -> env.g.nativeint_ty (* failwith "cannot import this kind of type (ptr, fptr)" *)
    | ILType.Modified(_,_,ty) -> 
         // All custom modifiers are ignored
         ImportILType env m tinst ty
    | ILType.TypeVar u16 -> 
         try List.nth tinst (int u16) 
         with _ -> 
              error(Error(FSComp.SR.impNotEnoughTypeParamsInScopeWhileImporting(),m))

let rec CanImportILType (env:ImportMap) m typ =  
    match typ with
    | ILType.Void -> true
    | ILType.Array(_bounds,ty) -> CanImportILType env m ty
    | ILType.Boxed  tspec | ILType.Value tspec ->
        CanImportILTypeRef env m tspec.TypeRef 
        && tspec.GenericArgs |> ILList.toList |> List.forall (CanImportILType env m) 
    | ILType.Byref ty -> CanImportILType env m ty
    | ILType.Ptr ty  -> CanImportILType env m ty
    | ILType.FunctionPointer _ -> true
    | ILType.Modified(_,_,ty) -> CanImportILType env m ty
    | ILType.TypeVar _u16 -> true
//
//#if EXTENSIONTYPING
//
///// Import a provided type reference as an F# type TyconRef
//let ImportProvidedNamedType (env:ImportMap) (m:range) (st:Tainted<ProvidedType>) = 
//    // See if a reverse-mapping exists for a generated/relocated System.Type
//    match st.PUntaint((fun st -> st.TryGetTyconRef()),m) with 
//    | Some x -> (x :?> TyconRef)
//    | None ->         
//        let tref = ExtensionTyping.GetILTypeRefOfProvidedType (st,m)
//        ImportILTypeRef env m tref
//
///// Import a provided type as an AbstractIL type
//let rec ImportProvidedTypeAsILType (env:ImportMap) (m:range) (st:Tainted<ProvidedType>) = 
//    if st.PUntaint ((fun x -> x.IsVoid),m) then ILType.Void
//    elif st.PUntaint((fun st -> st.IsGenericParameter),m) then
//        mkILTyvarTy (uint16 (st.PUntaint((fun st -> st.GenericParameterPosition),m)))
//    elif st.PUntaint((fun st -> st.IsArray),m) then 
//        let et = ImportProvidedTypeAsILType env m (st.PApply((fun st -> st.GetElementType()),m))
//        ILType.Array(ILArrayShape.FromRank (st.PUntaint((fun st -> st.GetArrayRank()),m)), et)
//    elif st.PUntaint((fun st -> st.IsByRef),m) then 
//        let et = ImportProvidedTypeAsILType env m (st.PApply((fun st -> st.GetElementType()),m))
//        ILType.Byref et
//    elif st.PUntaint((fun st -> st.IsPointer),m) then 
//        let et = ImportProvidedTypeAsILType env m (st.PApply((fun st -> st.GetElementType()),m))
//        ILType.Ptr et
//    else
//        let gst, genericArgs = 
//            if st.PUntaint((fun st -> st.IsGenericType),m) then 
//                let args = st.PApplyArray((fun st -> st.GetGenericArguments()),"GetGenericArguments",m) |> Array.map (ImportProvidedTypeAsILType env m) |> List.ofArray 
//                let gst = st.PApply((fun st -> st.GetGenericTypeDefinition()),m)
//                gst, args
//            else   
//                st, []
//        let tref = ExtensionTyping.GetILTypeRefOfProvidedType (gst,m)
//        let tcref = ImportProvidedNamedType env m gst
//        let tps = tcref.Typars m
//        if tps.Length <> genericArgs.Length then 
//           error(Error(FSComp.SR.impInvalidNumberOfGenericArguments(tcref.CompiledName, tps.Length, genericArgs.Length),m))
//        // We're converting to an IL type, where generic arguments are erased
//        let genericArgs = List.zip tps genericArgs |> List.filter (fun (tp,_) -> not tp.IsErased) |> List.map snd
//
//        let tspec = mkILTySpec(tref,genericArgs)
//        if st.PUntaint((fun st -> st.IsValueType),m) then 
//            ILType.Value tspec 
//        else 
//            mkILBoxedType tspec
//
///// Import a provided type as an F# type.
//let rec ImportProvidedType (env:ImportMap) (m:range) (* (tinst:TypeInst) *) (st:Tainted<ProvidedType>) = 
//
//    let g = env.g
//    if st.PUntaint((fun st -> st.IsArray),m) then 
//        let elemTy = (ImportProvidedType env m (* tinst *) (st.PApply((fun st -> st.GetElementType()),m)))
//        mkArrayTy g (st.PUntaint((fun st -> st.GetArrayRank()),m))  elemTy m
//    elif st.PUntaint((fun st -> st.IsByRef),m) then 
//        let elemTy = (ImportProvidedType env m (* tinst *) (st.PApply((fun st -> st.GetElementType()),m)))
//        mkByrefTy g elemTy
//    elif st.PUntaint((fun st -> st.IsPointer),m) then 
//        let elemTy = (ImportProvidedType env m (* tinst *) (st.PApply((fun st -> st.GetElementType()),m)))
//        mkNativePtrType g elemTy
//    else
//
//        // REVIEW: Extension type could try to be its own generic arg (or there could be a type loop)
//        let tcref, genericArgs = 
//            if st.PUntaint((fun st -> st.IsGenericType),m) then 
//                let tcref = ImportProvidedNamedType env m (st.PApply((fun st -> st.GetGenericTypeDefinition()),m))
//                let args = st.PApplyArray((fun st -> st.GetGenericArguments()),"GetGenericArguments",m) |> Array.map (ImportProvidedType env m (* tinst *) ) |> List.ofArray 
//                tcref,args
//            else 
//                let tcref = ImportProvidedNamedType env m st
//                tcref, [] 
//        
//        /// Adjust for the known primitive numeric types that accept units of measure. 
//        let tcref = 
//            if tyconRefEq g tcref g.system_Double_tcref && genericArgs.Length = 1 then g.pfloat_tcr
//            elif tyconRefEq g tcref g.system_Single_tcref && genericArgs.Length = 1 then g.pfloat32_tcr
//            elif tyconRefEq g tcref g.system_Decimal_tcref && genericArgs.Length = 1 then g.pdecimal_tcr
//            elif tyconRefEq g tcref g.system_Int16_tcref && genericArgs.Length = 1 then g.pint16_tcr
//            elif tyconRefEq g tcref g.system_Int32_tcref && genericArgs.Length = 1 then g.pint_tcr
//            elif tyconRefEq g tcref g.system_Int64_tcref && genericArgs.Length = 1 then g.pint64_tcr
//            elif tyconRefEq g tcref g.system_SByte_tcref && genericArgs.Length = 1 then g.pint8_tcr
//            else tcref
//        
//        let tps = tcref.Typars m
//        if tps.Length <> genericArgs.Length then 
//           error(Error(FSComp.SR.impInvalidNumberOfGenericArguments(tcref.CompiledName, tps.Length, genericArgs.Length),m))
//
//        let genericArgs = 
//            (tps,genericArgs) ||> List.map2 (fun tp genericArg ->  
//                if tp.Kind = TyparKind.Measure then  
//                    let rec conv ty = 
//                        match ty with 
//                        | TType_app (tcref,[t1;t2]) when tyconRefEq g tcref g.measureproduct_tcr -> MeasureProd (conv t1, conv t2)
//                        | TType_app (tcref,[t1]) when tyconRefEq g tcref g.measureinverse_tcr -> MeasureInv (conv t1)
//                        | TType_app (tcref,[]) when tyconRefEq g tcref g.measureone_tcr -> MeasureOne 
//                        | TType_app (tcref,[]) when tcref.TypeOrMeasureKind = TyparKind.Measure -> MeasureCon tcref
//                        | TType_app (tcref,_) -> 
//                            errorR(Error(FSComp.SR.impInvalidMeasureArgument1(tcref.CompiledName, tp.Name),m))
//                            MeasureOne
//                        | _ -> 
//                            errorR(Error(FSComp.SR.impInvalidMeasureArgument2(tp.Name),m))
//                            MeasureOne
//
//                    TType_measure (conv genericArg)
//                else
//                    genericArg)
//
//        ImportTyconRefApp env tcref genericArgs
//
//
///// Import a provided method reference as an Abstract IL method reference
//let ImportProvidedMethodBaseAsILMethodRef (env:ImportMap) (m:range) (mbase: Tainted<ProvidedMethodBase>) = 
//     let tref = ExtensionTyping.GetILTypeRefOfProvidedType (mbase.PApply((fun mbase -> mbase.DeclaringType),m), m)
//
//     let mbase = 
//         // Find the formal member corresponding to the called member
//         match mbase.OfType<ProvidedMethodInfo>() with 
//         | Some minfo when 
//                    minfo.PUntaint((fun minfo -> minfo.IsGenericMethod|| minfo.DeclaringType.IsGenericType),m) -> 
//                let declaringType = minfo.PApply((fun minfo -> minfo.DeclaringType),m)
//                let declaringGenericTypeDefn =  
//                    if declaringType.PUntaint((fun t -> t.IsGenericType),m) then 
//                        declaringType.PApply((fun declaringType -> declaringType.GetGenericTypeDefinition()),m)
//                    else 
//                        declaringType
//                let methods = declaringGenericTypeDefn.PApplyArray((fun x -> x.GetMethods()),"GetMethods",m) 
//                let metadataToken = minfo.PUntaint((fun minfo -> minfo.MetadataToken),m)
//                let found = methods |> Array.tryFind (fun x -> x.PUntaint((fun x -> x.MetadataToken),m) = metadataToken) 
//                match found with
//                |   Some found -> found.Coerce(m)
//                |   None -> 
//                        let methodName = minfo.PUntaint((fun minfo -> minfo.Name),m)
//                        let typeName = declaringGenericTypeDefn.PUntaint((fun declaringGenericTypeDefn -> declaringGenericTypeDefn.FullName),m)
//                        error(NumberedError(FSComp.SR.etIncorrectProvidedMethod(ExtensionTyping.DisplayNameOfTypeProvider(minfo.TypeProvider, m),methodName,metadataToken,typeName), m))
//         | _ -> 
//         match mbase.OfType<ProvidedConstructorInfo>() with 
//         | Some cinfo when cinfo.PUntaint((fun x -> x.DeclaringType.IsGenericType),m) -> 
//                let declaringType = cinfo.PApply((fun x -> x.DeclaringType),m)
//                let declaringGenericTypeDefn =  declaringType.PApply((fun x -> x.GetGenericTypeDefinition()),m)
//                // We have to find the uninstantiated formal signature corresponing to this instantiated constructor.
//                // Annoyingly System.Reflection doesn't give us a MetadataToken to compare on, so we have to look by doing
//                // the instantiation and comparing..
//                let found = 
//                    let ctors = declaringGenericTypeDefn.PApplyArray((fun x -> x.GetConstructors()),"GetConstructors",m) 
//                    let actualParameterTypes = 
//                        [ for p in cinfo.PApplyArray((fun x -> x.GetParameters()), "GetParameters",m) do
//                            yield ImportProvidedType env m (p.PApply((fun p -> p.ParameterType),m)) ]
//                    let actualGenericArgs = argsOfAppTy env.g (ImportProvidedType env m declaringType)
//                    ctors |> Array.tryFind (fun ctor -> 
//                       let formalParameterTypesAfterInstantiation = 
//                           [ for p in ctor.PApplyArray((fun x -> x.GetParameters()), "GetParameters",m) do
//                                let ilFormalTy = ImportProvidedTypeAsILType env m (p.PApply((fun p -> p.ParameterType),m))
//                                yield ImportILType env m actualGenericArgs ilFormalTy ]
//                       (formalParameterTypesAfterInstantiation,actualParameterTypes) ||>  List.lengthsEqAndForall2 (typeEquiv env.g))
//                     
//                match found with
//                |   Some found -> found.Coerce(m)
//                |   None -> 
//                    let typeName = declaringGenericTypeDefn.PUntaint((fun x -> x.FullName),m)
//                    error(NumberedError(FSComp.SR.etIncorrectProvidedConstructor(ExtensionTyping.DisplayNameOfTypeProvider(cinfo.TypeProvider, m),typeName), m))
//         | _ -> mbase
//
//     let rty = 
//         match mbase.OfType<ProvidedMethodInfo>() with 
//         |  Some minfo -> minfo.PApply((fun minfo -> minfo.ReturnType),m)
//         |  None ->
//            match mbase.OfType<ProvidedConstructorInfo>() with
//            | Some _  -> mbase.PApply((fun _ -> ProvidedType.Void),m)
//            | _ -> failwith "unexpected"
//     let genericArity = 
//        if mbase.PUntaint((fun x -> x.IsGenericMethod),m) then 
//            mbase.PUntaint((fun x -> x.GetGenericArguments().Length),m)
//        else 0
//     let callingConv = (if mbase.PUntaint((fun x -> x.IsStatic),m) then ILCallingConv.Static else ILCallingConv.Instance)
//     let parameters = 
//         [ for p in mbase.PApplyArray((fun x -> x.GetParameters()), "GetParameters",m) do
//              yield ImportProvidedTypeAsILType env m (p.PApply((fun p -> p.ParameterType),m)) ]
//     mkILMethRef (tref, callingConv, mbase.PUntaint((fun x -> x.Name),m), genericArity, parameters, ImportProvidedTypeAsILType env m rty )
//#endif

//-------------------------------------------------------------------------
// Load an IL assembly into the compiler's internal data structures
// Careful use is made of laziness here to ensure we don't read the entire IL
// assembly on startup.
//-------------------------------------------------------------------------- 


/// Import a set of Abstract IL generic parameter specifications as a list of new
/// F# generic parameters.  
/// 
/// Fixup the constrants so that any references to the generic parameters
/// in the constraints now refer to the new generic parameters.
let ImportILGenericParameters amap m scoref tinst (gps: ILGenericParameterDefs) = 
    match gps with 
    | [] -> []
    | _ -> 
        let amap = amap()
        let tps = gps |> List.map (fun gp -> NewRigidTypar gp.Name m) 

        let tptys = tps |> List.map mkTyparTy
        let importInst = tinst@tptys
        (tps,gps) ||> List.iter2 (fun tp gp -> 
            let constraints = gp.Constraints |> ILList.toList |> List.map (fun ilty -> TyparConstraint.CoercesTo(ImportILType amap m importInst (rescopeILType scoref ilty),m) )
            let constraints = if gp.HasReferenceTypeConstraint then (TyparConstraint.IsReferenceType(m)::constraints) else constraints
            let constraints = if gp.HasNotNullableValueTypeConstraint then (TyparConstraint.IsNonNullableStruct(m)::constraints) else constraints
            let constraints = if gp.HasDefaultConstructorConstraint then (TyparConstraint.RequiresDefaultConstructor(m)::constraints) else constraints
            tp.FixupConstraints constraints);
        tps


/// Given a list of items each keyed by an ordered list of keys, apply 'nodef' to the each group
/// with the same leading key. Apply 'tipf' to the elements where the keylist is empty, and return 
/// the overall results.  Used to bucket types, so System.Char and System.Collections.Generic.List 
/// both get initially bucketed under 'System'.
let multisetDiscriminateAndMap nodef tipf (items: ('Key list * 'Value) list) = 
    // Find all the items with an empty key list and call 'tipf' 
    let tips = 
        [ for (keylist,v) in items do 
             match keylist with 
             | [] -> yield tipf v
             | _ -> () ]

    // Find all the items with a non-empty key list. Bucket them together by
    // the first key. For each bucket, call 'nodef' on that head key and the bucket.
    let nodes = 
        let buckets = new Dictionary<_,_>(10)
        for (keylist,v) in items do
            match keylist with 
            | [] -> ()
            | key::rest -> 
                buckets.[key] <- (rest,v) :: (if buckets.ContainsKey key then buckets.[key] else []);

        [ for (KeyValue(key,items)) in buckets -> nodef key items ]

    tips @ nodes
 

/// Import an IL type definition as a new F# TAST Entity node.
let rec ImportILTypeDef amap m scoref cpath enc nm (tdef:ILTypeDef)  =
    let lazyModuleOrNamespaceTypeForNestedTypes = 
        lazy 
            let cpath = mkNestedCPath cpath nm ModuleOrType
            ImportILTypeDefs amap m scoref cpath (enc@[tdef]) tdef.NestedTypes
    // Add the type itself. 
    NewILTycon 
        (Some cpath) 
        (nm,m) 
        // The read of the type parameters may fail to resolve types. We pick up a new range from the point where that read is forced
        // Make sure we reraise the original exception one occurs - see findOriginalException.
        (LazyWithContext.Create((fun m -> ImportILGenericParameters amap m scoref [] tdef.GenericParams), ErrorLogger.findOriginalException))
        (scoref,enc,tdef) 
        lazyModuleOrNamespaceTypeForNestedTypes 
       

/// Import a list of (possibly nested) IL types as a new ModuleOrNamespaceType node
/// containing new entities, bucketing by namespace along the way.
and ImportILTypeDefList amap m cpath enc items =
    // Split into the ones with namespaces and without. Add the ones with namespaces in buckets.
    // That is, discriminate based in the first element of the namespace list (e.g. "System") 
    // and, for each bag, fold-in a lazy computation to add the types under that bag .
    //
    // nodef - called for each bucket, where 'n' is the head element of the namespace used
    // as a key in the discrimination, tgs is the remaining descriptors.  We create an entity for 'n'.
    //
    // tipf - called if there are no namespace items left to discriminate on. 
    let entities = 
        items 
        |> multisetDiscriminateAndMap 
            (fun n tgs ->
                let modty = lazy (ImportILTypeDefList amap m (mkNestedCPath cpath n Namespace) enc tgs)
                NewModuleOrNamespace (Some cpath) taccessPublic (mkSynId m n) XmlDoc.Empty [] modty)
            (fun (n,info:Lazy<_>) -> 
                let (scoref2,_,lazyTypeDef:Lazy<ILTypeDef>) = info.Force()
                ImportILTypeDef amap m scoref2 cpath enc n (lazyTypeDef.Force()))

    let kind = match enc with [] -> Namespace | _ -> ModuleOrType
    NewModuleOrNamespaceType kind entities []
      
/// Import a table of IL types as a ModuleOrNamespaceType.
///
and ImportILTypeDefs amap m scoref cpath enc (tdefs: ILTypeDefs) =
    // We be very careful not to force a read of the type defs here
    tdefs.AsListOfLazyTypeDefs
    |> List.map (fun (ns,n,attrs,lazyTypeDef) -> (ns,(n,notlazy(scoref,attrs,lazyTypeDef))))
    |> ImportILTypeDefList amap m cpath enc

/// Import the main type definitions in an IL assembly.
///
/// Example: for a collection of types "System.Char", "System.Int32" and "Library.C"
/// the return ModuleOrNamespaceType will contain namespace entities for "System" and "Library", which in turn contain
/// type definition entities for ["Char"; "Int32"]  and ["C"] respectively.  
let ImportILAssemblyMainTypeDefs amap m scoref modul = 
    modul.TypeDefs |> ImportILTypeDefs amap m scoref (CompPath(scoref,[])) [] 

/// Import the "exported types" table for multi-module assemblies. 
let ImportILAssemblyExportedType amap m auxModLoader (scoref:ILScopeRef) (exportedType:ILExportedTypeOrForwarder) = 
    // Forwarders are dealt with separately in the ref->def dereferencing logic in tast.fs as they effectively give rise to type equivalences
    if exportedType.IsForwarder then 
        []
    else
        let info = 
            lazy (match 
                    (try 
                        let modul = auxModLoader exportedType.ScopeRef
                        Some (lazy modul.TypeDefs.FindByName exportedType.Name) 
                     with :? System.Collections.Generic.KeyNotFoundException -> None)
                    with 
                  | None -> 
                     error(Error(FSComp.SR.impReferenceToDllRequiredByAssembly(exportedType.ScopeRef.QualifiedName, scoref.QualifiedName, exportedType.Name),m))
                  | Some lazyTypeDef -> 
                     scoref,exportedType.CustomAttrs,lazyTypeDef)
              
        let ns,n = splitILTypeName exportedType.Name
        [ ImportILTypeDefList amap m (CompPath(scoref,[])) [] [(ns,(n,info))]  ]

/// Import the "exported types" table for multi-module assemblies. 
let ImportILAssemblyExportedTypes amap m auxModLoader scoref (exportedTypes: ILExportedTypesAndForwarders) = 
    [ for exportedType in exportedTypes.AsList do 
         yield! ImportILAssemblyExportedType amap m auxModLoader scoref exportedType ]

/// Import both the main type definitions and the "exported types" table, i.e. all the 
/// types defined in an IL assembly.
let ImportILAssemblyTypeDefs (amap, m, auxModLoader, aref, mainmod:ILModuleDef) = 
    let scoref = ILScopeRef.Assembly aref
    let mtypsForExportedTypes = ImportILAssemblyExportedTypes amap m auxModLoader scoref mainmod.ManifestOfAssembly.ExportedTypes
    let mainmod = ImportILAssemblyMainTypeDefs amap m scoref mainmod
    combineModuleOrNamespaceTypeList [] m (mainmod :: mtypsForExportedTypes)

/// Import the type forwarder table for an IL assembly
let ImportILAssemblyTypeForwarders (amap, m, exportedTypes:ILExportedTypesAndForwarders) = 
    // Note 'td' may be in another module or another assembly!
    // Note: it is very important that we call auxModLoader lazily
    [ //printfn "reading forwarders..." 
        for exportedType in exportedTypes.AsList do 
            let ns,n = splitILTypeName exportedType.Name
            //printfn "found forwarder for %s..." n
            let tcref = lazy ImportILTypeRefUncached (amap()) m (ILTypeRef.Create(exportedType.ScopeRef,[],exportedType.Name))
            yield (Array.ofList ns,n),tcref
            let rec nested (nets:ILNestedExportedTypes) enc = 
                [ for net in nets.AsList do 
                    
                    //printfn "found nested forwarder for %s..." net.Name
                    let tcref = lazy ImportILTypeRefUncached (amap()) m (ILTypeRef.Create (exportedType.ScopeRef,enc,net.Name))
                    yield (Array.ofList enc,exportedType.Name),tcref 
                    yield! nested net.Nested (enc @ [ net.Name ]) ]
            yield! nested exportedType.Nested (ns@[n]) 
    ] |> Map.ofList
  

/// Import an IL assembly as a new TAST CCU
let ImportILAssembly(amap:(unit -> ImportMap),m,auxModuleLoader,sref,sourceDir,filename,ilModule:ILModuleDef,invalidateCcu:IEvent<string>) = 
        invalidateCcu |> ignore
        let aref =   
            match sref with 
            | ILScopeRef.Assembly aref -> aref 
            | _ -> error(InternalError("ImportILAssembly: cannot reference .NET netmodules directly, reference the containing assembly instead",m))
        let nm = aref.Name
        let mty = ImportILAssemblyTypeDefs(amap,m,auxModuleLoader,aref,ilModule)


        let ccuData = 
          { IsFSharp=false;
            UsesFSharp20PlusQuotations=false;
//#if EXTENSIONTYPING
//            InvalidateEvent=invalidateCcu;
//            IsProviderGenerated = false;
//            ImportProvidedType = (fun ty -> ImportProvidedType (amap()) m ty)
//#endif
            QualifiedName= Some sref.QualifiedName;
            Contents = NewCcuContents sref m nm mty ;
            ILScopeRef = sref;
            Stamp = newStamp();
            SourceCodeDirectory = sourceDir;  // note: not an accurate value, but IL assemblies don't give us this information in any attributes. 
            FileName = filename
            MemberSignatureEquality= (fun ty1 ty2 -> Tastops.typeEquivAux EraseAll (amap()).g ty1 ty2)
            TypeForwarders = 
               (match ilModule.Manifest with 
                | None -> Map.empty
                | Some manifest -> ImportILAssemblyTypeForwarders(amap,m,manifest.ExportedTypes)) }
                
        CcuThunk.Create(nm,ccuData)
