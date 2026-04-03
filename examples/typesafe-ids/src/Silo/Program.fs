open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open TypeSafeIds.Domain
open TypeSafeIds.Domain.Ids
open TypeSafeIds.Domain.Routing

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.Services.AddFSharpGrain<UserState, UserCommand>(UserGrainDef.user) |> ignore
builder.Services.AddFSharpGrain<OrderState, OrderCommand>(OrderGrainDef.order) |> ignore
builder.Services.AddFSharpGrain<RouterState, IncomingMessage>(RouterGrainDef.router) |> ignore

let host = builder.Build()

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

        // -------------------------------------------------------------------
        // 1. Type-Safe IDs — Units of Measure (IMPOSSIBLE in C#)
        // -------------------------------------------------------------------
        printfn "--- Feature 1: Type-Safe IDs (Units of Measure) ---"
        printfn ""

        let user1 = userId 1L
        let user2 = userId 2L
        let order1 = orderId 100L
        let order2 = orderId 200L

        printfn "Created User %d, User %d" (rawId user1) (rawId user2)
        printfn "Created Order %d, Order %d" (rawId order1) (rawId order2)

        // This compiles — correct type:
        let userRef = UserGrainDef.getUser factory user1
        let! _ = GrainRef.invoke userRef (fun g -> g.HandleMessage(SetProfile("Alice", "alice@example.com")))
        printfn "Set profile for User %d" (rawId user1)

        // This would NOT compile — wrong type:
        // let wrong = UserGrainDef.getUser factory order1
        // Error: Expected int64<UserId>, got int64<OrderId>

        let orderRef = OrderGrainDef.getOrder factory order1
        let! _ = GrainRef.invoke orderRef (fun g -> g.HandleMessage(CreateOrder(user1, 99.99m)))
        printfn "Created Order %d for User %d" (rawId order1) (rawId user1)

        // This would NOT compile — wrong type:
        // let wrong = OrderGrainDef.getOrder factory user1
        // Error: Expected int64<OrderId>, got int64<UserId>

        printfn ""

        // -------------------------------------------------------------------
        // 2. Active Pattern Routing (IMPOSSIBLE in C#)
        // -------------------------------------------------------------------
        printfn "--- Feature 2: Active Pattern Message Routing ---"
        printfn ""

        let messages =
            [
                { SenderId = 1L; Content = "What's the status?"; Timestamp = DateTime.UtcNow; IsVip = true; SpamScore = 0.1 }
                { SenderId = 2L; Content = "Buy cheap watches!!!"; Timestamp = DateTime.UtcNow; IsVip = false; SpamScore = 0.95 }
                { SenderId = 3L; Content = "/cancel order 42"; Timestamp = DateTime.UtcNow; IsVip = false; SpamScore = 0.0 }
                { SenderId = 4L; Content = "Hello there!"; Timestamp = DateTime.UtcNow; IsVip = false; SpamScore = 0.1 }
                { SenderId = 5L; Content = "How do I reset my password?"; Timestamp = DateTime.UtcNow; IsVip = false; SpamScore = 0.2 }
                { SenderId = 6L; Content = String.replicate 100 "long message "; Timestamp = DateTime.UtcNow; IsVip = false; SpamScore = 0.1 }
            ]

        let routerRef = GrainRef.ofString<IRouterGrain> factory "main-router"

        for msg in messages do
            let! route = GrainRef.invoke routerRef (fun g -> g.HandleMessage(msg))
            printfn "  Sender %d -> %s" msg.SenderId (route :?> string)

        printfn ""

        // -------------------------------------------------------------------
        // 3. Exhaustive Matching (compiler catches missing cases)
        // -------------------------------------------------------------------
        printfn "--- Feature 3: Exhaustive State Transitions ---"
        printfn ""

        let! confirmed = GrainRef.invoke orderRef (fun g -> g.HandleMessage(ConfirmOrder))
        printfn "Order %d confirmed: %A" (rawId order1) confirmed

        let! shipped = GrainRef.invoke orderRef (fun g -> g.HandleMessage(ShipOrder))
        printfn "Order %d shipped: %A" (rawId order1) shipped

        let! delivered = GrainRef.invoke orderRef (fun g -> g.HandleMessage(DeliverOrder))
        printfn "Order %d delivered: %A" (rawId order1) delivered

        // Invalid transition: delivered -> cancelled
        let! cancelledAfterDelivery = GrainRef.invoke orderRef (fun g -> g.HandleMessage(CancelOrder))
        printfn "Order %d cancel after delivery: %A (no-op, status unchanged)" (rawId order1) cancelledAfterDelivery

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
