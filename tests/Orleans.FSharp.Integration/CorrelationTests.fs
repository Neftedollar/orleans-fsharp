module Orleans.FSharp.Integration.CorrelationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Microsoft.Extensions.Logging
open Orleans.FSharp
open Orleans.FSharp.Testing
open Orleans.FSharp.Sample

/// <summary>
/// Integration tests for correlation ID propagation across grain calls.
/// Verifies that withCorrelation sets a correlation ID that propagates
/// through grain call chains and appears in structured log entries.
/// </summary>
[<Collection("ClusterCollection")>]
type CorrelationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``Correlation ID flows through grain call chain`` () =
        task {
            let correlationId = Guid.NewGuid().ToString("N")

            do!
                Log.withCorrelation correlationId (fun () ->
                    task {
                        let grainA = fixture.GrainFactory.GetGrain<ICounterGrain>(500L)
                        let! _ = grainA.HandleMessage(Increment)

                        let grainB = fixture.GrainFactory.GetGrain<ICounterGrain>(501L)
                        let! _ = grainB.HandleMessage(Increment)

                        let grainC = fixture.GrainFactory.GetGrain<ICounterGrain>(502L)
                        let! _ = grainC.HandleMessage(Increment)

                        // Verify correlation ID is still set after grain calls
                        let currentId = Log.currentCorrelationId ()
                        test <@ currentId = Some correlationId @>
                    })
        }

    [<Fact>]
    member _.``Correlation ID is available within withCorrelation scope`` () =
        task {
            let correlationId = "test-corr-id-integration"

            do!
                Log.withCorrelation correlationId (fun () ->
                    task {
                        let id = Log.currentCorrelationId ()
                        test <@ id = Some correlationId @>
                    })
        }

    [<Fact>]
    member _.``Correlation ID is None outside withCorrelation scope`` () =
        task {
            do!
                Log.withCorrelation "temp-id" (fun () ->
                    task { return () })

            // After the scope, correlation should be restored to previous (None)
            let id = Log.currentCorrelationId ()
            test <@ id = None @>
        }

    [<Fact>]
    member _.``Nested correlation scopes restore correctly`` () =
        task {
            do!
                Log.withCorrelation "outer-id" (fun () ->
                    task {
                        do!
                            Log.withCorrelation "inner-id" (fun () ->
                                task {
                                    let id = Log.currentCorrelationId ()
                                    test <@ id = Some "inner-id" @>

                                    // Call a grain within inner scope
                                    let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(503L)
                                    let! _ = grain.HandleMessage(Increment)
                                    return ()
                                })

                        let id = Log.currentCorrelationId ()
                        test <@ id = Some "outer-id" @>
                    })
        }

    [<Fact>]
    member _.``Log entries within correlation scope contain structured data`` () =
        task {
            let factory = LogCapture.create ()
            let logger = (factory :> ILoggerFactory).CreateLogger("IntegrationTest")
            let correlationId = "integration-corr-123"

            do!
                Log.withCorrelation correlationId (fun () ->
                    task {
                        Log.logInfo logger "Grain {GrainType} processing {Command}" [| box "Counter"; box "Increment" |]
                        Log.logWarning logger "Slow operation on {GrainId}" [| box 500L |]
                        return ()
                    })

            let entries = LogCapture.captureLogs factory
            test <@ entries.Length = 2 @>

            // All entries should have the correlation ID
            for entry in entries do
                test <@ entry.Properties |> Map.containsKey "CorrelationId" @>

                let corrId = entry.Properties.["CorrelationId"] |> unbox<string>
                test <@ corrId = correlationId @>

            // First entry should have GrainType and Command properties
            test <@ entries.[0].Properties |> Map.containsKey "GrainType" @>
            test <@ entries.[0].Properties |> Map.containsKey "Command" @>
        }
