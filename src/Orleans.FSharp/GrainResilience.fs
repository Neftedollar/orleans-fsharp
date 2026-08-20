namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Polly
open Polly.CircuitBreaker
open Polly.Retry
open Polly.Timeout

/// <summary>
/// Options that control how resilience strategies are composed for Orleans grain calls.
/// </summary>
type ResilienceOptions =
    {
        /// <summary>Maximum number of retry attempts (not counting the initial attempt).</summary>
        MaxRetryAttempts: int

        /// <summary>Base delay between retry attempts.</summary>
        RetryDelay: TimeSpan

        /// <summary>
        /// Number of consecutive failures that cause the circuit to open.
        /// Set to <c>None</c> to disable the circuit breaker.
        /// Maps to <c>MinimumThroughput</c> in Polly v8's rate-based circuit breaker.
        /// <para>
        /// Circuit state lives in the pipeline object, and <c>execute</c> builds a fresh pipeline
        /// on every call — so a breaker configured here is scoped to one call and cannot trip
        /// across calls. For a shared breaker, build the pipeline once with <c>buildPipeline</c>
        /// (or <c>circuitBreaker</c>) and reuse the returned object.
        /// </para>
        /// </summary>
        CircuitBreakerThreshold: int option

        /// <summary>
        /// Duration the circuit remains open before moving to the half-open state.
        /// Only used when <see cref="CircuitBreakerThreshold"/> is set.
        /// </summary>
        CircuitBreakerDuration: TimeSpan option

        /// <summary>
        /// Deadline over the whole protected operation, not over one attempt: the timeout is the
        /// outermost strategy, so it spans every retry attempt plus the delays between them.
        /// Set to <c>None</c> to disable.
        /// </summary>
        Timeout: TimeSpan option
    }

/// <summary>
/// F#-idiomatic Polly v8 wrappers for building resilient Orleans grain call pipelines.
/// Provides helpers for retry, circuit-breaker, timeout, and composable pipelines.
/// </summary>
[<RequireQualifiedAccess>]
module GrainResilience =

    /// <summary>
    /// Sensible default options: 3 retries with a 1-second delay, no circuit breaker, no timeout.
    /// </summary>
    let defaultOptions: ResilienceOptions =
        {
            MaxRetryAttempts = 3
            RetryDelay = TimeSpan.FromSeconds(1.0)
            CircuitBreakerThreshold = None
            CircuitBreakerDuration = None
            Timeout = None
        }

    /// <summary>
    /// Builds a <see cref="ResiliencePipeline{T}"/> from <see cref="ResilienceOptions"/>.
    /// In Polly v8 the strategy added first is the outermost one, so the layering is
    /// timeout (outer) → circuit breaker → retry (inner) → the protected call. The timeout is
    /// therefore a deadline over the whole retry sequence, and the circuit breaker sees one
    /// outcome per pipeline execution — the retry strategy's final verdict, not each attempt.
    /// <para>
    /// The returned pipeline holds the circuit-breaker state. Build it once and reuse it if you
    /// want that state shared across calls; <c>execute</c> deliberately does not, see
    /// <see cref="ResilienceOptions.CircuitBreakerThreshold"/>.
    /// </para>
    /// </summary>
    /// <typeparam name="T">The result type returned by the grain call.</typeparam>
    /// <param name="options">Options that control retry, circuit-breaker, and timeout behaviour.</param>
    /// <returns>A configured <see cref="ResiliencePipeline{T}"/>.</returns>
    let buildPipeline<'T> (options: ResilienceOptions) : ResiliencePipeline<'T> =
        let builder = ResiliencePipelineBuilder<'T>()

        // Outermost: deadline over the whole execution (every attempt and every retry delay)
        match options.Timeout with
        | Some t -> builder.AddTimeout(t) |> ignore
        | None -> ()

        // Middle: circuit breaker
        match options.CircuitBreakerThreshold with
        | Some threshold ->
            let breakDuration =
                options.CircuitBreakerDuration
                |> Option.defaultValue (TimeSpan.FromSeconds(30.0))

            builder.AddCircuitBreaker(
                CircuitBreakerStrategyOptions<'T>(
                    FailureRatio = 1.0,
                    MinimumThroughput = threshold,
                    SamplingDuration = TimeSpan.FromSeconds(float threshold * 2.0),
                    BreakDuration = breakDuration
                )
            )
            |> ignore
        | None -> ()

        // Innermost: retry
        if options.MaxRetryAttempts > 0 then
            builder.AddRetry(
                RetryStrategyOptions<'T>(MaxRetryAttempts = options.MaxRetryAttempts, Delay = options.RetryDelay)
            )
            |> ignore

        builder.Build()

    /// <summary>
    /// Awaits an already-started operation under the pipeline's own cancellation token, so a
    /// deadline is enforced for the caller even when the operation cannot be cancelled.
    /// </summary>
    /// <remarks>
    /// A <c>unit -> Task&lt;'T&gt;</c> takes no token, so nothing can interrupt the work itself.
    /// What this does guarantee is that the caller stops waiting when the token fires: the
    /// in-flight task is abandoned and an <c>OperationCanceledException</c> is raised, which
    /// Polly's timeout strategy converts into <see cref="TimeoutRejectedException"/>.
    /// Abandoning means the underlying call keeps running to completion with its effects intact —
    /// a deadline here is not a rollback.
    /// The fault observer is not optional: measured, an abandoned task that faults afterwards
    /// raises <c>TaskScheduler.UnobservedTaskException</c>, and reading <c>Exception</c> marks it
    /// handled. When the token cannot fire at all, the task is returned untouched (no allocation,
    /// no behaviour change).
    /// </remarks>
    let private awaitUnderDeadline<'T> (ct: CancellationToken) (inflight: Task<'T>) : ValueTask<'T> =
        if not ct.CanBeCanceled then
            ValueTask<'T>(inflight)
        else
            inflight.ContinueWith(
                Action<Task<'T>>(fun completed -> completed.Exception |> ignore),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted ||| TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
            |> ignore

            ValueTask<'T>(inflight.WaitAsync(ct))

    /// <summary>
    /// Builds a pipeline from options and executes the supplied grain-call function,
    /// returning its result.
    /// </summary>
    /// <remarks>
    /// Each attempt re-invokes <paramref name="f"/>, so <c>MaxRetryAttempts = 2</c> calls it at
    /// most three times. A configured <c>Timeout</c> is honoured: when the deadline passes the
    /// caller gets a <see cref="TimeoutRejectedException"/> and the in-flight call is abandoned —
    /// abandoned, not cancelled, since <paramref name="f"/> takes no token. Use
    /// <c>executeCancellable</c> when the operation can honour one.
    /// A fresh pipeline is built per call, so a circuit breaker configured in
    /// <paramref name="options"/> holds no state across calls; see
    /// <see cref="ResilienceOptions.CircuitBreakerThreshold"/>.
    /// </remarks>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="options">Resilience options to apply.</param>
    /// <param name="f">The grain call to protect — a function returning <c>Task&lt;T&gt;</c>.</param>
    /// <returns>A <c>Task&lt;T&gt;</c> that completes with the grain call result.</returns>
    let execute<'T> (options: ResilienceOptions) (f: unit -> Task<'T>) : Task<'T> =
        let pipeline = buildPipeline<'T> options

        pipeline
            .ExecuteAsync(fun (ct: CancellationToken) -> awaitUnderDeadline<'T> ct (f ()))
            .AsTask()

    /// <summary>
    /// Same as <c>execute</c>, but hands the protected operation the pipeline's cancellation
    /// token so a genuinely cancellable call is cancelled at the deadline instead of abandoned.
    /// </summary>
    /// <remarks>
    /// The token is cancelled when the configured <c>Timeout</c> elapses; an
    /// <c>OperationCanceledException</c> raised in response is reported to the caller as
    /// <see cref="TimeoutRejectedException"/>. The deadline is enforced either way — an operation
    /// that ignores the token is still abandoned, exactly as under <c>execute</c>.
    /// To honour a caller's own token as well, link it inside <paramref name="f"/> with
    /// <c>CancellationTokenSource.CreateLinkedTokenSource</c>; a cancel through that path surfaces
    /// as <c>OperationCanceledException</c>, not as a timeout.
    /// </remarks>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="options">Resilience options to apply.</param>
    /// <param name="f">The grain call to protect, taking the pipeline's cancellation token.</param>
    /// <returns>A <c>Task&lt;T&gt;</c> that completes with the grain call result.</returns>
    let executeCancellable<'T> (options: ResilienceOptions) (f: CancellationToken -> Task<'T>) : Task<'T> =
        let pipeline = buildPipeline<'T> options

        pipeline
            .ExecuteAsync(fun (ct: CancellationToken) -> awaitUnderDeadline<'T> ct (f ct))
            .AsTask()

    /// <summary>
    /// Shorthand: retries <paramref name="maxAttempts"/> times with <paramref name="delay"/>
    /// between attempts. No circuit breaker or timeout.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="maxAttempts">Maximum number of retry attempts (not counting the initial try).</param>
    /// <param name="delay">Delay between retry attempts.</param>
    /// <param name="f">The grain call to protect.</param>
    /// <returns>A <c>Task&lt;T&gt;</c> that completes with the grain call result.</returns>
    let retry<'T> (maxAttempts: int) (delay: TimeSpan) (f: unit -> Task<'T>) : Task<'T> =
        execute<'T>
            { defaultOptions with
                MaxRetryAttempts = maxAttempts
                RetryDelay = delay }
            f

    /// <summary>
    /// Shorthand: enforces a deadline of <paramref name="timeout"/> on a single call.
    /// No retries or circuit breaker.
    /// Throws <see cref="TimeoutRejectedException"/> when the deadline is exceeded.
    /// </summary>
    /// <remarks>
    /// The deadline always fires for the caller. It does not cancel the call: <paramref name="f"/>
    /// takes no token, so the in-flight operation is abandoned and keeps running, effects and all.
    /// Use <c>withTimeoutCancellable</c> when the operation can honour a token.
    /// </remarks>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="timeout">The maximum allowed duration for <paramref name="f"/>.</param>
    /// <param name="f">The grain call to protect.</param>
    /// <returns>A <c>Task&lt;T&gt;</c> that completes with the grain call result.</returns>
    let withTimeout<'T> (timeout: TimeSpan) (f: unit -> Task<'T>) : Task<'T> =
        execute<'T>
            { defaultOptions with
                MaxRetryAttempts = 0
                Timeout = Some timeout }
            f

    /// <summary>
    /// Shorthand: enforces a deadline of <paramref name="timeout"/> on a single cancellable call,
    /// handing the operation the token that is cancelled when the deadline passes.
    /// No retries or circuit breaker.
    /// Throws <see cref="TimeoutRejectedException"/> when the deadline is exceeded.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="timeout">The maximum allowed duration for <paramref name="f"/>.</param>
    /// <param name="f">The grain call to protect, taking the deadline's cancellation token.</param>
    /// <returns>A <c>Task&lt;T&gt;</c> that completes with the grain call result.</returns>
    let withTimeoutCancellable<'T> (timeout: TimeSpan) (f: CancellationToken -> Task<'T>) : Task<'T> =
        executeCancellable<'T>
            { defaultOptions with
                MaxRetryAttempts = 0
                Timeout = Some timeout }
            f

    /// <summary>
    /// Creates a reusable <see cref="ResiliencePipeline"/> (non-generic) backed solely by a
    /// circuit breaker. Because the returned pipeline object is shared you get shared circuit
    /// state across every call that uses it — which is the intended usage pattern.
    /// Throws <see cref="BrokenCircuitException"/> when the circuit is open.
    /// </summary>
    /// <param name="threshold">
    /// Minimum number of failures within the sampling window before the circuit opens.
    /// </param>
    /// <param name="breakDuration">How long the circuit stays open before becoming half-open.</param>
    /// <returns>A reusable <see cref="ResiliencePipeline"/> with circuit-breaker state.</returns>
    let circuitBreaker (threshold: int) (breakDuration: TimeSpan) : ResiliencePipeline =
        ResiliencePipelineBuilder()
            .AddCircuitBreaker(
                CircuitBreakerStrategyOptions(
                    FailureRatio = 1.0,
                    MinimumThroughput = threshold,
                    SamplingDuration = TimeSpan.FromSeconds(float threshold * 2.0),
                    BreakDuration = breakDuration
                )
            )
            .Build()
