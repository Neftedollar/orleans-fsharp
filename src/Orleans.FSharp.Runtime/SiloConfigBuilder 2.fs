namespace Orleans.FSharp.Runtime

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans.Hosting
open Serilog

/// <summary>
/// Specifies how the silo discovers and communicates with other silos in the cluster.
/// </summary>
[<NoEquality; NoComparison>]
type ClusteringMode =
    /// <summary>Localhost clustering for local development (single silo).</summary>
    | Localhost
    /// <summary>Custom clustering configuration via an ISiloBuilder transformation.</summary>
    | CustomClustering of (ISiloBuilder -> ISiloBuilder)

/// <summary>
/// Specifies the type of storage provider for grain state persistence.
/// </summary>
[<NoEquality; NoComparison>]
type StorageProvider =
    /// <summary>In-memory grain storage (data lost on silo restart).</summary>
    | Memory
    /// <summary>Custom storage provider configuration via an ISiloBuilder transformation.</summary>
    | CustomStorage of (ISiloBuilder -> ISiloBuilder)

/// <summary>
/// Specifies the type of stream provider for grain event streaming.
/// </summary>
[<NoEquality; NoComparison>]
type StreamProvider =
    /// <summary>In-memory stream provider (suitable for development and testing).</summary>
    | MemoryStream
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
    let applyToSiloBuilder (config: SiloConfig) (siloBuilder: ISiloBuilder) : unit =
        // Apply clustering
        match config.ClusteringMode with
        | Some Localhost -> siloBuilder.UseLocalhostClustering() |> ignore
        | Some(CustomClustering f) -> f siloBuilder |> ignore
        | None -> ()

        // Apply storage providers
        config.StorageProviders
        |> Map.iter (fun name provider ->
            match provider with
            | Memory -> siloBuilder.AddMemoryGrainStorage(name) |> ignore
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
            | CustomStream f -> f siloBuilder |> ignore)

        // Memory streams require a PubSubStore grain storage.
        // Add one automatically if not already configured.
        if needsPubSubStore && not (config.StorageProviders |> Map.containsKey "PubSubStore") then
            siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore

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
    /// addMemoryStreams, useSerilog, configureServices.
    /// </summary>
    let siloConfig = SiloConfigBuilder()
