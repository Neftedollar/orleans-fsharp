namespace Orleans.FSharp.Integration

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Hosting
open Orleans.TestingHost
open Xunit

/// <summary>
/// Silo configurator that adds memory grain storage and ensures the CodeGen assembly is loaded
/// for grain discovery by Orleans.
/// </summary>
type TestSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.AddMemoryStreams("StreamProvider") |> ignore
            siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore
            siloBuilder.UseInMemoryReminderService() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider("LogStorage") |> ignore
            siloBuilder.AddBroadcastChannel("BroadcastProvider") |> ignore

            siloBuilder.Services.Configure<Orleans.Hosting.ReminderOptions>(fun (options: Orleans.Hosting.ReminderOptions) ->
                options.MinimumReminderPeriod <- TimeSpan.FromSeconds(1.0))
            |> ignore

/// <summary>
/// Client configurator that ensures the CodeGen assembly is loaded on the client side
/// for type alias resolution.
/// </summary>
type TestClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddMemoryStreams("StreamProvider") |> ignore

/// <summary>
/// Shared xUnit fixture that starts a TestCluster for integration tests.
/// Implements IAsyncLifetime for async setup and teardown.
/// </summary>
type ClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    /// <summary>Gets the running TestCluster instance.</summary>
    member _.Cluster = cluster

    /// <summary>Gets the GrainFactory for creating grain references.</summary>
    member _.GrainFactory = cluster.GrainFactory

    /// <summary>Gets the cluster client for advanced operations like streaming.</summary>
    member _.Client = cluster.Client

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                // Force the CodeGen assembly to be loaded into the current AppDomain.
                // Orleans discovers grains by scanning loaded assemblies for ApplicationPartAttribute.
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CounterGrainImpl>.Assembly
                let _ = codeGenAssembly.GetTypes()

                let builder = TestClusterBuilder()
                builder.Options.InitialSilosCount <- 1s
                builder.AddSiloBuilderConfigurator<TestSiloConfigurator>() |> ignore
                builder.AddClientBuilderConfigurator<TestClientConfigurator>() |> ignore
                cluster <- builder.Build()
                do! cluster.DeployAsync()
            }

        member _.DisposeAsync() =
            task {
                if not (isNull (box cluster)) then
                    do! cluster.StopAllSilosAsync()
                    cluster.Dispose()
            }

/// <summary>
/// xUnit collection definition that shares a single ClusterFixture across all integration tests.
/// </summary>
[<CollectionDefinition("ClusterCollection")>]
type ClusterCollection() =
    interface ICollectionFixture<ClusterFixture>
