module Orleans.FSharp.Integration.FilterIntegrationTests

open System.Collections.Concurrent
open Xunit
open Swensen.Unquote
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Sample

/// <summary>
/// Thread-safe call log shared between the filter and tests.
/// Uses a ConcurrentBag to safely record intercepted method names.
/// </summary>
module FilterCallLog =
    let calls = ConcurrentBag<string>()

    let clear () =
        while calls.TryTake() |> fst do
            ()

/// <summary>
/// Silo configurator that registers an incoming grain call filter which logs
/// the interface method name for every grain call.
/// </summary>
type FilterTestSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.UseInMemoryReminderService() |> ignore

            let filter =
                Filter.incoming (fun ctx ->
                    task {
                        let methodName = ctx.InterfaceMethod.Name
                        FilterCallLog.calls.Add(methodName)
                        do! ctx.Invoke()
                    })

            siloBuilder.AddIncomingGrainCallFilter(filter) |> ignore

/// <summary>
/// Shared xUnit fixture for filter integration tests.
/// </summary>
type FilterClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    member _.Cluster = cluster
    member _.GrainFactory = cluster.GrainFactory

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CodeGenAssemblyMarker>.Assembly
                let _ = codeGenAssembly.GetTypes()

                let builder = TestClusterBuilder()
                builder.Options.InitialSilosCount <- 1s
                builder.AddSiloBuilderConfigurator<FilterTestSiloConfigurator>() |> ignore
                cluster <- builder.Build()
                do! cluster.DeployAsync()
            }

        member _.DisposeAsync() =
            task {
                if not (isNull (box cluster)) then
                    do! cluster.StopAllSilosAsync()
                    cluster.Dispose()
            }

[<CollectionDefinition("FilterCollection")>]
type FilterCollection() =
    interface ICollectionFixture<FilterClusterFixture>

/// <summary>
/// Integration tests verifying incoming grain call filters intercept grain method calls.
/// </summary>
[<Collection("FilterCollection")>]
type FilterIntegrationTests(fixture: FilterClusterFixture) =

    do FilterCallLog.clear ()

    [<Fact>]
    member _.``Incoming filter intercepts grain call`` () =
        task {
            FilterCallLog.clear ()
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(9000L)
            let! _ = grain.HandleMessage(Increment)

            let calls = FilterCallLog.calls |> Seq.toList
            test <@ calls |> List.exists (fun name -> name = "HandleMessage") @>
        }

    [<Fact>]
    member _.``Incoming filter intercepts multiple grain calls`` () =
        task {
            FilterCallLog.clear ()
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(9001L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(GetValue)

            let handleMessageCount =
                FilterCallLog.calls
                |> Seq.filter (fun name -> name = "HandleMessage")
                |> Seq.length

            test <@ handleMessageCount >= 3 @>
        }

    [<Fact>]
    member _.``Filter does not prevent grain from executing`` () =
        task {
            FilterCallLog.clear ()
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(9002L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = unbox<int> result
            test <@ value = 2 @>
        }

    [<Fact>]
    member _.``Filter intercepts calls to different grain types`` () =
        task {
            FilterCallLog.clear ()
            let counterGrain = fixture.GrainFactory.GetGrain<ICounterGrain>(9003L)
            let! _ = counterGrain.HandleMessage(Increment)

            let echoGrain = fixture.GrainFactory.GetGrain<IEchoGrain>("filter-echo-test")
            let! _ = echoGrain.HandleMessage(Echo "hello")

            let handleMessageCount =
                FilterCallLog.calls
                |> Seq.filter (fun name -> name = "HandleMessage")
                |> Seq.length

            test <@ handleMessageCount >= 2 @>
        }
