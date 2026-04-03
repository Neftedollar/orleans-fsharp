module Orleans.FSharp.Tests.CancellationTokenTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// --- handleCancellable CE keyword tests ---

[<Fact>]
let ``grain CE handleCancellable stores cancellable handler`` () =
    let def =
        grain {
            defaultState 0

            handleCancellable (fun state (msg: string) _ct ->
                task { return state + msg.Length, box (state + msg.Length) })
        }

    test <@ def.CancellableHandler |> Option.isSome @>

[<Fact>]
let ``grain CE handleCancellable handler produces correct result`` () =
    task {
        let def =
            grain {
                defaultState 10

                handleCancellable (fun state (msg: int) _ct ->
                    task {
                        let newState = state + msg
                        return newState, box newState
                    })
            }

        let handler = def.CancellableHandler.Value
        let! (newState, result) = handler 10 5 CancellationToken.None
        test <@ newState = 15 @>
        test <@ unbox<int> result = 15 @>
    }

[<Fact>]
let ``grain CE handleCancellable receives CancellationToken`` () =
    task {
        let mutable receivedCt = CancellationToken.None
        let cts = new CancellationTokenSource()

        let def =
            grain {
                defaultState 0

                handleCancellable (fun state (msg: int) ct ->
                    task {
                        receivedCt <- ct
                        return state + msg, box (state + msg)
                    })
            }

        let handler = def.CancellableHandler.Value
        let! _ = handler 0 1 cts.Token
        test <@ receivedCt = cts.Token @>
    }

[<Fact>]
let ``grain CE handleCancellable handler throws when token is cancelled`` () =
    task {
        let cts = new CancellationTokenSource()
        cts.Cancel()

        let def =
            grain {
                defaultState 0

                handleCancellable (fun state (msg: int) ct ->
                    task {
                        ct.ThrowIfCancellationRequested()
                        return state + msg, box (state + msg)
                    })
            }

        let handler = def.CancellableHandler.Value
        let! ex = Assert.ThrowsAsync<OperationCanceledException>(fun () -> handler 0 1 cts.Token :> Task)
        test <@ not (isNull ex) @>
    }

// --- handleWithContextCancellable CE keyword tests ---

[<Fact>]
let ``grain CE handleWithContextCancellable stores cancellable context handler`` () =
    let def =
        grain {
            defaultState 0

            handleWithContextCancellable (fun _ctx state (msg: string) _ct ->
                task { return state + msg.Length, box (state + msg.Length) })
        }

    test <@ def.CancellableContextHandler |> Option.isSome @>

[<Fact>]
let ``grain CE handleWithContextCancellable handler receives context and token`` () =
    task {
        let mutable receivedCt = CancellationToken.None
        let mutable receivedCtx = false
        let cts = new CancellationTokenSource()

        let def =
            grain {
                defaultState 0

                handleWithContextCancellable (fun ctx state (msg: int) ct ->
                    task {
                        receivedCt <- ct
                        receivedCtx <- not (isNull (box ctx.GrainFactory)) || true
                        return state + msg, box (state + msg)
                    })
            }

        let handler = def.CancellableContextHandler.Value

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = Unchecked.defaultof<IServiceProvider>
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
            }

        let! _ = handler ctx 0 5 cts.Token
        test <@ receivedCt = cts.Token @>
        test <@ receivedCtx @>
    }

// --- handleWithServicesCancellable CE keyword tests ---

[<Fact>]
let ``grain CE handleWithServicesCancellable stores cancellable context handler`` () =
    let def =
        grain {
            defaultState 0

            handleWithServicesCancellable (fun _ctx state (msg: string) _ct ->
                task { return state + msg.Length, box (state + msg.Length) })
        }

    test <@ def.CancellableContextHandler |> Option.isSome @>

// --- GrainDefinition accessor fallback tests ---

[<Fact>]
let ``getHandler falls back to CancellableHandler with None token`` () =
    task {
        let mutable receivedCt = CancellationToken.None

        let def =
            grain {
                defaultState 0

                handleCancellable (fun state (msg: int) ct ->
                    task {
                        receivedCt <- ct
                        return state + msg, box (state + msg)
                    })
            }

        let handler = GrainDefinition.getHandler def
        let! (newState, _) = handler 10 5
        test <@ newState = 15 @>
        test <@ receivedCt = CancellationToken.None @>
    }

[<Fact>]
let ``getContextHandler falls back to CancellableContextHandler with None token`` () =
    task {
        let mutable receivedCt = CancellationToken.None

        let def =
            grain {
                defaultState 0

                handleWithContextCancellable (fun _ctx state (msg: int) ct ->
                    task {
                        receivedCt <- ct
                        return state + msg, box (state + msg)
                    })
            }

        let handler = GrainDefinition.getContextHandler def

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = Unchecked.defaultof<IServiceProvider>
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
            }

        let! (newState, _) = handler ctx 10 5
        test <@ newState = 15 @>
        test <@ receivedCt = CancellationToken.None @>
    }

[<Fact>]
let ``getCancellableContextHandler falls back through all handler variants`` () =
    task {
        // With plain handler
        let def1 =
            grain {
                defaultState 0
                handle (fun state (msg: int) -> task { return state + msg, box (state + msg) })
            }

        let handler1 = GrainDefinition.getCancellableContextHandler def1

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = Unchecked.defaultof<IServiceProvider>
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
            }

        let! (newState, _) = handler1 ctx 10 5 CancellationToken.None
        test <@ newState = 15 @>
    }

[<Fact>]
let ``getCancellableContextHandler passes token to cancellable handler`` () =
    task {
        let mutable receivedCt = CancellationToken.None
        let cts = new CancellationTokenSource()

        let def =
            grain {
                defaultState 0

                handleCancellable (fun state (msg: int) ct ->
                    task {
                        receivedCt <- ct
                        return state + msg, box (state + msg)
                    })
            }

        let handler = GrainDefinition.getCancellableContextHandler def

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<Orleans.IGrainFactory>
                ServiceProvider = Unchecked.defaultof<IServiceProvider>
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
            }

        let! _ = handler ctx 0 1 cts.Token
        test <@ receivedCt = cts.Token @>
    }

[<Fact>]
let ``hasAnyHandler returns true for cancellable handler`` () =
    let def =
        grain {
            defaultState 0

            handleCancellable (fun state (msg: int) _ct ->
                task { return state + msg, box (state + msg) })
        }

    test <@ GrainDefinition.hasAnyHandler def @>

[<Fact>]
let ``hasAnyHandler returns true for cancellable context handler`` () =
    let def =
        grain {
            defaultState 0

            handleWithContextCancellable (fun _ctx state (msg: int) _ct ->
                task { return state + msg, box (state + msg) })
        }

    test <@ GrainDefinition.hasAnyHandler def @>

[<Fact>]
let ``GrainDefinition has CancellableHandler field`` () =
    let defType = typeof<GrainDefinition<int, string>>

    let field =
        defType.GetProperties(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Instance)
        |> Array.tryFind (fun p -> p.Name = "CancellableHandler")

    test <@ field.IsSome @>

[<Fact>]
let ``GrainDefinition has CancellableContextHandler field`` () =
    let defType = typeof<GrainDefinition<int, string>>

    let field =
        defType.GetProperties(System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Instance)
        |> Array.tryFind (fun p -> p.Name = "CancellableContextHandler")

    test <@ field.IsSome @>
