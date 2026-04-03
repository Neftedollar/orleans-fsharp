open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open OrderProcessing.Domain

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        addMemoryReminderService
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.Services.AddFSharpGrain<OrderState, OrderCommand>(OrderGrainDef.order) |> ignore

let host = builder.Build()

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()
        let orderRef = GrainRef.ofString<IOrderGrain> factory "order-001"

        printfn "--- Order Processing: DU State Machine + Reminders + Timers ---"
        printfn ""

        // Place an order
        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Place "Widget x10"))
        printfn "Place order:   %A" result

        // Wait a moment for timer to fire
        printfn ""
        printfn "Waiting for timer status check..."
        do! Task.Delay(6000)
        printfn ""

        // Confirm the order
        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Confirm))
        printfn "Confirm order: %A" result

        // Ship the order
        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Ship))
        printfn "Ship order:    %A" result

        // Try invalid transition (cancel a shipped order)
        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Cancel "changed mind"))
        printfn "Cancel (invalid): %A" result

        // Deliver the order
        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Deliver))
        printfn "Deliver order: %A" result

        // Check final status
        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(GetStatus))
        printfn ""
        printfn "Final status:  %A" result

        // Wait for reminder tick
        printfn ""
        printfn "Waiting for reminder tick..."
        do! Task.Delay(12000)

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
