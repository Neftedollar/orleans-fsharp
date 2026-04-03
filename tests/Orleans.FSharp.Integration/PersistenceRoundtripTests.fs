module Orleans.FSharp.Integration.PersistenceRoundtripTests

open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

[<Collection("ClusterCollection")>]
type PersistenceRoundtripTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``Order grain starts in Idle state`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-idle-test")
            let! result = grain.HandleMessage(GetStatus)
            let status = unbox<OrderStatus> result
            test <@ status = Idle @>
        }

    [<Fact>]
    member _.``Order grain transitions from Idle to Processing`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-place-test")
            let! result = grain.HandleMessage(Place "Widget order")
            let status = unbox<OrderStatus> result
            test <@ status = Processing "Widget order" @>
        }

    [<Fact>]
    member _.``Order grain transitions from Processing to Completed`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-confirm-test")
            let! _ = grain.HandleMessage(Place "Gadget order")
            let! result = grain.HandleMessage(Confirm)
            let status = unbox<OrderStatus> result
            test <@ status = Completed "Order confirmed: Gadget order" @>
        }

    [<Fact>]
    member _.``Order grain transitions from Processing to Failed on Cancel`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-cancel-test")
            let! _ = grain.HandleMessage(Place "Doomed order")
            let! result = grain.HandleMessage(Cancel)
            let status = unbox<OrderStatus> result
            test <@ status = Failed "Cancelled by user" @>
        }

    [<Fact>]
    member _.``Order grain rejects invalid transition`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-invalid-test")
            // Can't Ship from Idle
            let! result = grain.HandleMessage(Ship)
            let errorMsg = unbox<string> result
            test <@ errorMsg = "Cannot ship: no order is being processed" @>
        }

    [<Fact>]
    member _.``Order grain rejects Ship before Confirm`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-ship-before-confirm")
            let! _ = grain.HandleMessage(Place "My order")
            let! result = grain.HandleMessage(Ship)
            let errorMsg = unbox<string> result
            test <@ errorMsg = "Cannot ship: order must be confirmed first" @>
        }

    [<Fact>]
    member _.``Order grain state persists across calls`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-persist-test")
            let! _ = grain.HandleMessage(Place "Persistent order")
            let! _ = grain.HandleMessage(Confirm)
            // Status should reflect the confirmed state
            let! result = grain.HandleMessage(GetStatus)
            let status = unbox<OrderStatus> result
            test <@ status = Completed "Order confirmed: Persistent order" @>
        }

    [<Fact>]
    member _.``Order grain can place new order after failure`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-retry-test")
            let! _ = grain.HandleMessage(Place "First attempt")
            let! _ = grain.HandleMessage(Cancel)
            // Should be in Failed state
            let! statusResult = grain.HandleMessage(GetStatus)
            let status = unbox<OrderStatus> statusResult
            test <@ status = Failed "Cancelled by user" @>
            // Can place a new order from Failed state
            let! result = grain.HandleMessage(Place "Second attempt")
            let newStatus = unbox<OrderStatus> result
            test <@ newStatus = Processing "Second attempt" @>
        }

    [<Fact>]
    member _.``Order grain full lifecycle: Place -> Confirm -> GetStatus`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("order-lifecycle-test")
            
            // Start idle
            let! idleResult = grain.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> idleResult = Idle @>
            
            // Place order
            let! placeResult = grain.HandleMessage(Place "Full lifecycle")
            test <@ unbox<OrderStatus> placeResult = Processing "Full lifecycle" @>
            
            // Confirm
            let! confirmResult = grain.HandleMessage(Confirm)
            test <@ unbox<OrderStatus> confirmResult = Completed "Order confirmed: Full lifecycle" @>
            
            // Verify final state
            let! finalResult = grain.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> finalResult = Completed "Order confirmed: Full lifecycle" @>
        }
