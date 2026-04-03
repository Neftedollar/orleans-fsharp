namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

/// <summary>
/// Internal hosted service that invokes a shutdown handler when the host lifetime
/// signals application stopping.
/// </summary>
[<Sealed>]
type internal ShutdownHandlerService(handler: CancellationToken -> Task<unit>) =
    inherit BackgroundService()

    override _.ExecuteAsync(stoppingToken: CancellationToken) : Task =
        task {
            // Wait until the host signals stopping
            let tcs = TaskCompletionSource()

            use _reg =
                stoppingToken.Register(fun () -> tcs.TrySetResult() |> ignore)

            do! tcs.Task
            // Run the handler with the stopping token
            do! handler stoppingToken
        }

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
    /// Multiple handlers can be registered; they run in registration order.
    /// </summary>
    /// <param name="handler">The async function to run on shutdown.</param>
    /// <param name="builder">The host builder to configure.</param>
    /// <returns>The configured host builder.</returns>
    let onShutdown (handler: CancellationToken -> Task<unit>) (builder: IHostBuilder) : IHostBuilder =
        builder.ConfigureServices(fun services ->
            services.AddHostedService<ShutdownHandlerService>(fun _sp ->
                new ShutdownHandlerService(handler))
            |> ignore)
