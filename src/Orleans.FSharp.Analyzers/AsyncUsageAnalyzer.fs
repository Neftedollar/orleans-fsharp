namespace Orleans.FSharp.Analyzers

open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FSharp.Analyzers.SDK
open FSharp.Analyzers.SDK.ASTCollecting

/// <summary>
/// Marker attribute to opt out of the OF0001 async usage diagnostic.
/// Apply to a let-binding to suppress the warning for that function.
/// </summary>
[<System.AttributeUsage(System.AttributeTargets.Method ||| System.AttributeTargets.Property)>]
type AllowAsyncAttribute() =
    inherit System.Attribute()

/// <summary>
/// Pure detection logic for async { } computation expression usage.
/// Scans the untyped F# AST (ParsedInput) and reports diagnostics
/// for each occurrence of the async builder, unless suppressed by [&lt;AllowAsync&gt;].
/// </summary>
[<RequireQualifiedAccess>]
module AsyncUsageAnalyzer =

    /// <summary>Diagnostic code for async usage detection.</summary>
    [<Literal>]
    let DiagnosticCode = "OF0001"

    /// <summary>Diagnostic message recommending task over async.</summary>
    [<Literal>]
    let DiagnosticMessage =
        "Use 'task { }' instead of 'async { }' for Orleans compatibility. Orleans is Task-native; async introduces overhead and potential deadlocks."

    /// <summary>
    /// Checks whether a list of attributes contains [&lt;AllowAsync&gt;].
    /// Recognizes both "AllowAsync" and "AllowAsyncAttribute" forms.
    /// </summary>
    let private hasAllowAsyncAttribute (attrs: SynAttributes) : bool =
        attrs
        |> List.exists (fun attrList ->
            attrList.Attributes
            |> List.exists (fun attr ->
                let name =
                    attr.TypeName.LongIdent
                    |> List.map (fun ident -> ident.idText)
                    |> String.concat "."

                name = "AllowAsync" || name = "AllowAsyncAttribute"))

    /// <summary>
    /// Checks whether a SynExpr is an application of the "async" builder
    /// to a computation expression body: async { ... }.
    /// In the F# untyped AST this appears as:
    ///   SynExpr.App(_, _, SynExpr.Ident("async"), SynExpr.ComputationExpr(...), _)
    /// </summary>
    let private isAsyncComputationExpr (expr: SynExpr) : (Range * string) option =
        match expr with
        | SynExpr.App(_, _, SynExpr.Ident ident, SynExpr.ComputationExpr _, range) when ident.idText = "async" ->
            Some(range, ident.idText)
        | _ -> None

    /// <summary>
    /// Detects all async { } computation expression usages in a parsed F# AST.
    /// Returns a list of Message diagnostics for each occurrence that is not
    /// suppressed by an [&lt;AllowAsync&gt;] attribute on the containing binding.
    /// </summary>
    /// <param name="tree">The untyped AST (ParsedInput) to scan.</param>
    /// <returns>A list of diagnostic messages for each detected async usage.</returns>
    let detectAsyncUsage (tree: ParsedInput) : Message list =
        let diagnostics = ResizeArray<Message>()

        let collector =
            { new SyntaxCollectorBase() with
                override _.WalkBinding(_path, binding) =
                    let (SynBinding(_, _, _, _, attrs, _, _, _pat, _, expr, _, _, _)) = binding

                    let suppressed = hasAllowAsyncAttribute attrs

                    if not suppressed then
                        // Check the top-level expression of the binding
                        let rec findAsyncExprs (e: SynExpr) =
                            match isAsyncComputationExpr e with
                            | Some(range, _) ->
                                diagnostics.Add(
                                    { Type = DiagnosticCode
                                      Message = DiagnosticMessage
                                      Code = DiagnosticCode
                                      Severity = Severity.Warning
                                      Range = range
                                      Fixes = [] }
                                )
                            | None ->
                                // Recurse into sub-expressions to find nested async { }
                                match e with
                                | SynExpr.App(_, _, funcExpr, argExpr, _) ->
                                    findAsyncExprs funcExpr
                                    findAsyncExprs argExpr
                                | SynExpr.LetOrUse(bindings = bindings; body = body) ->
                                    for binding in bindings do
                                        let (SynBinding(attributes = innerAttrs; expr = innerExpr)) = binding

                                        if not (hasAllowAsyncAttribute innerAttrs) then
                                            findAsyncExprs innerExpr

                                    findAsyncExprs body
                                | SynExpr.Sequential(_, _, expr1, expr2, _, _) ->
                                    findAsyncExprs expr1
                                    findAsyncExprs expr2
                                | SynExpr.IfThenElse(ifExpr, thenExpr, elseExpr, _, _, _, _) ->
                                    findAsyncExprs ifExpr
                                    findAsyncExprs thenExpr
                                    elseExpr |> Option.iter findAsyncExprs
                                | SynExpr.Match(_, _, clauses, _, _) ->
                                    for (SynMatchClause(_, _, resultExpr, _, _, _)) in clauses do
                                        findAsyncExprs resultExpr
                                | SynExpr.Paren(innerExpr, _, _, _) -> findAsyncExprs innerExpr
                                | SynExpr.Lambda(_, _, _, bodyExpr, _, _, _) -> findAsyncExprs bodyExpr
                                | SynExpr.Do(innerExpr, _) -> findAsyncExprs innerExpr
                                | SynExpr.Typed(innerExpr, _, _) -> findAsyncExprs innerExpr
                                | _ -> ()

                        findAsyncExprs expr
            }

        walkAst collector tree
        diagnostics |> Seq.toList

/// <summary>
/// Module containing the analyzer entry point that detects async { } computation expression usage
/// and suggests using task { } instead for Orleans compatibility.
/// </summary>
module AnalyzerEntryPoint =

    /// <summary>
    /// CLI analyzer that detects async { } usage and suggests task { }.
    /// </summary>
    [<CliAnalyzer("OF0001-AsyncUsage", "Detects async { } usage and suggests task { }", "https://github.com/example/orleans-fsharp/docs/analyzers/OF0001.md")>]
    let asyncUsageAnalyzer: Analyzer<CliContext> =
        fun (ctx: CliContext) ->
            async { return AsyncUsageAnalyzer.detectAsyncUsage ctx.ParseFileResults.ParseTree }
