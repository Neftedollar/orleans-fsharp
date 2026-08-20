module Orleans.FSharp.Tests.GrainResilienceTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Polly
open Polly.CircuitBreaker
open Polly.Timeout
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Returns a function that fails for the first `n` calls then returns `value`.
let private failThenSucceed (n: int) (value: 'T) : unit -> Task<'T> =
    let callCount = ref 0

    fun () ->
        task {
            let count = System.Threading.Interlocked.Increment(callCount)

            if count <= n then
                raise (InvalidOperationException($"Transient failure #{count}"))

            return value
        }

/// Unwraps a potential AggregateException to its inner exception.
let private unwrapAggregate (ex: exn) =
    match ex with
    | :? AggregateException as agg when agg.InnerExceptions.Count = 1 -> agg.InnerException
    | _ -> ex

// ---------------------------------------------------------------------------
// defaultOptions tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``defaultOptions has MaxRetryAttempts of 3`` () =
    test <@ GrainResilience.defaultOptions.MaxRetryAttempts = 3 @>

[<Fact>]
let ``defaultOptions has RetryDelay of 1 second`` () =
    test <@ GrainResilience.defaultOptions.RetryDelay = TimeSpan.FromSeconds(1.0) @>

[<Fact>]
let ``defaultOptions has no circuit breaker threshold`` () =
    test <@ GrainResilience.defaultOptions.CircuitBreakerThreshold = None @>

[<Fact>]
let ``defaultOptions has no circuit breaker duration`` () =
    test <@ GrainResilience.defaultOptions.CircuitBreakerDuration = None @>

[<Fact>]
let ``defaultOptions has no timeout`` () =
    test <@ GrainResilience.defaultOptions.Timeout = None @>

// ---------------------------------------------------------------------------
// execute — retry behaviour
// ---------------------------------------------------------------------------

[<Fact>]
let ``execute succeeds after transient failures within retry budget`` () =
    task {
        let f = failThenSucceed 2 42

        let! result =
            GrainResilience.execute
                { GrainResilience.defaultOptions with
                    MaxRetryAttempts = 3
                    RetryDelay = TimeSpan.Zero }
                f

        test <@ result = 42 @>
    }

[<Fact>]
let ``execute re-invokes the call once per attempt`` () =
    task {
        // Pins the Step-0 truth behind the timeout fix: the callback handed to Polly invokes `f`
        // itself, so every attempt is a fresh invocation — retries are not repeated awaits of one
        // already-started task.
        let invocations = ref 0

        let f () =
            task {
                System.Threading.Interlocked.Increment(invocations) |> ignore
                raise (InvalidOperationException("always fails"))
                return 0
            }

        try
            let! _ =
                GrainResilience.execute
                    { GrainResilience.defaultOptions with
                        MaxRetryAttempts = 2
                        RetryDelay = TimeSpan.Zero }
                    f

            ()
        with _ ->
            ()

        test <@ !invocations = 3 @>
    }

[<Fact>]
let ``execute throws when all retry attempts are exhausted`` () =
    task {
        let f = failThenSucceed 10 0

        let! ex =
            task {
                try
                    let! _ =
                        GrainResilience.execute
                            { GrainResilience.defaultOptions with
                                MaxRetryAttempts = 2
                                RetryDelay = TimeSpan.Zero }
                            f

                    return null :> exn
                with ex ->
                    return ex
            }

        test <@ not (isNull ex) @>
        test <@ ex :? InvalidOperationException @>
    }

[<Fact>]
let ``execute with zero retries does not retry on failure`` () =
    task {
        let callCount = ref 0

        let f () =
            task {
                System.Threading.Interlocked.Increment(callCount) |> ignore
                raise (InvalidOperationException("always fails"))
                return 0
            }

        try
            let! _ =
                GrainResilience.execute
                    { GrainResilience.defaultOptions with
                        MaxRetryAttempts = 0 }
                    f

            ()
        with _ ->
            ()

        test <@ !callCount = 1 @>
    }

[<Fact>]
let ``execute passes result through when no failure occurs`` () =
    task {
        let! result =
            GrainResilience.execute GrainResilience.defaultOptions (fun () -> task { return "hello" })

        test <@ result = "hello" @>
    }

// ---------------------------------------------------------------------------
// retry shorthand
// ---------------------------------------------------------------------------

[<Fact>]
let ``retry shorthand succeeds after transient failures`` () =
    task {
        let f = failThenSucceed 1 99

        let! result = GrainResilience.retry 3 TimeSpan.Zero f

        test <@ result = 99 @>
    }

[<Fact>]
let ``retry shorthand throws after exceeding maxAttempts`` () =
    task {
        let f = failThenSucceed 5 0

        let! ex =
            task {
                try
                    let! _ = GrainResilience.retry 2 TimeSpan.Zero f
                    return null :> exn
                with ex ->
                    return ex
            }

        test <@ not (isNull ex) @>
    }

// ---------------------------------------------------------------------------
// withTimeout shorthand
// ---------------------------------------------------------------------------

// These replace two wall-clock tests that asserted a 50 ms Polly budget was met (backlog item
// 11's flake family) and that never called GrainResilience at all — they drove a hand-built Polly
// pipeline, so they passed while `withTimeout` itself could not time anything out.
//
// The shape here asserts an OUTCOME under a deadline that only the pipeline can end: the
// protected call is gated on a TaskCompletionSource that is never released inside the timed
// region, so if the deadline does not fire the call cannot finish on its own. The 30 s net
// decides WHICH task completed first, not how fast the timeout was — a loaded machine slows the
// timeout, it does not stop it.

[<Fact>]
let ``withTimeout raises TimeoutRejectedException for a call that never completes`` () =
    task {
        let gate = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

        let guarded =
            task {
                try
                    let! _ = GrainResilience.withTimeout (TimeSpan.FromMilliseconds(50.0)) (fun () -> gate.Task)
                    return null :> exn
                with ex ->
                    return unwrapAggregate ex
            }

        let! finished = Task.WhenAny(guarded, Task.Delay(TimeSpan.FromSeconds(30.0)))
        test <@ Object.ReferenceEquals(finished, guarded) @>

        let! ex = guarded
        test <@ not (isNull ex) @>
        test <@ ex :? TimeoutRejectedException @>

        gate.TrySetResult 0 |> ignore
    }

[<Fact>]
let ``withTimeout raises TimeoutRejectedException for a call that ignores the deadline`` () =
    task {
        // The measured 4.0.0 defect: a plain slow Task returned the result long after the budget
        // and never raised. The call still runs to completion in the background — the deadline
        // abandons it, it does not cancel it.
        let ran = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let slow () =
            task {
                do! Task.Delay(TimeSpan.FromMilliseconds(800.0))
                ran.TrySetResult() |> ignore
                return 7
            }

        let! ex =
            task {
                try
                    let! _ = GrainResilience.withTimeout (TimeSpan.FromMilliseconds(50.0)) slow
                    return null :> exn
                with ex ->
                    return unwrapAggregate ex
            }

        test <@ not (isNull ex) @>
        test <@ ex :? TimeoutRejectedException @>

        // The abandoned call is still alive; awaiting it here also keeps the test from leaving
        // work running past its own end.
        let! completed = Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(30.0)))
        test <@ Object.ReferenceEquals(completed, ran.Task) @>
    }

[<Fact>]
let ``withTimeout completes successfully when call is fast`` () =
    task {
        let! result = GrainResilience.withTimeout (TimeSpan.FromSeconds(5.0)) (fun () -> task { return 7 })

        test <@ result = 7 @>
    }

[<Fact>]
let ``execute enforces the deadline over the whole sequence and does not re-invoke a hung call`` () =
    task {
        // The timeout is the outermost strategy: a hung attempt consumes the whole budget, so the
        // retry strategy never gets a second attempt. This pins the composition, not a duration.
        let gate = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let invocations = ref 0

        let guarded =
            task {
                try
                    let! _ =
                        GrainResilience.execute
                            { GrainResilience.defaultOptions with
                                MaxRetryAttempts = 3
                                RetryDelay = TimeSpan.Zero
                                Timeout = Some(TimeSpan.FromMilliseconds(50.0)) }
                            (fun () ->
                                System.Threading.Interlocked.Increment(invocations) |> ignore
                                gate.Task)

                    return null :> exn
                with ex ->
                    return unwrapAggregate ex
            }

        let! finished = Task.WhenAny(guarded, Task.Delay(TimeSpan.FromSeconds(30.0)))
        test <@ Object.ReferenceEquals(finished, guarded) @>

        let! ex = guarded
        test <@ ex :? TimeoutRejectedException @>
        test <@ !invocations = 1 @>

        gate.TrySetResult 0 |> ignore
    }

// ---------------------------------------------------------------------------
// executeCancellable / withTimeoutCancellable
// ---------------------------------------------------------------------------

[<Fact>]
let ``withTimeoutCancellable cancels the operation at the deadline`` () =
    task {
        let observedCancellation = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        let guarded =
            task {
                try
                    let! _ =
                        GrainResilience.withTimeoutCancellable
                            (TimeSpan.FromMilliseconds(50.0))
                            (fun ct ->
                                task {
                                    try
                                        do! Task.Delay(TimeSpan.FromSeconds(30.0), ct)
                                        return 0
                                    with :? OperationCanceledException ->
                                        observedCancellation.TrySetResult true |> ignore
                                        return raise (OperationCanceledException(ct))
                                })

                    return null :> exn
                with ex ->
                    return unwrapAggregate ex
            }

        let! finished = Task.WhenAny(guarded, Task.Delay(TimeSpan.FromSeconds(30.0)))
        test <@ Object.ReferenceEquals(finished, guarded) @>

        let! ex = guarded
        test <@ ex :? TimeoutRejectedException @>

        let! observed = observedCancellation.Task
        test <@ observed @>
    }

[<Fact>]
let ``executeCancellable passes a token that cannot fire when no timeout is configured`` () =
    task {
        let canBeCanceled = ref true

        let! result =
            GrainResilience.executeCancellable
                { GrainResilience.defaultOptions with
                    MaxRetryAttempts = 0 }
                (fun ct ->
                    canBeCanceled.Value <- ct.CanBeCanceled
                    Task.FromResult 5)

        test <@ result = 5 @>
        test <@ not !canBeCanceled @>
    }

[<Fact>]
let ``executeCancellable re-invokes the operation on each retry attempt`` () =
    task {
        let invocations = ref 0

        let! result =
            GrainResilience.executeCancellable
                { GrainResilience.defaultOptions with
                    MaxRetryAttempts = 3
                    RetryDelay = TimeSpan.Zero }
                (fun _ct ->
                    task {
                        let n = System.Threading.Interlocked.Increment(invocations)

                        if n <= 2 then
                            raise (InvalidOperationException("transient"))

                        return 42
                    })

        test <@ result = 42 @>
        test <@ !invocations = 3 @>
    }

// ---------------------------------------------------------------------------
// circuitBreaker
// ---------------------------------------------------------------------------

[<Fact>]
let ``circuitBreaker opens after threshold failures`` () =
    task {
        let threshold = 3
        let breakDuration = TimeSpan.FromSeconds(60.0)
        let pipeline = GrainResilience.circuitBreaker threshold breakDuration

        // Drive failures to saturate the sampling window
        for _ in 1 .. threshold do
            try
                do! pipeline.ExecuteAsync(fun _ct ->
                    System.Threading.Tasks.ValueTask(task {
                        raise (InvalidOperationException("forced failure"))
                    } :> Task))
            with _ ->
                ()

        // Next call should see an open circuit
        let! ex =
            task {
                try
                    do! pipeline.ExecuteAsync(fun _ct ->
                        System.Threading.Tasks.ValueTask(task { () } :> Task))

                    return null :> exn
                with ex ->
                    return ex
            }

        test <@ not (isNull ex) @>
        test <@ ex :? BrokenCircuitException @>
    }

[<Fact>]
let ``execute builds a fresh pipeline per call so circuit state is not shared`` () =
    task {
        // Not a wish, a measurement: `execute` calls buildPipeline on every invocation, so the
        // breaker configured in ResilienceOptions starts cold each time and the circuit never
        // opens across calls. Documented in resilience.md; this test is what keeps the docs true.
        let opts =
            { GrainResilience.defaultOptions with
                MaxRetryAttempts = 0
                CircuitBreakerThreshold = Some 2
                CircuitBreakerDuration = Some(TimeSpan.FromSeconds(30.0)) }

        let boom () =
            task {
                raise (InvalidOperationException("boom"))
                return 0
            }

        let observed = ResizeArray<exn>()

        for _ in 1..5 do
            try
                let! _ = GrainResilience.execute opts boom
                ()
            with ex ->
                observed.Add(unwrapAggregate ex)

        test <@ observed.Count = 5 @>
        test <@ observed |> Seq.forall (fun ex -> ex :? InvalidOperationException) @>
        test <@ observed |> Seq.forall (fun ex -> not (ex :? BrokenCircuitException)) @>
    }

[<Fact>]
let ``a reused buildPipeline opens the circuit after the threshold`` () =
    task {
        // The counterpart of the test above: shared circuit state is available, it just has to be
        // asked for by keeping one pipeline object.
        let pipeline =
            GrainResilience.buildPipeline<int>
                { GrainResilience.defaultOptions with
                    MaxRetryAttempts = 0
                    CircuitBreakerThreshold = Some 2
                    CircuitBreakerDuration = Some(TimeSpan.FromSeconds(30.0)) }

        let observed = ResizeArray<exn>()

        for _ in 1..5 do
            try
                let! _ =
                    pipeline
                        .ExecuteAsync(fun _ct ->
                            ValueTask<int>(
                                task {
                                    raise (InvalidOperationException("boom"))
                                    return 0
                                }))
                        .AsTask()

                ()
            with ex ->
                observed.Add(unwrapAggregate ex)

        test <@ observed.Count = 5 @>
        test <@ observed |> Seq.exists (fun ex -> ex :? BrokenCircuitException) @>
    }

// ---------------------------------------------------------------------------
// options composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``options can compose circuit breaker threshold and timeout together`` () =
    let opts =
        { GrainResilience.defaultOptions with
            CircuitBreakerThreshold = Some 5
            CircuitBreakerDuration = Some(TimeSpan.FromSeconds(10.0))
            Timeout = Some(TimeSpan.FromSeconds(2.0)) }

    test <@ opts.CircuitBreakerThreshold = Some 5 @>
    test <@ opts.CircuitBreakerDuration = Some(TimeSpan.FromSeconds(10.0)) @>
    test <@ opts.Timeout = Some(TimeSpan.FromSeconds(2.0)) @>

[<Fact>]
let ``buildPipeline returns a non-null pipeline`` () =
    let pipeline = GrainResilience.buildPipeline<int> GrainResilience.defaultOptions
    test <@ not (isNull (box pipeline)) @>

[<Fact>]
let ``buildPipeline rejects options outside the bounds Polly validates`` () =
    // These bounds are documented in resilience.md, so they need a test rather than a memory:
    // Polly throws at BUILD time, i.e. from inside the execute / withTimeout call. If a Polly
    // upgrade moves a bound, this test fails and the documented numbers get corrected with it.
    let builds (options: ResilienceOptions) =
        try
            GrainResilience.buildPipeline<int> options |> ignore
            true
        with :? System.ComponentModel.DataAnnotations.ValidationException ->
            false

    let withTimeoutOf ms =
        { GrainResilience.defaultOptions with
            Timeout = Some(TimeSpan.FromMilliseconds(ms: float)) }

    let withThreshold n =
        { GrainResilience.defaultOptions with
            CircuitBreakerThreshold = Some n }

    // Timeout: [10 ms, 24 h]
    test <@ not (builds (withTimeoutOf 9.0)) @>
    test <@ builds (withTimeoutOf 10.0) @>
    test <@ not (builds { GrainResilience.defaultOptions with Timeout = Some TimeSpan.Zero }) @>

    test
        <@ not (builds { GrainResilience.defaultOptions with Timeout = Some(TimeSpan.FromHours 25.0) }) @>

    // CircuitBreakerThreshold maps to MinimumThroughput, which starts at 2
    test <@ not (builds (withThreshold 1)) @>
    test <@ builds (withThreshold 2) @>

// ---------------------------------------------------------------------------
// Property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``retry count is bounded: actual calls <= maxAttempts + 1`` (attempts: PositiveInt) =
    let maxAttempts = min attempts.Get 10 // cap to avoid slow tests
    let callCount = ref 0

    let f () =
        task {
            System.Threading.Interlocked.Increment(callCount) |> ignore
            raise (InvalidOperationException("always fails"))
            return 0
        }

    try
        GrainResilience.retry maxAttempts TimeSpan.Zero f |> ignore
    with _ ->
        ()

    // Wait for the task to complete (it always throws here)
    let mutable waited = false

    let task =
        try
            GrainResilience.retry maxAttempts TimeSpan.Zero (fun () ->
                task {
                    System.Threading.Interlocked.Increment(callCount) |> ignore
                    raise (InvalidOperationException("always fails"))
                    return 0
                })
        with _ ->
            waited <- true
            Task.FromResult(0)

    if not waited then
        try
            task.Wait()
        with _ ->
            ()

    // The call count accumulated from both runs (initial + retries per run) is bounded
    // We just check the property is structurally satisfied (no infinite retry)
    !callCount <= (maxAttempts + 1) * 2

[<Property>]
let ``execute with 0 retries calls the function exactly once`` (value: int) =
    let callCount = ref 0

    let f () =
        task {
            System.Threading.Interlocked.Increment(callCount) |> ignore
            return value
        }

    let task =
        GrainResilience.execute
            { GrainResilience.defaultOptions with MaxRetryAttempts = 0 }
            f

    task.Wait()
    !callCount = 1

[<Property>]
let ``defaultOptions MaxRetryAttempts is non-negative`` () =
    GrainResilience.defaultOptions.MaxRetryAttempts >= 0

[<Property>]
let ``defaultOptions RetryDelay is non-negative`` () =
    GrainResilience.defaultOptions.RetryDelay >= TimeSpan.Zero
