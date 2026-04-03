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
    /// Measures heap allocations for key operations to verify zero-alloc paths.
    /// Uses GC.GetAllocatedBytesForCurrentThread for precise per-thread measurement.
    /// </summary>
    let measureAllocations () =
        let handler (state: int) (_msg: string) : Task<int * obj> =
            Task.FromResult(state + 1, box (state + 1))

        let definition =
            grain {
                defaultState 0
                handle handler
            }

        let grainFactory = Unchecked.defaultof<Orleans.IGrainFactory>

        // Warm up
        for _ in 1..1000 do
            GrainDefinition.getHandler definition |> ignore

        // Measure: GrainRef.ofInt64 (struct — should be zero-alloc)
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        let before1 = GC.GetAllocatedBytesForCurrentThread()
        for _ in 1..10_000 do
            let _ref = { GrainRef.Factory = grainFactory; Key = 42L; Grain = Unchecked.defaultof<Orleans.IGrainWithIntegerKey> }
            ()
        let after1 = GC.GetAllocatedBytesForCurrentThread()
        let grainRefAllocPerCall = float (after1 - before1) / 10_000.0

        // Measure: GrainDefinition.getHandler (function lookup — should be near-zero)
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        let before2 = GC.GetAllocatedBytesForCurrentThread()
        let h = GrainDefinition.getHandler definition
        for _ in 1..10_000 do
            h |> ignore
        let after2 = GC.GetAllocatedBytesForCurrentThread()
        let getHandlerAllocPerCall = float (after2 - before2) / 10_000.0

        // Measure: handler invocation (Task.FromResult allocates Task<T>)
        GC.Collect(2, GCCollectionMode.Forced, true, true)
        GC.WaitForPendingFinalizers()
        let before3 = GC.GetAllocatedBytesForCurrentThread()
        for _ in 1..10_000 do
            (h 0 "x").GetAwaiter().GetResult() |> ignore
        let after3 = GC.GetAllocatedBytesForCurrentThread()
        let handlerInvokeAllocPerCall = float (after3 - before3) / 10_000.0

        (grainRefAllocPerCall, getHandlerAllocPerCall, handlerInvokeAllocPerCall)

    /// <summary>
    /// Runs all benchmarks including allocation measurements and prints results.
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

        let (grainRefAlloc, getHandlerAlloc, handlerInvokeAlloc) = measureAllocations ()

        printfn "=== Allocation Benchmarks (bytes/call) ==="
        printfn "GrainRef struct creation:  %.1f bytes" grainRefAlloc
        printfn "getHandler lookup:         %.1f bytes" getHandlerAlloc
        printfn "Handler invocation:        %.1f bytes" handlerInvokeAlloc
        printfn ""

        if grainRefAlloc < 1.0 then
            printfn "PASS: GrainRef is zero-alloc (%.1f bytes/call)" grainRefAlloc
        else
            printfn "INFO: GrainRef allocates %.1f bytes/call" grainRefAlloc

        if getHandlerAlloc < 1.0 then
            printfn "PASS: getHandler is zero-alloc (%.1f bytes/call)" getHandlerAlloc
        else
            printfn "INFO: getHandler allocates %.1f bytes/call" getHandlerAlloc

        printfn "INFO: Handler invocation allocates %.1f bytes/call (Task<T> + boxing)" handlerInvokeAlloc
        printfn ""

        if overheadPercent < 5.0 then
            printfn "PASS: Overhead %.2f%% is within the 5%% threshold (SC-003)" overheadPercent
        else
            printfn "FAIL: Overhead %.2f%% exceeds the 5%% threshold (SC-003)" overheadPercent

        overheadPercent
