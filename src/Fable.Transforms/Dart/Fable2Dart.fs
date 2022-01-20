module rec Fable.Transforms.Fable2Dart

open Fable
open Fable.AST
open Fable.AST.Dart
open System.Collections.Generic
open Fable.Transforms.AST

type ReturnStrategy =
    | Return
    | ReturnVoid
    | Assign of Expression
    | Target of Ident

type ITailCallOpportunity =
    abstract Label: string
    abstract Args: string list
    abstract IsRecursiveRef: Fable.Expr -> bool

type UsedNames =
  { RootScope: HashSet<string>
    DeclarationScopes: HashSet<string>
    CurrentDeclarationScope: HashSet<string> }

type Context =
  { File: Fable.File
    UsedNames: UsedNames
    DecisionTargets: (Fable.Ident list * Fable.Expr) list
    HoistVars: Fable.Ident list -> bool
    TailCallOpportunity: ITailCallOpportunity option
    OptimizeTailCall: unit -> unit }

type MemberKind =
    | ClassConstructor
    | NonAttached of funcName: string
    | Attached of isStatic: bool

type IDartCompiler =
    inherit Compiler
    abstract GetAllImports: unit -> Import list
    abstract GetImportExpr: Context * selector: string * path: string * SourceLocation option -> Expression
    abstract TransformAsExpr: Context * Fable.Expr -> Expression
    abstract TransformAsStatements: Context * ReturnStrategy option * Fable.Expr -> Statement list
    abstract TransformImport: Context * selector:string * path:string -> Fable.Expr
    abstract TransformFunction: Context * string option * Fable.Ident list * Fable.Expr -> Ident list * Statement list
    abstract WarnOnlyOnce: string * ?range: SourceLocation -> unit

module Util =
    let (|TransformExpr|) (com: IDartCompiler) ctx e =
        com.TransformAsExpr(ctx, e)

    let (|Function|_|) = function
        | Fable.Lambda(arg, body, _) -> Some([arg], body)
        | Fable.Delegate(args, body, _) -> Some(args, body)
        | _ -> None

    let (|Lets|_|) = function
        | Fable.Let(ident, value, body) -> Some([ident, value], body)
        | Fable.LetRec(bindings, body) -> Some(bindings, body)
        | _ -> None

    let discardUnitArg (args: Fable.Ident list) =
        match args with
        | [] -> []
        | [unitArg] when unitArg.Type = Fable.Unit -> []
        | [thisArg; unitArg] when thisArg.IsThisArgument && unitArg.Type = Fable.Unit -> [thisArg]
        | args -> args

    let getUniqueNameInRootScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name)
            || ctx.UsedNames.DeclarationScopes.Contains(name))
        ctx.UsedNames.RootScope.Add(name) |> ignore
        name

    let getUniqueNameInDeclarationScope (ctx: Context) name =
        let name = (name, Naming.NoMemberPart) ||> Naming.sanitizeIdent (fun name ->
            ctx.UsedNames.RootScope.Contains(name) || ctx.UsedNames.CurrentDeclarationScope.Contains(name))
        ctx.UsedNames.CurrentDeclarationScope.Add(name) |> ignore
        name

    type NamedTailCallOpportunity(_com: Compiler, ctx, name, args: Fable.Ident list) =
        // Capture the current argument values to prevent delayed references from getting corrupted,
        // for that we use block-scoped ES2015 variable declarations. See #681, #1859
        // TODO: Local unique ident names
        let argIds = discardUnitArg args |> List.map (fun arg ->
            getUniqueNameInDeclarationScope ctx (arg.Name + "_mut"))
        interface ITailCallOpportunity with
            member _.Label = name
            member _.Args = argIds
            member _.IsRecursiveRef(e) =
                match e with Fable.IdentExpr id -> name = id.Name | _ -> false

    let getDecisionTarget (ctx: Context) targetIndex =
        match List.tryItem targetIndex ctx.DecisionTargets with
        | None -> failwithf $"Cannot find DecisionTree target %i{targetIndex}"
        | Some(idents, target) -> idents, target

    let rec isStatement ctx preferStatement (expr: Fable.Expr) =
        match expr with
        | Fable.Unresolved _
        | Fable.Value _ | Fable.Import _  | Fable.IdentExpr _
        | Fable.Lambda _ | Fable.Delegate _ | Fable.ObjectExpr _
        | Fable.Call _ | Fable.CurriedApply _ | Fable.Operation _
        | Fable.Get _ | Fable.Test _ -> false

        | Fable.TypeCast(e,_) -> isStatement ctx preferStatement e

        | Fable.Set _ -> true // TODO: Depends on language target

        | Fable.TryCatch _
        | Fable.Sequential _ | Fable.Let _ | Fable.LetRec _
        | Fable.ForLoop _ | Fable.WhileLoop _ -> true

        | Fable.Extended(kind, _) ->
            match kind with
            | Fable.Throw _ // TODO: Depends on language target
            | Fable.Debugger | Fable.RegionStart _ -> true
            | Fable.Curry _ -> false

        // TODO: If IsSatement is false, still try to infer it? See #2414
        // /^\s*(break|continue|debugger|while|for|switch|if|try|let|const|var)\b/
        | Fable.Emit(i,_,_) -> i.IsStatement

        | Fable.DecisionTreeSuccess(targetIndex,_, _) ->
            getDecisionTarget ctx targetIndex
            |> snd |> isStatement ctx preferStatement

        // Make it also statement if we have more than, say, 3 targets?
        // That would increase the chances to convert it into a switch
        | Fable.DecisionTree(_,targets) ->
            preferStatement
            || List.exists (snd >> (isStatement ctx false)) targets

        | Fable.IfThenElse(_,thenExpr,elseExpr,_) ->
            preferStatement || isStatement ctx false thenExpr || isStatement ctx false elseExpr

    let transformType (com: IDartCompiler) ctx (t: Fable.Type) =
        match t with
        | Fable.Boolean -> Boolean
        | Fable.String -> String
        | Fable.Number(kind, _) ->
            match kind with
            | Int8 | UInt8 | Int16 | UInt16 | Int32 | UInt32 -> Integer
            | Float32 | Float64 -> Double
        | _ -> failwith "todo"

    let transformIdent (com: IDartCompiler) ctx (id: Fable.Ident): Ident =
        { Name = id.Name
          Type = transformType com ctx id.Type }

    let transformValue (_: IDartCompiler) (_: Context) (_: SourceLocation option) value: Expression =
        match value with
        | Fable.BoolConstant v -> BooleanLiteral v |> Literal
        | Fable.StringConstant v -> StringLiteral v |> Literal
        | Fable.NumberConstant(v, kind, _) ->
            match kind with
            | Int8 | UInt8 | Int16 | UInt16 | Int32 | UInt32 -> IntegerLiteral(int64 v) |> Literal
            | Float32 | Float64 -> DoubleLiteral(v) |> Literal
        | _ -> failwith "TODO"

    let transformOperation com ctx (_: SourceLocation option) opKind: Expression =
        match opKind with
        | Fable.Binary(op, TransformExpr com ctx left, TransformExpr com ctx right) ->
            BinaryExpression(op, left, right)
        | Fable.Unary(op, TransformExpr com ctx expr) -> failwith "todo"
        | Fable.Logical(op, TransformExpr com ctx left, TransformExpr com ctx right) -> failwith "todo"

    let resolveExpr strategy expr: Statement =
        match strategy with
        | None | Some ReturnVoid -> ExpressionStatement expr
        | Some Return -> ReturnStatement expr
        | Some(Assign left) -> Assignment(left, expr) |> ExpressionStatement
        | Some(Target left) -> Assignment(IdentExpression left, expr) |> ExpressionStatement

    let rec transformAsExpr (com: IDartCompiler) ctx (expr: Fable.Expr): Expression =
        match expr with
        | Fable.Value(kind, r) -> transformValue com ctx r kind
        | Fable.Operation(kind, _, r) -> transformOperation com ctx r kind
        | Fable.IdentExpr ident -> transformIdent com ctx ident |> IdentExpression
        | e -> failwith $"todo: transform expr %A{e}"

    let rec transformAsStatements (com: IDartCompiler) ctx returnStrategy (expr: Fable.Expr): Statement list =
        match expr with
        | Fable.Value(kind, r) ->
            [transformValue com ctx r kind |> resolveExpr returnStrategy]

        | Fable.Operation(kind, _, r) ->
            [transformOperation com ctx r kind |> resolveExpr returnStrategy]

        | _ -> [] // TODO

    // TODO: tail calls, hoist vars
    let transformFunction com ctx name (args: Fable.Ident list) (body: Fable.Expr): Ident list * Statement list =
        let args = discardUnitArg args |> List.map (transformIdent com ctx)
        let ret = if body.Type = Fable.Unit then ReturnVoid else Return
        let body = transformAsStatements com ctx (Some ret) body
        args, body

    let getMemberArgsAndBody (com: IDartCompiler) ctx kind (args: Fable.Ident list) (body: Fable.Expr) =
        let funcName, args, body =
            match kind, args with
            | Attached(isStatic=false), (thisArg::args) ->
                let body =
                    // TODO: If ident is not captured maybe we can just replace it with "this"
                    if FableTransforms.isIdentUsed thisArg.Name body then
                        let thisKeyword = Fable.IdentExpr { thisArg with Name = "this" }
                        Fable.Let(thisArg, thisKeyword, body)
                    else body
                None, args, body
            | Attached(isStatic=true), _
            | ClassConstructor, _ -> None, args, body
            | NonAttached funcName, _ -> Some funcName, args, body
            | _ -> None, args, body

        transformFunction com ctx funcName args body

    let transformModuleFunction (com: IDartCompiler) ctx (memb: Fable.MemberDecl) =
        let returnType = transformType com ctx memb.Body.Type
        let args, body = getMemberArgsAndBody com ctx (NonAttached memb.Name) memb.Args memb.Body
        let isEntryPoint =
            memb.Info.Attributes
            |> Seq.exists (fun att -> att.Entity.FullName = Atts.entryPoint)
        if isEntryPoint then
            failwith "todo: main function"
        else
            let genParams = [] // TODO
            FunctionDeclaration(memb.Name, args, body, genParams, returnType)

    let rec transformDeclaration (com: IDartCompiler) ctx decl =
        let withCurrentScope ctx (usedNames: Set<string>) f =
            let ctx = { ctx with UsedNames = { ctx.UsedNames with CurrentDeclarationScope = HashSet usedNames } }
            let result = f ctx
            ctx.UsedNames.DeclarationScopes.UnionWith(ctx.UsedNames.CurrentDeclarationScope)
            result

        match decl with
        | Fable.ModuleDeclaration decl ->
            decl.Members |> List.collect (transformDeclaration com ctx)

        | Fable.MemberDeclaration memb ->
            withCurrentScope ctx memb.UsedNames <| fun ctx ->
                if memb.Info.IsValue then
                    [] // TODO
                else
                    [transformModuleFunction com ctx memb]

        // TODO: Action declarations are not supported in Dart, compile as: var _ = ...
        | Fable.ActionDeclaration _
//            withCurrentScope ctx decl.UsedNames <| fun ctx ->
//                transformAction com ctx decl.Body

        | Fable.ClassDeclaration _ -> [] // TODO

    let getIdentForImport (ctx: Context) (path: string) =
        Path.GetFileNameWithoutExtension(path)
        |> getUniqueNameInRootScope ctx

module Compiler =
    open Util

    type DartCompiler (com: Compiler) =
        let onlyOnceWarnings = HashSet<string>()
        let imports = Dictionary<string,Import>()

        interface IDartCompiler with
            member _.WarnOnlyOnce(msg, ?range) =
                if onlyOnceWarnings.Add(msg) then
                    addWarning com [] range msg

            // TODO: the returned expression should be typed
            member _.GetImportExpr(ctx, selector, path, r) = failwith "todo: getImportExpr"
            member _.GetAllImports() = imports.Values |> Seq.toList
            member bcom.TransformAsExpr(ctx, e) = transformAsExpr bcom ctx e
            member bcom.TransformAsStatements(ctx, ret, e) = transformAsStatements bcom ctx ret e
            member bcom.TransformFunction(ctx, name, args, body) = transformFunction bcom ctx name args body
            member bcom.TransformImport(ctx, selector, path) = failwith "todo: transformImport" // transformImport bcom ctx None selector path

        interface Compiler with
            member _.Options = com.Options
            member _.Plugins = com.Plugins
            member _.LibraryDir = com.LibraryDir
            member _.CurrentFile = com.CurrentFile
            member _.OutputDir = com.OutputDir
            member _.OutputType = com.OutputType
            member _.ProjectFile = com.ProjectFile
            member _.IsPrecompilingInlineFunction = com.IsPrecompilingInlineFunction
            member _.WillPrecompileInlineFunction(file) = com.WillPrecompileInlineFunction(file)
            member _.GetImplementationFile(fileName) = com.GetImplementationFile(fileName)
            member _.GetRootModule(fileName) = com.GetRootModule(fileName)
            member _.TryGetEntity(fullName) = com.TryGetEntity(fullName)
            member _.GetInlineExpr(fullName) = com.GetInlineExpr(fullName)
            member _.AddWatchDependency(fileName) = com.AddWatchDependency(fileName)
            member _.AddLog(msg, severity, ?range, ?fileName:string, ?tag: string) =
                com.AddLog(msg, severity, ?range=range, ?fileName=fileName, ?tag=tag)

    let makeCompiler com = DartCompiler(com)

    let transformFile (com: Compiler) (file: Fable.File) =
        let com = makeCompiler com :> IDartCompiler
        let declScopes =
            let hs = HashSet()
            for decl in file.Declarations do
                hs.UnionWith(decl.UsedNames)
            hs

        let ctx =
          { File = file
            UsedNames = { RootScope = HashSet file.UsedNamesInRootScope
                          DeclarationScopes = declScopes
                          CurrentDeclarationScope = Unchecked.defaultof<_> }
            DecisionTargets = []
            HoistVars = fun _ -> false
            TailCallOpportunity = None
            OptimizeTailCall = fun () -> () }
        let rootDecls = List.collect (transformDeclaration com ctx) file.Declarations
        let imports = com.GetAllImports()
        { File.Imports = imports
          Declarations = rootDecls }
