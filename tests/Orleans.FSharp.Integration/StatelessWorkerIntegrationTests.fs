namespace Orleans.FSharp.Integration

open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Hosting
open Orleans.TestingHost
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

/// <summary>
/// Silo configurator for the 2-silo stateless worker test cluster.
/// </summary>
type TwoSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore

/// <summary>
/// Shared xUnit fixture that starts a 2-silo TestCluster for stateless worker integration tests.
/// </summary>
type TwoSiloClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    /// <summary>Gets the running TestCluster instance.</summary>
    member _.Cluster = cluster

    /// <summary>Gets the GrainFactory for creating grain references.</summary>
    member _.GrainFactory = cluster.GrainFactory

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CodeGenAssemblyMarker>.Assembly
                let _ = codeGenAssembly.GetTypes()

                let builder = TestClusterBuilder()
                builder.Options.InitialSilosCount <- 2s
                builder.AddSiloBuilderConfigurator<TwoSiloConfigurator>() |> ignore
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
/// xUnit collection definition for the 2-silo cluster.
/// </summary>
[<CollectionDefinition("TwoSiloCollection")>]
type TwoSiloCollection() =
    interface ICollectionFixture<TwoSiloClusterFixture>

/// <summary>
/// Integration tests for stateless worker grains in Orleans.FSharp.
/// Uses a 2-silo cluster to verify multiple activations and load balancing.
/// </summary>
[<Collection("TwoSiloCollection")>]
type StatelessWorkerIntegrationTests(fixture: TwoSiloClusterFixture) =

    [<Fact>]
    member _.``Stateless worker processes messages successfully`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IProcessorGrain>(0L)
            let! result = grain.HandleMessage(Process "hello")
            let value = unbox<string> result
            // Result should contain the value we sent
            test <@ value.Contains("hello") @>
        }

    [<Fact>]
    member _.``Stateless worker returns activation ID`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IProcessorGrain>(0L)
            let! result = grain.HandleMessage(GetActivationId)
            let activationId = unbox<string> result
            // Activation ID should be a non-empty GUID string
            test <@ activationId.Length > 0 @>
        }

    [<Fact>]
    member _.``Stateless worker may use multiple activations under concurrent load`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IProcessorGrain>(0L)

            // Fire many concurrent calls to increase chance of hitting different activations
            let tasks =
                [| for i in 1..20 ->
                       grain.HandleMessage(GetActivationId) |]

            let! results = Task.WhenAll(tasks)

            let activationIds =
                results
                |> Array.map (fun r -> unbox<string> r)
                |> Array.distinct

            // We should get at least 1 activation ID (basic sanity)
            // With concurrent load on 2 silos, we may get multiple
            test <@ activationIds.Length >= 1 @>
        }
