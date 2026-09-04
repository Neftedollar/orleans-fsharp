namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

/// <summary>
/// One shutdown callback in registration order.
/// </summary>
[<Sealed>]
type internal ShutdownHandlerRegistration(handler: CancellationToken -> Task<unit>) =
    member _.Handler = handler

/// <summary>
/// Internal hosted service which invokes every registered shutdown handler from
/// <see cref="M:Microsoft.Extensions.Hosting.IHostedService.StopAsync"/>.
/// </summary>
/// <remarks>
/// The token supplied to <c>StopAsync</c> is the Generic Host's shutdown-timeout token: it is
/// usable when graceful shutdown starts and is cancelled only when the configured drain budget
/// expires. A <c>BackgroundService.ExecuteAsync</c> stopping token is already cancelled by the
/// time shutdown begins, which is why handlers must run here instead.
/// </remarks>
[<Sealed>]
type internal ShutdownHandlerService(registrations: Collections.Generic.IEnumerable<ShutdownHandlerRegistration>) =

    interface IHostedService with
        member _.StartAsync(_cancellationToken: CancellationToken) = Task.CompletedTask

        member _.StopAsync(shutdownTimeoutToken: CancellationToken) : Task =
            task {
                let failures = ResizeArray<exn>()

                for registration in registrations do
                    shutdownTimeoutToken.ThrowIfCancellationRequested()

                    try
                        // A callback is expected to observe the token, but a third-party callback
                        // can ignore it. WaitAsync makes the host's shutdown budget authoritative
                        // without pretending that the underlying callback itself was cancelled.
                        do!
                            (registration.Handler shutdownTimeoutToken)
                                .WaitAsync(shutdownTimeoutToken)
                    with
                    | :? OperationCanceledException when shutdownTimeoutToken.IsCancellationRequested ->
                        // The host's drain budget is exhausted. Do not start more work after the
                        // deadline, and preserve cancellation as the terminal condition.
                        shutdownTimeoutToken.ThrowIfCancellationRequested()
                    | error -> failures.Add error

                shutdownTimeoutToken.ThrowIfCancellationRequested()

                if failures.Count > 0 then
                    raise (AggregateException("One or more shutdown handlers failed.", failures))
            }
            :> Task

/// <summary>
/// Functions for configuring graceful shutdown behavior of Orleans silos.
/// Provides helpers for drain timeouts, shutdown handlers, and silo stop operations.
/// </summary>
[<RequireQualifiedAccess>]
module Shutdown =

    /// <summary>
    /// Configures the host shutdown timeout (drain period) during which the silo
    /// finishes processing in-flight requests before stopping.
    /// </summary>
    /// <param name="drainTimeout">The maximum time to wait for in-flight requests to complete.</param>
    /// <param name="builder">The host builder to configure.</param>
    /// <returns>The configured host builder.</returns>
    let configureGracefulShutdown (drainTimeout: TimeSpan) (builder: IHostBuilder) : IHostBuilder =
        builder.ConfigureServices(fun services ->
            services.Configure<HostOptions>(fun (options: HostOptions) ->
                options.ShutdownTimeout <- drainTimeout)
            |> ignore)

    /// <summary>
    /// Stops the host gracefully, allowing in-flight requests to drain
    /// within the configured shutdown timeout.
    /// </summary>
    /// <param name="host">The running host to stop.</param>
    /// <returns>A Task that completes when the host has stopped.</returns>
    let stopHost (host: IHost) : Task<unit> =
        task { do! host.StopAsync() }

    /// <summary>
    /// Registers a shutdown handler that runs when the host receives a shutdown signal.
    /// The handler receives a CancellationToken that is triggered when the drain timeout expires.
    /// Multiple handlers can be registered; they run in registration order. Ordinary failures are
    /// collected so later handlers still run, then reported together after the sequence completes.
    /// Expiry of the host shutdown timeout stops the sequence immediately.
    /// </summary>
    /// <param name="handler">The async function to run on shutdown.</param>
    /// <param name="builder">The host builder to configure.</param>
    /// <returns>The configured host builder.</returns>
    let onShutdown (handler: CancellationToken -> Task<unit>) (builder: IHostBuilder) : IHostBuilder =
        ArgumentNullException.ThrowIfNull(handler)
        ArgumentNullException.ThrowIfNull(builder)

        builder.ConfigureServices(fun services ->
            services.AddSingleton(ShutdownHandlerRegistration(handler)) |> ignore
            services.AddHostedService<ShutdownHandlerService>() |> ignore)
