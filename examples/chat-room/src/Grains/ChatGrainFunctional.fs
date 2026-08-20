/// <summary>
/// Functional-runtime equivalent of <c>ChatGrainDef.chat</c> in <c>ChatGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Full room domain matching the
/// spec's own chat-room shape (docs/functional-grains.md "Overview" /
/// <c>src/Orleans.FSharp.Sample/ChatRoomFunctional.fs</c>): membership (<c>join</c>/<c>leave</c>),
/// <c>say</c> with membership validation returning a typed <c>Result</c>, a <c>readOnly</c>
/// <c>history</c> query, a <c>oneWay</c> + <c>alwaysInterleave</c> <c>typing</c> indicator, and
/// explicit <c>stateFrom</c> persistence.
///
/// Pub/sub: push notification IS live here, through FUNCTIONAL OBSERVERS (see
/// <c>RoomObserverApi</c> below and the run transcript in the README). Everything under this
/// paragraph is the story of the CLASSIC observer path, which is still walled -- it is kept
/// because the wall is real and because it is exactly what functional observers route around.
///
/// The old grain's headline feature is push notification via <c>FSharpObserverManager&lt;IChatObserver&gt;</c>
/// -- observers are on the KEEP list, not deprecated, and are grain-model agnostic in principle
/// (docs/functional-grains.md, "Observers, streams, and the other orthogonal surfaces"). This file
/// was first written *with* <c>subscribe</c>/<c>unsubscribe</c>/a notifying <c>say</c> holding
/// <c>FSharpObserverManager&lt;IChatObserver&gt;</c> in state, then run standalone to see what
/// actually happens -- not left as a compile-only guess. Result, from a real run against this
/// example's own Program.fs:
///
/// <c>Observer.createRef&lt;IChatObserver&gt;</c> -- the *client-side* call that turns a local
/// object into an Orleans-addressable reference, before any grain (old or functional) is even
/// involved -- throws immediately:
///
///   System.InvalidOperationException: Unable to find an IGrainReferenceActivatorProvider for
///   grain type sys.client
///
/// This is the same C#-codegen wall documented in "Running a silo from a standalone F# process":
/// <c>IChatObserver</c> is declared in F# (ChatTypes.fs) and ChatRoom.sln has no C# CodeGen
/// project to generate its reference-activator/proxy, so <c>CreateObjectReference&lt;IChatObserver&gt;</c>
/// cannot resolve -- independent of which grain the observer would be passed to. A second
/// candidate, <c>Orleans.FSharp.BroadcastChannel</c> (also KEEP-list), was checked before writing
/// any code for it: docs/streaming.md states plainly that broadcast-channel *consumers* are grains
/// implementing <c>IOnBroadcastChannelSubscribed</c> with <c>[ImplicitChannelSubscription]</c>,
/// "handled by the C# CodeGen" -- the identical wall, one hop later.
///
/// What closes it: a FUNCTIONAL observer needs no application interface at all. The one
/// C#-declared interface lives inside <c>Orleans.FSharp.Abstractions</c>, so Orleans' proxy
/// generator has already run over it, and an application observer is an ordinary F# handler
/// record -- <c>RoomObserverApi</c> below. This example subscribes one from its client and
/// receives every message live; <c>history</c> remains as an ordinary paged query rather than as
/// a substitute for push. The old grain (classic observer push, still fully intact) remains the
/// reference for the deprecated model -- see ChatGrain.fs and this example's README.
/// </summary>
namespace ChatRoom.Grains

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans.FSharp

type RoomActor = private RoomActor of unit

/// <summary>The observer brand: what a subscriber to this room is.</summary>
type RoomObserver = private RoomObserver of unit

/// <summary>
/// A subscriber's handler record. Every field is a push operation, <c>'Msg -> Task&lt;unit&gt;</c>;
/// no interface, and no code generation in this project.
/// </summary>
[<NoEquality; NoComparison>]
type RoomObserverApi =
    { /// <summary>A message was posted: (sender, text).</summary>
      onMessage: (string * string) -> Task<unit>
      /// <summary>Someone joined or left: (member, joined).</summary>
      onPresence: (string * bool) -> Task<unit> }

/// <summary>The typed handle a subscriber hands to the room.</summary>
type RoomObserverHandle = FunctionalObserverHandle<RoomObserver, RoomObserverApi>

/// <summary>Rejection reasons for <c>say</c>, returned instead of thrown.</summary>
type ChatError =
    /// <summary>The sender has not <c>join</c>-ed the room.</summary>
    | NotAMember
    /// <summary>The message text was empty or whitespace-only.</summary>
    | EmptyMessage

[<NoEquality; NoComparison>]
type RoomApi =
    { /// <summary>Adds a member to the room. Idempotent.</summary>
      join: string -> Task<unit>
      /// <summary>Removes a member from the room. Idempotent.</summary>
      leave: string -> Task<unit>
      /// <summary>Posts (sender, message); rejects non-members and empty text. Returns the new
      /// total message count on success.</summary>
      say: string * string -> Task<Result<int, ChatError>>
      /// <summary>Returns up to <c>take</c> most recent (sender, message, timestamp) entries,
      /// newest first. An ordinary paged query -- push is handled by observers, not by polling.</summary>
      history: int -> Task<(string * string * DateTimeOffset) list>
      /// <summary>Fire-and-forget typing indicator; never blocks the sender and interleaves with
      /// every other call. Two inputs, so one tuple argument: an operation takes exactly one
      /// argument, and a multi-input operation groups its inputs in a tuple.</summary>
      typing: (string * bool) -> Task<unit>
      /// <summary>Current member count.</summary>
      memberCount: unit -> Task<int>
      /// <summary>Subscribes a client-hosted observer for live push. Returns the subscriber
      /// count. The handle is an ordinary operation argument -- nothing else is needed.</summary>
      subscribe: RoomObserverHandle -> Task<int>
      /// <summary>Unsubscribes an observer. Returns the remaining subscriber count.</summary>
      unsubscribe: RoomObserverHandle -> Task<int> }

[<RequireQualifiedAccess>]
module RoomObserverApi =
    /// <summary>
    /// The observer contract, shared by the client that subscribes and the grain that pushes.
    /// Mirrors <c>grainContract</c>: an observer type and a version, and nothing else -- a push
    /// operation's wire ID is always its field name.
    /// </summary>
    let contract =
        observerContract<RoomObserver, RoomObserverApi> {
            observerType "chat-room.room.observer"
            version 1
        }

[<RequireQualifiedAccess>]
module RoomApi =
    let contract =
        grainContract<RoomActor, string, RoomApi> {
            grainType "chat-room.room.functional"
            version 1
            stringKey

            readOnly (_.history)
            readOnly (_.memberCount)
            oneWay (_.typing)
            alwaysInterleave (_.typing)
        }

    let ref = FunctionalGrain.ref contract

type RoomState =
    { Members: Set<string>
      Messages: (string * string * DateTimeOffset) list }

/// <summary>
/// Ephemeral per-activation state. The subscriber set is deliberately NOT part of
/// <c>RoomState</c>: a manager holds live Orleans object references, which cannot survive an
/// activation let alone a storage round-trip, and the F# codec refuses a state type carrying one
/// rather than writing something that merely looks restorable.
/// </summary>
type RoomLive =
    { Persisted: RoomState
      Subscribers: FunctionalObserverManager<RoomObserver, RoomObserverApi> }

module RoomFunctionalDef =

    let roomState = PersistentState.create<RoomState> "state" "Default"

    /// <summary>Push one notification to every live subscriber, swallowing nothing silently
    /// that matters: delivery is best-effort by design, so a dead subscriber is simply expired.
    /// </summary>
    let private fanOut (live: RoomLive) selector message =
        live.Subscribers.Notify selector message

    let room =
        grainFor RoomApi.contract {
            // The handler's state is the LIVE, per-activation shape: durable data plus the
            // subscriber set. The subscriber set holds live Orleans object references, which
            // cannot survive an activation let alone a storage round-trip, so the durable half
            // is a named persistent holder rather than the handler state itself -- the F# codec
            // would refuse a state type carrying a handle, which is exactly the right answer.
            defaultState (fun () ->
                { Persisted = { Members = Set.empty; Messages = [] }
                  Subscribers = FunctionalObserverManager<RoomObserver, RoomObserverApi>(TimeSpan.FromMinutes 5.0) })

            usePersistentState roomState (fun _key -> { Members = Set.empty; Messages = [] })
            collectionAge (TimeSpan.FromMinutes 30.0)

            // Load the durable half once per activation into the live state.
            onActivate (fun context state ->
                task {
                    let storage = context.persistentState roomState
                    return { state with Persisted = storage.State }
                })

            handle
                (_.join)
                (fun context state sender ->
                    task {
                        let persisted =
                            { state.Persisted with Members = Set.add sender state.Persisted.Members }

                        let storage = context.persistentState roomState
                        storage.State <- persisted
                        do! storage.WriteStateAsync()
                        do! fanOut state (_.onPresence) (sender, true)
                        return { state with Persisted = persisted }, ()
                    })

            handle
                (_.leave)
                (fun context state sender ->
                    task {
                        let persisted =
                            { state.Persisted with Members = Set.remove sender state.Persisted.Members }

                        let storage = context.persistentState roomState
                        storage.State <- persisted
                        do! storage.WriteStateAsync()
                        do! fanOut state (_.onPresence) (sender, false)
                        return { state with Persisted = persisted }, ()
                    })

            handle
                (_.say)
                (fun context state (sender, message) ->
                    task {
                        if not (Set.contains sender state.Persisted.Members) then
                            return state, Error NotAMember
                        elif String.IsNullOrWhiteSpace message then
                            return state, Error EmptyMessage
                        else
                            let entry = (sender, message, context.utcNow)

                            let persisted =
                                { state.Persisted with
                                    Messages = entry :: state.Persisted.Messages |> List.truncate 100 }

                            let storage = context.persistentState roomState
                            storage.State <- persisted
                            do! storage.WriteStateAsync()

                            // The push: every subscriber sees the message live, and this handler
                            // does not wait for any of them.
                            do! fanOut state (_.onMessage) (sender, message)

                            return { state with Persisted = persisted }, Ok persisted.Messages.Length
                    })

            handle
                (_.history)
                (fun _context state take ->
                    task { return state, state.Persisted.Messages |> List.truncate (max 0 take) })

            handle
                (_.typing)
                (fun context state (sender, isTyping) ->
                    task {
                        context.logger.LogDebug("{Sender} typing={IsTyping}", sender, isTyping)
                        return state, ()
                    })

            handle
                (_.memberCount)
                (fun _context state () -> task { return state, state.Persisted.Members.Count })

            handle
                (_.subscribe)
                (fun _context state (handle: RoomObserverHandle) ->
                    task {
                        state.Subscribers.Subscribe handle
                        return state, state.Subscribers.Count
                    })

            handle
                (_.unsubscribe)
                (fun _context state (handle: RoomObserverHandle) ->
                    task {
                        state.Subscribers.Unsubscribe handle |> ignore
                        return state, state.Subscribers.Count
                    })
        }
