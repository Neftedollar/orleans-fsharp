module Orleans.FSharp.Tests.DeactivationControlTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp

[<Fact>]
let ``GrainContext deactivateOnIdle calls the registered function`` () =
    let mutable deactivateCalled = false

    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = ServiceCollection().BuildServiceProvider()
            States = Map.empty
            DeactivateOnIdle = Some(fun () -> deactivateCalled <- true)
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    GrainContext.deactivateOnIdle ctx
    test <@ deactivateCalled @>

[<Fact>]
let ``GrainContext delayDeactivation calls the registered function with correct delay`` () =
    let mutable receivedDelay = TimeSpan.Zero

    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = ServiceCollection().BuildServiceProvider()
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = Some(fun delay -> receivedDelay <- delay)
            GrainId = None
            PrimaryKey = None
        }

    let expectedDelay = TimeSpan.FromMinutes(5.0)
    GrainContext.delayDeactivation ctx expectedDelay
    test <@ receivedDelay = expectedDelay @>

[<Fact>]
let ``GrainContext deactivateOnIdle throws when None`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = ServiceCollection().BuildServiceProvider()
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    raises<InvalidOperationException> <@ GrainContext.deactivateOnIdle ctx @>

[<Fact>]
let ``GrainContext delayDeactivation throws when None`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = ServiceCollection().BuildServiceProvider()
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    raises<InvalidOperationException> <@ GrainContext.delayDeactivation ctx (TimeSpan.FromMinutes(1.0)) @>

[<Fact>]
let ``grain CE handler can use deactivateOnIdle via context`` () =
    task {
        let mutable deactivateCalled = false

        let def =
            grain {
                defaultState 0

                handleWithContext (fun ctx state (_msg: string) ->
                    task {
                        GrainContext.deactivateOnIdle ctx
                        return state, box state
                    })
            }

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = ServiceCollection().BuildServiceProvider()
                States = Map.empty
                DeactivateOnIdle = Some(fun () -> deactivateCalled <- true)
                DelayDeactivation = None
                GrainId = None
                PrimaryKey = None
            }

        let handler = GrainDefinition.getContextHandler def
        let! (_newState, _result) = handler ctx 0 "test"
        test <@ deactivateCalled @>
    }

[<Fact>]
let ``grain CE handler can use delayDeactivation via context`` () =
    task {
        let mutable receivedDelay = TimeSpan.Zero

        let def =
            grain {
                defaultState 0

                handleWithContext (fun ctx state (_msg: string) ->
                    task {
                        GrainContext.delayDeactivation ctx (TimeSpan.FromMinutes(10.0))
                        return state, box state
                    })
            }

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = ServiceCollection().BuildServiceProvider()
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = Some(fun delay -> receivedDelay <- delay)
                GrainId = None
                PrimaryKey = None
            }

        let handler = GrainDefinition.getContextHandler def
        let! (_newState, _result) = handler ctx 0 "test"
        test <@ receivedDelay = TimeSpan.FromMinutes(10.0) @>
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``delayDeactivation passes any TimeSpan to the registered function`` (minutes: PositiveInt) =
    let delay = TimeSpan.FromMinutes(float minutes.Get)
    let mutable received = TimeSpan.Zero
    let ctx =
        { GrainContext.empty with
            DelayDeactivation = Some(fun d -> received <- d) }
    GrainContext.delayDeactivation ctx delay
    received = delay

[<Property>]
let ``deactivateOnIdle always calls the registered function`` (value: int) =
    // 'value' is just noise to force FsCheck to run multiple times
    let mutable called = false
    let ctx =
        { GrainContext.empty with
            DeactivateOnIdle = Some(fun () -> called <- true) }
    GrainContext.deactivateOnIdle ctx
    called
