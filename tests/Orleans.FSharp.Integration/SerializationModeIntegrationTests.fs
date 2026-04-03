module Orleans.FSharp.Integration.SerializationModeIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp.Sample

/// <summary>
/// Silo configurator that enables JSON fallback serialization for clean F# types.
/// Types without [GenerateSerializer] will be serialized using System.Text.Json
/// with FSharp.SystemTextJson converters.
/// </summary>
type JsonFallbackSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.UseInMemoryReminderService() |> ignore

            // Enable JSON fallback serialization
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                siloBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer(
                        serializerBuilder,
                        isSupported = System.Func<System.Type, bool>(fun _ -> true),
                        jsonSerializerOptions = Orleans.FSharp.FSharpJson.serializerOptions)
                    |> ignore))
            |> ignore

/// <summary>
/// xUnit fixture that starts a TestCluster with JSON fallback serialization enabled.
/// </summary>
type JsonFallbackClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    /// <summary>Gets the running TestCluster instance.</summary>
    member _.Cluster = cluster

    /// <summary>Gets the GrainFactory for creating grain references.</summary>
    member _.GrainFactory = cluster.GrainFactory

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CounterGrainImpl>.Assembly
                let _ = codeGenAssembly.GetTypes()

                let builder = TestClusterBuilder()
                builder.Options.InitialSilosCount <- 1s
                builder.AddSiloBuilderConfigurator<JsonFallbackSiloConfigurator>() |> ignore
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
/// xUnit collection for tests sharing the JSON fallback cluster.
/// </summary>
[<CollectionDefinition("JsonFallbackCluster")>]
type JsonFallbackClusterCollection() =
    interface ICollectionFixture<JsonFallbackClusterFixture>

// ---------------------------------------------------------------------------
// Mode 3 (Explicit) integration tests — [GenerateSerializer] + [Id]
// ---------------------------------------------------------------------------

[<Collection("ClusterCollection")>]
type ExplicitModeIntegrationTests(fixture: ClusterFixture) =

    /// <summary>
    /// Mode 3 (Explicit): [GenerateSerializer] + [Id] DU roundtrips through Orleans grain.
    /// CounterCommand is defined with explicit attributes.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode CounterCommand serializes through grain call`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(42L)
            let! result = grain.HandleMessage(Increment)
            let value = result :?> int
            test <@ value = 1 @>
        }

    /// <summary>
    /// Mode 3 (Explicit): multiple commands roundtrip through Orleans grain.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode multiple commands serialize correctly`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(43L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = result :?> int
            test <@ value = 3 @>
        }

// ---------------------------------------------------------------------------
// Mode 1 (Clean/JSON fallback) integration tests
// ---------------------------------------------------------------------------

[<Collection("JsonFallbackCluster")>]
type JsonFallbackIntegrationTests(fixture: JsonFallbackClusterFixture) =

    /// <summary>
    /// Mode 1 (Clean): explicitly attributed type still works with JSON fallback enabled.
    /// This verifies the fallback doesn't break the native serializer for attributed types.
    /// </summary>
    [<Fact>]
    member _.``JSON fallback does not break explicitly attributed grain types`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(100L)
            let! result = grain.HandleMessage(Increment)
            let value = result :?> int
            test <@ value = 1 @>
        }

    /// <summary>
    /// Mode 1 (Clean): verify the JSON fallback cluster starts successfully.
    /// </summary>
    [<Fact>]
    member _.``JSON fallback cluster starts successfully`` () =
        task {
            test <@ fixture.Cluster <> null @>
            test <@ fixture.GrainFactory <> null @>
        }
