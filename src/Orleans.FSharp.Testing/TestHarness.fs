namespace Orleans.FSharp.Testing

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Runtime

/// <summary>
/// A test harness wrapping an Orleans TestCluster with integrated log capture.
/// Provides a convenient API for grain testing with automatic setup and teardown.
/// </summary>
type TestHarness =
    {
        /// <summary>The underlying Orleans TestCluster.</summary>
        Cluster: TestCluster
        /// <summary>The cluster client for grain communication.</summary>
        Client: IClusterClient
        /// <summary>The log capturing factory that records all log entries.</summary>
        LogFactory: CapturingLoggerFactory
    }

/// <summary>
/// Functions for creating and managing test harnesses for Orleans grain testing.
/// </summary>
[<RequireQualifiedAccess>]
module TestHarness =

    /// <summary>
    /// Silo configurator that adds memory storage and memory streams for testing.
    /// </summary>
    type private TestSiloConfigurator() =
        interface ISiloConfigurator with
            /// <summary>Adds memory grain storage, memory streams, and the PubSub store used by tests.</summary>
            /// <param name="siloBuilder">The silo builder to configure.</param>
            member _.Configure(siloBuilder: ISiloBuilder) =
                siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
                siloBuilder.AddMemoryGrainStorage("Default") |> ignore
                siloBuilder.AddMemoryStreams("StreamProvider") |> ignore
                siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore

    /// <summary>
    /// Creates a TestHarness with a default in-memory configuration.
    /// The harness includes memory grain storage, memory streams, and a log capture sink.
    /// </summary>
    /// <returns>A Task containing the initialized TestHarness.</returns>
    let createTestCluster () : Task<TestHarness> =
        task {
            let logFactory = new CapturingLoggerFactory()

            let builder = TestClusterBuilder()
            builder.Options.InitialSilosCount <- 1s
            builder.AddSiloBuilderConfigurator<TestSiloConfigurator>() |> ignore

            let cluster = builder.Build()
            do! cluster.DeployAsync()

            return
                {
                    Cluster = cluster
                    Client = cluster.Client
                    LogFactory = logFactory
                }
        }

    /// <summary>The configuration captured for the next silo built by <see cref="CustomSiloConfigurator"/>.</summary>
    type private SiloConfiguratorRef =
        { Config: SiloConfig; LogFactory: CapturingLoggerFactory }

    /// <summary>
    /// Holds the <see cref="SiloConfiguratorRef"/> for the cluster currently being built, since
    /// <see cref="CustomSiloConfigurator"/> is instantiated by Orleans with no way to pass it directly.
    /// </summary>
    let private mutableConfigRef = ref None

    /// <summary>
    /// Silo configurator that applies the <see cref="SiloConfig"/> captured in
    /// <see cref="mutableConfigRef"/>. State travels through the ref cell because
    /// <c>TestClusterBuilder.AddSiloBuilderConfigurator&lt;'T&gt;</c> only accepts types with a
    /// parameterless constructor, so nothing can be passed to this type directly.
    /// </summary>
    type private CustomSiloConfigurator() =
        interface ISiloConfigurator with
            /// <summary>Applies the captured <see cref="SiloConfig"/> and registers the shared log factory, if one was set.</summary>
            /// <param name="siloBuilder">The silo builder to configure.</param>
            member _.Configure(siloBuilder: ISiloBuilder) =
                match !mutableConfigRef with
                | Some { Config = cfg; LogFactory = lf } ->
                    SiloConfig.applyToSiloBuilder cfg siloBuilder
                    siloBuilder.Services.AddSingleton<ILoggerFactory>(lf) |> ignore
                | None -> ()

    /// <summary>
    /// Creates a TestHarness with a custom SiloConfig applied.
    /// The custom configuration is applied via SiloConfig.applyToSiloBuilder
    /// instead of the default in-memory configurator.
    /// </summary>
    /// <param name="config">The silo configuration to apply.</param>
    /// <returns>A Task containing the initialized TestHarness.</returns>
    let createTestClusterWith (config: SiloConfig) : Task<TestHarness> =
        task {
            let logFactory = new CapturingLoggerFactory()

            // Set the ref before building (thread-safe since we're single-threaded here)
            mutableConfigRef := Some { Config = config; LogFactory = logFactory }

            let builder = TestClusterBuilder()
            builder.Options.InitialSilosCount <- 1s
            builder.AddSiloBuilderConfigurator<CustomSiloConfigurator>() |> ignore

            let cluster = builder.Build()
            do! cluster.DeployAsync()

            return
                {
                    Cluster = cluster
                    Client = cluster.Client
                    LogFactory = logFactory
                }
        }

    // ── FSharpGrain helpers ──────────────────────────────────────────
    // deprecated API self-reference (spec-003 deprecation pass)
#nowarn "44"

    /// <summary>
    /// Gets an FSharpGrain handle from the test harness using a string key.
    /// This is the universal-pattern shortcut — no interface type needed.
    /// </summary>
    /// <param name="harness">The test harness to get the grain from.</param>
    /// <param name="key">The string key for the grain.</param>
    /// <typeparam name="'State">The grain state type.</typeparam>
    /// <typeparam name="'Message">The grain message/command type.</typeparam>
    /// <returns>An FSharpGrainHandle for the specified grain.</returns>
    [<Obsolete("TestHarness.getFSharpGrain serves the superseded grain{} message-passing cluster: for functional grains define the grain with grainContract<...> / grainFor, register it on the TestingHost silo with AddFunctionalGrain, and get a typed API record with FunctionalGrain.ref contract harness.Client key. See docs/functional-grains.md for the migration.", false)>]
    let getFSharpGrain<'State, 'Message>
        (harness: TestHarness)
        (key: string)
        : FSharpGrainHandle<'State, 'Message> =
        FSharpGrain.ref<'State, 'Message> harness.Cluster.GrainFactory key

    /// <summary>
    /// Gets an FSharpGrain handle from the test harness using a Guid key.
    /// This is the universal-pattern shortcut — no interface type needed.
    /// </summary>
    /// <param name="harness">The test harness to get the grain from.</param>
    /// <param name="key">The Guid key for the grain.</param>
    /// <typeparam name="'State">The grain state type.</typeparam>
    /// <typeparam name="'Message">The grain message/command type.</typeparam>
    /// <returns>An FSharpGrainGuidHandle for the specified grain.</returns>
    [<Obsolete("TestHarness.getFSharpGrainGuid serves the superseded grain{} message-passing cluster: for functional grains define the grain with grainContract<...> / grainFor over a Guid key, register it on the TestingHost silo with AddFunctionalGrain, and get a typed API record with FunctionalGrain.ref contract harness.Client key. See docs/functional-grains.md for the migration.", false)>]
    let getFSharpGrainGuid<'State, 'Message>
        (harness: TestHarness)
        (key: Guid)
        : FSharpGrainGuidHandle<'State, 'Message> =
        FSharpGrain.refGuid<'State, 'Message> harness.Cluster.GrainFactory key

    /// <summary>
    /// Gets an FSharpGrain handle from the test harness using an int64 key.
    /// This is the universal-pattern shortcut — no interface type needed.
    /// </summary>
    /// <param name="harness">The test harness to get the grain from.</param>
    /// <param name="key">The int64 key for the grain.</param>
    /// <typeparam name="'State">The grain state type.</typeparam>
    /// <typeparam name="'Message">The grain message/command type.</typeparam>
    /// <returns>An FSharpGrainIntHandle for the specified grain.</returns>
    [<Obsolete("TestHarness.getFSharpGrainInt serves the superseded grain{} message-passing cluster: for functional grains define the grain with grainContract<...> / grainFor over an int64 key, register it on the TestingHost silo with AddFunctionalGrain, and get a typed API record with FunctionalGrain.ref contract harness.Client key. See docs/functional-grains.md for the migration.", false)>]
    let getFSharpGrainInt<'State, 'Message>
        (harness: TestHarness)
        (key: int64)
        : FSharpGrainIntHandle<'State, 'Message> =
        FSharpGrain.refInt<'State, 'Message> harness.Cluster.GrainFactory key
#warnon "44"

    /// <summary>
    /// Gets a typed grain reference from the test harness using a string key.
    /// </summary>
    /// <param name="harness">The test harness to get the grain from.</param>
    /// <param name="key">The string key for the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let getGrainByString<'TInterface when 'TInterface :> IGrainWithStringKey>
        (harness: TestHarness)
        (key: string)
        : GrainRef<'TInterface, string> =
        GrainRef.ofString<'TInterface> harness.Cluster.GrainFactory key

    /// <summary>
    /// Gets a typed grain reference from the test harness using an int64 key.
    /// </summary>
    /// <param name="harness">The test harness to get the grain from.</param>
    /// <param name="key">The int64 key for the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let getGrainByInt64<'TInterface when 'TInterface :> IGrainWithIntegerKey>
        (harness: TestHarness)
        (key: int64)
        : GrainRef<'TInterface, int64> =
        GrainRef.ofInt64<'TInterface> harness.Cluster.GrainFactory key

    /// <summary>
    /// Gets a typed grain reference from the test harness using a Guid key.
    /// </summary>
    /// <param name="harness">The test harness to get the grain from.</param>
    /// <param name="key">The Guid key for the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let getGrainByGuid<'TInterface when 'TInterface :> IGrainWithGuidKey>
        (harness: TestHarness)
        (key: Guid)
        : GrainRef<'TInterface, Guid> =
        GrainRef.ofGuid<'TInterface> harness.Cluster.GrainFactory key

    /// <summary>
    /// Gets all captured log entries from the test harness.
    /// </summary>
    /// <param name="harness">The test harness to get logs from.</param>
    /// <returns>A list of captured log entries sorted by timestamp.</returns>
    let captureLogs (harness: TestHarness) : CapturedLogEntry list =
        LogCapture.captureLogs harness.LogFactory

    /// <summary>
    /// Resets the test harness state by clearing all captured log entries.
    /// </summary>
    /// <param name="harness">The test harness to reset.</param>
    /// <returns>A Task that completes when the reset is finished.</returns>
    let reset (harness: TestHarness) : Task<unit> =
        task { harness.LogFactory.Clear() }

    /// <summary>
    /// Disposes the test harness, stopping all silos and releasing resources.
    /// </summary>
    /// <param name="harness">The test harness to dispose.</param>
    /// <returns>A Task that completes when disposal is finished.</returns>
    let dispose (harness: TestHarness) : Task<unit> =
        task {
            if not (isNull (box harness.Cluster)) then
                do! harness.Cluster.StopAllSilosAsync()
                harness.Cluster.Dispose()
        }
