/// <summary>
/// Observer contracts, the notify-direction protocol token, and the boundary that keeps an
/// observer handle out of durable storage.
/// </summary>
module Orleans.FSharp.Tests.FunctionalObserverContractTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Tests.FunctionalTransportHarness

type RoomObserver = private RoomObserver of unit
type TickObserver = private TickObserver of unit

type ChatMessage = { author: string; text: string }

[<NoEquality; NoComparison>]
type RoomObserverApi =
    { onMessage: ChatMessage -> Task<unit>
      onClosed: string -> Task<unit> }

[<NoEquality; NoComparison>]
type ReplyingObserverApi = { ask: string -> Task<int> }

let private rejects (body: unit -> unit) =
    Assert.Throws<InvalidOperationException>(Action body)

// ──────────────────────────────────────────────────────────────────────────────
// Sealing
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``an observer contract seals its observer type and version`` () =
    let contract =
        observerContract<RoomObserver, RoomObserverApi> () {
            observerType "chat.room.observer"
            version 3
        }

    test <@ contract.ObserverTypeName = "chat.room.observer" @>
    test <@ contract.Version = 3 @>

[<Fact>]
let ``an observer type is derived on exactly the terms a grain type is`` () =
    // A brand declared in an F# module is a CLR-NESTED type, so its name is not a simple name
    // and cannot be derived from — the same rule, and the same diagnostic shape, grainContract
    // applies to an actor brand. Deriving happens in a namespace; this file is a module.
    let error =
        rejects (fun () -> observerContract<TickObserver, RoomObserverApi> () { version 1 } |> ignore)

    test <@ error.Message.Contains "is a nested type" @>
    test <@ error.Message.Contains "Declare an explicit 'observerType'" @>

[<Fact>]
let ``an observer version defaults to one`` () =
    let contract = observerContract<RoomObserver, RoomObserverApi> () { observerType "chat.room.observer" }

    test <@ contract.Version = 1 @>

[<Fact>]
let ``a push operation that returns data fails contract construction`` () =
    // The one rule that is observer-specific: everything else is the grain API-shape rule.
    let error =
        rejects (fun () ->
            observerContract<RoomObserver, ReplyingObserverApi> () { observerType "chat.asking.observer" }
            |> ignore)

    test <@ error.Message.Contains "an observer never returns data" @>
    test <@ error.Message.Contains "'Msg -> Task<unit>" @>

[<Fact>]
let ``a blank or repeated observer type is rejected`` () =
    let blank =
        rejects (fun () -> observerContract<RoomObserver, RoomObserverApi> () { observerType "  " } |> ignore)

    test <@ blank.Message.Contains "'observerType' requires a non-blank value" @>

    let repeated =
        rejects (fun () ->
            observerContract<RoomObserver, RoomObserverApi> () {
                observerType "one"
                observerType "two"
            }
            |> ignore)

    test <@ repeated.Message.Contains "already set" @>

// ──────────────────────────────────────────────────────────────────────────────
// The notify direction
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a notify token cannot collide with a request or reply token`` () =
    // The collision the specification has to rule out: an observer type and a grain type sharing
    // a name, a version, and an operation ID. The direction is part of the hashed preimage, so
    // the three digests differ — a collision would need a SHA-256 preimage collision.
    let notify = ProtocolToken.notify "chat.room" 1 "say"
    let request = ProtocolToken.request "chat.room" 1 "say"
    let reply = ProtocolToken.reply "chat.room" 1 "say"

    test <@ notify.Length = ProtocolToken.Length @>
    test <@ not (ProtocolToken.equal notify request) @>
    test <@ not (ProtocolToken.equal notify reply) @>
    test <@ not (ProtocolToken.equal request reply) @>

[<Fact>]
let ``a notify token is stable and separates observer type, version, and operation`` () =
    test <@ ProtocolToken.equal (ProtocolToken.notify "o" 1 "a") (ProtocolToken.notify "o" 1 "a") @>
    test <@ not (ProtocolToken.equal (ProtocolToken.notify "o" 1 "a") (ProtocolToken.notify "p" 1 "a")) @>
    test <@ not (ProtocolToken.equal (ProtocolToken.notify "o" 1 "a") (ProtocolToken.notify "o" 2 "a")) @>
    test <@ not (ProtocolToken.equal (ProtocolToken.notify "o" 1 "a") (ProtocolToken.notify "o" 1 "b")) @>

// ──────────────────────────────────────────────────────────────────────────────
// Durable storage never sees a handle
// ──────────────────────────────────────────────────────────────────────────────

type private StateWithHandle =
    { subscribers: FunctionalObserverHandle<RoomObserver, RoomObserverApi> list }

[<Fact>]
let ``a state record carrying observer handles cannot be serialized`` () =
    // Storage for a functional grain goes through the F# binary codec, and the ONLY thing that
    // keeps a live object reference out of a durable record is that the codec refuses it. It has
    // to be a refusal, not a silently empty write: an empty subscriber list restored from storage
    // would look like a working grain that has quietly stopped pushing.
    //
    // The refusal comes from the observer REFERENCE, one level below the handle. `isSupportedType`
    // is a structural predicate and claims the handle class the same way it claims a grain
    // contract or an API record, so asserting on it would prove nothing about storage.
    let error =
        rejects (fun () ->
            FSharpBinaryFormat.serialize (box { subscribers = [] }) typeof<StateWithHandle> |> ignore)

    test <@ error.Message.Contains "unsupported type" @>
    test <@ error.Message.Contains "IFunctionalObserverTarget" @>

[<Fact>]
let ``an observer contract cannot be serialized`` () =
    // Contracts are configuration, never wire data — the same guarantee, and the same mechanism,
    // grain contracts have.
    let contract = observerContract<RoomObserver, RoomObserverApi> () { observerType "chat.room.observer" }

    let error =
        rejects (fun () -> FSharpBinaryFormat.serialize (box contract) (contract.GetType()) |> ignore)

    test <@ error.Message.Contains "unsupported type" @>

    let handlers =
        rejects (fun () ->
            FSharpBinaryFormat.serialize
                (box { onMessage = (fun _ -> Task.FromResult()); onClosed = (fun _ -> Task.FromResult()) })
                typeof<RoomObserverApi>
            |> ignore)

    test <@ handlers.Message.Contains "unsupported type" @>

// ──────────────────────────────────────────────────────────────────────────────
// Hot path
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Records every dispatched envelope; never fails a send.</summary>
type private RecordingTarget() =
    let received = ConcurrentQueue<FunctionalNotificationEnvelope>()

    member _.Received = received.ToArray()

    interface IFunctionalObserverTarget with
        member _.DispatchAsync(envelope: FunctionalNotificationEnvelope) : Task =
            received.Enqueue envelope
            Task.CompletedTask

/// <summary>
/// A handle constructed directly over a fake target, bypassing FunctionalObserver.create /
/// createFrom (which need a real Orleans grain factory) — exactly the "hand-built" level these
/// tests exercise.
/// </summary>
let private handleOver<'Brand, 'Api>
    (services: IServiceProvider)
    (observerType: string)
    (version: int)
    (target: IFunctionalObserverTarget)
    : FunctionalObserverHandle<'Brand, 'Api> =
    FunctionalObserverHandle<'Brand, 'Api>(observerType, version, target, payloadCodec services, null)

[<Fact>]
let ``a notifier-based push performs no reflection, selector evaluation, or generic closing`` () =
    let services = buildServices true None
    let target = RecordingTarget()

    let handle: FunctionalObserverHandle<RoomObserver, RoomObserverApi> =
        handleOver services "chat.room.observer" 1 target

    let push = FunctionalObserver.notifier handle (_.onMessage)

    task {
        // Warm the payload codec first — codec build and its own generic closing are a separate
        // promise from the one this test is about.
        do! push { author = "warm"; text = "warm" }

        let counters = FunctionalInstrumentation.start ()

        try
            do! push { author = "alice"; text = "hi" }
            do! push { author = "alice"; text = "again" }

            test <@ counters.ApiShapeBuilds = 0 @>
            test <@ counters.SelectorEvaluations = 0 @>
            test <@ counters.GenericClosings = 0 @>
        finally
            FunctionalInstrumentation.stop ()

        test <@ target.Received.Length = 3 @>
    }

[<Fact>]
let ``resolving the push closure is where the selector is evaluated`` () =
    // The counterpart to the test above: notifier itself DOES evaluate the selector, exactly
    // once, at resolution time. The zero counts above are a fact about the returned closure, not
    // about notifier never touching the selector at all.
    let services = buildServices true None
    let target = RecordingTarget()

    let handle: FunctionalObserverHandle<RoomObserver, RoomObserverApi> =
        handleOver services "chat.room.observer" 1 target

    let counters = FunctionalInstrumentation.start ()

    try
        FunctionalObserver.notifier handle (_.onMessage) |> ignore
        test <@ counters.SelectorEvaluations = 1 @>
    finally
        FunctionalInstrumentation.stop ()

[<Fact>]
let ``notify resolves its selector on every call, unlike notifier`` () =
    let services = buildServices true None
    let target = RecordingTarget()

    let handle: FunctionalObserverHandle<RoomObserver, RoomObserverApi> =
        handleOver services "chat.room.observer" 1 target

    task {
        do! FunctionalObserver.notify handle (_.onMessage) { author = "warm"; text = "warm" }

        let counters = FunctionalInstrumentation.start ()

        try
            do! FunctionalObserver.notify handle (_.onMessage) { author = "a"; text = "a" }
            do! FunctionalObserver.notify handle (_.onMessage) { author = "b"; text = "b" }

            test <@ counters.SelectorEvaluations = 2 @>
        finally
            FunctionalInstrumentation.stop ()
    }

[<Fact>]
let ``the manager's fan-out resolves its selector once per Notify call, not once per subscriber`` () =
    let services = buildServices true None
    let manager = FunctionalObserverManager<RoomObserver, RoomObserverApi>(TimeSpan.FromMinutes 5.0)

    let targets = [ for _ in 1..5 -> RecordingTarget() ]

    for target in targets do
        manager.Subscribe(handleOver services "chat.room.observer" 1 target)

    task {
        // Warm the payload codec first.
        do! manager.Notify (_.onMessage) { author = "warm"; text = "warm" }

        let counters = FunctionalInstrumentation.start ()

        try
            do! manager.Notify (_.onMessage) { author = "alice"; text = "hi" }
            test <@ counters.SelectorEvaluations = 1 @>
        finally
            FunctionalInstrumentation.stop ()

        test <@ targets |> List.forall (fun target -> target.Received.Length = 2) @>
    }

[<Fact>]
let ``a bad selector on Notify fails loudly and leaves every subscription untouched`` () =
    // Resolving the field OUTSIDE the fan-out loop (the change above) has a second, independent
    // benefit: before it, a selector that failed to resolve was caught by the SAME per-subscriber
    // "dead reference" handler that removes an observer whose object reference is gone -- so a
    // bad selector would have silently emptied the whole subscriber set instead of failing
    // loudly. This pins the improvement.
    let services = buildServices true None
    let manager = FunctionalObserverManager<RoomObserver, RoomObserverApi>(TimeSpan.FromMinutes 5.0)
    let targets = [ for _ in 1..3 -> RecordingTarget() ]

    for target in targets do
        manager.Subscribe(handleOver services "chat.room.observer" 1 target)

    // A selector that never returns one of the probe's own field sentinels.
    let badSelector: OperationSelector<RoomObserverApi, ChatMessage, unit> =
        fun _api -> fun (_msg: ChatMessage) -> Task.FromResult(())

    task {
        let! _error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                manager.Notify badSelector { author = "x"; text = "x" } :> Task)

        test <@ manager.Count = 3 @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Payload limits: the two notification boundaries
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``an oversized notify is rejected at the caller notify send boundary before anything is dispatched`` () =
    let services = buildServices true (Some 64)
    let target = RecordingTarget()

    let handle: FunctionalObserverHandle<RoomObserver, RoomObserverApi> =
        handleOver services "chat.room.observer" 1 target

    let push = FunctionalObserver.notifier handle (_.onMessage)

    task {
        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                push { author = String('x', 4096); text = "" } :> Task)

        test <@ error.Message.Contains "caller notify send" @>
        test <@ error.Message.Contains "notify" @>
        test <@ error.Message.Contains "chat.room.observer" @>
        test <@ error.Message.Contains "onMessage" @>
        test <@ error.Message.Contains "64" @>
        test <@ target.Received = [||] @>
    }

[<Fact>]
let ``an oversized hand-built envelope is rejected at the observer receive boundary before the handler runs`` () =
    let contract =
        observerContract<RoomObserver, RoomObserverApi> () { observerType "chat.room.observer" }

    let mutable handlerRan = false

    let handlers: RoomObserverApi =
        { onMessage = fun _ -> task { handlerRan <- true }
          onClosed = fun _ -> task { handlerRan <- true } }

    let services = buildServices true None
    let codec = payloadCodec services :> IFunctionalPayloadCodec

    let target =
        FunctionalObserverObject<RoomObserver, RoomObserverApi>(contract, handlers, codec, 8, (fun _ -> ()))
        :> IFunctionalObserverTarget

    let oversizedPayload: byte[] = Array.zeroCreate 4096
    let token = ProtocolToken.notify "chat.room.observer" 1 "onMessage"

    let envelope =
        FunctionalNotificationEnvelope("chat.room.observer", 1, "onMessage", token, oversizedPayload)

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> target.DispatchAsync envelope |> ignore)

    test <@ error.Message.Contains "observer receive" @>
    test <@ error.Message.Contains "onMessage" @>
    test <@ error.Message.Contains "8" @>
    test <@ not handlerRan @>

[<Fact>]
let ``an oversized Notify call fails once and leaves every subscription untouched`` () =
    // The manager's field resolution hoists out of the fan-out loop for the selector; the
    // payload-limit check hoists the SAME way, for the SAME reason: the per-subscriber loop
    // catches a failed send and treats it as a dead reference, so a per-subscriber size check
    // would let one oversized broadcast silently empty the whole subscriber set instead of
    // failing loudly, once, up front.
    let services = buildServices true (Some 64)
    let manager = FunctionalObserverManager<RoomObserver, RoomObserverApi>(TimeSpan.FromMinutes 5.0)
    let targets = [ for _ in 1..3 -> RecordingTarget() ]

    for target in targets do
        manager.Subscribe(handleOver services "chat.room.observer" 1 target)

    task {
        test <@ manager.Count = 3 @>

        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                manager.Notify (_.onMessage) { author = String('x', 4096); text = "" } :> Task)

        test <@ error.Message.Contains "caller notify send" @>
        test <@ manager.Count = 3 @>
        test <@ targets |> List.forall (fun target -> target.Received.Length = 0) @>
    }

[<Fact>]
let ``a normal-size push is unaffected by the observer payload limit`` () =
    let services = buildServices true (Some 4096)
    let target = RecordingTarget()

    let handle: FunctionalObserverHandle<RoomObserver, RoomObserverApi> =
        handleOver services "chat.room.observer" 1 target

    task {
        do! FunctionalObserver.notify handle (_.onMessage) { author = "alice"; text = "hi" }
        test <@ target.Received.Length = 1 @>
    }
