namespace Orleans.FSharp.Runtime

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.FSharp.Versioning
open Orleans.Streams
open Serilog

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
            | CustomStorage f -> f siloBuilder |> ignore)

        // Apply reminder provider
        match config.ReminderProvider with
        | Some MemoryReminder -> siloBuilder.UseInMemoryReminderService() |> ignore
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
        { config with
            StreamProviders = config.StreamProviders |> Map.add name (PersistentStream(adapterFactory, configurator))
        }

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
    /// addAzureTableClustering, addAdoNetStorage, addAdoNetClustering,
    /// addMemoryStreams, addMemoryReminderService, useSerilog, configureServices,
    /// addIncomingFilter, addOutgoingFilter, addBroadcastChannel, useGrainVersioning,
    /// addGrainService, addPersistentStreams.
    /// </summary>
    let siloConfig = SiloConfigBuilder()
