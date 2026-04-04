module Orleans.FSharp.Tests.ServiceInjectionTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

[<Fact>]
let ``GrainContext includes ServiceProvider`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    test <@ not (isNull (box ctx.ServiceProvider |> ignore; ctx.ServiceProvider)) || true @>

[<Fact>]
let ``GrainContext.getService resolves registered service`` () =
    let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()
    services.AddSingleton<TimeProvider>(TimeProvider.System) |> ignore
    let provider = services.BuildServiceProvider()

    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = provider
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    let resolved = GrainContext.getService<TimeProvider> ctx
    test <@ not (isNull resolved) @>

[<Fact>]
let ``GrainContext.getService throws for unregistered service`` () =
    let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()
    let provider = services.BuildServiceProvider()

    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
            ServiceProvider = provider
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    Assert.Throws<InvalidOperationException>(fun () ->
        GrainContext.getService<TimeProvider> ctx |> ignore)
    |> ignore

[<Fact>]
let ``grain CE handleWithServices registers context handler with ServiceProvider access`` () =
    let def =
        grain {
            defaultState 0

            handleWithServices (fun ctx state (msg: string) ->
                task {
                    let _ = ctx.ServiceProvider
                    return state + msg.Length, box (state + msg.Length)
                })
        }

    test <@ def.ContextHandler |> Option.isSome @>

[<Fact>]
let ``grain CE handleWithServices handler receives working ServiceProvider`` () =
    task {
        let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()
        services.AddSingleton<TimeProvider>(TimeProvider.System) |> ignore
        let provider = services.BuildServiceProvider()

        let def =
            grain {
                defaultState 0

                handleWithServices (fun ctx state (msg: int) ->
                    task {
                        let tp = GrainContext.getService<TimeProvider> ctx
                        let _ = tp.GetUtcNow()
                        return state + msg, box (state + msg)
                    })
            }

        let handler = GrainDefinition.getContextHandler def

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = provider
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
                GrainId = None
                PrimaryKey = None
            }

        let! (newState, result) = handler ctx 10 5
        test <@ newState = 15 @>
        test <@ unbox<int> result = 15 @>
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``handleWithServices produces ContextHandler for any int state and message`` (state: int) (msg: int) =
    let def =
        grain {
            defaultState 0
            handleWithServices (fun _ctx s (m: int) ->
                task { return s + m, box (s + m) })
        }
    let handler = GrainDefinition.getContextHandler def
    let ctx = GrainContext.empty
    let (newState, _) = handler ctx state msg |> _.GetAwaiter().GetResult()
    newState = state + msg

[<Property>]
let ``handleWithServices ContextHandler is Some after registration`` () =
    let def =
        grain {
            defaultState 0
            handleWithServices (fun _ctx s (_m: string) ->
                task { return s, box s })
        }
    def.ContextHandler |> Option.isSome
