namespace Orleans.FSharp.Integration

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ---------------------------------------------------------------------------
// GrainResilience integration tests
// ---------------------------------------------------------------------------

/// <summary>
/// Integration tests for <see cref="GrainResilience"/> against a live TestCluster.
///
/// The retry tests use a local atomic counter to simulate transient failures —
/// the closure passed to <c>GrainResilience.retry</c> throws for the first N
/// invocations, then delegates to a real grain call. This tests that Polly
/// correctly retries the wrapped closure (which includes real grain calls)
/// and eventually returns the grain's result.
///
/// Tests that don't need simulated failures call grains directly, verifying
/// that <c>execute</c> and <c>withTimeout</c> pass through results unchanged.
/// </summary>
[<Collection("ClusterCollection")>]
type GrainResilienceIntegrationTests(fixture: ClusterFixture) =

    let key () = Guid.NewGuid().ToString("N")

    // ── retry — succeeds on the first attempt ────────────────────────────────

    [<Fact>]
    member _.``retry with 0 failures needed succeeds on first call`` () =
        task {
            let grain = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())

            let! count =
                GrainResilience.retry<int> 3 TimeSpan.Zero (fun () ->
                    FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall grain)

            test <@ count = 1 @>
        }

    // ── retry — retries through simulated transient failures ─────────────────

    [<Fact>]
    member _.``retry succeeds after 2 simulated transient failures`` () =
        task {
            let grain = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())
            let remaining = ref 2   // fail 2 times then succeed

            let! count =
                GrainResilience.retry<int> 3 TimeSpan.Zero (fun () ->
                    task {
                        if Interlocked.Decrement(remaining) >= 0 then
                            failwith "Simulated transient failure"
                        return! FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall grain
                    })

            test <@ count = 1 @>  // grain was called once (on the successful attempt)
        }

    // ── retry — exhausts all retries ─────────────────────────────────────────

    [<Fact>]
    member _.``retry throws after all attempts are exhausted`` () =
        task {
            let! ex =
                Assert.ThrowsAnyAsync<exn>(fun () ->
                    // Always fail — never reaches the grain
                    GrainResilience.retry<int> 2 TimeSpan.Zero (fun () ->
                        task { return failwith<int> "always fails" })
                    :> Task)

            test <@ ex <> null @>
        }

    // ── retry — verifies attempt count via local counter ────────────────────

    [<Fact>]
    member _.``retry lambda is invoked the expected number of times`` () =
        task {
            let invocations = ref 0
            let remaining = ref 2   // fail 2 times

            let! _ =
                GrainResilience.retry<int> 3 TimeSpan.Zero (fun () ->
                    task {
                        Interlocked.Increment(invocations) |> ignore
                        if Interlocked.Decrement(remaining) >= 0 then
                            failwith "transient"
                        return 42
                    })

            // Initial attempt + 2 retries = 3 invocations
            test <@ !invocations = 3 @>
        }

    // ── retry — grain call succeeds on retry ────────────────────────────────

    [<Fact>]
    member _.``retry returns correct grain result after retries`` () =
        task {
            let grain = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())
            let remaining = ref 1   // fail once, then call grain

            let! count =
                GrainResilience.retry<int> 3 TimeSpan.Zero (fun () ->
                    task {
                        if Interlocked.Decrement(remaining) >= 0 then
                            failwith "transient"
                        return! FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall grain
                    })

            // The grain was called exactly once (on the successful attempt)
            let! callCount = FSharpGrain.ask<FlakyState, FlakyCommand, int> GetCallCount grain
            test <@ count = 1 @>
            test <@ callCount = 1 @>
        }

    // ── execute — default options, healthy grain ─────────────────────────────

    [<Fact>]
    member _.``execute with default options passes through grain result unchanged`` () =
        task {
            let grain = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())

            let! count =
                GrainResilience.execute<int> GrainResilience.defaultOptions (fun () ->
                    FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall grain)

            test <@ count = 1 @>
        }

    // ── execute — retry + timeout combined ───────────────────────────────────

    [<Fact>]
    member _.``execute with retry and generous timeout succeeds after transient failure`` () =
        task {
            let grain = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())
            let remaining = ref 1

            let opts =
                { GrainResilience.defaultOptions with
                    MaxRetryAttempts = 3
                    RetryDelay = TimeSpan.Zero
                    Timeout = Some(TimeSpan.FromSeconds 10.0) }

            let! count =
                GrainResilience.execute<int> opts (fun () ->
                    task {
                        if Interlocked.Decrement(remaining) >= 0 then
                            failwith "transient"
                        return! FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall grain
                    })

            test <@ count = 1 @>
        }

    // ── withTimeout — completes within deadline ──────────────────────────────

    [<Fact>]
    member _.``withTimeout succeeds when grain responds before the deadline`` () =
        task {
            let grain = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())

            let! count =
                GrainResilience.withTimeout<int> (TimeSpan.FromSeconds 10.0) (fun () ->
                    FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall grain)

            test <@ count = 1 @>
        }

    // ── buildPipeline — zero retries smoke test ──────────────────────────────

    [<Fact>]
    member _.``buildPipeline with MaxRetryAttempts 0 executes lambda exactly once`` () =
        task {
            let invocations = ref 0
            let opts = { GrainResilience.defaultOptions with MaxRetryAttempts = 0 }

            let! result =
                GrainResilience.execute<int> opts (fun () ->
                    task {
                        Interlocked.Increment(invocations) |> ignore
                        return 99
                    })

            test <@ result = 99 @>
            test <@ !invocations = 1 @>
        }

    // ── multiple grain calls via retry ───────────────────────────────────────

    [<Fact>]
    member _.``retry does not mix up results from different grain calls`` () =
        task {
            let g1 = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())
            let g2 = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())

            let! r1 =
                GrainResilience.retry<int> 3 TimeSpan.Zero (fun () ->
                    FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall g1)

            // Call g2 twice so its counter is 2
            let! _ = FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall g2
            let! r2 =
                GrainResilience.retry<int> 3 TimeSpan.Zero (fun () ->
                    FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall g2)

            test <@ r1 = 1 @>   // g1 called once
            test <@ r2 = 2 @>   // g2 called twice total (once before + once in retry)
        }

    // ── retry — 1 max attempt succeeds on first retry ────────────────────────

    [<Fact>]
    member _.``retry with MaxRetryAttempts 1 succeeds if second invocation succeeds`` () =
        task {
            let grain = FSharpGrain.ref<FlakyState, FlakyCommand> fixture.GrainFactory (key())
            let remaining = ref 1

            let! count =
                GrainResilience.retry<int> 1 TimeSpan.Zero (fun () ->
                    task {
                        if Interlocked.Decrement(remaining) >= 0 then
                            failwith "transient"
                        return! FSharpGrain.ask<FlakyState, FlakyCommand, int> TryCall grain
                    })

            test <@ count = 1 @>
        }
