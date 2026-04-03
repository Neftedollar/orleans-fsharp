module Orleans.FSharp.Integration.SiloConfigTests

open Xunit
open Swensen.Unquote
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.Sample

/// <summary>
/// Silo configurator that uses the siloConfig CE to configure the test silo.
/// Validates that SiloConfig can be applied to an ISiloBuilder.
/// </summary>
type SiloConfigTestConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            let config =
                siloConfig {
                    addMemoryStorage "Default"
                    addMemoryStreams "StreamProvider"
                }

            SiloConfig.applyToSiloBuilder config siloBuilder

/// <summary>
/// Shared xUnit fixture that starts a TestCluster configured via siloConfig CE.
/// </summary>
type SiloConfigClusterFixture() =
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
                builder.AddSiloBuilderConfigurator<SiloConfigTestConfigurator>() |> ignore
                cluster <- builder.Build()
                do! cluster.DeployAsync()
            }

        member _.DisposeAsync() =
            task {
                if not (isNull (box cluster)) then
                    do! cluster.StopAllSilosAsync()
                    cluster.Dispose()
            }

[<CollectionDefinition("SiloConfigCollection")>]
type SiloConfigCollection() =
    interface ICollectionFixture<SiloConfigClusterFixture>

/// <summary>
/// Integration tests verifying siloConfig CE produces a working silo.
/// </summary>
[<Collection("SiloConfigCollection")>]
type SiloConfigIntegrationTests(fixture: SiloConfigClusterFixture) =

    [<Fact>]
    member _.``Silo configured via siloConfig CE accepts counter grain calls`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(500L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 2 @>
        }

    [<Fact>]
    member _.``Silo configured via siloConfig CE has Default memory storage`` () =
        task {
            // The counter grain uses persist "Default", so if storage is not configured
            // the grain would fail. A successful increment proves storage is available.
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(501L)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 1 @>
        }

    [<Fact>]
    member _.``Silo configured via siloConfig CE supports order grain state transitions`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("siloconfig-order-test")
            let! _ = grain.HandleMessage(Place "Widget")
            let! result = grain.HandleMessage(Confirm)
            let status = unbox<OrderStatus> result
            test <@ status = Completed "Order confirmed: Widget" @>
        }
