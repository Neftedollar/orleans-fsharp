module Orleans.FSharp.Tests.ErrorMessageTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck.Xunit
open Orleans.FSharp

/// <summary>Test DU state type for error message verification.</summary>
type MyState =
    | Active
    | Inactive

/// <summary>Test DU message type for error message verification.</summary>
type MyMsg = | DoStuff

// ── Helper: a GrainDefinition with all handlers stripped ─────────────────────

/// <summary>
/// Returns a well-typed grain definition that has no handlers registered.
/// Used to test the exceptions thrown by getContextHandler / getCancellableContextHandler.
/// We build a well-formed definition with a dummy handler, then erase it with copy-and-update
/// so all structural fields keep their correct defaults while leaving handlers empty.
/// </summary>
let private noHandlerDef<'S, 'M> (state: 'S) : GrainDefinition<'S, 'M> =
    // Build with a placeholder handler, then strip it — this gives us all defaults.
    let dummy: 'S -> 'M -> Task<'S * obj> = fun s _ -> Task.FromResult(s, box s)
    let def: GrainDefinition<'S, 'M> =
        grain {
            defaultState state
            handle dummy
        }
    { def with Handler = None }

// ── Missing handler at grain {} build time ────────────────────────────────────

[<Fact>]
let ``Missing handler error contains F# state type name`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("Int32") @>
    test <@ ex.Message.Contains("String") @>

[<Fact>]
let ``Missing handler error mentions handler custom operation`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState 0
            }
            |> ignore<GrainDefinition<int, string>>)

    test <@ ex.Message.Contains("handle") @>
    test <@ ex.Message.Contains("grain") @>

[<Fact>]
let ``Missing handler error for custom DU types contains DU type names`` () =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState Active
            }
            |> ignore<GrainDefinition<MyState, MyMsg>>)

    test <@ ex.Message.Contains("MyState") @>
    test <@ ex.Message.Contains("MyMsg") @>

// ── getHandler called on a context-only definition ────────────────────────────

[<Fact>]
let ``getHandler on context-only grain throws InvalidOperationException when invoked`` () =
    task {
        // handleWithContext registers only a ContextHandler, not a plain Handler.
        // getHandler returns a lambda that throws when called — the error message
        // tells the caller to use getContextHandler instead.
        let def =
            grain {
                defaultState 0
                handleWithContext (fun _ctx state (msg: int) ->
                    task { return state + msg, box (state + msg) })
            }

        let handler = GrainDefinition.getHandler def
        let! ex = Assert.ThrowsAsync<InvalidOperationException>(fun () -> handler 0 5 :> Task)
        test <@ ex.Message.Contains("context-aware") && ex.Message.Contains("GrainContext") @>
    }

[<Fact>]
let ``getContextHandler on definition with no handler throws immediately`` () =
    let emptyDef = noHandlerDef<int, int> 0
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            GrainDefinition.getContextHandler emptyDef |> ignore)

    test <@ ex.Message.Contains("Int32") @>
    test <@ ex.Message.Contains("handle") @>   // "handle" or "handleWithContext" both satisfy

[<Fact>]
let ``getCancellableContextHandler on empty definition throws immediately`` () =
    let emptyDef = noHandlerDef<string, bool> ""
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            GrainDefinition.getCancellableContextHandler emptyDef |> ignore)

    test <@ ex.Message.Contains("String") && ex.Message.Contains("Boolean") @>

// ── getHandler fallback chain ─────────────────────────────────────────────────

[<Fact>]
let ``getHandler falls back to CancellableHandler when no plain Handler`` () =
    task {
        let mutable ctReceived = CancellationToken.None
        let def =
            grain {
                defaultState 0
                handleCancellable (fun state (msg: int) ct ->
                    task {
                        ctReceived <- ct
                        return state + msg, box (state + msg)
                    })
            }

        let handler = GrainDefinition.getHandler def
        let! (ns, _) = handler 0 10
        test <@ ns = 10 @>
        test <@ ctReceived = CancellationToken.None @>
    }

[<Fact>]
let ``getContextHandler falls back to plain Handler ignoring context`` () =
    task {
        let def =
            grain {
                defaultState 0
                handle (fun state (msg: int) -> task { return state + msg, box (state + msg) })
            }

        let handler = GrainDefinition.getContextHandler def
        let ctx = Unchecked.defaultof<GrainContext>
        let! (ns, _) = handler ctx 0 7
        test <@ ns = 7 @>
    }

[<Fact>]
let ``getContextHandler on handleWithContext uses stored context handler`` () =
    task {
        let mutable ctxSeen: GrainContext option = None
        let def =
            grain {
                defaultState 0
                handleWithContext (fun ctx state (msg: int) ->
                    task {
                        ctxSeen <- Some ctx
                        return state + msg, box (state + msg)
                    })
            }

        let mockCtx: GrainContext = { GrainFactory = null; ServiceProvider = null; States = Map.empty; DeactivateOnIdle = None; DelayDeactivation = None; GrainId = None; PrimaryKey = None }
        let handler = GrainDefinition.getContextHandler def
        let! (ns, _) = handler mockCtx 0 3
        test <@ ns = 3 @>
        test <@ ctxSeen.IsSome @>
    }

// ── CancellableContextHandler-only: getHandler throws ────────────────────────

[<Fact>]
let ``getHandler on CancellableContextHandler-only grain throws when invoked`` () =
    task {
        let def =
            grain {
                defaultState 0
                handleWithContextCancellable (fun _ctx state (msg: int) _ct ->
                    task { return state + msg, box (state + msg) })
            }

        let handler = GrainDefinition.getHandler def
        let! ex = Assert.ThrowsAsync<InvalidOperationException>(fun () -> handler 0 5 :> Task)
        test <@ ex.Message.Contains("context-aware") || ex.Message.Contains("cancellable") @>
    }

// ── getCancellableContextHandler falls back to plain handle ───────────────────

[<Fact>]
let ``getCancellableContextHandler falls back from plain handle, discarding context and token`` () =
    task {
        // The fallback chain: CancellableContextHandler > CancellableHandler > ContextHandler > Handler.
        // A grain with only a plain 'handle' should be reachable via getCancellableContextHandler.
        let def =
            grain {
                defaultState 0
                handle (fun state (msg: int) -> task { return state + msg, box (state + msg) })
            }

        let handler = GrainDefinition.getCancellableContextHandler def
        let ctx = Unchecked.defaultof<GrainContext>
        use cts = new System.Threading.CancellationTokenSource()
        let! (ns, _) = handler ctx 0 9 cts.Token
        test <@ ns = 9 @>
    }

[<Fact>]
let ``getCancellableContextHandler falls back from ContextHandler, discarding token`` () =
    task {
        let def =
            grain {
                defaultState 0
                handleWithContext (fun _ctx state (msg: int) ->
                    task { return state * msg, box (state * msg) })
            }

        let handler = GrainDefinition.getCancellableContextHandler def
        let ctx = Unchecked.defaultof<GrainContext>
        let! (ns, _) = handler ctx 3 4 System.Threading.CancellationToken.None
        test <@ ns = 12 @>
    }

// ── Error message FsCheck properties ─────────────────────────────────────────

[<Property>]
let ``Missing handler error always contains state and message type names`` (_seed: int) =
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            grain {
                defaultState _seed
            }
            |> ignore<GrainDefinition<int, string>>)
    ex.Message.Contains("Int32") && ex.Message.Contains("String")

[<Property>]
let ``getContextHandler on empty def error always contains type names`` (_seed: int) =
    let emptyDef = noHandlerDef<int, bool> _seed
    let ex =
        Assert.Throws<InvalidOperationException>(fun () ->
            GrainDefinition.getContextHandler emptyDef |> ignore)
    ex.Message.Contains("Int32") && ex.Message.Contains("Boolean")
