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
        /// </summary>
        CircuitBreakerThreshold: int option

        /// <summary>
        /// Duration the circuit remains open before moving to the half-open state.
        /// Only used when <see cref="CircuitBreakerThreshold"/> is set.
        /// </summary>
        CircuitBreakerDuration: TimeSpan option

        /// <summary>Per-attempt timeout. Set to <c>None</c> to disable.</summary>
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
    /// Strategies are added in this order (outer → inner): timeout, circuit breaker, retry.
    /// </summary>
    /// <typeparam name="T">The result type returned by the grain call.</typeparam>
    /// <param name="options">Options that control retry, circuit-breaker, and timeout behaviour.</param>
    /// <returns>A configured <see cref="ResiliencePipeline{T}"/>.</returns>
    let buildPipeline<'T> (options: ResilienceOptions) : ResiliencePipeline<'T> =
        let builder = ResiliencePipelineBuilder<'T>()

        // Outermost: per-call timeout
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
    /// Builds a pipeline from options and executes the supplied grain-call function,
    /// returning its result.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="options">Resilience options to apply.</param>
    /// <param name="f">The grain call to protect — a function returning <c>Task&lt;T&gt;</c>.</param>
    /// <returns>A <c>Task&lt;T&gt;</c> that completes with the grain call result.</returns>
    let execute<'T> (options: ResilienceOptions) (f: unit -> Task<'T>) : Task<'T> =
        let pipeline = buildPipeline<'T> options

        pipeline
            .ExecuteAsync(fun (_ct: CancellationToken) -> ValueTask<'T>(f()))
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
    /// Shorthand: enforces a per-call <paramref name="timeout"/>.
    /// No retries or circuit breaker.
    /// Throws <see cref="TimeoutRejectedException"/> when the deadline is exceeded.
    /// </summary>
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
