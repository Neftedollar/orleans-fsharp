namespace Orleans.FSharp

open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting

/// <summary>
/// Module providing interactive scripting support for F# scripts (.fsx).
/// Enables quick prototyping of Orleans grains without setting up a full project.
/// Start an in-process silo with <c>Scripting.startOnPorts</c>, get grain references,
/// and shut down when done.
/// </summary>
[<RequireQualifiedAccess>]
module Scripting =

    /// <summary>
    /// A handle to a running interactive silo.
    /// Provides access to the host, cluster client, and grain factory.
    /// </summary>
    type SiloHandle =
        {
            /// <summary>The running host instance.</summary>
            Host: IHost
            /// <summary>The cluster client for grain communication.</summary>
            Client: IClusterClient
            /// <summary>The grain factory for creating grain references.</summary>
            GrainFactory: IGrainFactory
        }

    /// <summary>
    /// Starts a silo on specific ports. Useful when running multiple silos
    /// in the same process (e.g., integration tests alongside the main cluster).
    /// </summary>
    /// <param name="siloPort">The silo-to-silo communication port.</param>
    /// <param name="gatewayPort">The client-to-silo gateway port.</param>
    /// <returns>A Task containing a SiloHandle for interacting with the silo.</returns>
    let startOnPorts (siloPort: int) (gatewayPort: int) : Task<SiloHandle> =
        task {
            let uniqueId = System.Guid.NewGuid().ToString("N").[..7]

            let host =
                Host
                    .CreateDefaultBuilder()
                    .ConfigureLogging(fun logging -> logging.SetMinimumLevel(LogLevel.Warning) |> ignore)
                    .UseOrleans(fun (siloBuilder: ISiloBuilder) ->
                        siloBuilder
                            .UseLocalhostClustering(
                                siloPort,
                                gatewayPort,
                                serviceId = $"fsx-{uniqueId}",
                                clusterId = $"fsx-{uniqueId}"
                            )
                            .AddMemoryGrainStorageAsDefault()
                            .AddMemoryGrainStorage("Default")
                            .AddMemoryGrainStorage("PubSubStore")
                            .AddMemoryStreams("StreamProvider")
                            .UseInMemoryReminderService()
                        |> ignore)
                    .Build()

            do! host.StartAsync()

            let client = host.Services.GetRequiredService<IClusterClient>()
            let grainFactory = host.Services.GetRequiredService<IGrainFactory>()

            return
                { Host = host
                  Client = client
                  GrainFactory = grainFactory }
        }

    /// <summary>
    /// Get a grain reference from the silo by integer key.
    /// </summary>
    /// <typeparam name="'T">The grain interface type. Must inherit from IGrainWithIntegerKey.</typeparam>
    /// <param name="handle">The silo handle returned from startOnPorts.</param>
    /// <param name="key">The integer key identifying the grain.</param>
    /// <returns>A typed grain reference.</returns>
    let getGrain<'T when 'T :> IGrainWithIntegerKey> (handle: SiloHandle) (key: int64) : 'T =
        handle.GrainFactory.GetGrain<'T>(key)

    /// <summary>
    /// Shutdown the silo and clean up resources.
    /// After calling this, the handle should not be used.
    /// </summary>
    /// <param name="handle">The silo handle to shut down.</param>
    /// <returns>A Task that completes when shutdown is finished.</returns>
    let shutdown (handle: SiloHandle) : Task<unit> =
        task { do! handle.Host.StopAsync() }
