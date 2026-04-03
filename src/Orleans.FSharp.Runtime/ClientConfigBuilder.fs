namespace Orleans.FSharp.Runtime

open System
open System.Net
open System.Security.Cryptography.X509Certificates
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Configuration
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
        /// <summary>TLS configuration for securing client communication, or None if not configured.</summary>
        TlsConfig: TlsConfig option
        /// <summary>The cluster identifier to connect to, or None if not set.</summary>
        ClusterId: string option
        /// <summary>The unique service identifier for the target Orleans deployment, or None if not set.</summary>
        ServiceId: string option
        /// <summary>The gateway list refresh period, or None if not set.</summary>
        GatewayListRefreshPeriod: TimeSpan option
        /// <summary>The preferred gateway index for client connections, or None if not set.</summary>
        PreferredGatewayIndex: int option
        /// <summary>Whether to register FSharp.SystemTextJson as a fallback serializer for types without [GenerateSerializer].</summary>
        UseJsonFallbackSerialization: bool
        /// <summary>Whether to register FSharpBinaryCodec as a binary serializer for F# types without [GenerateSerializer] or [Id] attributes.</summary>
        UseFSharpBinarySerialization: bool
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
            TlsConfig = None
            ClusterId = None
            ServiceId = None
            GatewayListRefreshPeriod = None
            PreferredGatewayIndex = None
            UseJsonFallbackSerialization = false
            UseFSharpBinarySerialization = false
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
    /// <summary>
    /// Invokes an extension method on IClientBuilder by name, searching loaded assemblies.
    /// Throws InvalidOperationException with a helpful message if the method or assembly is not found.
    /// </summary>
    let private invokeClientExtensionMethod (methodName: string) (args: obj array) (packageHint: string) (clientBuilder: IClientBuilder) =
        let clientBuilderType = typeof<IClientBuilder>

        let extensionMethod =
            AppDomain.CurrentDomain.GetAssemblies()
            |> Array.collect (fun asm ->
                try asm.GetTypes() with _ -> Array.empty)
            |> Array.collect (fun t ->
                if t.IsAbstract && t.IsSealed then // static classes
                    t.GetMethods(Reflection.BindingFlags.Static ||| Reflection.BindingFlags.Public)
                    |> Array.filter (fun m ->
                        m.Name = methodName
                        && m.GetParameters().Length = args.Length + 1
                        && clientBuilderType.IsAssignableFrom(m.GetParameters().[0].ParameterType))
                else
                    Array.empty)
            |> Array.tryHead

        match extensionMethod with
        | Some m ->
            m.Invoke(null, Array.append [| box clientBuilder |] args) |> ignore
        | None ->
            invalidOp
                $"Extension method '{methodName}' not found. Install the NuGet package '{packageHint}' and ensure it is referenced in your project."

    let applyToBuilder (config: ClientConfig) (clientBuilder: IClientBuilder) : unit =
        // Apply cluster identity (ClusterId, ServiceId)
        if config.ClusterId.IsSome || config.ServiceId.IsSome then
            clientBuilder.Services.Configure<ClusterOptions>(fun (options: ClusterOptions) ->
                config.ClusterId |> Option.iter (fun id -> options.ClusterId <- id)
                config.ServiceId |> Option.iter (fun id -> options.ServiceId <- id))
            |> ignore

        // Apply gateway config
        match config.GatewayListRefreshPeriod with
        | Some period ->
            clientBuilder.Services.Configure<GatewayOptions>(fun (options: GatewayOptions) ->
                options.GatewayListRefreshPeriod <- period)
            |> ignore
        | None -> ()

        match config.PreferredGatewayIndex with
        | Some idx ->
            clientBuilder.Services.Configure<GatewayOptions>(fun (options: GatewayOptions) ->
                options.PreferredGatewayIndex <- idx)
            |> ignore
        | None -> ()

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

        // Apply JSON fallback serialization (FSharp.SystemTextJson as fallback for unattributed types)
        if config.UseJsonFallbackSerialization then
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer(
                        serializerBuilder,
                        isSupported = System.Func<System.Type, bool>(fun _ -> true),
                        jsonSerializerOptions = Orleans.FSharp.FSharpJson.serializerOptions)
                    |> ignore))
            |> ignore

        // Apply F# binary serialization (FSharpBinaryCodec for DU/record/option/list/map without attributes)
        if config.UseFSharpBinarySerialization then
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.FSharp.FSharpBinaryCodecRegistration.addToSerializerBuilder serializerBuilder |> ignore))
            |> ignore

        // Apply TLS configuration
        match config.TlsConfig with
        | Some(TlsSubject subject) ->
            invokeClientExtensionMethod "UseTls" [| box subject |] "Microsoft.Orleans.Connections.Security" clientBuilder
        | Some(TlsCertificate cert) ->
            invokeClientExtensionMethod "UseTls" [| box cert |] "Microsoft.Orleans.Connections.Security" clientBuilder
        | Some(MutualTlsSubject subject) ->
            invokeClientExtensionMethod "UseTls" [| box subject |] "Microsoft.Orleans.Connections.Security" clientBuilder
        | Some(MutualTlsCertificate cert) ->
            invokeClientExtensionMethod "UseTls" [| box cert |] "Microsoft.Orleans.Connections.Security" clientBuilder
        | None -> ()

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
    /// Registers FSharp.SystemTextJson as a fallback JSON serializer for Orleans.
    /// Types without [GenerateSerializer] will be serialized using System.Text.Json
    /// with FSharp.SystemTextJson converters (DU, Record, Option, etc.).
    /// This enables "clean" F# types (no Orleans attributes) to pass through grain boundaries.
    /// Requires the Microsoft.Orleans.Serialization.SystemTextJson NuGet package (included in Orleans.FSharp.Runtime).
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <returns>The updated client configuration with JSON fallback serialization enabled.</returns>
    [<CustomOperation("useJsonFallbackSerialization")>]
    member _.UseJsonFallbackSerialization(config: ClientConfig) =
        { config with UseJsonFallbackSerialization = true }

    /// <summary>
    /// Registers FSharpBinaryCodec as a binary serializer for F# types.
    /// Types without [GenerateSerializer] or [Id] attributes will be serialized using
    /// a compact binary format via FSharp.Reflection. Supports DUs, records, options,
    /// lists, maps, sets, arrays, and tuples.
    /// This eliminates the need for the C# CodeGen project entirely.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <returns>The updated client configuration with F# binary serialization enabled.</returns>
    [<CustomOperation("useFSharpBinarySerialization")>]
    member _.UseFSharpBinarySerialization(config: ClientConfig) =
        { config with UseFSharpBinarySerialization = true }

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
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Stream provider name cannot be empty or whitespace"
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

    /// <summary>
    /// Configures TLS for the client using a certificate subject name from the certificate store.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.
    /// WARNING: In production, always use valid certificates from a trusted CA.
    /// Do not disable certificate validation in production environments.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="subject">The certificate subject name to look up in the certificate store.</param>
    /// <returns>The updated client configuration with TLS enabled.</returns>
    [<CustomOperation("useTls")>]
    member _.UseTls(config: ClientConfig, subject: string) =
        { config with TlsConfig = Some(TlsSubject subject) }

    /// <summary>
    /// Configures TLS for the client using an X509Certificate2 instance.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.
    /// WARNING: In production, always use valid certificates from a trusted CA.
    /// Do not disable certificate validation in production environments.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="certificate">The X509Certificate2 to use for TLS.</param>
    /// <returns>The updated client configuration with TLS enabled.</returns>
    [<CustomOperation("useTlsWithCertificate")>]
    member _.UseTlsWithCertificate(config: ClientConfig, certificate: X509Certificate2) =
        { config with
            TlsConfig = Some(TlsCertificate certificate)
        }

    /// <summary>
    /// Configures mutual TLS (mTLS) for the client using a certificate subject name from the certificate store.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.
    /// WARNING: In production, always use valid certificates from a trusted CA.
    /// Do not disable certificate validation in production environments.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="subject">The certificate subject name to look up in the certificate store.</param>
    /// <returns>The updated client configuration with mutual TLS enabled.</returns>
    [<CustomOperation("useMutualTls")>]
    member _.UseMutualTls(config: ClientConfig, subject: string) =
        { config with
            TlsConfig = Some(MutualTlsSubject subject)
        }

    /// <summary>
    /// Sets the cluster identifier to connect to.
    /// Maps to Orleans ClusterOptions.ClusterId.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="id">The cluster identifier string.</param>
    /// <returns>The updated client configuration with the cluster ID set.</returns>
    [<CustomOperation("clusterId")>]
    member _.ClusterId(config: ClientConfig, id: string) =
        if System.String.IsNullOrWhiteSpace(id) then
            invalidArg (nameof id) "Cluster ID cannot be empty or whitespace"
        { config with ClusterId = Some id }

    /// <summary>
    /// Sets the unique service identifier for the target Orleans deployment.
    /// Maps to Orleans ClusterOptions.ServiceId.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="id">The service identifier string.</param>
    /// <returns>The updated client configuration with the service ID set.</returns>
    [<CustomOperation("serviceId")>]
    member _.ServiceId(config: ClientConfig, id: string) =
        if System.String.IsNullOrWhiteSpace(id) then
            invalidArg (nameof id) "Service ID cannot be empty or whitespace"
        { config with ServiceId = Some id }

    /// <summary>
    /// Sets how often the client refreshes its list of available gateways.
    /// Maps to Orleans GatewayOptions.GatewayListRefreshPeriod.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="period">The refresh period as a TimeSpan.</param>
    /// <returns>The updated client configuration with the refresh period set.</returns>
    [<CustomOperation("gatewayListRefreshPeriod")>]
    member _.GatewayListRefreshPeriod(config: ClientConfig, period: TimeSpan) =
        { config with GatewayListRefreshPeriod = Some period }

    /// <summary>
    /// Sets the preferred gateway index for client connections.
    /// Maps to Orleans GatewayOptions.PreferredGatewayIndex.
    /// </summary>
    /// <param name="config">The current client configuration being built.</param>
    /// <param name="index">The zero-based preferred gateway index.</param>
    /// <returns>The updated client configuration with the preferred gateway index set.</returns>
    [<CustomOperation("preferredGatewayIndex")>]
    member _.PreferredGatewayIndex(config: ClientConfig, index: int) =
        { config with PreferredGatewayIndex = Some index }

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
    /// addMemoryStreams, configureServices, useTls, useTlsWithCertificate, useMutualTls.
    /// </summary>
    let clientConfig = ClientConfigBuilder()
