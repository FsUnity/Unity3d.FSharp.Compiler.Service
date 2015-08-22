﻿//----------------------------------------------------------------------------
// Copyright (c) 2002-2012 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------

namespace Microsoft.FSharp.Compiler.SourceCodeServices

open Internal.Utilities
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library
open Microsoft.FSharp.Compiler.AbstractIL.IL
open Microsoft.FSharp.Compiler.Lib
open Microsoft.FSharp.Compiler.Infos
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.Tast
open Microsoft.FSharp.Compiler.TcGlobals
open Microsoft.FSharp.Compiler.Tastops
open Microsoft.FSharp.Compiler.QuotationTranslator
open Microsoft.FSharp.Compiler.TypeRelations


[<AutoOpen>]
module ExprTranslationImpl = 
    type ExprTranslationEnv = 
        { //Map from Val to binding index
          vs: ValMap<unit>; 
          //Map from typar stamps to binding index
          tyvs: StampMap<FSharpGenericParameter>;
          // Map for values bound by the 
          //     'let v = isinst e in .... if nonnull v then ...v .... ' 
          // construct arising out the compilation of pattern matching. We decode these back to the form
          //     'if istype v then ...unbox v .... ' 
          isinstVals: ValMap<TType * Expr> 
          substVals: ValMap<Expr> }

        static member Empty = 
            { vs=ValMap<_>.Empty; 
              tyvs = Map.empty ;
              isinstVals = ValMap<_>.Empty 
              substVals = ValMap<_>.Empty }

        member env.BindTypar (v:Typar, gp) = 
            { env with tyvs = env.tyvs.Add(v.Stamp,gp ) }

        member env.BindTypars vs = 
            (env, vs) ||> List.fold (fun env v -> env.BindTypar v) // fold left-to-right because indexes are left-to-right 

        member env.BindVal v = 
            { env with vs = env.vs.Add v () }

        member env.BindIsInstVal v (ty,e) = 
            { env with isinstVals =  env.isinstVals.Add v (ty,e) }

        member env.BindSubstVal v e = 
            { env with substVals = env.substVals.Add v e  }

        member env.BindVals vs = (env,vs) ||> List.fold (fun env v -> env.BindVal v) 
        member env.BindCurriedVals vsl = (env,vsl) ||> List.fold (fun env vs -> env.BindVals vs) 

    exception IgnoringPartOfQuotedTermWarning of string * Range.range

    let wfail (msg,m:range) = failwith (msg + sprintf " at %s" (m.ToString()))


/// The core tree of data produced by converting F# compiler TAST expressions into the form which we make available through the compiler API
/// through active patterns.
type E =
    | Value  of FSharpMemberFunctionOrValue
    | ThisValue  of FSharpType 
    | BaseValue  of FSharpType 
    | Application of FSharpExpr * FSharpType list * FSharpExpr list  
    | Lambda of FSharpMemberFunctionOrValue * FSharpExpr  
    | TypeLambda of FSharpGenericParameter list * FSharpExpr  
    | Quote  of FSharpExpr  
    | IfThenElse   of FSharpExpr * FSharpExpr * FSharpExpr  
    | DecisionTree   of FSharpExpr * (FSharpMemberOrFunctionOrValue list * FSharpExpr) list
    | DecisionTreeSuccess of int * FSharpExpr list
    | Call of FSharpExpr option * FSharpMemberOrFunctionOrValue * FSharpType list * FSharpType list * FSharpExpr list 
    | NewObject of FSharpMemberOrFunctionOrValue * FSharpType list * FSharpExpr list 
    | LetRec of ( FSharpMemberFunctionOrValue * FSharpExpr) list * FSharpExpr  
    | Let of (FSharpMemberFunctionOrValue * FSharpExpr) * FSharpExpr 
    | NewRecord of FSharpType * FSharpExpr list 
    | ObjectExpr of FSharpType * FSharpExpr * FSharpObjectExprOverride list * (FSharpType * FSharpObjectExprOverride list) list
    | FSharpFieldGet of  FSharpExpr option * FSharpType * FSharpField 
    | FSharpFieldSet of  FSharpExpr option * FSharpType * FSharpField * FSharpExpr 
    | NewUnionCase of FSharpType * FSharpUnionCase * FSharpExpr list  
    | UnionCaseGet of FSharpExpr * FSharpType * FSharpUnionCase * FSharpField 
    | UnionCaseSet of FSharpExpr * FSharpType * FSharpUnionCase * FSharpField  * FSharpExpr
    | UnionCaseTag of FSharpExpr * FSharpType 
    | UnionCaseTest of FSharpExpr  * FSharpType * FSharpUnionCase 
    | TraitCall of FSharpType list * string * FSharpType list * FSharpType list * FSharpExpr list
    | NewTuple of FSharpType * FSharpExpr list  
    | TupleGet of FSharpType * int * FSharpExpr 
    | Coerce of FSharpType * FSharpExpr  
    | NewArray of FSharpType * FSharpExpr list  
    | TypeTest of FSharpType * FSharpExpr  
    | AddressSet of FSharpExpr * FSharpExpr  
    | ValueSet of FSharpMemberOrFunctionOrValue * FSharpExpr  
    | Unused
    | DefaultValue of FSharpType  
    | Const of obj * FSharpType
    | AddressOf of FSharpExpr 
    | Sequential of FSharpExpr * FSharpExpr  
    | FastIntegerForLoop of FSharpExpr * FSharpExpr * FSharpExpr * bool
    | WhileLoop of FSharpExpr * FSharpExpr  
    | TryFinally of FSharpExpr * FSharpExpr  
    | TryWith of FSharpExpr * FSharpMemberFunctionOrValue * FSharpExpr * FSharpMemberFunctionOrValue * FSharpExpr  
    | NewDelegate of FSharpType * FSharpExpr  
    | ILFieldGet of FSharpExpr option * FSharpType * string 
    | ILFieldSet of FSharpExpr option * FSharpType * string  * FSharpExpr 
    | ILAsm of string * FSharpType list * FSharpExpr list

/// Used to represent the information at an object expression member 
and [<Sealed>]  FSharpObjectExprOverride(gps: FSharpGenericParameter list, args:FSharpMemberFunctionOrValue list list, body: FSharpExpr) = 
    member __.GenericParameters = gps
    member __.CurriedParameterGroups = args
    member __.Body = body

/// The type of expressions provided through the compiler API.
and [<Sealed>] FSharpExpr (cenv, f: (unit -> FSharpExpr) option, e: E, m:range, ty) =

    member x.Range = m
    member x.Type = FSharpType(cenv, ty)
    member x.cenv = cenv
    member x.E = match f with None -> e | Some f -> f().E
    override x.ToString() = sprintf "%+A" x.E

    member x.ImmediateSubExpressions = 
        match x.E with 
        | E.Value _v -> []
        | E.Const (_constValue, _ty) -> []
        | E.TypeLambda (_v, body) -> [body]
        | E.Lambda (_v, body) -> [body]
        | E.Application (f, _tyargs, arg) -> f :: arg
        | E.IfThenElse (e1, e2, e3) -> [e1;e2;e3]
        | E.Let ((_bindingVar, bindingExpr), b) -> [bindingExpr;b]
        | E.LetRec (ves, b) -> (List.map snd ves) @ [b]
        | E.NewRecord (_recordType, es) -> es
        | E.NewUnionCase (_unionType, _unionCase, es) -> es
        | E.NewTuple (_tupleType, es) -> es
        | E.TupleGet (_tupleType, _itemIndex, tupleExpr) -> [tupleExpr]
        | E.Call (objOpt, _b, _c, _d, es) -> (match objOpt with None -> es | Some x -> x::es)
        | E.NewObject (_a, _b, c) -> c
        | E.FSharpFieldGet (objOpt, _b, _c) -> (match objOpt with None -> [] | Some x -> [x])
        | E.FSharpFieldSet (objOpt, _b, _c, d) -> (match objOpt with None -> [d] | Some x -> [x;d])
        | E.UnionCaseGet (obj, _b, _c, _d) -> [obj]
        | E.UnionCaseTag (obj, _b) -> [obj]
        | E.UnionCaseTest (obj, _b, _c) -> [obj]
        | E.NewArray (_ty, elems) -> elems
        | E.Coerce (_ty, b) -> [b]
        | E.Quote (a) -> [a]
        | E.TypeTest (_ty, b) -> [b]
        | E.Sequential (a, b) -> [a;b]
        | E.FastIntegerForLoop (a, b, c, _dir) -> [a;b;c]
        | E.WhileLoop (guard, body) -> [guard; body]
        | E.TryFinally (body, b) -> [body; b]
        | E.TryWith (body, _b, _c, _d, handler) -> [body; handler]
        | E.NewDelegate (_ty, body) -> [body]
        | E.DefaultValue (_ty) -> []
        | E.AddressSet (lvalueExpr, rvalueExpr) -> [lvalueExpr; rvalueExpr]
        | E.ValueSet (_v, rvalueExpr) -> [rvalueExpr]
        | E.AddressOf (lvalueExpr) -> [lvalueExpr]
        | E.ThisValue (_ty) -> []
        | E.BaseValue (_ty) -> []
        | E.ILAsm (_code, _tyargs, argExprs) -> argExprs
        | E.ILFieldGet (objOpt, _ty, _fieldName) -> (match objOpt with None -> [] | Some x -> [x])
        | E.ILFieldSet (objOpt, _ty, _fieldName, d) -> (match objOpt with None -> [d] | Some x -> [x;d])
        | E.ObjectExpr (_ty, basecall, overrides, interfaceImpls) -> 
             [ yield basecall; 
               for m in overrides do yield m.Body
               for (_, ms) in interfaceImpls do for m in ms do yield m.Body ]
        | E.DecisionTree (inputExpr, targetCases) -> 
            [ yield inputExpr; 
              for (_targetVars, targetExpr) in targetCases do yield targetExpr ]
        | E.DecisionTreeSuccess (_targetNumber, targetArgs) -> targetArgs
        | E.UnionCaseSet (obj, _unionType, _unionCase, _unionField, valueExpr) -> [ yield obj; yield valueExpr ]
        | E.TraitCall (_sourceTypes, _traitName, _paramTypes, _retTypes, args) -> args
        | E.Unused -> [] // unexpected


/// The implementation of the conversion operation
module FSharpExprConvert =

    let IsStaticInitializationField (rfref: RecdFieldRef)  = 
        rfref.RecdField.IsCompilerGenerated && 
        rfref.RecdField.IsStatic &&
        rfref.RecdField.IsMutable &&
        rfref.RecdField.Name.StartsWith "init" 

        // Match "if [AI_clt](init@41,6) then IntrinsicFunctions.FailStaticInit () else ()"
    let (|StaticInitializationCheck|_|) e = 
        match e with 
        | Expr.Match (_,_,TDSwitch(Expr.Op(TOp.ILAsm ([ AI_clt ],_),_,[Expr.Op(TOp.ValFieldGet rfref,_,_,_) ;_],_),_,_,_),_,_,_) when IsStaticInitializationField rfref -> Some ()
        | _ -> None

        // Match "init@41 <- 6"
    let (|StaticInitializationCount|_|) e = 
        match e with 
        | Expr.Op(TOp.ValFieldSet rfref,_,_,_)  when IsStaticInitializationField rfref -> Some ()
        | _ -> None

    let ConvType cenv typ = FSharpType(cenv, typ)
    let ConvTypes cenv typs = List.map (ConvType cenv) typs
    let ConvILTypeRefApp (cenv:Impl.cenv) m tref tyargs = 
        let tcref = Import.ImportILTypeRef cenv.amap m tref
        ConvType cenv (mkAppTy tcref tyargs)

    let ConvUnionCaseRef cenv (ucref:UnionCaseRef) = FSharpUnionCase(cenv, ucref)
    let ConvRecdFieldRef cenv (rfref:RecdFieldRef) = FSharpField(cenv,rfref )

    let rec exprOfExprAddr (cenv:Impl.cenv) expr = 
        match expr with 
        | Expr.Op(op,tyargs,args,m) -> 
            match op, args, tyargs  with
            | TOp.LValueOp(LGetAddr,vref),_,_ -> exprForValRef m vref
            | TOp.ValFieldGetAddr(rfref),[],_ -> mkStaticRecdFieldGet(rfref,tyargs,m)
            | TOp.ValFieldGetAddr(rfref),[arg],_ -> mkRecdFieldGetViaExprAddr(exprOfExprAddr cenv arg,rfref,tyargs,m)
            | TOp.ILAsm([ I_ldflda(fspec) ],rtys),[arg],_  -> mkAsmExpr([ mkNormalLdfld(fspec) ],tyargs, [exprOfExprAddr cenv arg], rtys, m)
            | TOp.ILAsm([ I_ldsflda(fspec) ],rtys),_,_  -> mkAsmExpr([ mkNormalLdsfld(fspec) ],tyargs, args, rtys, m)
            | TOp.ILAsm(([ I_ldelema(_ro,_isNativePtr,shape,_tyarg) ] ),_), (arr::idxs), [elemty]  -> 
                match shape.Rank, idxs with 
                | 1, [idx1] -> mkCallArrayGet cenv.g m elemty arr idx1
                | 2, [idx1; idx2] -> mkCallArray2DGet cenv.g m elemty arr idx1 idx2
                | 3, [idx1; idx2; idx3] -> mkCallArray3DGet cenv.g m elemty arr idx1 idx2 idx3
                | 4, [idx1; idx2; idx3; idx4] -> mkCallArray4DGet cenv.g m elemty arr idx1 idx2 idx3 idx4
                | _ -> expr
            | _ -> expr
        | _ -> expr


    let Mk cenv m ty e = FSharpExpr(cenv, None, e, m, ty)

    let Mk2 cenv (orig:Expr) e = FSharpExpr(cenv, None, e, orig.Range, tyOfExpr cenv.g orig)

    let rec ConvLValueExpr (cenv:Impl.cenv) env expr = ConvExpr cenv env (exprOfExprAddr cenv expr)

    and ConvExpr cenv env expr = 
        Mk2 cenv expr (ConvExprPrim cenv env expr) 

    and ConvExprLinear cenv env expr contf = 
        ConvExprPrimLinear cenv env expr (fun exprR -> contf (Mk2 cenv expr exprR))

    // Tail recursive function to process the subset of expressions considered "linear"
    and ConvExprPrimLinear cenv env expr contf = 

        match expr with 
        // Large lists 
        | Expr.Op(TOp.UnionCase ucref,tyargs,[e1;e2],_) -> 
            let mkR = ConvUnionCaseRef cenv ucref 
            let typR = ConvType cenv (mkAppTy ucref.TyconRef tyargs)
            let e1R = ConvExpr cenv env e1
            // tail recursive 
            ConvExprLinear cenv env e2 (contf << (fun e2R -> E.NewUnionCase(typR, mkR, [e1R; e2R]) ))

        // Large sequences of let bindings
        | Expr.Let (bind,body,_,_) ->  
            match ConvLetBind cenv env bind with 
            | None, env -> ConvExprPrimLinear cenv env body contf
            | Some(bindR),env -> 
                // tail recursive 
                ConvExprLinear cenv env body (contf << (fun bodyR -> E.Let(bindR,bodyR)))

        // Remove initialization checks
        // Remove static initialization counter updates
        // Remove static initialization counter checks
        //
        // Put in ConvExprPrimLinear because of the overlap with Expr.Sequential below
        //
        // TODO: allow clients to see static initialization checks if they want to
        | Expr.Sequential(ObjectInitializationCheck cenv.g, x1, NormalSeq, _, _) 
        | Expr.Sequential  (StaticInitializationCount,x1,NormalSeq,_,_)              
        | Expr.Sequential  (StaticInitializationCheck,x1,NormalSeq,_,_) ->
            ConvExprPrim cenv env x1 |> contf

        // Large sequences of sequential code
        | Expr.Sequential (e1,e2,NormalSeq,_,_)  -> 
            let e1R = ConvExpr cenv env e1
            // tail recursive 
            ConvExprLinear cenv env e2 (contf << (fun e2R -> E.Sequential(e1R, e2R)))

        | Expr.Sequential  (x0,x1,ThenDoSeq,_,_) ->  E.Sequential(ConvExpr cenv env x0, ConvExpr cenv env x1) 

        | ModuleValueOrMemberUse cenv.g (vref,vFlags,_f,_fty,tyargs,curriedArgs) when (nonNil tyargs || nonNil curriedArgs) && vref.IsMemberOrModuleBinding ->
            ConvModuleValueOrMemberUseLinear cenv env (expr,vref,vFlags,tyargs,curriedArgs) contf

        | Expr.Match (_spBind,m,dtree,tgs,_,retTy) ->
            let dtreeR = ConvDecisionTree cenv env retTy dtree m
            // tailcall 
            ConvTargetsLinear cenv env (List.ofArray tgs) (contf << fun (targetsR: _ list) -> 
                let (|E|) (x:FSharpExpr) = x.E

                // If the match is really an "if-then-else" then return it as such.
                match dtreeR with 
                | E(E.IfThenElse(a,E(E.DecisionTreeSuccess(0,[])), E(E.DecisionTreeSuccess(1,[])))) -> E.IfThenElse(a,snd targetsR.[0],snd targetsR.[1])
                | _ -> E.DecisionTree(dtreeR,targetsR))

        | _ -> 
            ConvExprPrim cenv env expr |> contf


    /// A nasty function copied from creflect.fs. Made nastier by taking a continuation to process the 
    /// arguments to the call in a tail-recursive fashion.
    and ConvModuleValueOrMemberUseLinear (cenv:Impl.cenv) env (expr:Expr,vref,vFlags,tyargs,curriedArgs) contf =
        let m = expr.Range 

        let (numEnclTypeArgs,_,isNewObj,_valUseFlags,_isSelfInit,takesInstanceArg,_isPropGet,_isPropSet) = 
            GetMemberCallInfo cenv.g (vref,vFlags)

        let isMember,curriedArgInfos = 

            match vref.MemberInfo with 
            | Some _ when not vref.IsExtensionMember -> 
                // This is an application of a member method
                // We only count one argument block for these.
                let _tps,curriedArgInfos,_,_ = GetTypeOfMemberInFSharpForm cenv.g vref 
                true,curriedArgInfos
            | _ -> 
                // This is an application of a module value or extension member
                let arities = arityOfVal vref.Deref 
                let _tps,curriedArgInfos,_,_ = GetTopValTypeInFSharpForm cenv.g arities vref.Type m
                false,curriedArgInfos

        // Compute the object arguments as they appear in a compiled call
        // Strip off the object argument, if any. The curriedArgInfos are already adjusted to compiled member form
        let objArgs,curriedArgs = 
            match takesInstanceArg,curriedArgs with 
            | false,curriedArgs -> [],curriedArgs
            | true,(objArg::curriedArgs) -> [objArg],curriedArgs
            | true,[] -> failwith ("warning: unexpected missing object argument when generating quotation for call to F# object member "+vref.LogicalName)

        // Check to see if there aren't enough arguments or if there is a tuple-arity mismatch
        // If so, adjust and try again
        if curriedArgs.Length < curriedArgInfos.Length ||
            ((List.take curriedArgInfos.Length curriedArgs,curriedArgInfos) ||> List.exists2 (fun arg argInfo -> (argInfo.Length > (tryDestTuple arg).Length))) then

            // Too few arguments or incorrect tupling? Convert to a lambda and beta-reduce the 
            // partially applied arguments to 'let' bindings 
            let topValInfo = 
                match vref.ValReprInfo with 
                | None -> failwith ("no arity information found for F# value "+vref.LogicalName)
                | Some a -> a 

            let expr,exprty = AdjustValForExpectedArity cenv.g m vref vFlags topValInfo 
            let splitCallExpr = MakeApplicationAndBetaReduce cenv.g (expr,exprty,[tyargs],curriedArgs,m)
            // tailcall
            ConvExprPrimLinear cenv env splitCallExpr contf

        else        
            let curriedArgs,laterArgs = List.chop curriedArgInfos.Length curriedArgs 

            // detuple the args
            let untupledCurriedArgs = 
                (curriedArgs,curriedArgInfos) ||> List.map2 (fun arg curriedArgInfo -> 
                    let numUntupledArgs = curriedArgInfo.Length 
                    (if numUntupledArgs = 0 then [] 
                        elif numUntupledArgs = 1 then [arg] 
                        else tryDestTuple arg))

            let contf2 = 
                match laterArgs with 
                | [] -> contf 
                | _ -> (fun subCallR -> (subCallR, laterArgs) ||> List.fold (fun fR arg -> E.Application (Mk2 cenv arg fR,[],[ConvExpr cenv env arg])))
                    
            if isMember then 
                let callArgs = (objArgs::untupledCurriedArgs) |> List.concat
                let enclTyArgs, methTyArgs = List.splitAfter numEnclTypeArgs tyargs
                // tailcall
                ConvObjectModelCallLinear cenv env (isNewObj, FSharpMemberFunctionOrValue(cenv,vref), enclTyArgs, methTyArgs, callArgs) contf2
            else
                let v = FSharpMemberOrFunctionOrValue(cenv, vref)
                // tailcall
                ConvObjectModelCallLinear cenv env (false, v, [], tyargs, List.concat untupledCurriedArgs) contf2

     and ConvExprPrim (cenv:Impl.cenv) (env:ExprTranslationEnv) expr = 
        // Eliminate integer 'for' loops 
        let expr = DetectFastIntegerForLoops cenv.g expr

        // Eliminate subsumption coercions for functions. This must be done post-typechecking because we need
        // complete inference types.
        let expr = NormalizeAndAdjustPossibleSubsumptionExprs cenv.g expr

        // Remove TExpr_ref nodes
        let expr = stripExpr expr 

        match expr with 
        
        // Uses of possibly-polymorphic values which were not polymorphic in the end
        | Expr.App(InnerExprPat(Expr.Val _ as ve),_fty,[],[],_) -> 
            ConvExprPrim cenv env ve

        // These cases are the start of a "linear" sequence where we use tail recursion to allow use to 
        // deal with large expressions.
        | Expr.Op(TOp.UnionCase _,_,[_;_],_) // big lists
        | Expr.Let _   // big linear sequences of 'let'
        | Expr.Match _   // big linear sequences of 'match ... -> ....' 
        | Expr.Sequential _ ->
            ConvExprPrimLinear cenv env expr (fun e -> e)

        | Expr.Val(vref,_vFlags,m) -> 
            ConvValRef cenv env m vref 

        | ModuleValueOrMemberUse cenv.g (vref,vFlags,_f,_fty,tyargs,curriedArgs) when (nonNil tyargs || nonNil curriedArgs) && vref.IsMemberOrModuleBinding ->
            // Process applications of top-level values in a tail-recursive way
            ConvModuleValueOrMemberUseLinear cenv env (expr,vref,vFlags,tyargs,curriedArgs) (fun e -> e)

        // Simple applications 
        | Expr.App(f,_fty,tyargs,args,_m) -> 
            E.Application (ConvExpr cenv env f, ConvTypes cenv tyargs, ConvExprs cenv env args) 
    
        | Expr.Const(c,m,ty) -> 
            ConvConst cenv env m c ty

        | Expr.LetRec(binds,body,_,_) -> 
            let vs = valsOfBinds binds
            let vsR = vs |> FlatList.map (ConvVal cenv)
            let env = env.BindVals vs
            let bodyR = ConvExpr cenv env body 
            let bindsR = FlatList.zip vsR (binds |> FlatList.map (fun b -> b.Expr |> ConvExpr cenv env))
            E.LetRec(FlatList.toList bindsR,bodyR) 
  
        | Expr.Lambda(_,_,_,vs,b,_,_) -> 
            let v,b = MultiLambdaToTupledLambda vs b 
            let vR = ConvVal cenv v 
            let bR  = ConvExpr cenv (env.BindVal v) b 
            E.Lambda(vR, bR) 

        | Expr.Quote(ast,_,_,_,_) -> 
            E.Quote(ConvExpr cenv env ast) 

        | Expr.TyLambda (_,tps,b,_,_) -> 
            let gps = [ for tp in tps -> FSharpGenericParameter(cenv,tp) ]
            let env = env.BindTypars (Seq.zip tps gps |> Seq.toList)
            E.TypeLambda(gps, ConvExpr cenv env b) 

        | Expr.Obj (_,typ,_,_,[TObjExprMethod(TSlotSig(_,ctyp, _,_,_,_),_,tps,[tmvs],e,_) as tmethod],_,m) when isDelegateTy cenv.g typ -> 
            let f = mkLambdas m tps tmvs (e,GetFSharpViewOfReturnType cenv.g (returnTyOfMethod cenv.g tmethod))
            let fR = ConvExpr cenv env f 
            let tyargR = ConvType cenv ctyp 
            E.NewDelegate(tyargR, fR) 

        | Expr.StaticOptimization (_,_,x,_) -> 
            ConvExprPrim cenv env x

        | Expr.TyChoose _  -> 
            ConvExprPrim cenv env (ChooseTyparSolutionsForFreeChoiceTypars cenv.g cenv.amap expr)

        | Expr.Obj (_lambdaId,typ,_basev,basecall,overrides, iimpls,_m)      -> 
            let basecallR = ConvExpr cenv env basecall
            let ConvertMethods methods = 
                [ for (TObjExprMethod(_slotsig,_,tps,tmvs,body,_)) in methods -> 
                    let vslR = List.map (List.map (ConvVal cenv)) tmvs 
                    let tpsR = [ for tp in tps -> FSharpGenericParameter(cenv,tp) ]
                    let env = ExprTranslationEnv.Empty.BindTypars (Seq.zip tps tpsR |> Seq.toList)
                    let env = env.BindCurriedVals tmvs
                    let bodyR = ConvExpr cenv env body
                    FSharpObjectExprOverride(tpsR, vslR, bodyR) ]
            let overridesR = ConvertMethods overrides 
            let iimplsR = List.map (fun (ty,impls) -> ConvType cenv ty, ConvertMethods impls) iimpls

            E.ObjectExpr(ConvType cenv typ, basecallR, overridesR, iimplsR)

        | Expr.Op(op,tyargs,args,m) -> 
            match op,tyargs,args with 
            | TOp.UnionCase ucref,_,_ -> 
                let mkR = ConvUnionCaseRef cenv ucref 
                let typR = ConvType cenv (mkAppTy ucref.TyconRef tyargs)
                let argsR = ConvExprs cenv env args
                E.NewUnionCase(typR, mkR, argsR) 

            | TOp.Tuple,tyargs,_ -> 
                let tyR = ConvType cenv (mkTupledTy cenv.g tyargs)
                let argsR = ConvExprs cenv env args
                E.NewTuple(tyR, argsR) 

            | TOp.Recd (_,tcref),_,_  -> 
                let typR = ConvType cenv (mkAppTy tcref tyargs)
                let argsR = ConvExprs cenv env args
                E.NewRecord(typR, argsR) 

            | TOp.UnionCaseFieldGet (ucref,n),tyargs,[e1] -> 
                let mkR = ConvUnionCaseRef cenv ucref 
                let typR = ConvType cenv (mkAppTy ucref.TyconRef tyargs)
                let projR = FSharpField(cenv, ucref, n)
                E.UnionCaseGet(ConvExpr cenv env e1, typR, mkR, projR) 

            | TOp.UnionCaseFieldSet (ucref,n),tyargs,[e1;e2] -> 
                let mkR = ConvUnionCaseRef cenv ucref 
                let typR = ConvType cenv (mkAppTy ucref.TyconRef tyargs)
                let projR = FSharpField(cenv, ucref, n)
                E.UnionCaseSet(ConvExpr cenv env e1, typR, mkR, projR, ConvExpr cenv env e2) 

            | TOp.ValFieldGetAddr(_rfref),_tyargs,_ -> 
                E.AddressOf(ConvLValueExpr cenv env expr)

            | TOp.ValFieldGet(rfref),tyargs,[] ->
                let projR = ConvRecdFieldRef cenv rfref 
                let typR = ConvType cenv (mkAppTy rfref.TyconRef tyargs)
                E.FSharpFieldGet(None, typR, projR) 

            | TOp.ValFieldGet(rfref),tyargs,[obj] ->
                let objR = ConvLValueExpr cenv env obj
                let projR = ConvRecdFieldRef cenv rfref 
                let typR = ConvType cenv (mkAppTy rfref.TyconRef tyargs)
                E.FSharpFieldGet(Some objR, typR, projR) 

            | TOp.TupleFieldGet(n),tyargs,[e] -> 
                let tyR = ConvType cenv (mkTupledTy cenv.g tyargs)
                E.TupleGet(tyR, n, ConvExpr cenv env e) 

            | TOp.ILAsm([ I_ldfld(_,_,fspec) ],_), enclTypeArgs, [obj] -> 
                let typR = ConvILTypeRefApp cenv m fspec.EnclosingTypeRef enclTypeArgs 
                let objR = ConvLValueExpr cenv env obj
                E.ILFieldGet(Some objR, typR, fspec.Name) 

            | TOp.ILAsm(( [ I_ldsfld (_,fspec) ] | [ I_ldsfld (_,fspec); AI_nop ]),_),enclTypeArgs,[]  -> 
                let typR = ConvILTypeRefApp cenv m fspec.EnclosingTypeRef enclTypeArgs 
                E.ILFieldGet(None, typR, fspec.Name) 

            | TOp.ILAsm([ I_stfld(_,_,fspec) ],_),enclTypeArgs,[obj;arg]  -> 
                let typR = ConvILTypeRefApp cenv m fspec.EnclosingTypeRef enclTypeArgs 
                let objR = ConvLValueExpr cenv env obj
                let argR = ConvExpr cenv env arg
                E.ILFieldSet(Some objR, typR, fspec.Name, argR) 

            | TOp.ILAsm([ I_stsfld(_,fspec) ],_),enclTypeArgs,[arg]  -> 
                let typR = ConvILTypeRefApp cenv m fspec.EnclosingTypeRef enclTypeArgs 
                let argR = ConvExpr cenv env arg
                E.ILFieldSet(None, typR, fspec.Name, argR) 


            | TOp.ILAsm([ AI_ceq ],_),_,[arg1;arg2]  -> 
                let ty = tyOfExpr cenv.g arg1
                let eq = mkCallEqualsOperator cenv.g m ty arg1 arg2
                ConvExprPrim cenv env eq

            | TOp.ILAsm([ I_throw ],_),_,[arg1]  -> 
                let raiseExpr = mkCallRaise cenv.g m (tyOfExpr cenv.g expr) arg1 
                ConvExprPrim cenv env raiseExpr        

            | TOp.ILAsm(il,_),tyargs,args                         -> 
                E.ILAsm(sprintf "%+A" il, ConvTypes cenv tyargs, ConvExprs cenv env args)

            | TOp.ExnConstr tcref,tyargs,args              -> 
                E.NewRecord(ConvType cenv (mkAppTy tcref tyargs), ConvExprs cenv env args) 

            | TOp.ValFieldSet rfref, _tinst,[obj;arg]     -> 
                let objR = ConvLValueExpr cenv env obj
                let argR = ConvExpr cenv env arg
                let typR = ConvType cenv (mkAppTy rfref.TyconRef tyargs)
                let projR = ConvRecdFieldRef cenv rfref 
                E.FSharpFieldSet(Some objR, typR, projR, argR) 

            | TOp.ValFieldSet rfref, _tinst,[arg]     -> 
                let argR = ConvExpr cenv env arg
                let typR = ConvType cenv (mkAppTy rfref.TyconRef tyargs)
                let projR = ConvRecdFieldRef cenv rfref 
                E.FSharpFieldSet(None, typR, projR, argR) 

            | TOp.ExnFieldGet(tcref,i),[],[obj] -> 
                let exnc = stripExnEqns tcref
                let fspec = exnc.TrueInstanceFieldsAsList.[i]
                let fref = mkRecdFieldRef tcref fspec.Name
                let typR = ConvType cenv (mkAppTy tcref tyargs)
                let objR = ConvExpr cenv env (mkCoerceExpr (obj, mkAppTy tcref [], m, cenv.g.exn_ty))
                E.FSharpFieldGet(Some objR, typR, ConvRecdFieldRef cenv fref) 

            | TOp.ExnFieldSet(tcref,i),[],[obj;e2] -> 
                let exnc = stripExnEqns tcref
                let fspec = exnc.TrueInstanceFieldsAsList.[i]
                let fref = mkRecdFieldRef tcref fspec.Name
                let typR = ConvType cenv (mkAppTy tcref tyargs)
                let objR = ConvExpr cenv env (mkCoerceExpr (obj, mkAppTy tcref [], m, cenv.g.exn_ty))
                E.FSharpFieldSet(Some objR, typR, ConvRecdFieldRef cenv fref, ConvExpr cenv env e2) 

            | TOp.Coerce,[tgtTy;srcTy],[x]  -> 
                if typeEquiv cenv.g tgtTy srcTy then 
                    ConvExprPrim cenv env x
                else
                    E.Coerce(ConvType cenv tgtTy,ConvExpr cenv env x) 

            | TOp.Reraise,[toTy],[]         -> 
                // rebuild reraise<T>() and Convert 
                mkReraiseLibCall cenv.g toTy m |> ConvExprPrim cenv env 

            | TOp.LValueOp(LGetAddr,vref),[],[] -> 
                E.AddressOf(ConvExpr cenv env (exprForValRef m vref)) 

            | TOp.LValueOp(LByrefSet,vref),[],[e] -> 
                E.AddressSet(ConvExpr cenv env (exprForValRef m vref), ConvExpr cenv env e) 

            | TOp.LValueOp(LSet,vref),[],[e] -> 
                E.ValueSet(FSharpMemberOrFunctionOrValue(cenv, vref), ConvExpr cenv env e) 

            | TOp.LValueOp(LByrefGet,vref),[],[] -> 
                ConvValRef cenv env m vref 

            | TOp.Array,[ty],xa -> 
                    E.NewArray(ConvType cenv ty,ConvExprs cenv env xa)                             

            | TOp.While _,[],[Expr.Lambda(_,_,_,[_],test,_,_);Expr.Lambda(_,_,_,[_],body,_,_)]  -> 
                    E.WhileLoop(ConvExpr cenv env test, ConvExpr cenv env body) 
        
            | TOp.For(_, (FSharpForLoopUp |FSharpForLoopDown as dir) ), [], [Expr.Lambda(_,_,_,[_], lim0,_,_); Expr.Lambda(_,_,_,[_], SimpleArrayLoopUpperBound, lm,_); SimpleArrayLoopBody cenv.g (arr, elemTy, body)] ->
                let lim1 = 
                    let len = mkCallArrayLength cenv.g lm elemTy arr // Array.length arr
                    mkCallSubtractionOperator cenv.g lm cenv.g.int32_ty len (Expr.Const(Const.Int32 1, m, cenv.g.int32_ty)) // len - 1
                E.FastIntegerForLoop(ConvExpr cenv env lim0, ConvExpr cenv env lim1, ConvExpr cenv env body, (dir = FSharpForLoopUp)) 

            | TOp.For(_,dir),[],[Expr.Lambda(_,_,_,[_],lim0,_,_);Expr.Lambda(_,_,_,[_],lim1,_,_);body]  -> 
                match dir with 
                | FSharpForLoopUp -> E.FastIntegerForLoop(ConvExpr cenv env lim0,ConvExpr cenv env lim1, ConvExpr cenv env body,true) 
                | FSharpForLoopDown -> E.FastIntegerForLoop(ConvExpr cenv env lim0,ConvExpr cenv env lim1, ConvExpr cenv env body,false) 
                | _ -> failwith "unexpected for-loop form"

            | TOp.ILCall(_,_,_,isNewObj,_valUseFlags,_isProp,_,ilMethRef,enclTypeArgs,methTypeArgs,_tys),[],callArgs -> 
                let tcref = Import.ImportILTypeRef cenv.amap m ilMethRef.EnclosingTypeRef
                let mdef = 
                    try resolveILMethodRefWithRescope (rescopeILType (p13 tcref.ILTyconInfo)) tcref.ILTyconRawMetadata ilMethRef
                    with _ -> failwith (sprintf "A call to '%s' could not be resolved" (ilMethRef.ToString()))
                let minfo = MethInfo.CreateILMeth(cenv.amap, m, generalizedTyconRef tcref, mdef) 
                let v = FSharpMemberFunctionOrValue(cenv, minfo)
                ConvObjectModelCallLinear cenv env (isNewObj, v, enclTypeArgs, methTypeArgs, callArgs) (fun e -> e)

            | TOp.TryFinally _,[_resty],[Expr.Lambda(_,_,_,[_],e1,_,_); Expr.Lambda(_,_,_,[_],e2,_,_)] -> 
                E.TryFinally(ConvExpr cenv env e1,ConvExpr cenv env e2) 

            | TOp.TryCatch _,[_resty],[Expr.Lambda(_,_,_,[_],e1,_,_); Expr.Lambda(_,_,_,[vf],ef,_,_); Expr.Lambda(_,_,_,[vh],eh,_,_)] -> 
                let vfR = ConvVal cenv vf
                let envf = env.BindVal vf
                let vhR = ConvVal cenv vh
                let envh = env.BindVal vh
                E.TryWith(ConvExpr cenv env e1,vfR,ConvExpr cenv envf ef,vhR,ConvExpr cenv envh eh) 

            | TOp.Bytes bytes,[],[] -> E.Const(box bytes, ConvType cenv (tyOfExpr cenv.g expr))

            | TOp.UInt16s arr,[],[] -> E.Const(box arr, ConvType cenv (tyOfExpr cenv.g expr))
              
            | TOp.UnionCaseProof _,_,[e]       -> ConvExprPrim cenv env e  // Note: we erase the union case proof conversions when converting to quotations
            | TOp.UnionCaseTagGet tycr,tyargs,[arg1]          -> 
                let typR = ConvType cenv (mkAppTy tycr tyargs)
                E.UnionCaseTag(ConvExpr cenv env arg1, typR) 

            | TOp.TraitCall (TTrait(tys,nm,_memFlags,argtys,_rty,_colution)),_,_                    -> 
                let tysR = ConvTypes cenv tys
                let tyargsR = ConvTypes cenv tyargs
                let argtysR = ConvTypes cenv argtys
                let argsR = ConvExprs cenv env args
                E.TraitCall(tysR, nm, argtysR, tyargsR, argsR) 

            | TOp.RefAddrGet,[ty],[e]  -> 
                let replExpr = mkRecdFieldGetAddrViaExprAddr(e, mkRefCellContentsRef cenv.g, [ty],m)
                ConvExprPrim cenv env replExpr

            | _ -> wfail (sprintf "unhandled construct in AST", m)
        | _ -> 
            wfail (sprintf "unhandled construct in AST", expr.Range)


    and ConvLetBind cenv env (bind : Binding) = 
        match bind.Expr with 
        // Map for values bound by the 
        //     'let v = isinst e in .... if nonnull v then ...v .... ' 
        // construct arising out the compilation of pattern matching. We decode these back to the form
        //     'if istype e then ...unbox e .... ' 
        // It's bit annoying that pattern matching does this tranformation. Like all premature optimization we pay a 
        // cost here to undo it.
        | Expr.Op(TOp.ILAsm([ I_isinst _ ],_),[ty],[e],_) -> 
            None, env.BindIsInstVal bind.Var (ty,e)
    
        // Remove let <compilerGeneratedVar> = <var> from quotation tree
        | Expr.Val _ when bind.Var.IsCompilerGenerated -> 
            None, env.BindSubstVal bind.Var bind.Expr

        // Remove let <compilerGeneratedVar> = () from quotation tree
        | Expr.Const(Const.Unit,_,_) when bind.Var.IsCompilerGenerated -> 
            None, env.BindSubstVal bind.Var bind.Expr

        // Remove let unionCase = ... from quotation tree
        | Expr.Op(TOp.UnionCaseProof _,_,[e],_) -> 
            None, env.BindSubstVal bind.Var e

        | _ ->
            let v = bind.Var
            let vR = ConvVal cenv v 
            let rhsR = ConvExpr cenv env bind.Expr
            let envinner = env.BindVal v
            Some(vR,rhsR),envinner

    and ConvObjectModelCallLinear cenv env (isNewObj, v:FSharpMemberFunctionOrValue, enclTyArgs, methTyArgs,callArgs) contf =
        let enclTyArgsR = ConvTypes cenv enclTyArgs
        let methTyArgsR = ConvTypes cenv methTyArgs
        let obj, callArgs = 
            if v.IsInstanceMember then 
                match callArgs with 
                | obj :: rest -> Some obj, rest
                | _ -> failwith (sprintf "unexpected shape of arguments: %A" callArgs)
            else
                None, callArgs
        let objR = Option.map (ConvLValueExpr cenv env) obj
        // tailcall
        ConvExprsLinear cenv env callArgs (contf << fun callArgsR -> 
            if isNewObj then 
                E.NewObject(v, enclTyArgsR, callArgsR) 
            else 
                E.Call(objR, v, enclTyArgsR, methTyArgsR, callArgsR))


    and ConvExprs cenv env args = List.map (ConvExpr cenv env) args 

    // Process a list of expressions in a tail-recursive way. Identical to "ConvExprs" but the result is eventually passed to contf.
    and ConvExprsLinear cenv env args contf = 
        match args with 
        | [] -> contf []
        | [arg] -> ConvExprLinear cenv env arg (fun argR -> contf [argR])
        | arg::rest -> ConvExprLinear cenv env arg (fun argR -> ConvExprsLinear cenv env rest (fun restR -> contf (argR :: restR)))

    and ConvTargetsLinear cenv env tgs contf = 
        match tgs with 
        | [] -> contf []
        | TTarget(vars,rhs,_)::rest -> 
            let varsR = (List.rev vars) |> List.map (ConvVal cenv)
            ConvExprLinear cenv env rhs (fun targetR -> 
            ConvTargetsLinear cenv env rest (fun restR -> 
            contf ((varsR, targetR) :: restR)))

    and ConvValRef cenv env m (vref:ValRef) =
        let v = vref.Deref
        if env.isinstVals.ContainsVal v then 
            let (ty,e) = env.isinstVals.[v]
            ConvExprPrim cenv env (mkCallUnbox cenv.g m ty e)
        elif env.substVals.ContainsVal v then 
            let e = env.substVals.[v]
            ConvExprPrim cenv env e
        elif v.BaseOrThisInfo = CtorThisVal then 
            E.ThisValue(ConvType cenv v.Type) 
        elif v.BaseOrThisInfo = BaseVal then 
            E.BaseValue(ConvType cenv v.Type) 
        else 
            E.Value(FSharpMemberFunctionOrValue(cenv, vref)) 

    and ConvVal cenv (v:Val) =  
        let vref = mkLocalValRef v 
        FSharpMemberFunctionOrValue(cenv,  vref) 

    and ConvConst cenv env m c ty =
        match TryEliminateDesugaredConstants cenv.g m c with 
        | Some e -> ConvExprPrim cenv env e
        | None ->
            let tyR = ConvType cenv ty
            match c with 
            | Const.Bool    i ->  E.Const(box i, tyR)
            | Const.SByte   i ->  E.Const(box i, tyR)
            | Const.Byte    i ->  E.Const(box i, tyR)
            | Const.Int16   i ->  E.Const(box i, tyR)
            | Const.UInt16  i ->  E.Const(box i, tyR)
            | Const.Int32   i ->  E.Const(box i, tyR)
            | Const.UInt32  i ->  E.Const(box i, tyR)
            | Const.Int64   i ->  E.Const(box i, tyR)
            | Const.IntPtr   i ->  E.Const(box (nativeint i), tyR)
            | Const.UInt64  i ->  E.Const(box i, tyR)
            | Const.UIntPtr   i ->  E.Const(box (unativeint i), tyR)
            | Const.Double   i ->  E.Const(box i, tyR)
            | Const.Single i ->  E.Const(box i, tyR)
            | Const.String  i ->  E.Const(box i, tyR)
            | Const.Char    i ->  E.Const(box i, tyR)
            | Const.Unit      ->  E.Const(box (), tyR)
            | Const.Zero      ->  E.DefaultValue (ConvType cenv ty)
            | _ -> 
                wfail("FSharp.Compiler.Service cannot yet return this kind of constant", m)

    and ConvDecisionTree cenv env dtreeRetTy x m = 
        ConvDecisionTreePrim cenv env dtreeRetTy x |> Mk cenv m dtreeRetTy

    and ConvDecisionTreePrim cenv env dtreeRetTy x = 
        match x with 
        | TDSwitch(e1,csl,dfltOpt,m) -> 
            let acc = 
                match dfltOpt with 
                | Some d -> ConvDecisionTreePrim cenv env dtreeRetTy d 
                | None -> wfail( "FSharp.Compiler.Service cannot yet return this kind of pattern match", m)
            (csl,acc) ||> List.foldBack (fun (TCase(discrim,dtree)) acc -> 
                    let acc = acc |> Mk cenv m dtreeRetTy
                    match discrim with 
                    | Test.UnionCase (ucref, tyargs) -> 
                        let objR = ConvExpr cenv env e1
                        let ucR = ConvUnionCaseRef cenv ucref 
                        let utypR = ConvType cenv (mkAppTy ucref.TyconRef tyargs)
                        E.IfThenElse (E.UnionCaseTest (objR, utypR, ucR) |> Mk cenv m cenv.g.bool_ty, ConvDecisionTree cenv env dtreeRetTy dtree m, acc) 
                    | Test.Const (Const.Bool true) -> 
                        let e1R = ConvExpr cenv env e1
                        E.IfThenElse (e1R, ConvDecisionTree cenv env dtreeRetTy dtree m, acc) 
                    | Test.Const (Const.Bool false) -> 
                        let e1R = ConvExpr cenv env e1
                        // Note, reverse the branches
                        E.IfThenElse (e1R, acc, ConvDecisionTree cenv env dtreeRetTy dtree m) 
                    | Test.Const c -> 
                        let ty = tyOfExpr cenv.g e1
                        let eq = mkCallEqualsOperator cenv.g m ty e1 (Expr.Const (c, m, ty))
                        let eqR = ConvExpr cenv env eq 
                        E.IfThenElse (eqR, ConvDecisionTree cenv env dtreeRetTy dtree m, acc) 
                    | Test.IsNull -> 
                        // Decompile cached isinst tests
                        match e1 with 
                        | Expr.Val(vref,_,_) when env.isinstVals.ContainsVal vref.Deref  ->
                            let (ty,e) =  env.isinstVals.[vref.Deref]
                            let tyR = ConvType cenv ty
                            let eR = ConvExpr cenv env e
                            // note: reverse the branches - a null test is a failure of an isinst test
                            E.IfThenElse (E.TypeTest (tyR,eR) |> Mk cenv m cenv.g.bool_ty, acc, ConvDecisionTree cenv env dtreeRetTy dtree m) 
                        | _ -> 
                            let ty = tyOfExpr cenv.g e1
                            let eq = mkCallEqualsOperator cenv.g m ty e1 (Expr.Const (Const.Zero, m, ty))
                            let eqR = ConvExpr cenv env eq 
                            E.IfThenElse (eqR, ConvDecisionTree cenv env dtreeRetTy dtree m, acc) 
                    | Test.IsInst (_srcty, tgty) -> 
                        let e1R = ConvExpr cenv env e1
                        E.IfThenElse (E.TypeTest (ConvType cenv tgty, e1R)  |> Mk cenv m cenv.g.bool_ty, ConvDecisionTree cenv env dtreeRetTy dtree m, acc) 
                    | Test.ActivePatternCase _ -> wfail("unexpected Test.ActivePatternCase test in quoted expression",m)
                    | Test.ArrayLength _ -> wfail("FSharp.Compiler.Service cannot yet return array pattern matching", m))

        | TDSuccess (args,n) -> 
                // TAST stores pattern bindings in reverse order for some reason
                // Reverse them here to give a good presentation to the user
                let args = List.rev (FlatList.toList args)
                let argsR = ConvExprs cenv env args          
                E.DecisionTreeSuccess(n, argsR)
          
        | TDBind(bind,rest) -> 
                // The binding may be a compiler-generated binding that gets removed in the quotation presentation
                match ConvLetBind cenv env bind with 
                | None, env -> ConvDecisionTreePrim cenv env dtreeRetTy rest 
                | Some(bindR),env -> E.Let(bindR,ConvDecisionTree cenv env dtreeRetTy rest bind.Var.Range) 

    /// Wrap the conversion in a function to make it on-demand.  Any pattern matching on the FSharpExpr will
    /// force the evaluation of the entire conversion process eagerly.
    let ConvExprOnDemand cenv env expr = 
        FSharpExpr(cenv, Some(fun () -> ConvExpr cenv env expr), E.Unused, expr.Range, tyOfExpr cenv.g expr)



/// The contents of the F# assembly as provided through the compiler API
type FSharpAssemblyContents(cenv: Impl.cenv, mimpls: TypedImplFile list) = 

    new (g, thisCcu, tcImports, mimpls) = FSharpAssemblyContents(Impl.cenv(g,thisCcu,tcImports), mimpls)

    member __.ImplementationFiles = 
        [ for mimpl in mimpls -> FSharpImplementationFileContents(cenv, mimpl)]

and FSharpImplementationFileDeclaration = 
    | Entity of FSharpEntity * FSharpImplementationFileDeclaration list
    | MemberOrFunctionOrValue  of FSharpMemberOrFunctionOrValue * FSharpMemberOrFunctionOrValue list list * FSharpExpr
    | InitAction of FSharpExpr

and FSharpImplementationFileContents(cenv, mimpl) = 
    let (TImplFile(qname,_pragmas,ModuleOrNamespaceExprWithSig(_mty,mdef,_),hasExplicitEntryPoint,isScript)) = mimpl 
    let rec getDecls2 (ModuleOrNamespaceExprWithSig(_mty,def,_m)) = getDecls def
    and getBind (bind: Binding) = 
        let v = bind.Var
        assert v.IsCompiledAsTopLevel
        let topValInfo = InferArityOfExprBinding cenv.g v bind.Expr
        let tps,_ctorThisValOpt,_baseValOpt,vsl,body,_bodyty = IteratedAdjustArityOfLambda cenv.g cenv.amap topValInfo bind.Expr
        let v = FSharpMemberOrFunctionOrValue(cenv, mkLocalValRef v)
        let gps = v.GenericParameters
        let vslR = List.map (List.map (FSharpExprConvert.ConvVal cenv)) vsl 
        let env = ExprTranslationEnv.Empty.BindTypars (Seq.zip tps gps |> Seq.toList)
        let env = env.BindCurriedVals vsl 
        let e = FSharpExprConvert.ConvExprOnDemand cenv env body
        FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(v, vslR, e) 

    and getDecls mdef = 
        match mdef with 
        | TMDefRec(tycons,binds,mbinds,_m) -> 
            [ for tycon in tycons do 
                  let entity = FSharpEntity(cenv, mkLocalEntityRef tycon)
                  yield FSharpImplementationFileDeclaration.Entity(entity, []) 
              for bind in binds do 
                  yield getBind bind
              for (ModuleOrNamespaceBinding(mspec, def)) in mbinds do 
                  let entity = FSharpEntity(cenv, mkLocalEntityRef mspec)
                  yield FSharpImplementationFileDeclaration.Entity (entity, getDecls def) ]
        | TMAbstract(mexpr) -> getDecls2 mexpr
        | TMDefLet(bind,_m)  ->
            [ yield getBind bind  ]
        | TMDefDo(expr,_m)  ->
            [ let expr = FSharpExprConvert.ConvExprOnDemand cenv ExprTranslationEnv.Empty expr
              yield FSharpImplementationFileDeclaration.InitAction(expr)  ]
        | TMDefs(mdefs) -> 
            [ for mdef in mdefs do yield! getDecls mdef ]

    member __.QualifiedName = qname.Text
    member __.FileName = qname.Range.FileName
    member __.Declarations = getDecls mdef 
    member __.HasExplicitEntryPoint = hasExplicitEntryPoint
    member __.IsScript = isScript


module BasicPatterns = 
    let (|Value|_|) (e:FSharpExpr) = match e.E with E.Value (v) -> Some (v) | _ -> None
    let (|Const|_|) (e:FSharpExpr) = match e.E with E.Const (v,ty) -> Some (v,ty) | _ -> None
    let (|TypeLambda|_|) (e:FSharpExpr) = match e.E with E.TypeLambda (v,e) -> Some (v,e) | _ -> None
    let (|Lambda|_|) (e:FSharpExpr) = match e.E with E.Lambda (v,e) -> Some (v,e) | _ -> None
    let (|Application|_|) (e:FSharpExpr) = match e.E with E.Application (f,tys,e) -> Some (f,tys,e) | _ -> None
    let (|IfThenElse|_|) (e:FSharpExpr) = match e.E with E.IfThenElse (e1,e2,e3) -> Some (e1,e2,e3) | _ -> None
    let (|Let|_|) (e:FSharpExpr) = match e.E with E.Let ((v,e),b) -> Some ((v,e),b) | _ -> None
    let (|LetRec|_|) (e:FSharpExpr) = match e.E with E.LetRec (ves,b) -> Some (ves,b) | _ -> None
    let (|NewRecord|_|) (e:FSharpExpr) = match e.E with E.NewRecord (ty,es) -> Some (ty,es) | _ -> None
    let (|NewUnionCase|_|) (e:FSharpExpr) = match e.E with E.NewUnionCase (e,tys,es) -> Some (e,tys,es) | _ -> None
    let (|NewTuple|_|) (e:FSharpExpr) = match e.E with E.NewTuple (ty,es) -> Some (ty,es) | _ -> None
    let (|TupleGet|_|) (e:FSharpExpr) = match e.E with E.TupleGet (ty,n,es) -> Some (ty,n,es) | _ -> None
    let (|Call|_|) (e:FSharpExpr) = match e.E with E.Call (a,b,c,d,e) -> Some (a,b,c,d,e) | _ -> None
    let (|NewObject|_|) (e:FSharpExpr) = match e.E with E.NewObject (a,b,c) -> Some (a,b,c) | _ -> None
    let (|FSharpFieldGet|_|) (e:FSharpExpr) = match e.E with E.FSharpFieldGet (a,b,c) -> Some (a,b,c) | _ -> None
    let (|FSharpFieldSet|_|) (e:FSharpExpr) = match e.E with E.FSharpFieldSet (a,b,c,d) -> Some (a,b,c,d) | _ -> None
    let (|UnionCaseGet|_|) (e:FSharpExpr) = match e.E with E.UnionCaseGet (a,b,c,d) -> Some (a,b,c,d) | _ -> None
    let (|UnionCaseTag|_|) (e:FSharpExpr) = match e.E with E.UnionCaseTag (a,b) -> Some (a,b) | _ -> None
    let (|UnionCaseTest|_|) (e:FSharpExpr) = match e.E with E.UnionCaseTest (a,b,c) -> Some (a,b,c) | _ -> None
    let (|NewArray|_|) (e:FSharpExpr) = match e.E with E.NewArray (a,b) -> Some (a,b) | _ -> None
    let (|Coerce|_|) (e:FSharpExpr) = match e.E with E.Coerce (a,b) -> Some (a,b) | _ -> None
    let (|Quote|_|) (e:FSharpExpr) = match e.E with E.Quote (a) -> Some (a) | _ -> None
    let (|TypeTest|_|) (e:FSharpExpr) = match e.E with E.TypeTest (a,b) -> Some (a,b) | _ -> None
    let (|Sequential|_|) (e:FSharpExpr) = match e.E with E.Sequential (a,b) -> Some (a,b) | _ -> None
    let (|FastIntegerForLoop|_|) (e:FSharpExpr) = match e.E with E.FastIntegerForLoop (a,b,c,d) -> Some (a,b,c,d) | _ -> None
    let (|WhileLoop|_|) (e:FSharpExpr) = match e.E with E.WhileLoop (a,b) -> Some (a,b) | _ -> None
    let (|TryFinally|_|) (e:FSharpExpr) = match e.E with E.TryFinally (a,b) -> Some (a,b) | _ -> None
    let (|TryWith|_|) (e:FSharpExpr) = match e.E with E.TryWith (a,b,c,d,e) -> Some (a,b,c,d,e) | _ -> None
    let (|NewDelegate|_|) (e:FSharpExpr) = match e.E with E.NewDelegate (ty,e) -> Some (ty,e) | _ -> None
    let (|DefaultValue|_|) (e:FSharpExpr) = match e.E with E.DefaultValue (ty) -> Some (ty) | _ -> None
    let (|AddressSet|_|) (e:FSharpExpr) = match e.E with E.AddressSet (a,b) -> Some (a,b) | _ -> None
    let (|ValueSet|_|) (e:FSharpExpr) = match e.E with E.ValueSet (a,b) -> Some (a,b) | _ -> None
    let (|AddressOf|_|) (e:FSharpExpr) = match e.E with E.AddressOf (a) -> Some (a) | _ -> None
    let (|ThisValue|_|) (e:FSharpExpr) = match e.E with E.ThisValue (a) -> Some (a) | _ -> None
    let (|BaseValue|_|) (e:FSharpExpr) = match e.E with E.BaseValue (a) -> Some (a) | _ -> None
    let (|ILAsm|_|) (e:FSharpExpr) = match e.E with E.ILAsm (a,b,c) -> Some (a,b,c) | _ -> None
    let (|ILFieldGet|_|) (e:FSharpExpr) = match e.E with E.ILFieldGet (a,b,c) -> Some (a,b,c) | _ -> None
    let (|ILFieldSet|_|) (e:FSharpExpr) = match e.E with E.ILFieldSet (a,b,c,d) -> Some (a,b,c,d) | _ -> None
    let (|ObjectExpr|_|) (e:FSharpExpr) = match e.E with E.ObjectExpr (a,b,c,d) -> Some (a,b,c,d) | _ -> None
    let (|DecisionTree|_|) (e:FSharpExpr) = match e.E with E.DecisionTree (a,b) -> Some (a,b) | _ -> None
    let (|DecisionTreeSuccess|_|) (e:FSharpExpr) = match e.E with E.DecisionTreeSuccess (a,b) -> Some (a,b) | _ -> None
    let (|UnionCaseSet|_|) (e:FSharpExpr) = match e.E with E.UnionCaseSet (a,b,c,d,e) -> Some (a,b,c,d,e) | _ -> None
    let (|TraitCall|_|) (e:FSharpExpr) = match e.E with E.TraitCall (a,b,c,d,e) -> Some (a,b,c,d,e) | _ -> None



