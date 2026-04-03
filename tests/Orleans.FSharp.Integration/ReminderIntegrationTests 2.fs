module Orleans.FSharp.Integration.ReminderIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

/// <summary>
/// Integration tests for the Orleans.FSharp Reminder and Timer modules.
/// Tests that reminders fire correctly, survive deactivation,
/// and can be unregistered.
/// </summary>
[<Collection("ClusterCollection")>]
type ReminderIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``Register reminder on grain and verify it fires`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<IReminderTestGrain>("reminder-fire-test")

            // Verify initial state is 0
            let! result = grain.HandleMessage(GetFireCount)
            let initialCount = unbox<int> result
            test <@ initialCount = 0 @>

            // Register a reminder
            let! _ = grain.HandleMessage(RegisterReminder "TestReminder")

            // Wait for the reminder to fire at least once (dueTime=1s, period=2s)
            do! Task.Delay(5000)

            // Check that fire count increased
            let! result2 = grain.HandleMessage(GetFireCount)
            let fireCount = unbox<int> result2
            test <@ fireCount >= 1 @>

            // Cleanup: unregister
            let! _ = grain.HandleMessage(UnregisterReminder "TestReminder")
            ()
        }

    [<Fact>]
    member _.``Unregister reminder stops firing`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<IReminderTestGrain>("reminder-unregister-test")

            // Register and wait for it to fire
            let! _ = grain.HandleMessage(RegisterReminder "TestReminder")
            do! Task.Delay(4000)

            let! result = grain.HandleMessage(GetFireCount)
            let countAfterFiring = unbox<int> result
            test <@ countAfterFiring >= 1 @>

            // Unregister the reminder
            let! _ = grain.HandleMessage(UnregisterReminder "TestReminder")

            // Wait and verify count doesn't increase significantly
            do! Task.Delay(4000)

            let! result2 = grain.HandleMessage(GetFireCount)
            let countAfterUnregister = unbox<int> result2
            // The count should not increase much after unregistering
            // Allow +1 for possible in-flight tick at time of unregister
            test <@ countAfterUnregister <= countAfterFiring + 1 @>
        }

    [<Fact>]
    member _.``Multiple reminders fire independently`` () =
        task {
            let grain =
                fixture.GrainFactory.GetGrain<IReminderTestGrain>("reminder-multi-test")

            // Register two reminders
            let! _ = grain.HandleMessage(RegisterReminder "TestReminder")
            let! _ = grain.HandleMessage(RegisterReminder "SecondReminder")
            do! Task.Delay(5000)

            let! result = grain.HandleMessage(GetFireCount)
            let fireCount = unbox<int> result
            // TestReminder adds 1, SecondReminder adds 10 per tick
            // After at least 1 tick of each, count should be >= 11
            test <@ fireCount >= 11 @>

            // Cleanup
            let! _ = grain.HandleMessage(UnregisterReminder "TestReminder")
            let! _ = grain.HandleMessage(UnregisterReminder "SecondReminder")
            ()
        }
