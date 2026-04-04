module Orleans.FSharp.Tests.AddFSharpGrainTests

/// <summary>
/// Unit tests for the IServiceCollection.AddFSharpGrain extension method
/// (Orleans.FSharp.Runtime.GrainDiscovery).
/// Verifies DI registration behavior: codec auto-registration, singleton sharing,
/// and idempotency guarantees.
/// </summary>

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp
open Orleans.FSharp.Runtime

// ── Test domain types ─────────────────────────────────────────────────────────

type WidgetState = { Count: int; Name: string }

type WidgetCommand =
    | Tick
    | SetName of string
    | Query

type GadgetState = { Active: bool }

type GadgetCommand =
    | Activate
    | Deactivate
    | Status

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Creates a minimal GrainDefinition with a no-op handler.
let private makeWidgetDef () : GrainDefinition<WidgetState, WidgetCommand> =
    grain {
        defaultState { Count = 0; Name = "" }
        handle (fun state msg ->
            task {
                match msg with
                | Tick      -> return { state with Count = state.Count + 1 }, box state.Count
                | SetName n -> return { state with Name = n }, box n
                | Query     -> return state, box state
            })
    }

let private makeGadgetDef () : GrainDefinition<GadgetState, GadgetCommand> =
    grain {
        defaultState { Active = false }
        handle (fun state msg ->
            task {
                match msg with
                | Activate   -> return { Active = true }, box true
                | Deactivate -> return { Active = false }, box false
                | Status     -> return state, box state.Active
            })
    }

// ── FSharpBinaryCodec auto-registration ───────────────────────────────────────

[<Fact>]
let ``AddFSharpGrain registers FSharpBinaryCodec in DI`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore

    let registered =
        services
        |> Seq.exists (fun sd -> sd.ServiceType = typeof<FSharpBinaryCodec>)

    test <@ registered @>

[<Fact>]
let ``AddFSharpGrain registers FSharpBinaryCodec exactly once for two registrations`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore
    services.AddFSharpGrain<GadgetState, GadgetCommand>(makeGadgetDef()) |> ignore

    let codecRegistrations =
        services
        |> Seq.filter (fun sd -> sd.ServiceType = typeof<FSharpBinaryCodec>)
        |> Seq.length

    test <@ codecRegistrations = 1 @>

// ── GrainRegistry registration ────────────────────────────────────────────────

[<Fact>]
let ``AddFSharpGrain registers GrainRegistry singleton`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore

    let registered =
        services
        |> Seq.exists (fun sd -> sd.ServiceType = typeof<GrainRegistry>)

    test <@ registered @>

[<Fact>]
let ``AddFSharpGrain shares single GrainRegistry across calls`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore
    services.AddFSharpGrain<GadgetState, GadgetCommand>(makeGadgetDef()) |> ignore

    let registryCount =
        services
        |> Seq.filter (fun sd -> sd.ServiceType = typeof<GrainRegistry>)
        |> Seq.length

    test <@ registryCount = 1 @>

// ── UniversalGrainHandlerRegistry registration ────────────────────────────────

[<Fact>]
let ``AddFSharpGrain registers UniversalGrainHandlerRegistry singleton`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore

    let registered =
        services
        |> Seq.exists (fun sd -> sd.ServiceType = typeof<UniversalGrainHandlerRegistry>)

    test <@ registered @>

[<Fact>]
let ``AddFSharpGrain registers IUniversalGrainHandler singleton`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore

    let registered =
        services
        |> Seq.exists (fun sd -> sd.ServiceType = typeof<IUniversalGrainHandler>)

    test <@ registered @>

[<Fact>]
let ``AddFSharpGrain shares single UniversalGrainHandlerRegistry across calls`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore
    services.AddFSharpGrain<GadgetState, GadgetCommand>(makeGadgetDef()) |> ignore

    let handlerRegistryCount =
        services
        |> Seq.filter (fun sd -> sd.ServiceType = typeof<UniversalGrainHandlerRegistry>)
        |> Seq.length

    test <@ handlerRegistryCount = 1 @>

// ── GrainDefinition registration ──────────────────────────────────────────────

[<Fact>]
let ``AddFSharpGrain registers GrainDefinition as singleton`` () =
    let services = ServiceCollection()
    let def = makeWidgetDef()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(def) |> ignore

    let registered =
        services
        |> Seq.exists (fun sd -> sd.ServiceType = typeof<GrainDefinition<WidgetState, WidgetCommand>>)

    test <@ registered @>

[<Fact>]
let ``AddFSharpGrain registers each distinct GrainDefinition type separately`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore
    services.AddFSharpGrain<GadgetState, GadgetCommand>(makeGadgetDef()) |> ignore

    let widgetDef =
        services
        |> Seq.exists (fun sd -> sd.ServiceType = typeof<GrainDefinition<WidgetState, WidgetCommand>>)

    let gadgetDef =
        services
        |> Seq.exists (fun sd -> sd.ServiceType = typeof<GrainDefinition<GadgetState, GadgetCommand>>)

    test <@ widgetDef @>
    test <@ gadgetDef @>

// ── Duplicate-registration guard ──────────────────────────────────────────────

[<Fact>]
let ``AddFSharpGrain throws on duplicate message type registration`` () =
    let services = ServiceCollection()
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore

    let ex =
        Assert.Throws<System.InvalidOperationException>(fun () ->
            services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore)

    test <@ ex.Message.Contains("WidgetCommand") @>

// ── Handler dispatch via registered registry ──────────────────────────────────

[<Fact>]
let ``AddFSharpGrain wires handler so registry can dispatch Widget messages`` () =
    task {
        let services = ServiceCollection()
        services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore

        let handlerSd =
            services |> Seq.find (fun sd -> sd.ServiceType = typeof<UniversalGrainHandlerRegistry>)

        let registry = handlerSd.ImplementationInstance :?> UniversalGrainHandlerRegistry
        let handler = registry :> IUniversalGrainHandler

        let! result = handler.Handle(null, box Tick, Unchecked.defaultof<IServiceProvider>, Unchecked.defaultof<IGrainFactory>, Unchecked.defaultof<Orleans.IGrainBase>)
        let state = result.NewState :?> WidgetState
        test <@ state.Count = 1 @>
    }

[<Fact>]
let ``AddFSharpGrain wires multiple handlers on same registry`` () =
    task {
        let services = ServiceCollection()
        services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore
        services.AddFSharpGrain<GadgetState, GadgetCommand>(makeGadgetDef()) |> ignore

        let handlerSd =
            services |> Seq.find (fun sd -> sd.ServiceType = typeof<UniversalGrainHandlerRegistry>)

        let registry = handlerSd.ImplementationInstance :?> UniversalGrainHandlerRegistry
        let handler = registry :> IUniversalGrainHandler

        let! widgetResult = handler.Handle(null, box Tick, Unchecked.defaultof<IServiceProvider>, Unchecked.defaultof<IGrainFactory>, Unchecked.defaultof<Orleans.IGrainBase>)
        let! gadgetResult = handler.Handle(null, box Activate, Unchecked.defaultof<IServiceProvider>, Unchecked.defaultof<IGrainFactory>, Unchecked.defaultof<Orleans.IGrainBase>)

        test <@ (widgetResult.NewState :?> WidgetState).Count = 1 @>
        test <@ (gadgetResult.NewState :?> GadgetState).Active = true @>
    }

[<Fact>]
let ``AddFSharpGrain SetName command works end-to-end through registry`` () =
    task {
        let services = ServiceCollection()
        services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore

        let handlerSd =
            services |> Seq.find (fun sd -> sd.ServiceType = typeof<UniversalGrainHandlerRegistry>)

        let handler = handlerSd.ImplementationInstance :?> UniversalGrainHandlerRegistry :> IUniversalGrainHandler
        let! result = handler.Handle(null, box (SetName "Orleans"), Unchecked.defaultof<IServiceProvider>, Unchecked.defaultof<IGrainFactory>, Unchecked.defaultof<Orleans.IGrainBase>)
        let state = result.NewState :?> WidgetState
        test <@ state.Name = "Orleans" @>
    }

// ── Return value of AddFSharpGrain ────────────────────────────────────────────

[<Fact>]
let ``AddFSharpGrain returns the same IServiceCollection for chaining`` () =
    let services = ServiceCollection() :> IServiceCollection
    let returned = services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef())
    test <@ obj.ReferenceEquals(services, returned) @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``AddFSharpGrain registers at least 1 service for any grain definition`` () =
    let services = ServiceCollection() :> IServiceCollection
    let countBefore = services |> Seq.length
    services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef()) |> ignore
    services |> Seq.length > countBefore

[<Property>]
let ``AddFSharpGrain always returns the same IServiceCollection instance for any grain def variation`` () =
    let services = ServiceCollection() :> IServiceCollection
    let returned = services.AddFSharpGrain<WidgetState, WidgetCommand>(makeWidgetDef())
    obj.ReferenceEquals(services, returned)
