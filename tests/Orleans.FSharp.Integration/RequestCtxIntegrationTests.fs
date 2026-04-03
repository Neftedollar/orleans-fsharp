module Orleans.FSharp.Integration.RequestCtxIntegrationTests

open System.Collections.Concurrent
open Xunit
open Swensen.Unquote
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Sample

/// <summary>
/// Thread-safe log to capture request context values read inside grain call filters.
/// The incoming filter reads a known key from RequestContext and logs it here.
/// </summary>
module RequestCtxLog =
    let capturedValues = ConcurrentBag<string option>()

    let clear () =
        while capturedValues.TryTake() |> fst do
            ()

/// <summary>
/// Silo configurator that registers an incoming filter which reads "PrincipalId"
/// from the request context and logs it.
/// </summary>
type RequestCtxTestSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.UseInMemoryReminderService() |> ignore

            let filter =
                Filter.incoming (fun ctx ->
                    task {
                        let principalId = RequestCtx.get<string> "PrincipalId"
                        RequestCtxLog.capturedValues.Add(principalId)
                        do! ctx.Invoke()
                    })

            siloBuilder.AddIncomingGrainCallFilter(filter) |> ignore

/// <summary>
/// Shared xUnit fixture for request context integration tests.
/// </summary>
type RequestCtxClusterFixture() =
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
                builder.AddSiloBuilderConfigurator<RequestCtxTestSiloConfigurator>() |> ignore
                cluster <- builder.Build()
                do! cluster.DeployAsync()
            }

        member _.DisposeAsync() =
            task {
                if not (isNull (box cluster)) then
                    do! cluster.StopAllSilosAsync()
                    cluster.Dispose()
            }

[<CollectionDefinition("RequestCtxCollection")>]
type RequestCtxCollection() =
    interface ICollectionFixture<RequestCtxClusterFixture>

/// <summary>
/// Integration tests verifying request context propagation across grain calls.
/// </summary>
[<Collection("RequestCtxCollection")>]
type RequestCtxIntegrationTests(fixture: RequestCtxClusterFixture) =

    do RequestCtxLog.clear ()

    [<Fact>]
    member _.``Request context value propagates to grain filter`` () =
        task {
            RequestCtxLog.clear ()
            RequestCtx.set "PrincipalId" (box "user-123")

            try
                let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(8000L)
                let! _ = grain.HandleMessage(Increment)

                let captured = RequestCtxLog.capturedValues |> Seq.toList
                test <@ captured |> List.exists (fun v -> v = Some "user-123") @>
            finally
                RequestCtx.remove "PrincipalId"
        }

    [<Fact>]
    member _.``Missing request context value shows as None in filter`` () =
        task {
            RequestCtxLog.clear ()
            // Ensure the key is not set
            RequestCtx.remove "PrincipalId"

            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(8001L)
            let! _ = grain.HandleMessage(Increment)

            let captured = RequestCtxLog.capturedValues |> Seq.toList
            test <@ captured |> List.exists (fun v -> v = None) @>
        }

    [<Fact>]
    member _.``withValue scopes request context for grain call`` () =
        task {
            RequestCtxLog.clear ()

            do!
                RequestCtx.withValue "PrincipalId" (box "scoped-user") (fun () ->
                    task {
                        let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(8002L)
                        let! _ = grain.HandleMessage(Increment)
                        return ()
                    })

            let captured = RequestCtxLog.capturedValues |> Seq.toList
            test <@ captured |> List.exists (fun v -> v = Some "scoped-user") @>

            // Value should be cleaned up after withValue
            let after = RequestCtx.get<string> "PrincipalId"
            test <@ after = None @>
        }

    [<Fact>]
    member _.``Different request context values propagate to separate calls`` () =
        task {
            RequestCtxLog.clear ()

            RequestCtx.set "PrincipalId" (box "alice")
            let grain1 = fixture.GrainFactory.GetGrain<ICounterGrain>(8003L)
            let! _ = grain1.HandleMessage(Increment)

            RequestCtx.set "PrincipalId" (box "bob")
            let grain2 = fixture.GrainFactory.GetGrain<ICounterGrain>(8004L)
            let! _ = grain2.HandleMessage(Increment)

            RequestCtx.remove "PrincipalId"

            let captured = RequestCtxLog.capturedValues |> Seq.toList
            test <@ captured |> List.exists (fun v -> v = Some "alice") @>
            test <@ captured |> List.exists (fun v -> v = Some "bob") @>
        }
