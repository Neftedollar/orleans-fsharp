namespace Orleans.FSharp.Runtime

open System
open System.Net
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Hosting

/// <summary>
/// Specifies how the client discovers and connects to silos in the cluster.
/// </summary>
[<NoEquality; NoComparison>]
type ClientClusteringMode =
    /// <summary>Localhost clustering for local development (connects to a single local silo).</summary>
    | Localhost
    /// <summary>Static gateway clustering using one or more silo endpoints (e.g., "127.0.0.1:30000").</summary>
    | StaticGateway of endpoints: string list
    /// <summary>Custom clustering configuration via an IClientBuilder transformation.</summary>
    | Custom of (IClientBuilder -> IClientBuilder)

/// <summary>
/// Immutable record describing a complete Orleans client configuration.
/// Built using the <c>clientConfig { }</c> computation expression.
/// </summary>
[<NoEquality; NoComparison>]
type ClientConfig =
    {
        /// <summary>The clustering mode (localhost, static, custom, or None if not set).</summary>
        ClusteringMode: ClientClusteringMode option
        /// <summary>Named stream providers for client-side event streaming.</summary>
        StreamProviders: Map<string, StreamProvider>
        /// <summary>Custom service registrations to apply to the host's DI container.</summary>
        CustomServices: (IServiceCollection -> unit) list
    }

/// <summary>
/// Functions for working with ClientConfig values.
/// </summary>
[<RequireQualifiedAccess>]
module ClientConfig =

    /// <summary>
    /// The default (empty) client configuration with no providers configured.
    /// </summary>
    let Default: ClientConfig =
        {
            ClusteringMode = None
            StreamProviders = Map.empty
            CustomServices = []
        }

    /// <summary>
    /// Validates a ClientConfig and returns a list of error messages.
    /// An empty list indicates the configuration is valid.
    /// </summary>
    /// <param name="config">The client configuration to validate.</param>
    /// <returns>A list of validation error messages, empty if valid.</returns>
    let validate (config: ClientConfig) : string list =
        [
            if config.ClusteringMode.IsNone then
                "No clustering mode specified. Use 'useLocalhostClustering' or provide a custom clustering configuration."
        ]

    /// <summary>
    /// Applies the client configuration to an IClientBuilder.
    /// Used when configuring a client via UseOrleansClient.
    /// </summary>
    /// <param name="config">The client configuration to apply.</param>
    /// <param name="clientBuilder">The IClientBuilder to configure.</param>
    let applyToBuilder (config: ClientConfig) (clientBuilder: IClientBuilder) : unit =
        // Apply clustering
        match config.ClusteringMode with
        | Some Localhost -> clientBuilder.UseLocalhostClustering() |> ignore
        | Some(StaticGateway endpoints) ->
            let ipEndpoints =
                endpoints
                |> List.map (fun ep ->
                    let parts = ep.Split(':')

                    if parts.Length <> 2 then
                        invalidOp $"Invalid endpoint format '{ep}'. Expected 'host:port' (e.g., '127.0.0.1:30000')."

                    let ip = IPAddress.Parse(parts.[0])
                    let port = Int32.Parse(parts.[1])
                    IPEndPoint(ip, port))
                |> Array.ofList

            clientBuilder.UseStaticClustering(ipEndpoints) |> ignore
        | Some(Custom f) -> f clientBuilder |> ignore
        | None -> ()

        // Apply stream providers (client-side)
        config.StreamProviders
        |> Map.iter (fun name provider ->
            match provider with
            | MemoryStream ->
                clientBuilder.AddMemoryStreams(name) |> ignore
            | CustomStream f ->
                // For client-side, CustomStream wraps ISiloBuilder transforms; skip non-applicable ones
                ()
            | PersistentStream _ ->
                // Persistent streams are silo-side only; skip on client
                ())

    /// <summary>
    /// Applies the client configuration to a HostApplicationBuilder.
    /// Calls UseOrleansClient on the builder and applies all configured providers.
    /// Also applies custom service registrations.
    /// </summary>
    /// <param name="config">The client configuration to apply.</param>
    /// <param name="builder">The HostApplicationBuilder to configure.</param>
    let applyToHost (config: ClientConfig) (builder: HostApplicationBuilder) : unit =
        builder.UseOrleansClient(fun clientBuilder -> applyToBuilder config clientBuilder)
        |> ignore

        // Apply custom services
        config.CustomServices
        |> List.iter (fun f -> f builder.Services)

    /// <summary>
    /// Builds an IClusterClient from the configuration using a HostApplicationBuilder.
    /// Starts the host and returns the cluster client.
    /// Note: The caller is responsible for managing the host lifetime.
    /// </summary>
    /// <param name="config">The client configuration to build from.</param>
    /// <returns>A tuple of the built IHost and the IClusterClient.</returns>
    let build (config: ClientConfig) : IHost * IClusterClient =
        let builder = HostApplicationBuilder()
        applyToHost config builder
        let host = builder.Build()
        let client = host.Services.GetRequiredService<IClusterClient>()
        (host, client)

/// <summary>
/// Computation expression builder for declaratively configuring an Orleans client.
/// Use the <c>clientConfig { }</c> syntax with custom operations to build a ClientConfig.
/// </summary>
/// <example>
/// <code>
/// let config = clientConfig {
///     useLocalhostClustering
/// }
/// </code>
/// </example>
type ClientConfigBuilder() =

    /// <summary>Yields the initial empty client configuration.</summary>
    member _.Yield(_: unit) : ClientConfig = ClientConfig.Default

    /// <summary>Returns the default configuration when the CE body is empty.</summary>
    member _.Zero() : ClientConfig = ClientConfig.Default

    /// <summary>
    /// Configures the client to use localhost clustering for local development.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <returns>The updated client configuration with localhost clustering enabled.</returns>
    [<CustomOperation("useLocalhostClustering")>]
    member _.UseLocalhostClustering(config: ClientConfig) =
        { config with
            ClusteringMode = Some Localhost
        }

    /// <summary>
    /// Configures the client to use static gateway clustering with the given endpoints.
    /// Each endpoint should be in "host:port" format (e.g., "127.0.0.1:30000").
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="endpoints">A list of gateway endpoint strings.</param>
    /// <returns>The updated client configuration with static gateway clustering enabled.</returns>
    [<CustomOperation("useStaticClustering")>]
    member _.UseStaticClustering(config: ClientConfig, endpoints: string list) =
        { config with
            ClusteringMode = Some(StaticGateway endpoints)
        }

    /// <summary>
    /// Adds an in-memory stream provider with the given name.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="name">The name of the stream provider.</param>
    /// <returns>The updated client configuration with the memory stream provider added.</returns>
    [<CustomOperation("addMemoryStreams")>]
    member _.AddMemoryStreams(config: ClientConfig, name: string) =
        { config with
            StreamProviders = config.StreamProviders |> Map.add name MemoryStream
        }

    /// <summary>
    /// Registers a custom service configuration function.
    /// Multiple configureServices calls accumulate (they do not replace each other).
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="f">A function that registers services on an IServiceCollection.</param>
    /// <returns>The updated client configuration with the service registration added.</returns>
    [<CustomOperation("configureServices")>]
    member _.ConfigureServices(config: ClientConfig, f: IServiceCollection -> unit) =
        { config with
            CustomServices = config.CustomServices @ [ f ]
        }

    /// <summary>Returns the completed client configuration.</summary>
    member _.Run(config: ClientConfig) = config

/// <summary>
/// Module containing the clientConfig computation expression builder instance.
/// </summary>
[<AutoOpen>]
module ClientConfigBuilderInstance =
    /// <summary>
    /// Computation expression for declaratively configuring an Orleans client.
    /// Supports custom operations: useLocalhostClustering, useStaticClustering,
    /// addMemoryStreams, configureServices.
    /// </summary>
    let clientConfig = ClientConfigBuilder()
