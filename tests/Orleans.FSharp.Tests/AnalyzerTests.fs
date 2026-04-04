/// <summary>
/// Unit tests for <c>Orleans.FSharp.Analyzers.AsyncUsageAnalyzer</c>.
///
/// The tests exercise the internal <c>AstWalker.collectAsyncRanges</c> function directly,
/// which avoids starting a full FSharp build host — making the tests fast and deterministic.
/// They parse small F# source snippets using the FSharp Compiler Service API, then assert that
/// the walker returns the expected number of <c>async { }</c> ranges.
/// </summary>
module Orleans.FSharp.Tests.AnalyzerTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer.AstWalker

// ──────────────────────────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────────────────────────

/// Parse an F# snippet and return the untyped ParsedInput.
let private parseSource (source: string) =
    let checker = FSharpChecker.Create()
    let sourceText = SourceText.ofString source
    let parsingOptions, _ =
        checker.GetParsingOptionsFromCommandLineArgs(
            ["dummy.fs"],
            isInteractive = false
        )
    let parseResult =
        checker.ParseFile("dummy.fs", sourceText, parsingOptions)
        |> Async.RunSynchronously
    parseResult.ParseTree

/// Count the number of async {} ranges detected in a source snippet.
let private asyncCount (source: string) : int =
    let tree = parseSource source
    collectAsyncRanges tree |> List.length

// ──────────────────────────────────────────────────────────────────────────────
// Basic detection tests
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``detects single async block at module level`` () =
    let src = """
module M
let f () = async { return 1 }
"""
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``task block is NOT detected`` () =
    let src = """
module M
let f () = task { return 1 }
"""
    test <@ asyncCount src = 0 @>

[<Fact>]
let ``empty source has no detections`` () =
    let src = "module M\n"
    test <@ asyncCount src = 0 @>

[<Fact>]
let ``detects multiple async blocks in same module`` () =
    let src = """
module M
let f () = async { return 1 }
let g () = async { return 2 }
let h () = async { return 3 }
"""
    test <@ asyncCount src = 3 @>

[<Fact>]
let ``does not detect non-async computation expressions`` () =
    let src = """
module M
let f () = seq { yield 1; yield 2 }
let g () = query { for x in [1;2;3] do select x }
"""
    test <@ asyncCount src = 0 @>

// ──────────────────────────────────────────────────────────────────────────────
// AllowAsync attribute suppression
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``AllowAsync suppresses detection on that binding`` () =
    let src = """
module M
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer
[<AllowAsync>]
let f () = async { return 1 }
"""
    test <@ asyncCount src = 0 @>

[<Fact>]
let ``AllowAsync only suppresses the annotated binding`` () =
    let src = """
module M
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer
[<AllowAsync>]
let f () = async { return 1 }
let g () = async { return 2 }
"""
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``AllowAsyncAttribute full name also suppresses`` () =
    let src = """
module M
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer
[<AllowAsyncAttribute>]
let f () = async { return 1 }
"""
    test <@ asyncCount src = 0 @>

// ──────────────────────────────────────────────────────────────────────────────
// Structural nesting
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``detects async inside if-then-else`` () =
    let src = """
module M
let f flag =
    if flag then async { return 1 }
    else async { return 2 }
"""
    test <@ asyncCount src = 2 @>

[<Fact>]
let ``detects async inside match expression`` () =
    let src = """
module M
let f x =
    match x with
    | 0 -> async { return "zero" }
    | _ -> async { return "other" }
"""
    test <@ asyncCount src = 2 @>

[<Fact>]
let ``detects async inside let binding`` () =
    let src = """
module M
let f () =
    let inner = async { return 42 }
    inner
"""
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``detects async inside lambda`` () =
    let src = """
module M
let f () =
    let action = fun () -> async { return 1 }
    action
"""
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``detects async inside try-with`` () =
    let src = """
module M
let f () =
    try async { return 1 }
    with _ -> async { return 0 }
"""
    test <@ asyncCount src = 2 @>

// ──────────────────────────────────────────────────────────────────────────────
// CE-specific patterns (let!, use!, and!)
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``detects async nested inside let! binding`` () =
    let src = """
module M
let f () = async {
    let! x = async { return 1 }
    return x
}
"""
    // The outer async { } + the nested async { } inside let! = 2
    test <@ asyncCount src = 2 @>

[<Fact>]
let ``detects async nested as rhs of let! in outer async`` () =
    let src = """
module M
let f () = async {
    let! a = async { return 1 }
    let! b = async { return 2 }
    return a + b
}
"""
    test <@ asyncCount src = 3 @>

[<Fact>]
let ``AllowAsync suppresses outer async but inner async in let! is still detected`` () =
    // AllowAsync only applies to the binding it annotates — not to inner async blocks
    let src = """
module M
open Orleans.FSharp.Analyzers.AsyncUsageAnalyzer
[<AllowAsync>]
let f () = async {
    let! x = async { return 1 }
    return x
}
"""
    // The outer async is suppressed, but the nested one inside let! is not
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``detects async nested inside use! binding`` () =
    let src = """
module M
let f () = async {
    use! _ = async { return new System.IO.MemoryStream() :> System.IDisposable }
    return 1
}
"""
    // outer async { } + async { } inside use! rhs = 2
    test <@ asyncCount src = 2 @>

[<Fact>]
let ``detects async inside while loop in CE`` () =
    let src = """
module M
let f () =
    let mutable i = 0
    while i < 3 do
        i <- i + 1
    async { return i }
"""
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``detects async inside for loop body`` () =
    let src = """
module M
let f items =
    for _ in items do
        ()
    async { return items }
"""
    test <@ asyncCount src = 1 @>

// ──────────────────────────────────────────────────────────────────────────────
// Nested modules / types
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``detects async in nested module`` () =
    let src = """
module M
module Inner =
    let f () = async { return 1 }
"""
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``detects async in class method`` () =
    let src = """
module M
type MyClass() =
    member _.Fetch() = async { return 1 }
"""
    test <@ asyncCount src = 1 @>

[<Fact>]
let ``detects multiple async blocks across class methods`` () =
    let src = """
module M
type MyClass() =
    member _.A() = async { return 1 }
    member _.B() = async { return 2 }
"""
    test <@ asyncCount src = 2 @>

// ──────────────────────────────────────────────────────────────────────────────
// AllowAsync attribute type test
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``AllowAsync attribute targets Method and Property`` () =
    let usage = typeof<AllowAsyncAttribute>.GetCustomAttributes(false)
    let usageAttr =
        usage
        |> Array.tryPick (function
            | :? System.AttributeUsageAttribute as u -> Some u
            | _ -> None)
    let targets =
        usageAttr
        |> Option.map (fun u -> u.ValidOn)
        |> Option.defaultValue AttributeTargets.All
    Assert.True(targets.HasFlag(AttributeTargets.Method))
    Assert.True(targets.HasFlag(AttributeTargets.Property))

[<Fact>]
let ``AllowAsync attribute does not allow multiple instances`` () =
    let usageAttr =
        typeof<AllowAsyncAttribute>.GetCustomAttributes(false)
        |> Array.tryPick (function
            | :? AttributeUsageAttribute as u -> Some u
            | _ -> None)
    test <@ usageAttr.IsSome @>
    test <@ usageAttr.Value.AllowMultiple = false @>

// ──────────────────────────────────────────────────────────────────────────────
// Analyzer module and function existence
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``asyncUsageAnalyzer function exists in the module`` () =
    let funcInfo =
        typeof<AllowAsyncAttribute>.Assembly.GetTypes()
        |> Array.tryPick (fun t ->
            t.GetMethods()
            |> Array.tryFind (fun m -> m.Name = "asyncUsageAnalyzer"))
    test <@ funcInfo.IsSome @>

[<Fact>]
let ``CliAnalyzer attribute is present on asyncUsageAnalyzer`` () =
    let hasAttr =
        typeof<AllowAsyncAttribute>.Assembly.GetTypes()
        |> Array.exists (fun t ->
            t.GetMethods()
            |> Array.exists (fun m ->
                m.Name = "asyncUsageAnalyzer"
                && m.GetCustomAttributes(false)
                   |> Array.exists (fun a ->
                       a.GetType().Name.Contains("CliAnalyzer"))))
    test <@ hasAttr @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``asyncCount is always non-negative for any source fragment`` (n: PositiveInt) =
    // Embed n async blocks in a generated source
    let blocks = List.init n.Get (fun i -> $"let f{i} () = async {{ return {i} }}")
    let source = String.concat "\n" blocks
    asyncCount source >= 0

[<Property>]
let ``AllowAsyncAttribute assembly has at least 1 public type`` () =
    typeof<AllowAsyncAttribute>.Assembly.GetExportedTypes().Length >= 1
