namespace Orleans.FSharp.Integration

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Hosting
open Orleans.TestingHost
open Xunit
open Orleans.FSharp
open Orleans.FSharp.Runtime

// ── Test grain types for the universal IFSharpGrain pattern ──────────────────

/// <summary>State for the integration-test ping grain (universal pattern).</summary>
[<Orleans.GenerateSerializer>]
type PingState = { [<Orleans.Id(0u)>] Count: int }

/// <summary>Commands for the integration-test ping grain.</summary>
[<Orleans.GenerateSerializer>]
type PingCommand =
    | [<Orleans.Id(0u)>] Ping
    | [<Orleans.Id(1u)>] GetCount

/// <summary>State for the text-accumulator grain (tests field-carrying DU cases).</summary>
[<Orleans.GenerateSerializer>]
type TextState = { [<Orleans.Id(0u)>] Text: string }

/// <summary>
/// Commands for the text-accumulator grain.
/// <c>Append</c> carries a payload (a field-carrying DU case), exercising the
/// nested-type dispatch fix in <c>UniversalGrainHandlerRegistry</c>.
/// </summary>
[<Orleans.GenerateSerializer>]
type TextCommand =
    | [<Orleans.Id(0u)>] Append of string
    | [<Orleans.Id(1u)>] GetText

/// <summary>Definition of the ping grain used to test <c>FSharpGrain.ref</c>.</summary>
[<AutoOpen>]
module TestGrains =
    let pingGrain =
        grain {
            defaultState { Count = 0 }
            handle (fun state cmd ->
                task {
                    match cmd with
                    | Ping      ->
                        let ns = { Count = state.Count + 1 }
                        // Return the new state as both the persisted state and the caller result
                        // so that FSharpGrain.send<PingState, PingCommand> can cast the result.
                        return ns, box ns
                    | GetCount  -> return state, box state
                })
        }

    /// <summary>
    /// Text-accumulator grain definition used to verify that field-carrying F# DU cases
    /// (e.g. <c>Append of string</c>) are correctly dispatched by <c>UniversalGrainHandlerRegistry</c>.
    /// </summary>
    let textGrain =
        grain {
            defaultState { Text = "" }
            handle (fun state cmd ->
                task {
                    match cmd with
                    | Append s  ->
                        let ns = { Text = state.Text + s }
                        return ns, box ns
                    | GetText   -> return state, box state
                })
        }

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

            // Register the universal-pattern ping grain for FSharpGrain.ref tests.
            // FSharpBinaryCodec is registered automatically by AddFSharpGrain — no manual
            // FSharpBinaryCodecRegistration.addToSerializerBuilder call needed here.
            siloBuilder.Services.AddFSharpGrain<PingState, PingCommand>(pingGrain) |> ignore
            // Register the text-accumulator grain for field-carrying DU case dispatch tests
            siloBuilder.Services.AddFSharpGrain<TextState, TextCommand>(textGrain) |> ignore

/// <summary>
/// Client configurator that ensures the CodeGen assembly is loaded on the client side
/// for type alias resolution.
/// </summary>
type TestClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddMemoryStreams("StreamProvider") |> ignore

            // Register F# binary serialization on the client so the proxy can deep-copy
            // F# types passed as `object` to IFSharpGrain.HandleMessage.
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun b ->
                    FSharpBinaryCodecRegistration.addToSerializerBuilder b |> ignore))
            |> ignore

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
                // Force Orleans assemblies to be loaded into the current AppDomain.
                // Orleans discovers grains by scanning loaded assemblies for ApplicationPartAttribute.
                // - CodeGen: per-grain proxies for Sample grains (legacy ICounterGrain etc.)
                // - Abstractions: universal IFSharpGrain proxies (new pattern)
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CodeGenAssemblyMarker>.Assembly
                let _ = codeGenAssembly.GetTypes()
                let abstractionsAssembly = typeof<Orleans.FSharp.IFSharpGrain>.Assembly
                let _ = abstractionsAssembly.GetTypes()

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
