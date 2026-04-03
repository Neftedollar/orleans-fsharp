namespace Orleans.FSharp.Runtime

open System
open System.Security.Cryptography.X509Certificates
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.FSharp.Versioning
open Orleans.Streams
open Serilog
open System.Threading
open Microsoft.Extensions.Diagnostics.HealthChecks

/// <summary>
/// Specifies TLS/mTLS configuration for securing Orleans silo communication.
/// </summary>
[<NoEquality; NoComparison>]
type TlsConfig =
    /// <summary>TLS using a certificate subject name from the certificate store.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.</summary>
    | TlsSubject of subject: string
    /// <summary>TLS using an X509Certificate2 instance.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.</summary>
    | TlsCertificate of certificate: X509Certificate2
    /// <summary>Mutual TLS using a certificate subject name from the certificate store.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.</summary>
    | MutualTlsSubject of subject: string
    /// <summary>Mutual TLS using an X509Certificate2 instance.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.</summary>
    | MutualTlsCertificate of certificate: X509Certificate2

/// <summary>
/// Configuration for the Orleans Dashboard.
/// </summary>
[<NoEquality; NoComparison>]
type DashboardConfig =
    /// <summary>Enable the dashboard with default options.
    /// Requires the Microsoft.Orleans.Dashboard NuGet package at runtime.</summary>
    | DashboardDefaults
    /// <summary>Enable the dashboard with a custom counter update interval in milliseconds.
    /// Requires the Microsoft.Orleans.Dashboard NuGet package at runtime.</summary>
    | DashboardWithOptions of counterUpdateIntervalMs: int * historyLength: int * hideTrace: bool

/// <summary>
/// Specifies how the silo discovers and communicates with other silos in the cluster.
/// </summary>
[<NoEquality; NoComparison>]
type ClusteringMode =
    /// <summary>Localhost clustering for local development (single silo).</summary>
    | Localhost
    /// <summary>Redis-based clustering using the given connection string.</summary>
    | RedisClustering of connectionString: string
    /// <summary>Azure Table-based clustering using the given connection string.</summary>
    | AzureTableClustering of connectionString: string
    /// <summary>ADO.NET-based clustering using the given connection string and provider invariant (e.g., "Npgsql", "System.Data.SqlClient").</summary>
    | AdoNetClustering of connectionString: string * invariant: string
    /// <summary>Custom clustering configuration via an ISiloBuilder transformation.</summary>
    | CustomClustering of (ISiloBuilder -> ISiloBuilder)

/// <summary>
/// Specifies the type of storage provider for grain state persistence.
/// </summary>
[<NoEquality; NoComparison>]
type StorageProvider =
    /// <summary>In-memory grain storage (data lost on silo restart).</summary>
    | Memory
    /// <summary>Redis-based grain storage using the given connection string.</summary>
    | RedisStorage of connectionString: string
    /// <summary>Azure Blob-based grain storage using the given connection string.</summary>
    | AzureBlobStorage of connectionString: string
    /// <summary>Azure Table-based grain storage using the given connection string.</summary>
    | AzureTableStorage of connectionString: string
    /// <summary>ADO.NET-based grain storage using the given connection string and provider invariant (e.g., "Npgsql", "System.Data.SqlClient").</summary>
    | AdoNetStorage of connectionString: string * invariant: string
    /// <summary>Azure Cosmos DB-based grain storage using the given account endpoint and database name.
    /// Requires the Microsoft.Orleans.Persistence.Cosmos NuGet package at runtime.</summary>
    | CosmosStorage of accountEndpoint: string * databaseName: string
    /// <summary>Amazon DynamoDB-based grain storage using the given AWS region.
    /// Requires the Microsoft.Orleans.Persistence.DynamoDB NuGet package at runtime.</summary>
    | DynamoDbStorage of region: string
    /// <summary>Custom storage provider configuration via an ISiloBuilder transformation.</summary>
    | CustomStorage of (ISiloBuilder -> ISiloBuilder)

/// <summary>
/// Specifies the type of stream provider for grain event streaming.
/// </summary>
[<NoEquality; NoComparison>]
type StreamProvider =
    /// <summary>In-memory stream provider (suitable for development and testing).</summary>
    | MemoryStream
    /// <summary>Persistent stream provider configured via an adapter factory and optional stream configurator.</summary>
    | PersistentStream of adapterFactory: Func<IServiceProvider, string, IQueueAdapterFactory> * configurator: Action<ISiloPersistentStreamConfigurator>
    /// <summary>Custom stream provider configuration via an ISiloBuilder transformation.</summary>
    | CustomStream of (ISiloBuilder -> ISiloBuilder)

/// <summary>
/// Specifies the type of reminder service for grain reminders.
/// </summary>
[<NoEquality; NoComparison>]
type ReminderProvider =
    /// <summary>In-memory reminder service (data lost on silo restart, suitable for development and testing).</summary>
    | MemoryReminder
    /// <summary>Redis-based reminder service using the given connection string.
    /// Requires the Microsoft.Orleans.Reminders.Redis NuGet package at runtime.</summary>
    | RedisReminder of connectionString: string
    /// <summary>Custom reminder service configuration via an ISiloBuilder transformation.</summary>
    | CustomReminder of (ISiloBuilder -> ISiloBuilder)

/// <summary>
/// Immutable record describing a complete silo configuration.
/// Built using the <c>siloConfig { }</c> computation expression.
/// </summary>
type SiloConfig =
    {
        /// <summary>The clustering mode (localhost, custom, or None if not set).</summary>
        ClusteringMode: ClusteringMode option
        /// <summary>Named storage providers for grain state persistence.</summary>
        StorageProviders: Map<string, StorageProvider>
        /// <summary>Named stream providers for grain event streaming.</summary>
        StreamProviders: Map<string, StreamProvider>
        /// <summary>The reminder service provider, or None if not configured.</summary>
        ReminderProvider: ReminderProvider option
        /// <summary>Whether to wire Serilog as the logging provider.</summary>
        UseSerilog: bool
        /// <summary>Custom service registrations to apply to the host's DI container.</summary>
        CustomServices: (IServiceCollection -> unit) list
        /// <summary>Named broadcast channel providers to register with the silo.</summary>
        BroadcastChannels: string list
        /// <summary>Grain versioning configuration (compatibility + selector strategy), or None if not configured.</summary>
        VersioningConfig: (CompatibilityStrategy * VersionSelectorStrategy) option
        /// <summary>Incoming grain call filters to register with the silo.</summary>
        IncomingFilters: IIncomingGrainCallFilter list
        /// <summary>Outgoing grain call filters to register with the silo.</summary>
        OutgoingFilters: IOutgoingGrainCallFilter list
        /// <summary>GrainService types to register with the silo. GrainServices run on every silo.</summary>
        GrainServiceTypes: Type list
        /// <summary>Startup tasks to run when the silo starts. Each task receives IServiceProvider and CancellationToken.</summary>
        StartupTasks: (IServiceProvider -> CancellationToken -> Tasks.Task) list
        /// <summary>Whether to register Orleans health checks with the DI container.</summary>
        EnableHealthChecks: bool
        /// <summary>TLS/mTLS configuration for securing silo communication, or None if not configured.</summary>
        TlsConfig: TlsConfig option
        /// <summary>Orleans Dashboard configuration, or None if not configured.</summary>
        DashboardConfig: DashboardConfig option
        /// <summary>The cluster identifier shared by all silos in the cluster, or None if not set.</summary>
        ClusterId: string option
        /// <summary>The unique service identifier for this Orleans deployment, or None if not set.</summary>
        ServiceId: string option
        /// <summary>A human-readable name for this silo instance, or None if not set.</summary>
        SiloName: string option
        /// <summary>The silo-to-silo communication port, or None if not set.</summary>
        SiloPort: int option
        /// <summary>The client-to-silo gateway port, or None if not set.</summary>
        GatewayPort: int option
        /// <summary>The IP address that this silo advertises to other silos, or None if not set.</summary>
        AdvertisedIpAddress: string option
        /// <summary>The global default grain collection age (idle timeout before deactivation), or None if not set.</summary>
        GrainCollectionAge: TimeSpan option
        /// <summary>Whether to register FSharp.SystemTextJson as a fallback serializer for types without [GenerateSerializer].</summary>
        UseJsonFallbackSerialization: bool
        /// <summary>Whether to register FSharpBinaryCodec as a binary serializer for F# types without [GenerateSerializer] or [Id] attributes.</summary>
        UseFSharpBinarySerialization: bool
    }

/// <summary>
/// Functions for working with SiloConfig values.
/// </summary>
[<RequireQualifiedAccess>]
module SiloConfig =

    /// <summary>
    /// The default (empty) silo configuration with no providers configured.
    /// </summary>
    let Default: SiloConfig =
        {
            ClusteringMode = None
            StorageProviders = Map.empty
            StreamProviders = Map.empty
            ReminderProvider = None
            UseSerilog = false
            CustomServices = []
            BroadcastChannels = []
            VersioningConfig = None
            IncomingFilters = []
            OutgoingFilters = []
            GrainServiceTypes = []
            StartupTasks = []
            EnableHealthChecks = false
            TlsConfig = None
            DashboardConfig = None
            ClusterId = None
            ServiceId = None
            SiloName = None
            SiloPort = None
            GatewayPort = None
            AdvertisedIpAddress = None
            GrainCollectionAge = None
            UseJsonFallbackSerialization = false
            UseFSharpBinarySerialization = false
        }

    /// <summary>
    /// Validates a SiloConfig and returns a list of error messages.
    /// An empty list indicates the configuration is valid.
    /// </summary>
    /// <param name="config">The silo configuration to validate.</param>
    /// <returns>A list of validation error messages, empty if valid.</returns>
    let validate (config: SiloConfig) : string list =
        [
            if config.ClusteringMode.IsNone then
                "No clustering mode specified. Use 'useLocalhostClustering' or provide a custom clustering configuration."
        ]

    /// <summary>
    /// Applies the silo configuration to an ISiloBuilder.
    /// Used when configuring a silo via TestClusterBuilder or UseOrleans.
    /// </summary>
    /// <param name="config">The silo configuration to apply.</param>
    /// <param name="siloBuilder">The ISiloBuilder to configure.</param>
    /// <summary>
    /// Invokes an extension method on ISiloBuilder by name, searching loaded assemblies.
    /// Throws InvalidOperationException with a helpful message if the method or assembly is not found.
    /// </summary>
    let private invokeExtensionMethod (methodName: string) (args: obj array) (packageHint: string) (siloBuilder: ISiloBuilder) =
        let siloBuilderType = typeof<ISiloBuilder>

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
                        && siloBuilderType.IsAssignableFrom(m.GetParameters().[0].ParameterType))
                else
                    Array.empty)
            |> Array.tryHead

        match extensionMethod with
        | Some m ->
            m.Invoke(null, Array.append [| box siloBuilder |] args) |> ignore
        | None ->
            invalidOp
                $"Extension method '{methodName}' not found. Install the NuGet package '{packageHint}' and ensure it is referenced in your project."

    let applyToSiloBuilder (config: SiloConfig) (siloBuilder: ISiloBuilder) : unit =
        // Apply clustering
        match config.ClusteringMode with
        | Some Localhost -> siloBuilder.UseLocalhostClustering() |> ignore
        | Some(RedisClustering connStr) ->
            invokeExtensionMethod "UseRedisClustering" [| box connStr |] "Microsoft.Orleans.Clustering.Redis" siloBuilder
        | Some(AzureTableClustering connStr) ->
            invokeExtensionMethod "UseAzureStorageClustering" [| box connStr |] "Microsoft.Orleans.Clustering.AzureStorage" siloBuilder
        | Some(AdoNetClustering(connStr, invariant)) ->
            invokeExtensionMethod "UseAdoNetClustering" [| box connStr; box invariant |] "Microsoft.Orleans.Clustering.AdoNet" siloBuilder
        | Some(CustomClustering f) -> f siloBuilder |> ignore
        | None -> ()

        // Apply cluster identity (ClusterId, ServiceId)
        if config.ClusterId.IsSome || config.ServiceId.IsSome then
            siloBuilder.Services.Configure<ClusterOptions>(fun (options: ClusterOptions) ->
                config.ClusterId |> Option.iter (fun id -> options.ClusterId <- id)
                config.ServiceId |> Option.iter (fun id -> options.ServiceId <- id))
            |> ignore

        // Apply silo name
        match config.SiloName with
        | Some name ->
            siloBuilder.Services.Configure<SiloOptions>(fun (options: SiloOptions) ->
                options.SiloName <- name)
            |> ignore
        | None -> ()

        // Apply endpoint options (SiloPort, GatewayPort, AdvertisedIPAddress)
        if config.SiloPort.IsSome || config.GatewayPort.IsSome || config.AdvertisedIpAddress.IsSome then
            siloBuilder.Services.Configure<EndpointOptions>(fun (options: EndpointOptions) ->
                config.SiloPort |> Option.iter (fun p -> options.SiloPort <- p)
                config.GatewayPort |> Option.iter (fun p -> options.GatewayPort <- p)
                config.AdvertisedIpAddress |> Option.iter (fun ip ->
                    options.AdvertisedIPAddress <- System.Net.IPAddress.Parse(ip)))
            |> ignore

        // Apply grain collection age
        match config.GrainCollectionAge with
        | Some age ->
            siloBuilder.Services.Configure<GrainCollectionOptions>(fun (options: GrainCollectionOptions) ->
                options.CollectionAge <- age)
            |> ignore
        | None -> ()

        // Apply storage providers
        config.StorageProviders
        |> Map.iter (fun name provider ->
            match provider with
            | Memory -> siloBuilder.AddMemoryGrainStorage(name) |> ignore
            | RedisStorage connStr ->
                invokeExtensionMethod "AddRedisGrainStorage" [| box name; box connStr |] "Microsoft.Orleans.Persistence.Redis" siloBuilder
            | AzureBlobStorage connStr ->
                invokeExtensionMethod "AddAzureBlobGrainStorage" [| box name; box connStr |] "Microsoft.Orleans.Persistence.AzureStorage" siloBuilder
            | AzureTableStorage connStr ->
                invokeExtensionMethod "AddAzureTableGrainStorage" [| box name; box connStr |] "Microsoft.Orleans.Persistence.AzureStorage" siloBuilder
            | AdoNetStorage(connStr, invariant) ->
                invokeExtensionMethod "AddAdoNetGrainStorage" [| box name; box connStr; box invariant |] "Microsoft.Orleans.Persistence.AdoNet" siloBuilder
            | CosmosStorage(accountEndpoint, databaseName) ->
                invokeExtensionMethod "AddCosmosGrainStorage" [| box name; box accountEndpoint; box databaseName |] "Microsoft.Orleans.Persistence.Cosmos" siloBuilder
            | DynamoDbStorage region ->
                invokeExtensionMethod "AddDynamoDBGrainStorage" [| box name; box region |] "Microsoft.Orleans.Persistence.DynamoDB" siloBuilder
            | CustomStorage f -> f siloBuilder |> ignore)

        // Apply reminder provider
        match config.ReminderProvider with
        | Some MemoryReminder -> siloBuilder.UseInMemoryReminderService() |> ignore
        | Some(RedisReminder connStr) ->
            invokeExtensionMethod "UseRedisReminderService" [| box connStr |] "Microsoft.Orleans.Reminders.Redis" siloBuilder
        | Some(CustomReminder f) -> f siloBuilder |> ignore
        | None -> ()

        // Apply stream providers
        let mutable needsPubSubStore = false

        config.StreamProviders
        |> Map.iter (fun name provider ->
            match provider with
            | MemoryStream ->
                siloBuilder.AddMemoryStreams(name) |> ignore
                needsPubSubStore <- true
            | PersistentStream(adapterFactory, configurator) ->
                siloBuilder.AddPersistentStreams(name, adapterFactory, configurator) |> ignore
                needsPubSubStore <- true
            | CustomStream f -> f siloBuilder |> ignore)

        // Memory streams require a PubSubStore grain storage.
        // Add one automatically if not already configured.
        if needsPubSubStore && not (config.StorageProviders |> Map.containsKey "PubSubStore") then
            siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore

        // Apply broadcast channels
        config.BroadcastChannels
        |> List.iter (fun name ->
            siloBuilder.AddBroadcastChannel(name) |> ignore)

        // Apply grain versioning
        match config.VersioningConfig with
        | Some(compat, selector) ->
            siloBuilder.Services.Configure<GrainVersioningOptions>(fun (options: GrainVersioningOptions) ->
                options.DefaultCompatibilityStrategy <- Versioning.compatibilityStrategyName compat
                options.DefaultVersionSelectorStrategy <- Versioning.versionSelectorStrategyName selector)
            |> ignore
        | None -> ()

        // Apply incoming grain call filters
        config.IncomingFilters
        |> List.iter (fun filter ->
            siloBuilder.AddIncomingGrainCallFilter(filter) |> ignore)

        // Apply outgoing grain call filters
        config.OutgoingFilters
        |> List.iter (fun filter ->
            siloBuilder.AddOutgoingGrainCallFilter(filter) |> ignore)

        // Apply grain service registrations
        config.GrainServiceTypes
        |> List.iter (fun serviceType ->
            GrainServicesSiloBuilderExtensions.AddGrainService(siloBuilder.Services, serviceType) |> ignore)

        // Apply TLS configuration
        match config.TlsConfig with
        | Some(TlsSubject subject) ->
            invokeExtensionMethod "UseTls" [| box subject |] "Microsoft.Orleans.Connections.Security" siloBuilder
        | Some(TlsCertificate cert) ->
            invokeExtensionMethod "UseTls" [| box cert |] "Microsoft.Orleans.Connections.Security" siloBuilder
        | Some(MutualTlsSubject subject) ->
            invokeExtensionMethod "UseTls" [| box subject |] "Microsoft.Orleans.Connections.Security" siloBuilder
        | Some(MutualTlsCertificate cert) ->
            invokeExtensionMethod "UseTls" [| box cert |] "Microsoft.Orleans.Connections.Security" siloBuilder
        | None -> ()

        // Apply Dashboard configuration
        match config.DashboardConfig with
        | Some DashboardDefaults ->
            invokeExtensionMethod "AddDashboard" [||] "Microsoft.Orleans.Dashboard" siloBuilder
        | Some(DashboardWithOptions(intervalMs, historyLen, hideTrace)) ->
            invokeExtensionMethod "AddDashboard" [||] "Microsoft.Orleans.Dashboard" siloBuilder
        | None -> ()

        // Apply JSON fallback serialization (FSharp.SystemTextJson as fallback for unattributed types)
        if config.UseJsonFallbackSerialization then
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                siloBuilder.Services,
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
                siloBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.FSharp.FSharpBinaryCodecRegistration.addToSerializerBuilder serializerBuilder |> ignore))
            |> ignore

        // Apply startup tasks
        config.StartupTasks
        |> List.iter (fun startupTask ->
            siloBuilder.AddStartupTask(fun sp ct -> startupTask sp ct) |> ignore)

    /// <summary>
    /// Applies the silo configuration to a HostApplicationBuilder.
    /// Calls UseOrleans on the builder and applies all configured providers.
    /// Also applies custom service registrations and Serilog if enabled.
    /// </summary>
    /// <param name="config">The silo configuration to apply.</param>
    /// <param name="builder">The HostApplicationBuilder to configure.</param>
    let applyToHost (config: SiloConfig) (builder: HostApplicationBuilder) : unit =
        builder.UseOrleans(fun siloBuilder -> applyToSiloBuilder config siloBuilder)
        |> ignore

        // Apply Serilog
        if config.UseSerilog then
            builder.Services.AddLogging(fun loggingBuilder ->
                loggingBuilder.AddSerilog() |> ignore)
            |> ignore

        // Apply health checks
        if config.EnableHealthChecks then
            builder.Services.AddHealthChecks() |> ignore

        // Apply custom services
        config.CustomServices
        |> List.iter (fun f -> f builder.Services)

/// <summary>
/// Computation expression builder for declaratively configuring an Orleans silo.
/// Use the <c>siloConfig { }</c> syntax with custom operations to build a SiloConfig.
/// </summary>
/// <example>
/// <code>
/// let config = siloConfig {
///     useLocalhostClustering
///     addMemoryStorage "Default"
///     addMemoryStreams "StreamProvider"
///     useSerilog
/// }
/// </code>
/// </example>
type SiloConfigBuilder() =

    /// <summary>Yields the initial empty silo configuration.</summary>
    member _.Yield(_: unit) : SiloConfig = SiloConfig.Default

    /// <summary>Returns the default configuration when the CE body is empty.</summary>
    member _.Zero() : SiloConfig = SiloConfig.Default

    /// <summary>
    /// Configures the silo to use localhost clustering for local development.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <returns>The updated silo configuration with localhost clustering enabled.</returns>
    [<CustomOperation("useLocalhostClustering")>]
    member _.UseLocalhostClustering(config: SiloConfig) =
        { config with
            ClusteringMode = Some Localhost
        }

    /// <summary>
    /// Adds an in-memory grain storage provider with the given name.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <returns>The updated silo configuration with the memory storage provider added.</returns>
    [<CustomOperation("addMemoryStorage")>]
    member _.AddMemoryStorage(config: SiloConfig, name: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name Memory
        }

    /// <summary>
    /// Adds a custom storage provider with the given name via an ISiloBuilder transformation.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <param name="configure">A function that configures the ISiloBuilder for this storage provider.</param>
    /// <returns>The updated silo configuration with the custom storage provider added.</returns>
    [<CustomOperation("addCustomStorage")>]
    member _.AddCustomStorage(config: SiloConfig, name: string, configure: ISiloBuilder -> ISiloBuilder) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name (CustomStorage configure)
        }

    /// <summary>
    /// Adds a Redis grain storage provider with the given name and connection string.
    /// Requires the Microsoft.Orleans.Persistence.Redis NuGet package at runtime.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <param name="connectionString">The Redis connection string (e.g., "localhost:6379").</param>
    /// <returns>The updated silo configuration with the Redis storage provider added.</returns>
    [<CustomOperation("addRedisStorage")>]
    member _.AddRedisStorage(config: SiloConfig, name: string, connectionString: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name (RedisStorage connectionString)
        }

    /// <summary>
    /// Configures the silo to use Redis-based clustering.
    /// Requires the Microsoft.Orleans.Clustering.Redis NuGet package at runtime.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="connectionString">The Redis connection string (e.g., "localhost:6379").</param>
    /// <returns>The updated silo configuration with Redis clustering enabled.</returns>
    [<CustomOperation("addRedisClustering")>]
    member _.AddRedisClustering(config: SiloConfig, connectionString: string) =
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        { config with
            ClusteringMode = Some(RedisClustering connectionString)
        }

    /// <summary>
    /// Adds an Azure Blob grain storage provider with the given name and connection string.
    /// Requires the Microsoft.Orleans.Persistence.AzureStorage NuGet package at runtime.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <param name="connectionString">The Azure Storage connection string.</param>
    /// <returns>The updated silo configuration with the Azure Blob storage provider added.</returns>
    [<CustomOperation("addAzureBlobStorage")>]
    member _.AddAzureBlobStorage(config: SiloConfig, name: string, connectionString: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name (AzureBlobStorage connectionString)
        }

    /// <summary>
    /// Adds an Azure Table grain storage provider with the given name and connection string.
    /// Requires the Microsoft.Orleans.Persistence.AzureStorage NuGet package at runtime.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <param name="connectionString">The Azure Storage connection string.</param>
    /// <returns>The updated silo configuration with the Azure Table storage provider added.</returns>
    [<CustomOperation("addAzureTableStorage")>]
    member _.AddAzureTableStorage(config: SiloConfig, name: string, connectionString: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name (AzureTableStorage connectionString)
        }

    /// <summary>
    /// Configures the silo to use Azure Table-based clustering.
    /// Requires the Microsoft.Orleans.Clustering.AzureStorage NuGet package at runtime.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="connectionString">The Azure Storage connection string.</param>
    /// <returns>The updated silo configuration with Azure Table clustering enabled.</returns>
    [<CustomOperation("addAzureTableClustering")>]
    member _.AddAzureTableClustering(config: SiloConfig, connectionString: string) =
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        { config with
            ClusteringMode = Some(AzureTableClustering connectionString)
        }

    /// <summary>
    /// Adds an ADO.NET grain storage provider with the given name, connection string, and provider invariant.
    /// Requires the Microsoft.Orleans.Persistence.AdoNet NuGet package at runtime.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <param name="connectionString">The ADO.NET connection string.</param>
    /// <param name="invariant">The ADO.NET provider invariant (e.g., "Npgsql", "System.Data.SqlClient").</param>
    /// <returns>The updated silo configuration with the ADO.NET storage provider added.</returns>
    [<CustomOperation("addAdoNetStorage")>]
    member _.AddAdoNetStorage(config: SiloConfig, name: string, connectionString: string, invariant: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(invariant) then
            invalidArg (nameof invariant) "Provider invariant cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name (AdoNetStorage(connectionString, invariant))
        }

    /// <summary>
    /// Configures the silo to use ADO.NET-based clustering.
    /// Requires the Microsoft.Orleans.Clustering.AdoNet NuGet package at runtime.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="connectionString">The ADO.NET connection string.</param>
    /// <param name="invariant">The ADO.NET provider invariant (e.g., "Npgsql", "System.Data.SqlClient").</param>
    /// <returns>The updated silo configuration with ADO.NET clustering enabled.</returns>
    [<CustomOperation("addAdoNetClustering")>]
    member _.AddAdoNetClustering(config: SiloConfig, connectionString: string, invariant: string) =
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(invariant) then
            invalidArg (nameof invariant) "Provider invariant cannot be empty or whitespace"
        { config with
            ClusteringMode = Some(AdoNetClustering(connectionString, invariant))
        }

    /// <summary>
    /// Adds an in-memory stream provider with the given name.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the stream provider.</param>
    /// <returns>The updated silo configuration with the memory stream provider added.</returns>
    [<CustomOperation("addMemoryStreams")>]
    member _.AddMemoryStreams(config: SiloConfig, name: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Stream provider name cannot be empty or whitespace"
        { config with
            StreamProviders = config.StreamProviders |> Map.add name MemoryStream
        }

    /// <summary>
    /// Configures the silo to use an in-memory reminder service.
    /// Suitable for development and testing. Reminders will not persist across silo restarts.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <returns>The updated silo configuration with the in-memory reminder service configured.</returns>
    [<CustomOperation("addMemoryReminderService")>]
    member _.AddMemoryReminderService(config: SiloConfig) =
        { config with
            ReminderProvider = Some MemoryReminder
        }

    /// <summary>
    /// Configures the silo to use a custom reminder service via an ISiloBuilder transformation.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="configure">A function that configures the ISiloBuilder for the reminder service.</param>
    /// <returns>The updated silo configuration with the custom reminder service configured.</returns>
    [<CustomOperation("addCustomReminderService")>]
    member _.AddCustomReminderService(config: SiloConfig, configure: ISiloBuilder -> ISiloBuilder) =
        { config with
            ReminderProvider = Some(CustomReminder configure)
        }

    /// <summary>
    /// Enables Serilog as the logging provider for the silo.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <returns>The updated silo configuration with Serilog enabled.</returns>
    [<CustomOperation("useSerilog")>]
    member _.UseSerilog(config: SiloConfig) = { config with UseSerilog = true }

    /// <summary>
    /// Registers FSharp.SystemTextJson as a fallback JSON serializer for Orleans.
    /// Types without [GenerateSerializer] will be serialized using System.Text.Json
    /// with FSharp.SystemTextJson converters (DU, Record, Option, etc.).
    /// This enables "clean" F# types (no Orleans attributes) to pass through grain boundaries.
    /// Requires the Microsoft.Orleans.Serialization.SystemTextJson NuGet package (included in Orleans.FSharp.Runtime).
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <returns>The updated silo configuration with JSON fallback serialization enabled.</returns>
    [<CustomOperation("useJsonFallbackSerialization")>]
    member _.UseJsonFallbackSerialization(config: SiloConfig) =
        { config with UseJsonFallbackSerialization = true }

    /// <summary>
    /// Registers FSharpBinaryCodec as a binary serializer for F# types.
    /// Types without [GenerateSerializer] or [Id] attributes will be serialized using
    /// a compact binary format via FSharp.Reflection. Supports DUs, records, options,
    /// lists, maps, sets, arrays, and tuples.
    /// This eliminates the need for the C# CodeGen project entirely.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <returns>The updated silo configuration with F# binary serialization enabled.</returns>
    [<CustomOperation("useFSharpBinarySerialization")>]
    member _.UseFSharpBinarySerialization(config: SiloConfig) =
        { config with UseFSharpBinarySerialization = true }

    /// <summary>
    /// Registers a custom service configuration function.
    /// Multiple configureServices calls accumulate (they do not replace each other).
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="f">A function that registers services on an IServiceCollection.</param>
    /// <returns>The updated silo configuration with the service registration added.</returns>
    [<CustomOperation("configureServices")>]
    member _.ConfigureServices(config: SiloConfig, f: IServiceCollection -> unit) =
        { config with
            CustomServices = config.CustomServices @ [ f ]
        }

    /// <summary>
    /// Adds an incoming grain call filter to the silo configuration.
    /// Incoming filters intercept calls arriving at a grain.
    /// Multiple addIncomingFilter calls accumulate (they do not replace each other).
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="filter">The incoming grain call filter to register.</param>
    /// <returns>The updated silo configuration with the incoming filter added.</returns>
    [<CustomOperation("addIncomingFilter")>]
    member _.AddIncomingFilter(config: SiloConfig, filter: IIncomingGrainCallFilter) =
        { config with
            IncomingFilters = config.IncomingFilters @ [ filter ]
        }

    /// <summary>
    /// Adds an outgoing grain call filter to the silo configuration.
    /// Outgoing filters intercept calls made from a grain to another grain.
    /// Multiple addOutgoingFilter calls accumulate (they do not replace each other).
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="filter">The outgoing grain call filter to register.</param>
    /// <returns>The updated silo configuration with the outgoing filter added.</returns>
    [<CustomOperation("addOutgoingFilter")>]
    member _.AddOutgoingFilter(config: SiloConfig, filter: IOutgoingGrainCallFilter) =
        { config with
            OutgoingFilters = config.OutgoingFilters @ [ filter ]
        }

    /// <summary>
    /// Adds a broadcast channel provider with the given name.
    /// Broadcast channels deliver messages to ALL subscriber grains (fan-out).
    /// Multiple addBroadcastChannel calls accumulate.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the broadcast channel provider.</param>
    /// <returns>The updated silo configuration with the broadcast channel added.</returns>
    [<CustomOperation("addBroadcastChannel")>]
    member _.AddBroadcastChannel(config: SiloConfig, name: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Broadcast channel name cannot be empty or whitespace"
        { config with
            BroadcastChannels = config.BroadcastChannels @ [ name ]
        }

    /// <summary>
    /// Configures grain interface versioning with the specified compatibility and selector strategies.
    /// This controls how grains of different versions communicate during rolling upgrades.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="compatibility">The compatibility strategy (e.g., BackwardCompatible).</param>
    /// <param name="selector">The version selector strategy (e.g., AllCompatibleVersions).</param>
    /// <returns>The updated silo configuration with grain versioning configured.</returns>
    [<CustomOperation("useGrainVersioning")>]
    member _.UseGrainVersioning(config: SiloConfig, compatibility: CompatibilityStrategy, selector: VersionSelectorStrategy) =
        { config with
            VersioningConfig = Some(compatibility, selector)
        }

    /// <summary>
    /// Registers a GrainService type with the silo.
    /// GrainServices run on every silo and are useful for background processing.
    /// The type must implement IGrainService.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="grainServiceType">The System.Type of the GrainService implementation.</param>
    /// <returns>The updated silo configuration with the grain service registered.</returns>
    [<CustomOperation("addGrainService")>]
    member _.AddGrainService(config: SiloConfig, grainServiceType: Type) =
        { config with
            GrainServiceTypes = config.GrainServiceTypes @ [ grainServiceType ]
        }

    /// <summary>
    /// Adds a persistent stream provider with the given name, adapter factory, and optional stream configurator.
    /// Persistent streams survive silo restarts and are backed by durable queues (e.g., EventHub, Redis).
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the stream provider.</param>
    /// <param name="adapterFactory">A factory function that creates the queue adapter.</param>
    /// <param name="configurator">An action to configure the persistent stream provider.</param>
    /// <returns>The updated silo configuration with the persistent stream provider added.</returns>
    [<CustomOperation("addPersistentStreams")>]
    member _.AddPersistentStreams
        (
            config: SiloConfig,
            name: string,
            adapterFactory: Func<IServiceProvider, string, IQueueAdapterFactory>,
            configurator: Action<ISiloPersistentStreamConfigurator>
        ) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Stream provider name cannot be empty or whitespace"
        { config with
            StreamProviders = config.StreamProviders |> Map.add name (PersistentStream(adapterFactory, configurator))
        }

    /// <summary>
    /// Adds a startup task that runs when the silo starts.
    /// The task receives an IServiceProvider and a CancellationToken.
    /// Multiple addStartupTask calls accumulate (they do not replace each other).
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="task">The startup task function.</param>
    /// <returns>The updated silo configuration with the startup task added.</returns>
    [<CustomOperation("addStartupTask")>]
    member _.AddStartupTask(config: SiloConfig, task: IServiceProvider -> CancellationToken -> Tasks.Task) =
        { config with
            StartupTasks = config.StartupTasks @ [ task ]
        }

    /// <summary>
    /// Enables health checks for the silo by registering IHealthChecksBuilder with the DI container.
    /// Map the health check endpoints in your ASP.NET Core pipeline to expose them.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <returns>The updated silo configuration with health checks enabled.</returns>
    [<CustomOperation("enableHealthChecks")>]
    member _.EnableHealthChecks(config: SiloConfig) =
        { config with EnableHealthChecks = true }

    /// <summary>
    /// Configures TLS using a certificate subject name from the certificate store.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.
    /// WARNING: In production, always use valid certificates from a trusted CA.
    /// Do not disable certificate validation in production environments.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="subject">The certificate subject name to look up in the certificate store.</param>
    /// <returns>The updated silo configuration with TLS enabled.</returns>
    [<CustomOperation("useTls")>]
    member _.UseTls(config: SiloConfig, subject: string) =
        { config with TlsConfig = Some(TlsSubject subject) }

    /// <summary>
    /// Configures TLS using an X509Certificate2 instance.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.
    /// WARNING: In production, always use valid certificates from a trusted CA.
    /// Do not disable certificate validation in production environments.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="certificate">The X509Certificate2 to use for TLS.</param>
    /// <returns>The updated silo configuration with TLS enabled.</returns>
    [<CustomOperation("useTlsWithCertificate")>]
    member _.UseTlsWithCertificate(config: SiloConfig, certificate: X509Certificate2) =
        { config with
            TlsConfig = Some(TlsCertificate certificate)
        }

    /// <summary>
    /// Configures mutual TLS (mTLS) using a certificate subject name from the certificate store.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.
    /// WARNING: In production, always use valid certificates from a trusted CA.
    /// Do not disable certificate validation in production environments.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="subject">The certificate subject name to look up in the certificate store.</param>
    /// <returns>The updated silo configuration with mutual TLS enabled.</returns>
    [<CustomOperation("useMutualTls")>]
    member _.UseMutualTls(config: SiloConfig, subject: string) =
        { config with
            TlsConfig = Some(MutualTlsSubject subject)
        }

    /// <summary>
    /// Configures mutual TLS (mTLS) using an X509Certificate2 instance.
    /// Requires the Microsoft.Orleans.Connections.Security NuGet package at runtime.
    /// WARNING: In production, always use valid certificates from a trusted CA.
    /// Do not disable certificate validation in production environments.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="certificate">The X509Certificate2 to use for mutual TLS.</param>
    /// <returns>The updated silo configuration with mutual TLS enabled.</returns>
    [<CustomOperation("useMutualTlsWithCertificate")>]
    member _.UseMutualTlsWithCertificate(config: SiloConfig, certificate: X509Certificate2) =
        { config with
            TlsConfig = Some(MutualTlsCertificate certificate)
        }

    /// <summary>
    /// Adds the Orleans Dashboard with default options.
    /// Requires the Microsoft.Orleans.Dashboard NuGet package at runtime.
    /// Map the dashboard endpoints with MapOrleansDashboard() in your ASP.NET Core pipeline.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <returns>The updated silo configuration with the dashboard enabled.</returns>
    [<CustomOperation("addDashboard")>]
    member _.AddDashboard(config: SiloConfig) =
        { config with
            DashboardConfig = Some DashboardDefaults
        }

    /// <summary>
    /// Adds the Orleans Dashboard with custom options.
    /// Requires the Microsoft.Orleans.Dashboard NuGet package at runtime.
    /// Map the dashboard endpoints with MapOrleansDashboard() in your ASP.NET Core pipeline.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="counterUpdateIntervalMs">The counter update interval in milliseconds (minimum 1000).</param>
    /// <param name="historyLength">The number of historical data points to retain.</param>
    /// <param name="hideTrace">Whether to disable the live trace endpoint.</param>
    /// <returns>The updated silo configuration with the dashboard enabled.</returns>
    [<CustomOperation("addDashboardWithOptions")>]
    member _.AddDashboardWithOptions(config: SiloConfig, counterUpdateIntervalMs: int, historyLength: int, hideTrace: bool) =
        { config with
            DashboardConfig = Some(DashboardWithOptions(counterUpdateIntervalMs, historyLength, hideTrace))
        }

    /// <summary>
    /// Adds an Azure Cosmos DB grain storage provider with the given name, account endpoint, and database name.
    /// Requires the Microsoft.Orleans.Persistence.Cosmos NuGet package at runtime.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <param name="accountEndpoint">The Cosmos DB account endpoint (e.g., "AccountEndpoint=...").</param>
    /// <param name="databaseName">The Cosmos DB database name.</param>
    /// <returns>The updated silo configuration with the Cosmos DB storage provider added.</returns>
    [<CustomOperation("addCosmosStorage")>]
    member _.AddCosmosStorage(config: SiloConfig, name: string, accountEndpoint: string, databaseName: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(accountEndpoint) then
            invalidArg (nameof accountEndpoint) "Account endpoint cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(databaseName) then
            invalidArg (nameof databaseName) "Database name cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name (CosmosStorage(accountEndpoint, databaseName))
        }

    /// <summary>
    /// Adds an Amazon DynamoDB grain storage provider with the given name and AWS region.
    /// Requires the Microsoft.Orleans.Persistence.DynamoDB NuGet package at runtime.
    /// If a provider with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The name of the storage provider.</param>
    /// <param name="region">The AWS region (e.g., "us-east-1").</param>
    /// <returns>The updated silo configuration with the DynamoDB storage provider added.</returns>
    [<CustomOperation("addDynamoDbStorage")>]
    member _.AddDynamoDbStorage(config: SiloConfig, name: string, region: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Storage provider name cannot be empty or whitespace"
        if System.String.IsNullOrWhiteSpace(region) then
            invalidArg (nameof region) "AWS region cannot be empty or whitespace"
        { config with
            StorageProviders = config.StorageProviders |> Map.add name (DynamoDbStorage region)
        }

    /// <summary>
    /// Configures the silo to use a Redis-based reminder service.
    /// Requires the Microsoft.Orleans.Reminders.Redis NuGet package at runtime.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="connectionString">The Redis connection string (e.g., "localhost:6379").</param>
    /// <returns>The updated silo configuration with the Redis reminder service configured.</returns>
    [<CustomOperation("addRedisReminderService")>]
    member _.AddRedisReminderService(config: SiloConfig, connectionString: string) =
        if System.String.IsNullOrWhiteSpace(connectionString) then
            invalidArg (nameof connectionString) "Connection string cannot be empty or whitespace"
        { config with
            ReminderProvider = Some(RedisReminder connectionString)
        }

    /// <summary>
    /// Sets the cluster identifier shared by all silos in the cluster.
    /// Maps to Orleans ClusterOptions.ClusterId.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="id">The cluster identifier string.</param>
    /// <returns>The updated silo configuration with the cluster ID set.</returns>
    [<CustomOperation("clusterId")>]
    member _.ClusterId(config: SiloConfig, id: string) =
        if System.String.IsNullOrWhiteSpace(id) then
            invalidArg (nameof id) "Cluster ID cannot be empty or whitespace"
        { config with ClusterId = Some id }

    /// <summary>
    /// Sets the unique service identifier for this Orleans deployment.
    /// Maps to Orleans ClusterOptions.ServiceId.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="id">The service identifier string.</param>
    /// <returns>The updated silo configuration with the service ID set.</returns>
    [<CustomOperation("serviceId")>]
    member _.ServiceId(config: SiloConfig, id: string) =
        if System.String.IsNullOrWhiteSpace(id) then
            invalidArg (nameof id) "Service ID cannot be empty or whitespace"
        { config with ServiceId = Some id }

    /// <summary>
    /// Sets a human-readable name for this silo instance.
    /// Maps to Orleans SiloOptions.SiloName.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="name">The silo name string.</param>
    /// <returns>The updated silo configuration with the silo name set.</returns>
    [<CustomOperation("siloName")>]
    member _.SiloName(config: SiloConfig, name: string) =
        if System.String.IsNullOrWhiteSpace(name) then
            invalidArg (nameof name) "Silo name cannot be empty or whitespace"
        { config with SiloName = Some name }

    /// <summary>
    /// Sets the silo-to-silo communication port.
    /// Maps to Orleans EndpointOptions.SiloPort.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="port">The port number for silo-to-silo communication.</param>
    /// <returns>The updated silo configuration with the silo port set.</returns>
    [<CustomOperation("siloPort")>]
    member _.SiloPort(config: SiloConfig, port: int) =
        { config with SiloPort = Some port }

    /// <summary>
    /// Sets the client-to-silo gateway port.
    /// Maps to Orleans EndpointOptions.GatewayPort.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="port">The port number for the client gateway.</param>
    /// <returns>The updated silo configuration with the gateway port set.</returns>
    [<CustomOperation("gatewayPort")>]
    member _.GatewayPort(config: SiloConfig, port: int) =
        { config with GatewayPort = Some port }

    /// <summary>
    /// Sets the IP address that this silo advertises to other silos.
    /// Maps to Orleans EndpointOptions.AdvertisedIPAddress.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="ip">The IP address string (e.g., "10.0.0.1").</param>
    /// <returns>The updated silo configuration with the advertised IP set.</returns>
    [<CustomOperation("advertisedIpAddress")>]
    member _.AdvertisedIpAddress(config: SiloConfig, ip: string) =
        { config with AdvertisedIpAddress = Some ip }

    /// <summary>
    /// Sets the global default grain collection age (idle timeout before deactivation).
    /// Maps to Orleans GrainCollectionOptions.CollectionAge.
    /// </summary>
    /// <param name="config">The current silo configuration being built.</param>
    /// <param name="age">The collection age as a TimeSpan.</param>
    /// <returns>The updated silo configuration with the grain collection age set.</returns>
    [<CustomOperation("grainCollectionAge")>]
    member _.GrainCollectionAge(config: SiloConfig, age: TimeSpan) =
        { config with GrainCollectionAge = Some age }

    /// <summary>Returns the completed silo configuration.</summary>
    member _.Run(config: SiloConfig) = config

/// <summary>
/// Module containing the siloConfig computation expression builder instance.
/// </summary>
[<AutoOpen>]
module SiloConfigBuilderInstance =
    /// <summary>
    /// Computation expression for declaratively configuring an Orleans silo.
    /// Supports custom operations: useLocalhostClustering, addMemoryStorage, addCustomStorage,
    /// addRedisStorage, addRedisClustering, addAzureBlobStorage, addAzureTableStorage,
    /// addAzureTableClustering, addAdoNetStorage, addAdoNetClustering, addCosmosStorage,
    /// addDynamoDbStorage, addMemoryStreams, addMemoryReminderService, addRedisReminderService,
    /// useSerilog, configureServices, addIncomingFilter, addOutgoingFilter, addBroadcastChannel,
    /// useGrainVersioning, addGrainService, addPersistentStreams, useTls, useTlsWithCertificate,
    /// useMutualTls, useMutualTlsWithCertificate, addDashboard, addDashboardWithOptions,
    /// enableHealthChecks, addStartupTask.
    /// </summary>
    let siloConfig = SiloConfigBuilder()
