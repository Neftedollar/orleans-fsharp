/// <summary>
/// Functional-runtime equivalent of <c>ChatGrainDef.chat</c> in <c>ChatGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Full room domain matching the
/// spec's own chat-room shape (docs/functional-grains.md "Overview" /
/// <c>src/Orleans.FSharp.Sample/ChatRoomFunctional.fs</c>): membership (<c>join</c>/<c>leave</c>),
/// <c>say</c> with membership validation returning a typed <c>Result</c>, a <c>readOnly</c>
/// <c>history</c> query, a <c>oneWay</c> + <c>alwaysInterleave</c> <c>typing</c> indicator, and
/// explicit <c>stateFrom</c> persistence.
///
/// Pub/sub, resolved by experiment (not assumption):
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
/// Fallback shipped here, honestly: <c>history</c> is a <c>readOnly</c> poll a client calls to see
/// new messages, replacing push notification. The old grain (observer-based push, still fully
/// intact) remains the reference for pub/sub -- see ChatGrain.fs and this example's README.
/// </summary>
namespace ChatRoom.Grains

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans.FSharp

type RoomActor = private RoomActor of unit

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
      /// newest first. The polling replacement for push notification -- see this file's header.</summary>
      history: int -> Task<(string * string * DateTimeOffset) list>
      /// <summary>Fire-and-forget typing indicator; never blocks the sender and interleaves with
      /// every other call. Spelled CURRIED, which is sugar for the tupled
      /// <c>(string * bool) -> Task&lt;unit&gt;</c>: the canonical wire argument is that tuple
      /// either way, so the two spellings are byte-identical on the wire and the choice is
      /// purely about how the call and the handler read. See docs/functional-grains.md,
      /// "Two spellings, one operation".</summary>
      typing: string -> bool -> Task<unit>
      /// <summary>Current member count.</summary>
      memberCount: unit -> Task<int> }

[<RequireQualifiedAccess>]
module RoomApi =
    let contract =
        grainContract<RoomActor, string, RoomApi> () {
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

module RoomFunctionalDef =

    let roomState = PersistentState.create<RoomState> "state" "Default"

    let room =
        grainFor RoomApi.contract {
            defaultState (fun () -> { Members = Set.empty; Messages = [] })

            stateFrom roomState
            collectionAge (TimeSpan.FromMinutes 30.0)

            handle
                (_.join)
                (fun context state sender ->
                    task {
                        let next = { state with Members = Set.add sender state.Members }
                        let storage = context.persistentState roomState
                        storage.State <- next
                        do! storage.WriteStateAsync()
                        return next, ()
                    })

            handle
                (_.leave)
                (fun context state sender ->
                    task {
                        let next = { state with Members = Set.remove sender state.Members }
                        let storage = context.persistentState roomState
                        storage.State <- next
                        do! storage.WriteStateAsync()
                        return next, ()
                    })

            handle
                (_.say)
                (fun context state (sender, message) ->
                    task {
                        if not (Set.contains sender state.Members) then
                            return state, Error NotAMember
                        elif String.IsNullOrWhiteSpace message then
                            return state, Error EmptyMessage
                        else
                            let entry = (sender, message, context.utcNow)

                            let next =
                                { state with
                                    Messages = entry :: state.Messages |> List.truncate 100 }

                            let storage = context.persistentState roomState
                            storage.State <- next
                            do! storage.WriteStateAsync()
                            return next, Ok next.Messages.Length
                    })

            handle
                (_.history)
                (fun _context state take ->
                    task { return state, state.Messages |> List.truncate (max 0 take) })

            // handle2, not handle: the arity of a curried field is part of the operation name.
            // The handler still takes the canonical tuple -- handlers are never curried.
            handle2
                (_.typing)
                (fun context state (sender, isTyping) ->
                    task {
                        context.logger.LogDebug("{Sender} typing={IsTyping}", sender, isTyping)
                        return state, ()
                    })

            handle (_.memberCount) (fun _context state () -> task { return state, state.Members.Count })
        }
