/// <summary>
/// Functional observers end to end on a real TestingHost cluster: a client-hosted handler record
/// receives pushes from a functional grain, with no application code generation on either side.
/// </summary>
/// <remarks>
/// The classic path in <c>FunctionalObserverIntegrationTests</c> needs an observer interface
/// declared in a C#-compiled assembly, because Orleans' proxy generators are Roslyn generators
/// and never run over F#. Functional observers close that gap the same way the functional runtime
/// closed it for grains: ONE C#-declared interface in the library serves every application
/// observer, and everything above it is an ordinary F# record.
/// </remarks>
module Orleans.FSharp.Integration.FunctionalPushIntegrationTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Swensen.Unquote
open Xunit

// ── The observer: a brand and a handler record, exactly like a grain contract ─

type RoomObserver = private RoomObserver of unit

type ChatMessage = { author: string; text: string }

[<NoEquality; NoComparison>]
type RoomObserverApi =
    { onMessage: ChatMessage -> Task<unit>
      onClosed: string -> Task<unit> }

let roomObserverContract =
    observerContract<RoomObserver, RoomObserverApi> () { observerType "push.room.observer" }

// ── The functional grain that pushes ─────────────────────────────────────────

type PushRoomActor = private PushRoomActor of unit

type PushHandle = FunctionalObserverHandle<RoomObserver, RoomObserverApi>

[<NoEquality; NoComparison>]
type PushRoomApi =
    { subscribe: PushHandle -> Task<int>
      subscribeTupled: (PushHandle * string) -> Task<string>
      drop: PushHandle -> Task<int>
      say: ChatMessage -> Task<int>
      close: string -> Task<int>
      liveCount: unit -> Task<int> }

type private PushRoomState =
    { observers: FunctionalObserverManager<RoomObserver, RoomObserverApi> }

/// <summary>The manager expiry the fixture's grains use; short, so expiry is observable.</summary>
let private managerExpiry = TimeSpan.FromSeconds 2.0

let pushRoomContract =
    grainContract<PushRoomActor, string, PushRoomApi> () {
        grainType "push.room"
        version 1
        stringKey
    }

let pushRoomRef = FunctionalGrain.ref pushRoomContract

module private PushRoomDefinition =
    let definition =
        grainFor pushRoomContract {
            defaultState (fun () ->
                { observers = FunctionalObserverManager<RoomObserver, RoomObserverApi> managerExpiry })

            handle (_.subscribe) (fun _ state (handle: PushHandle) ->
                task {
                    state.observers.Subscribe handle
                    return state, state.observers.Count
                })

            handle (_.subscribeTupled) (fun _ state ((handle, label): PushHandle * string) ->
                task {
                    state.observers.Subscribe handle
                    return state, $"{label}:{handle.ObserverType}"
                })

            handle (_.drop) (fun _ state (handle: PushHandle) ->
                task {
                    state.observers.Unsubscribe handle |> ignore
                    return state, state.observers.Count
                })

            handle (_.say) (fun _ state (message: ChatMessage) ->
                task {
                    let notified = state.observers.Count
                    do! state.observers.Notify (_.onMessage) message
                    return state, notified
                })

            handle (_.close) (fun _ state (reason: string) ->
                task {
                    let notified = state.observers.Count
                    do! state.observers.Notify (_.onClosed) reason
                    return state, notified
                })

            handle (_.liveCount) (fun _ state () -> task { return state, state.observers.Count })
        }

// ── Cluster ──────────────────────────────────────────────────────────────────

type private PushSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain PushRoomDefinition.definition |> ignore

type private PushClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type FunctionalPushClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<PushSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<PushClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Client = cluster.Client

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

// ── A recording client-side handler record ───────────────────────────────────

type private Recorder() =
    let messages = ConcurrentQueue<string>()
    let gate = new SemaphoreSlim(0)

    member _.Messages = messages |> Seq.toList

    member _.Record(text: string) =
        messages.Enqueue text
        gate.Release() |> ignore

    member _.WaitFor(count: int, timeoutMs: int) : Task<bool> =
        task {
            let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
            let mutable ok = messages.Count >= count

            while not ok && DateTime.UtcNow < deadline do
                let remaining = max 0 (int (deadline - DateTime.UtcNow).TotalMilliseconds)
                let! signalled = gate.WaitAsync remaining
                if signalled then ok <- messages.Count >= count

            return ok
        }

let private recordingApi (recorder: Recorder) =
    { onMessage = fun message -> task { recorder.Record $"{message.author}: {message.text}" }
      onClosed = fun reason -> task { recorder.Record $"closed:{reason}" } }

// ── Tests ────────────────────────────────────────────────────────────────────

type FunctionalPushTests(fixture: FunctionalPushClusterFixture) =

    interface IClassFixture<FunctionalPushClusterFixture>

    [<Fact>]
    member _.``a client-hosted observer receives pushes from a functional grain``() =
        task {
            let room = pushRoomRef fixture.Client "push-basic"
            let recorder = Recorder()
            let handle = FunctionalObserver.create roomObserverContract fixture.Client (recordingApi recorder)

            try
                let! subscribed = room.subscribe handle
                test <@ subscribed = 1 @>

                let! notified = room.say { author = "alice"; text = "hello" }
                test <@ notified = 1 @>

                let! arrived = recorder.WaitFor(1, 5000)
                test <@ arrived @>
                test <@ recorder.Messages = [ "alice: hello" ] @>

                // A second push operation on the same handle: the operation ID selects it.
                let! closed = room.close "bye"
                test <@ closed = 1 @>

                let! both = recorder.WaitFor(2, 5000)
                test <@ both @>
                test <@ recorder.Messages = [ "alice: hello"; "closed:bye" ] @>
            finally
                FunctionalObserver.unsubscribe fixture.Client handle
        }

    [<Fact>]
    member _.``a handle travels as a tuple element``() =
        task {
            // Orleans owns System.Tuple and routes each element to its own codec, so a handle is
            // an ordinary tuple slot. An F# record field would NOT be — the F# binary codec owns
            // records whole and has no codec for an Orleans object reference.
            let room = pushRoomRef fixture.Client "push-tupled"
            let recorder = Recorder()
            let handle = FunctionalObserver.create roomObserverContract fixture.Client (recordingApi recorder)

            try
                let! echoed = room.subscribeTupled (handle, "room-42")
                test <@ echoed = "room-42:push.room.observer" @>

                let! notified = room.say { author = "bob"; text = "tupled" }
                test <@ notified = 1 @>

                let! arrived = recorder.WaitFor(1, 5000)
                test <@ arrived @>
            finally
                FunctionalObserver.unsubscribe fixture.Client handle
        }

    [<Fact>]
    member _.``dropping a handle stops delivery``() =
        task {
            let room = pushRoomRef fixture.Client "push-drop"
            let recorder = Recorder()
            let handle = FunctionalObserver.create roomObserverContract fixture.Client (recordingApi recorder)

            try
                let! subscribed = room.subscribe handle
                test <@ subscribed = 1 @>

                let! remaining = room.drop handle
                test <@ remaining = 0 @>

                let! notified = room.say { author = "carol"; text = "unheard" }
                test <@ notified = 0 @>

                do! Task.Delay 500
                test <@ recorder.Messages = [] @>
            finally
                FunctionalObserver.unsubscribe fixture.Client handle
        }

    [<Fact>]
    member _.``a throwing observer is contained and never fails the notifying handler``() =
        task {
            // Best-effort delivery: the observer's own failure is logged on its side and the
            // notifying grain's handler completes normally, exactly as Orleans' own observers do.
            let room = pushRoomRef fixture.Client "push-throwing"
            let recorder = Recorder()

            let throwing =
                { onMessage = fun _ -> task { return failwith "the observer refuses this message" }
                  onClosed = fun reason -> task { recorder.Record $"closed:{reason}" } }

            let handle = FunctionalObserver.create roomObserverContract fixture.Client throwing

            try
                let! subscribed = room.subscribe handle
                test <@ subscribed = 1 @>

                // The handler returns normally even though every observer threw.
                let! notified = room.say { author = "dave"; text = "boom" }
                test <@ notified = 1 @>

                // …and the subscription survives: a throwing handler is not a dead observer.
                let! stillLive = room.liveCount ()
                test <@ stillLive = 1 @>

                let! closed = room.close "after-throw"
                test <@ closed = 1 @>

                let! arrived = recorder.WaitFor(1, 5000)
                test <@ arrived @>
                test <@ recorder.Messages = [ "closed:after-throw" ] @>
            finally
                FunctionalObserver.unsubscribe fixture.Client handle
        }

    [<Fact>]
    member _.``an unrefreshed subscription expires from the manager``() =
        task {
            let room = pushRoomRef fixture.Client "push-expiry"
            let recorder = Recorder()
            let handle = FunctionalObserver.create roomObserverContract fixture.Client (recordingApi recorder)

            try
                let! subscribed = room.subscribe handle
                test <@ subscribed = 1 @>

                // Past the manager's expiry with no refresh, the subscription is swept.
                do! Task.Delay(managerExpiry + TimeSpan.FromMilliseconds 750.0)

                let! expired = room.liveCount ()
                test <@ expired = 0 @>

                let! notified = room.say { author = "erin"; text = "too late" }
                test <@ notified = 0 @>

                do! Task.Delay 500
                test <@ recorder.Messages = [] @>

                // Re-subscribing refreshes it, so expiry is a liveness window and not a one-shot.
                let! resubscribed = room.subscribe handle
                test <@ resubscribed = 1 @>

                let! delivered = room.say { author = "erin"; text = "in time" }
                test <@ delivered = 1 @>

                let! arrived = recorder.WaitFor(1, 5000)
                test <@ arrived @>
            finally
                FunctionalObserver.unsubscribe fixture.Client handle
        }

    [<Fact>]
    member _.``releasing the object reference stops delivery at the client``() =
        task {
            let room = pushRoomRef fixture.Client "push-unsubscribe"
            let recorder = Recorder()
            let handle = FunctionalObserver.create roomObserverContract fixture.Client (recordingApi recorder)

            let! subscribed = room.subscribe handle
            test <@ subscribed = 1 @>

            let! notified = room.say { author = "frank"; text = "first" }
            test <@ notified = 1 @>

            let! arrived = recorder.WaitFor(1, 5000)
            test <@ arrived @>

            // The grain still holds the handle; the OBJECT REFERENCE behind it is gone.
            FunctionalObserver.unsubscribe fixture.Client handle

            let! afterRelease = room.say { author = "frank"; text = "second" }
            test <@ afterRelease = 1 @>

            do! Task.Delay 1000

            // Delivery stopped, and the grain's handler was not failed by the dead reference.
            test <@ recorder.Messages = [ "frank: first" ] @>
        }
