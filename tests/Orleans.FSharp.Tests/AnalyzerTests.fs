module Orleans.FSharp.Tests.AnalyzerTests

open Xunit
open Swensen.Unquote
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open FSharp.Analyzers.SDK
open Orleans.FSharp.Analyzers

// ---------------------------------------------------------------------------
// Helper: Parse F# source and run the analyzer
// ---------------------------------------------------------------------------

/// <summary>
/// Parses F# source code and returns the ParsedInput (untyped AST).
/// </summary>
let private parseSource (source: string) =
    let checker = FSharpChecker.Create()

    let parseResults =
        checker.ParseFile(
            "test.fs",
            SourceText.ofString source,
            { FSharpParsingOptions.Default with
                SourceFiles = [| "test.fs" |]
            }
        )
        |> Async.RunSynchronously

    parseResults.ParseTree

/// <summary>
/// Runs the async usage detection logic on the given source code
/// and returns diagnostics.
/// </summary>
let private analyzeSource (source: string) : Message list =
    let tree = parseSource source
    AsyncUsageAnalyzer.detectAsyncUsage tree

// ---------------------------------------------------------------------------
// Tests: Detecting async { } usage
// ---------------------------------------------------------------------------

[<Fact>]
let ``detectAsyncUsage finds async CE in let binding`` () =
    let source =
        """
module Test
let myFun () = async { return 42 }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length = 1 @>
    test <@ diagnostics.[0].Code = "OF0001" @>
    test <@ diagnostics.[0].Severity = Severity.Warning @>

[<Fact>]
let ``detectAsyncUsage finds multiple async CEs`` () =
    let source =
        """
module Test
let fun1 () = async { return 1 }
let fun2 () = async { return 2 }
let fun3 () = async { return 3 }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length = 3 @>

[<Fact>]
let ``detectAsyncUsage message mentions task`` () =
    let source =
        """
module Test
let myFun () = async { return 42 }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.[0].Message.Contains("task") @>
    test <@ diagnostics.[0].Message.Contains("async") @>

// ---------------------------------------------------------------------------
// Tests: task { } does NOT trigger diagnostic
// ---------------------------------------------------------------------------

[<Fact>]
let ``detectAsyncUsage ignores task CE`` () =
    let source =
        """
module Test
let myFun () = task { return 42 }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length = 0 @>

[<Fact>]
let ``detectAsyncUsage ignores task and flags async`` () =
    let source =
        """
module Test
let good () = task { return 1 }
let bad () = async { return 2 }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length = 1 @>
    test <@ diagnostics.[0].Code = "OF0001" @>

[<Fact>]
let ``detectAsyncUsage ignores code without CEs`` () =
    let source =
        """
module Test
let add x y = x + y
let result = add 1 2
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length = 0 @>

// ---------------------------------------------------------------------------
// Tests: [<AllowAsync>] attribute suppresses warning
// ---------------------------------------------------------------------------

[<Fact>]
let ``detectAsyncUsage suppressed by AllowAsync attribute`` () =
    let source =
        """
module Test
[<AllowAsync>]
let myFun () = async { return 42 }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length = 0 @>

[<Fact>]
let ``detectAsyncUsage AllowAsync only suppresses attributed function`` () =
    let source =
        """
module Test
[<AllowAsync>]
let allowed () = async { return 1 }
let notAllowed () = async { return 2 }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length = 1 @>

[<Fact>]
let ``detectAsyncUsage works with nested async in non-attributed function`` () =
    let source =
        """
module Test
let myFun () =
    let inner = async { return 42 }
    async { return! inner }
"""

    let diagnostics = analyzeSource source
    test <@ diagnostics.Length >= 1 @>
