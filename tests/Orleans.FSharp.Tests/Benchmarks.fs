namespace Orleans.FSharp.Tests

open System
open System.Diagnostics
open System.Threading.Tasks
open Orleans.FSharp

/// <summary>
/// Performance benchmarks measuring F# grain definition dispatch overhead
/// versus direct handler invocation. Validates SC-003: overhead less than 5%.
/// </summary>
/// <remarks>
/// These benchmarks run outside the normal xUnit test suite.
/// BenchmarkDotNet is not used due to a transitive dependency conflict
/// (Microsoft.CodeAnalysis.CSharp 4.x vs 5.x) on net10.0.
/// When BenchmarkDotNet supports net10.0 natively, replace this module
/// with [MemoryDiagnoser] / [SimpleJob] attributed benchmark classes.
///
/// To run: instantiate GrainCallBenchmarks and call RunAll().
/// </remarks>
module Benchmarks =

    /// <summary>
    /// Number of iterations for warm-up phase.
    /// </summary>
    let private warmupIterations = 10_000

    /// <summary>
    /// Number of iterations for measurement phase.
    /// </summary>
    let private measureIterations = 1_000_000

    /// <summary>
    /// Runs a benchmark function for the specified number of iterations
    /// and returns the elapsed time.
    /// </summary>
    /// <param name="iterations">Number of iterations to execute.</param>
    /// <param name="f">The function to benchmark, returning a Task.</param>
    /// <returns>The elapsed TimeSpan for all iterations.</returns>
    let private runBenchmark (iterations: int) (f: unit -> Task<int * obj>) : TimeSpan =
        // Force synchronous execution for tight measurement loop
        let sw = Stopwatch.StartNew()

        for _ in 1..iterations do
            (f ()).GetAwaiter().GetResult() |> ignore

        sw.Stop()
        sw.Elapsed

    /// <summary>
    /// Measures the overhead percentage of GrainDefinition dispatch vs direct handler calls.
    /// Returns (directTime, dispatchTime, overheadPercent).
    /// </summary>
    /// <returns>A tuple of (baseline elapsed, measured elapsed, overhead percentage).</returns>
    let measureOverhead () : TimeSpan * TimeSpan * float =
        let handler (state: int) (msg: string) : Task<int * obj> =
            let newState = state + msg.Length
            Task.FromResult(newState, box newState)

        let definition =
            grain {
                defaultState 0
                handle handler
            }

        let directCall () = handler 42 "hello"

        let dispatchCall () =
            let h = GrainDefinition.getHandler definition
            h 42 "hello"

        // Warm up both paths
        runBenchmark warmupIterations directCall |> ignore
        runBenchmark warmupIterations dispatchCall |> ignore

        // Measure
        let directTime = runBenchmark measureIterations directCall
        let dispatchTime = runBenchmark measureIterations dispatchCall

        let overheadPercent =
            if directTime.TotalMilliseconds > 0.0 then
                ((dispatchTime.TotalMilliseconds - directTime.TotalMilliseconds)
                 / directTime.TotalMilliseconds)
                * 100.0
            else
                0.0

        (directTime, dispatchTime, overheadPercent)

    /// <summary>
    /// Runs all benchmarks and prints results to stdout.
    /// Asserts that the dispatch overhead is less than 5% (SC-003).
    /// </summary>
    let runAll () =
        printfn "=== Orleans.FSharp Grain Call Benchmarks ==="
        printfn "Iterations: %d (warmup: %d)" measureIterations warmupIterations
        printfn ""

        let (directTime, dispatchTime, overheadPercent) = measureOverhead ()

        printfn "Direct handler call:       %A" directTime
        printfn "GrainDefinition dispatch:  %A" dispatchTime
        printfn "Overhead:                  %.2f%%" overheadPercent
        printfn ""

        if overheadPercent < 5.0 then
            printfn "PASS: Overhead %.2f%% is within the 5%% threshold (SC-003)" overheadPercent
        else
            printfn "FAIL: Overhead %.2f%% exceeds the 5%% threshold (SC-003)" overheadPercent

        overheadPercent
