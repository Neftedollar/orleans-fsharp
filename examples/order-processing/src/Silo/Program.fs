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

// Force-load Orleans.Reminders before the silo's first UseOrleans/AddSerializer pass (which
// SiloConfig.applyToHost triggers immediately below): addMemoryReminderService reaches
// Orleans.IReminderTableGrain's implementation (InMemoryReminderTable) only through an F# hop, and
// Orleans only manifests assemblies already loaded when it takes that snapshot -- the same
// mechanism, and the same documented fix, as the two force-loads applyToHost already does for
// addMemoryStorage / the F# surface itself. See docs/functional-grains.md, "Running a silo from a
// standalone F# process". Without this, the silo fails to start with:
// System.ArgumentException: Could not find an implementation for interface Orleans.IReminderTableGrain
// IReminderTableGrain itself is internal to Orleans.Reminders.dll; IReminderTable (public, same
// assembly) force-loads the same assembly.
typeof<Orleans.IReminderTable>.Assembly |> ignore

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.Services.AddFSharpGrain<OrderState, OrderCommand>(OrderGrainDef.order) |> ignore

// Functional-runtime equivalent of the grain above -- see OrderGrainFunctional.fs.
builder.UseOrleans(fun siloBuilder -> siloBuilder.AddFunctionalGrain(OrderFunctionalDef.order) |> ignore)
|> ignore

let host = builder.Build()

(*
    Classic grain { } model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so a bare `factory.GetGrain<IOrderGrain>(...)` fails with
    "Could not find an implementation for interface IOrderGrain" the moment it runs -- this example
    never had a C# CodeGen bridge project to fill that gap. See docs/functional-grains.md,
    "Running a silo from a standalone F# process" for the exact mechanism, and "Migrating from
    the grain { } CE" for the rewrite this file demonstrates.

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
*)

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

        printfn "--- Order Processing (Functional Grain Runtime): DU State Machine + Timer + Reminder ---"
        printfn ""

        // Full lifecycle on one order: place -> confirm -> ship -> deliver.
        let order1 = OrderApi.ref factory "order-001"
        let! placed1 = order1.place "Widget x10"
        printfn "Place order-001:   %A" placed1

        printfn ""
        printfn "Waiting for timer status check..."
        do! Task.Delay(6000)
        printfn ""

        let! confirmed1 = order1.confirm ()
        printfn "Confirm order-001: %A" confirmed1
        let! shipped1 = order1.ship ()
        printfn "Ship order-001:    %A" shipped1
        let! delivered1 = order1.deliver ()
        printfn "Deliver order-001: %A" delivered1

        let! status1 = order1.status ()
        printfn "Final status order-001: %A" status1

        // Invalid transition, typed: skipping straight from Created to Deliver is rejected with
        // InvalidTransition instead of throwing.
        printfn ""
        let order2 = OrderApi.ref factory "order-002"
        let! placed2 = order2.place "Widget x5"
        printfn "Place order-002:   %A" placed2
        let! invalidDeliver = order2.deliver ()
        printfn "Deliver order-002 (invalid -- skipped confirm+ship): %A" invalidDeliver

        // Cancel path: place -> confirm -> cancel.
        printfn ""
        let order3 = OrderApi.ref factory "order-003"
        let! placed3 = order3.place "Widget x2"
        printfn "Place order-003:   %A" placed3
        let! confirmed3 = order3.confirm ()
        printfn "Confirm order-003: %A" confirmed3
        let! cancelled3 = order3.cancel "changed mind"
        printfn "Cancel order-003:  %A" cancelled3

        // Cancelling an already-shipped order is rejected -- the same "invalid cancel" case the
        // old demo showed, now as a typed error instead of a Rejected string.
        printfn ""
        let order4 = OrderApi.ref factory "order-004"
        let! _ = order4.place "Widget x1"
        let! _ = order4.confirm ()
        let! _ = order4.ship ()
        let! invalidCancel = order4.cancel "too late"
        printfn "Cancel order-004 (invalid -- already shipped): %A" invalidCancel

        printfn ""
        printfn "Reminder note: OrderTimeout is registered for real, on Orleans' actual 1-minute"
        printfn "reminder-period floor, and auto-cancels any order left Created for 30+ minutes."
        printfn "Not waited for live here (that would be a 1-minute-plus demo) -- see"
        printfn "OrderGrainFunctional.fs and this example's README for the exact schedule."

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
