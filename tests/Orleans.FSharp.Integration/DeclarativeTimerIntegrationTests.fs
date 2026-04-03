module Orleans.FSharp.Integration.DeclarativeTimerIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

/// <summary>
/// Integration tests for the Orleans.FSharp declarative onTimer CE keyword.
/// Tests that timers declared via onTimer fire correctly on grain activation
/// and update grain state.
/// </summary>
[<Collection("ClusterCollection")>]
type DeclarativeTimerIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``Declarative timer fires and updates state`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<ITimerTestGrain>("timer-fire-test")

            // Verify initial state is 0
            let! result = grain.HandleMessage(GetTimerFireCount)
            let initialCount = unbox<int> result
            test <@ initialCount = 0 @>

            // Wait for the timer to fire at least once (dueTime=500ms, period=500ms)
            do! Task.Delay(3000)

            // Check that fire count increased
            let! result2 = grain.HandleMessage(GetTimerFireCount)
            let fireCount = unbox<int> result2
            test <@ fireCount >= 1 @>
        }

    [<Fact>]
    member _.``Declarative timer fires multiple times`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<ITimerTestGrain>("timer-multi-fire-test")

            // Trigger grain activation
            let! _ = grain.HandleMessage(GetTimerFireCount)

            // Wait for the timer to fire multiple times
            do! Task.Delay(4000)

            let! result = grain.HandleMessage(GetTimerFireCount)
            let fireCount = unbox<int> result
            // With 500ms period and 4s wait, should fire at least 3 times
            test <@ fireCount >= 3 @>
        }
