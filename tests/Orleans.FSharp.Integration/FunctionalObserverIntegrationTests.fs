/// <summary>
/// Task 8 fix round: proves — end to end, on a real TestingHost cluster — that grain observers
/// (<c>Observer.createRef</c> + <c>FSharpObserverManager</c>) work inside the FUNCTIONAL grain
/// runtime, i.e. that pub/sub notification is NOT a capability gap of <c>grainContract</c> /
/// <c>grainFor</c>.
/// </summary>
/// <remarks>
/// The reason this needed proving rather than asserting: an observer reference crosses the
/// functional transport as an ordinary operation argument, so it has to clear
/// <c>SerializerPreflight</c> (an Orleans codec must resolve for the declared argument type) and
/// then survive round-tripping as a live callback target. Neither is implied by "observers are
/// not on the deprecation list".
/// <para>
/// The observer interface (<c>ITestChatObserver</c>) is the existing C#-declared one from
/// <c>Orleans.FSharp.CodeGen</c>. That is a constraint of Orleans' source generators (which run
/// over C#, not F#), identical for the <c>grain { }</c> CE and for class grains — not something
/// the functional runtime introduces.
/// </para>
/// </remarks>
module Orleans.FSharp.Integration.FunctionalObserverIntegrationTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.CodeGen
open Swensen.Unquote
open Xunit

// ── The functional grain under test ──────────────────────────────────────────

type ObserverRoomActor = private ObserverRoomActor of unit

[<NoEquality; NoComparison>]
type ObserverRoomApi =
    {
        /// Subscribes an observer reference; replies with the subscriber count.
        subscribe: ITestChatObserver -> Task<int>
        /// Unsubscribes an observer reference; replies with the subscriber count.
        unsubscribe: ITestChatObserver -> Task<int>
        /// Notifies every subscriber; replies with the number notified.
        broadcast: string -> Task<int>
        /// Current subscriber count.
        subscriberCount: unit -> Task<int>
    }

type private ObserverRoomState =
    { manager: FSharpObserverManager<ITestChatObserver> }

module ObserverRoomContract =
    let contract =
        grainContract<ObserverRoomActor, string, ObserverRoomApi> {
            grainType "functional.observer.room"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

module private ObserverRoomDefinition =
    let definition =
        grainFor ObserverRoomContract.contract {
            defaultState (fun () ->
                { manager = FSharpObserverManager<ITestChatObserver>(TimeSpan.FromMinutes 5.0) })

            handle
                (_.subscribe)
                (fun _context state (observer: ITestChatObserver) ->
                    task {
                        state.manager.Subscribe observer
                        return state, state.manager.Count
                    })

            handle
                (_.unsubscribe)
                (fun _context state (observer: ITestChatObserver) ->
                    task {
                        state.manager.Unsubscribe observer
                        return state, state.manager.Count
                    })

            handle
                (_.broadcast)
                (fun _context state (text: string) ->
                    task {
                        let notified = state.manager.Count
                        do! state.manager.Notify(fun observer -> task { do! observer.ReceiveMessage text })
                        return state, notified
                    })

            handle (_.subscriberCount) (fun _context state () -> task { return state, state.manager.Count })
        }

// ── Cluster ──────────────────────────────────────────────────────────────────

type private FunctionalObserverSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain ObserverRoomDefinition.definition |> ignore

type private FunctionalObserverClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type FunctionalObserverClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<FunctionalObserverSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<FunctionalObserverClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Client = cluster.Client

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

// ── Client-side observer ─────────────────────────────────────────────────────

type private RecordingObserver() =
    let received = ConcurrentBag<string>()
    let gate = new SemaphoreSlim(0)

    member _.Messages = received |> Seq.toList

    member _.WaitFor(n: int, timeoutMs: int) : Task<bool> =
        task {
            let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
            let mutable ok = received.Count >= n

            while not ok && DateTime.UtcNow < deadline do
                let remaining = max 0 (int (deadline - DateTime.UtcNow).TotalMilliseconds)
                let! signalled = gate.WaitAsync remaining
                if signalled then ok <- received.Count >= n

            return ok
        }

    interface ITestChatObserver with
        member _.ReceiveMessage(msg: string) =
            received.Add msg
            gate.Release() |> ignore
            Task.CompletedTask

// ── Tests ────────────────────────────────────────────────────────────────────

type FunctionalObserverTests(fixture: FunctionalObserverClusterFixture) =

    interface IClassFixture<FunctionalObserverClusterFixture>

    [<Fact>]
    member _.``a functional grain accepts an observer reference and notifies it``() =
        task {
            let api = ObserverRoomContract.ref fixture.Client "observer-room-notify"
            let observer = RecordingObserver()
            let observerRef = Observer.createRef<ITestChatObserver> fixture.Client observer

            try
                let! subscribed = api.subscribe observerRef
                test <@ subscribed = 1 @>

                let! notified = api.broadcast "hello from a functional grain"
                test <@ notified = 1 @>

                let! arrived = observer.WaitFor(1, 5000)
                test <@ arrived @>
                test <@ observer.Messages = [ "hello from a functional grain" ] @>
            finally
                Observer.deleteRef<ITestChatObserver> fixture.Client observerRef
        }

    [<Fact>]
    member _.``unsubscribing through a functional grain stops notifications``() =
        task {
            let api = ObserverRoomContract.ref fixture.Client "observer-room-unsubscribe"
            let observer = RecordingObserver()
            let observerRef = Observer.createRef<ITestChatObserver> fixture.Client observer

            try
                let! subscribed = api.subscribe observerRef
                test <@ subscribed = 1 @>

                let! remaining = api.unsubscribe observerRef
                test <@ remaining = 0 @>

                let! count = api.subscriberCount ()
                test <@ count = 0 @>

                let! notified = api.broadcast "nobody should hear this"
                test <@ notified = 0 @>
                test <@ observer.Messages = [] @>
            finally
                Observer.deleteRef<ITestChatObserver> fixture.Client observerRef
        }
