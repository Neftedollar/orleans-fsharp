module Orleans.FSharp.Tests.GrainLifecycleTests

open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.Runtime

[<Fact>]
let ``grain CE registers lifecycle hook at SetupState stage`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onLifecycleStage GrainLifecycleStage.SetupState (fun _ct -> task { return () })
        }

    test <@ def.LifecycleHooks |> Map.containsKey GrainLifecycleStage.SetupState @>
    test <@ def.LifecycleHooks.[GrainLifecycleStage.SetupState] |> List.length = 1 @>

[<Fact>]
let ``grain CE registers lifecycle hook at Activate stage`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onLifecycleStage GrainLifecycleStage.Activate (fun _ct -> task { return () })
        }

    test <@ def.LifecycleHooks |> Map.containsKey GrainLifecycleStage.Activate @>

[<Fact>]
let ``grain CE registers lifecycle hook at First stage`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onLifecycleStage GrainLifecycleStage.First (fun _ct -> task { return () })
        }

    test <@ def.LifecycleHooks |> Map.containsKey GrainLifecycleStage.First @>

[<Fact>]
let ``grain CE accumulates multiple hooks at same stage`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onLifecycleStage GrainLifecycleStage.SetupState (fun _ct -> task { return () })
            onLifecycleStage GrainLifecycleStage.SetupState (fun _ct -> task { return () })
        }

    test <@ def.LifecycleHooks.[GrainLifecycleStage.SetupState] |> List.length = 2 @>

[<Fact>]
let ``grain CE registers hooks at different stages`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onLifecycleStage GrainLifecycleStage.First (fun _ct -> task { return () })
            onLifecycleStage GrainLifecycleStage.SetupState (fun _ct -> task { return () })
            onLifecycleStage GrainLifecycleStage.Activate (fun _ct -> task { return () })
        }

    test <@ def.LifecycleHooks |> Map.count = 3 @>

[<Fact>]
let ``grain CE default has no lifecycle hooks`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.LifecycleHooks |> Map.isEmpty @>

[<Fact>]
let ``grain CE lifecycle hook receives cancellation token`` () =
    task {
        let mutable receivedCt = CancellationToken.None
        let cts = new CancellationTokenSource()

        let def =
            grain {
                defaultState 0
                handle (fun state _msg -> task { return state, box state })

                onLifecycleStage GrainLifecycleStage.SetupState (fun ct ->
                    task {
                        receivedCt <- ct
                        return ()
                    })
            }

        let hooks = def.LifecycleHooks.[GrainLifecycleStage.SetupState]
        do! hooks.Head cts.Token
        test <@ receivedCt = cts.Token @>
        cts.Dispose()
    }

[<Fact>]
let ``grain CE lifecycle hooks execute in registration order`` () =
    task {
        let mutable order = []

        let def =
            grain {
                defaultState 0
                handle (fun state _msg -> task { return state, box state })

                onLifecycleStage GrainLifecycleStage.Activate (fun _ct ->
                    task {
                        order <- order @ [ "first" ]
                        return ()
                    })

                onLifecycleStage GrainLifecycleStage.Activate (fun _ct ->
                    task {
                        order <- order @ [ "second" ]
                        return ()
                    })
            }

        let hooks = def.LifecycleHooks.[GrainLifecycleStage.Activate]

        for hook in hooks do
            do! hook CancellationToken.None

        test <@ order = [ "first"; "second" ] @>
    }

[<Fact>]
let ``grain CE lifecycle hooks compose with other options`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            persist "Default"
            onActivate (fun state -> task { return state + 1 })
            onLifecycleStage GrainLifecycleStage.SetupState (fun _ct -> task { return () })
        }

    test <@ def.PersistenceName = Some "Default" @>
    test <@ def.OnActivate |> Option.isSome @>
    test <@ def.LifecycleHooks |> Map.containsKey GrainLifecycleStage.SetupState @>

[<Fact>]
let ``grain CE lifecycle hook with custom stage value`` () =
    let customStage = 5000

    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onLifecycleStage customStage (fun _ct -> task { return () })
        }

    test <@ def.LifecycleHooks |> Map.containsKey customStage @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``onLifecycleStage stores any positive int stage`` (stage: PositiveInt) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            onLifecycleStage stage.Get (fun _ct -> task { return () })
        }
    def.LifecycleHooks |> Map.containsKey stage.Get

[<Property>]
let ``two hooks at same stage produce list of length 2 for any stage`` (stage: PositiveInt) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            onLifecycleStage stage.Get (fun _ct -> task { return () })
            onLifecycleStage stage.Get (fun _ct -> task { return () })
        }
    def.LifecycleHooks.[stage.Get] |> List.length = 2

[<Property>]
let ``lifecycle hooks default to empty map for any grain definition`` (initial: int) =
    let def =
        grain {
            defaultState initial
            handle (fun state (_msg: string) -> task { return state, box state })
        }
    def.LifecycleHooks |> Map.isEmpty
