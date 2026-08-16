module Orleans.FSharp.Tests.AddFSharpGrainsFromAssemblyTests

/// <summary>
/// Unit tests for the IServiceCollection.AddFSharpGrainsFromAssembly attribute-scan
/// registration path (Orleans.FSharp.Runtime.GrainDiscovery).
/// The scan matches module-level GrainDefinition bindings by [&lt;FSharpGrain&gt;];
/// matching is inheritance-aware, so an attribute DERIVED from FSharpGrainAttribute
/// must be discovered too. That derived path had no coverage before this file.
/// </summary>

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp
open Orleans.FSharp.Runtime

// Task 8 deprecation pass: exercises the pre-functional-runtime grain{} attribute-scan
// registration path on purpose.
#nowarn "44"

/// A user-defined marker deriving from the built-in one. Orleans.FSharp must treat it
/// exactly like [<FSharpGrain>] — Attribute.IsDefined(_, inherit=false) matches derived
/// attribute types, a FullName equality check would not.
type DerivedFSharpGrainAttribute() =
    inherit FSharpGrainAttribute()

type ScannedDirectState = { Ticks: int }

type ScannedDirectCommand = DirectTick

type ScannedDerivedState = { Total: int }

type ScannedDerivedCommand = DerivedAdd of int

[<FSharpGrain>]
let scannedDirectly: GrainDefinition<ScannedDirectState, ScannedDirectCommand> =
    grain {
        defaultState { Ticks = 0 }

        handle (fun state _ ->
            task {
                let next = { state with Ticks = state.Ticks + 1 }
                return next, box next
            })
    }

[<DerivedFSharpGrain>]
let scannedViaDerivedAttribute: GrainDefinition<ScannedDerivedState, ScannedDerivedCommand> =
    grain {
        defaultState { Total = 0 }

        handle (fun state cmd ->
            task {
                let next =
                    match cmd with
                    | DerivedAdd n -> { state with Total = state.Total + n }

                return next, box next
            })
    }

let private thisAssembly = typeof<DerivedFSharpGrainAttribute>.Assembly

let private scan () =
    let services = ServiceCollection()
    services.AddFSharpGrainsFromAssembly(thisAssembly) |> ignore
    services

let private isRegistered<'Definition> (services: ServiceCollection) =
    services |> Seq.exists (fun sd -> sd.ServiceType = typeof<'Definition>)

[<Fact>]
let ``AddFSharpGrainsFromAssembly registers a definition marked with [<FSharpGrain>]`` () =
    let services = scan ()
    test <@ isRegistered<GrainDefinition<ScannedDirectState, ScannedDirectCommand>> services @>

[<Fact>]
let ``AddFSharpGrainsFromAssembly registers a definition marked with a DERIVED attribute`` () =
    let services = scan ()
    test <@ isRegistered<GrainDefinition<ScannedDerivedState, ScannedDerivedCommand>> services @>

[<Fact>]
let ``a derived-attribute grain dispatches through the universal handler registry`` () =
    task {
        let services = scan ()
        use provider = services.BuildServiceProvider()
        let handler = provider.GetRequiredService<IUniversalGrainHandler>()

        let defaultState =
            handler.GetDefaultState(typeof<ScannedDerivedCommand>) :?> ScannedDerivedState

        test <@ defaultState = { Total = 0 } @>

        let! dispatched =
            handler.Handle(box defaultState, box (DerivedAdd 7), provider, null, null)

        test <@ (dispatched.NewState :?> ScannedDerivedState) = { Total = 7 } @>
    }
    :> Task
