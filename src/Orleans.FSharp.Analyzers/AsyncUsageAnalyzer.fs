/// <summary>
/// Orleans.FSharp F# Analyzers.
/// OF0001 — warns when <c>async { }</c> is used instead of <c>task { }</c> in grain code.
/// </summary>
module Orleans.FSharp.Analyzers.AsyncUsageAnalyzer

open System
open FSharp.Analyzers.SDK
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

// ──────────────────────────────────────────────────────────────────────────────
// Public opt-out attribute
// ───────────────────���──────────────────────────────��───────────────────────────

/// <summary>
/// Place this attribute on a function or method binding to suppress OF0001 for that binding.
/// Use only when <c>async { }</c> is genuinely required (e.g., interop with an Async-returning
/// library or a script entry point that cannot use <c>task { }</c>).
/// </summary>
/// <example>
/// <code>
/// [&lt;AllowAsync&gt;]
/// let fetchData url = async {
///     let! resp = http.GetAsync(url) |> Async.AwaitTask
///     return resp
/// }
/// </code>
/// </example>
[<AttributeUsage(AttributeTargets.Method ||| AttributeTargets.Property, AllowMultiple = false)>]
type AllowAsyncAttribute() =
    inherit Attribute()

// ──────────────────────────────────────────────────────────────────────────────
// Internal AST walker  (FSharp.Compiler.Service 43.12)
// ───────────────────────────────────��──────────────────────────────────────────

/// <summary>
/// Internal AST walker.  Exposed as <c>internal</c> so the test project can exercise it
/// directly without spinning up a full FSharp.Analyzers.SDK build pipeline.
/// </summary>
module internal AstWalker =

    // ── attribute helpers ─────────────────────────────────────────────────────

    let private bindingHasAllowAsync (SynBinding(attributes = attrLists)) =
        attrLists
        |> List.exists (fun attrList ->
            attrList.Attributes
            |> List.exists (fun attr ->
                let lastName =
                    attr.TypeName.LongIdent
                    |> List.tryLast
                    |> Option.map _.idText
                    |> Option.defaultValue ""
                lastName = "AllowAsync" || lastName = "AllowAsyncAttribute"))

    // ── main walker ───────────────────────────────────────────────────────────

    /// <summary>
    /// Walk a <see cref="ParsedInput"/> and return the <see cref="range"/> of every
    /// <c>async { }</c> usage that is not suppressed by <c>[&lt;AllowAsync&gt;]</c>.
    /// </summary>
    let collectAsyncRanges (tree: ParsedInput) : range list =
        let acc = System.Collections.Generic.List<range>()

        // ── expression walker ─────────────────────────────────────────────────

        let rec walkExpr (suppress: bool) (expr: SynExpr) : unit =
            match expr with

            // ── Primary detection: async { body } ────────────────────────────
            | SynExpr.App(_, _, SynExpr.Ident id, SynExpr.ComputationExpr(_, body, _), _)
                when id.idText = "async" ->
                if not suppress then acc.Add(id.idRange)
                walkExpr suppress body

            // ── Structural recursion ─────────────────────────────────────────
            | SynExpr.App(_, _, f, arg, _) ->
                walkExpr suppress f
                walkExpr suppress arg

            | SynExpr.ComputationExpr(_, body, _) ->
                walkExpr suppress body

            // In FCS 43.12, Sequential has 6 fields (+ trivia at the end).
            | SynExpr.Sequential(_, _, e1, e2, _, _) ->
                walkExpr suppress e1
                walkExpr suppress e2

            // LetOrUse covers both let/use and let!/use! bindings.
            // In FCS 43.10+, the former LetOrUseBang case was merged into LetOrUse;
            // the 4th positional field (isBang) distinguishes them at runtime, but the
            // walker treats both identically — it recurses into bindings and body regardless,
            // so any nested async { } on either side of a let! RHS will be detected.
            | SynExpr.LetOrUse(_, _, _, _, bindings, body, _, _) ->
                for b in bindings do walkBinding b
                walkExpr suppress body

            // In FCS 43.12, Match has 5 fields.
            | SynExpr.Match(_, matchExpr, clauses, _, _) ->
                walkExpr suppress matchExpr
                walkClauses suppress clauses

            | SynExpr.MatchBang(_, matchExpr, clauses, _, _) ->
                walkExpr suppress matchExpr
                walkClauses suppress clauses

            // In FCS 43.12, IfThenElse has 7 fields.
            | SynExpr.IfThenElse(ifExpr, thenExpr, elseExpr, _, _, _, _) ->
                walkExpr suppress ifExpr
                walkExpr suppress thenExpr
                elseExpr |> Option.iter (walkExpr suppress)

            // Lambda: 7 fields in FCS 43.12.
            | SynExpr.Lambda(_, _, _, body, _, _, _) ->
                walkExpr suppress body

            | SynExpr.Paren(e, _, _, _) ->
                walkExpr suppress e

            | SynExpr.Typed(e, _, _) ->
                walkExpr suppress e

            | SynExpr.Tuple(_, exprs, _, _) ->
                for e in exprs do walkExpr suppress e

            // TryWith: 6 fields in FCS 43.12.
            | SynExpr.TryWith(tryExpr, withClauses, _, _, _, _) ->
                walkExpr suppress tryExpr
                walkClauses suppress withClauses

            // TryFinally: 6 fields in FCS 43.12.
            | SynExpr.TryFinally(tryExpr, finallyExpr, _, _, _, _) ->
                walkExpr suppress tryExpr
                walkExpr suppress finallyExpr

            | SynExpr.Do(e, _) ->
                walkExpr suppress e

            // DoBang: 3 fields (expr, range, trivia).
            | SynExpr.DoBang(e, _, _) ->
                walkExpr suppress e

            | SynExpr.DotGet(e, _, _, _) ->
                walkExpr suppress e

            | SynExpr.DotSet(e1, _, e2, _) ->
                walkExpr suppress e1
                walkExpr suppress e2

            // For: 9 fields in FCS 43.12 (doBody is 8th).
            | SynExpr.For(_, _, _, _, _, _, _, body, _) ->
                walkExpr suppress body

            // ForEach: 8 fields in FCS 43.12 (bodyExpr is 7th).
            | SynExpr.ForEach(_, _, _, _, _, _, body, _) ->
                walkExpr suppress body

            // While: 4 fields.
            | SynExpr.While(_, cond, body, _) ->
                walkExpr suppress cond
                walkExpr suppress body

            // ObjExpr: 8 fields; bindings at pos 4, members at pos 5.
            | SynExpr.ObjExpr(_, _, _, _, members, _, _, _) ->
                for md in members do walkMemberDef md

            // SynExprRecordField has 5 fields: fieldName, equalsRange, expr, range, blockSeparator.
            | SynExpr.Record(_, _, fields, _) ->
                for SynExprRecordField(_, _, value, _, _) in fields do
                    value |> Option.iter (walkExpr suppress)

            // YieldOrReturn: 4 fields (flags, expr, range, trivia).
            | SynExpr.YieldOrReturn(_, e, _, _) ->
                walkExpr suppress e

            // YieldOrReturnFrom: 4 fields.
            | SynExpr.YieldOrReturnFrom(_, e, _, _) ->
                walkExpr suppress e

            | _ -> ()   // atoms, identifiers, literals — no children

        // ── match-clause walker ───────────────────────────────────────────────

        and walkClauses (suppress: bool) (clauses: SynMatchClause list) : unit =
            // SynMatchClause has 6 fields in FCS 43.12.
            for SynMatchClause(_, whenExpr, resultExpr, _, _, _) in clauses do
                whenExpr |> Option.iter (walkExpr suppress)
                walkExpr suppress resultExpr

        // ── binding walker ────────────────────────────────────────────────────

        and walkBinding (binding: SynBinding) : unit =
            let suppress = bindingHasAllowAsync binding
            let (SynBinding(expr = expr)) = binding
            walkExpr suppress expr

        // ── member-def walker ─────────────────────────────────────────────────

        and walkMemberDef (md: SynMemberDefn) : unit =
            match md with
            // Member: 2 fields (memberDefn: SynBinding, range).
            | SynMemberDefn.Member(binding, _) ->
                walkBinding binding
            // LetBindings: 4 fields in F# 10 compiler.
            | SynMemberDefn.LetBindings(bindings, _, _, _) ->
                for b in bindings do walkBinding b
            // AutoProperty: 12 fields; synExpr is at position 10.
            | SynMemberDefn.AutoProperty(_, _, _, _, _, _, _, _, _, expr, _, _) ->
                walkExpr false expr
            | _ -> ()

        // ── type-definition walker ────────────────────────────────────────────

        let walkTypeDefn (SynTypeDefn(typeRepr = repr; members = extraMembers)) : unit =
            (match repr with
             | SynTypeDefnRepr.ObjectModel(members = mds) ->
                 for md in mds do walkMemberDef md
             | _ -> ())
            for md in extraMembers do walkMemberDef md

        // ── module-declaration walker ─────────────────────────────────────────

        let rec walkModuleDecl (decl: SynModuleDecl) : unit =
            match decl with
            | SynModuleDecl.Let(_, bindings, _) ->
                for b in bindings do walkBinding b
            | SynModuleDecl.Types(typeDefs, _) ->
                for td in typeDefs do walkTypeDefn td
            | SynModuleDecl.NestedModule(_, _, decls, _, _, _) ->
                for d in decls do walkModuleDecl d
            | SynModuleDecl.Expr(e, _) ->
                walkExpr false e
            | _ -> ()

        // ── entry point ───────────────────────────────────────────────────────

        match tree with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
            for SynModuleOrNamespace(decls = decls) in modules do
                for d in decls do walkModuleDecl d
        | ParsedInput.SigFile _ -> ()

        acc |> Seq.toList

// ──────────────────────────────────────────────────────────────────────────────
// Analyzer entry point
// ──────────────────────────────────────────────────────────────────────────────

[<Literal>]
let private DiagnosticCode = "OF0001"

[<Literal>]
let private AnalyzerName = "AsyncUsageAnalyzer"

[<Literal>]
let private HelpUri =
    "https://github.com/Neftedollar/orleans-fsharp/blob/main/docs/analyzers.md#OF0001"

/// <summary>
/// <para><b>OF0001</b> — <c>async { }</c> should be <c>task { }</c>.</para>
/// <para>
/// Orleans grain handlers and Task-returning methods must use <c>task { }</c>.
/// <c>async { }</c> requires <c>Async.AwaitTask</c> / <c>Async.StartAsTask</c> at the boundary,
/// adds unnecessary overhead, and is incompatible with the <c>task { }</c>-only constraint
/// enforced by the Orleans.FSharp project constitution.
/// </para>
/// <para>
/// To suppress this warning for a specific binding, apply <c>[&lt;AllowAsync&gt;]</c>.
/// </para>
/// </summary>
// AllowAsync is required here: FSharp.Analyzers.SDK mandates Async<Message list> as the
// return type of CliAnalyzer functions. This is exactly the SDK-interop scenario AllowAsync
// was designed for — the async { } block cannot be replaced with task { } without breaking
// the SDK contract.
[<AllowAsync>]
[<CliAnalyzer(AnalyzerName,
              "Warns when async { } is used where task { } should be preferred.",
              HelpUri)>]
let asyncUsageAnalyzer (ctx: CliContext) : Async<Message list> =
    async {
        let asyncRanges = AstWalker.collectAsyncRanges ctx.ParseFileResults.ParseTree

        return
            asyncRanges
            |> List.map (fun range ->
                { Type        = AnalyzerName
                  Message     =
                    "OF0001: Use task { } instead of async { } in Orleans grain handlers and "
                    + "Task-returning methods. async { } adds an unnecessary Async↔Task "
                    + "conversion and is banned by the Orleans.FSharp coding standard. "
                    + "Suppress with [<AllowAsync>] only when async { } is genuinely required."
                  Code        = DiagnosticCode
                  Severity    = Severity.Warning
                  Range       = range
                  Fixes       = [] })
    }
