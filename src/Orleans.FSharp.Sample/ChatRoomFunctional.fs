/// <summary>
/// The spec's runnable end-to-end sample: the exact chat-room contract, definition, and
/// registration from spec 003's "Public authoring model" section, verbatim down to the module
/// and binding names. Nothing here is annotated — <c>contract</c>, <c>ref</c>, and <c>rawRef</c>
/// infer their complete concrete types, and the point-free <c>let ref = FunctionalGrain.ref
/// contract</c> binding is the one the spec itself shows.
/// </summary>
namespace Chat.Contracts

open System
open System.Threading.Tasks
open Orleans.FSharp

/// <summary>A chat room member's mapped domain key.</summary>
[<Struct>]
type UserId = private UserId of string

[<RequireQualifiedAccess>]
module UserId =
    /// <summary>Wraps a raw string as a <see cref="UserId"/>.</summary>
    /// <param name="value">The raw user identifier.</param>
    let create value = UserId value
    /// <summary>Unwraps a <see cref="UserId"/> to its raw string.</summary>
    /// <param name="value">The user id to unwrap.</param>
    let value (UserId value) = value

/// <summary>A chat room's mapped domain key.</summary>
[<Struct>]
type RoomId = private RoomId of string

[<RequireQualifiedAccess>]
module RoomId =
    /// <summary>Wraps a raw string as a <see cref="RoomId"/>.</summary>
    /// <param name="value">The raw room identifier.</param>
    let create value = RoomId value
    /// <summary>Unwraps a <see cref="RoomId"/> to its raw string.</summary>
    /// <param name="value">The room id to unwrap.</param>
    let value (RoomId value) = value

/// <summary>The <c>say</c> command: post one message as its author.</summary>
type PostMessage = { author: UserId; text: string }

/// <summary>One posted message as stored in room history.</summary>
type ChatMessage =
    { author: UserId
      text: string
      sentAt: DateTimeOffset }

/// <summary>The <c>history</c> query: how many recent messages to return.</summary>
type HistoryRequest = { take: int }

/// <summary>The <c>typing</c> notification: one user's typing-indicator state.</summary>
type Typing = { user: UserId; isTyping: bool }

/// <summary>Why <c>say</c> rejected a post.</summary>
type PostError =
    /// <summary>The author has not joined the room.</summary>
    | NotAMember
    /// <summary>The message text is empty or white-space.</summary>
    | EmptyText

/// <summary>Phantom actor brand for the room contract; never constructed.</summary>
type RoomActor = private RoomActor of unit

/// <summary>The room's typed API shape: one function per grain operation.</summary>
[<NoEquality; NoComparison>]
type RoomApi =
    { join: UserId -> Task<unit>
      say: PostMessage -> Task<Result<int64, PostError>>
      history: HistoryRequest -> Task<ChatMessage list>
      typing: Typing -> Task<unit> }

[<RequireQualifiedAccess>]
module RoomApi =
    /// <summary>The room's functional grain contract: type, version, key mapping, and call policies.</summary>
    let contract =
        grainContract<RoomActor, RoomId, RoomApi> {
            grainType "chat.room"
            version 1
            stringKeyMapped RoomId.value RoomId.create

            readOnly (_.history)
            oneWay (_.typing)
            alwaysInterleave (_.typing)
        }

    /// The point-free binding the spec's "Public authoring model" section shows: no type
    /// annotation anywhere, yet `ref` infers `IGrainFactory -> RoomId -> RoomApi`.
    let ref = FunctionalGrain.ref contract
    /// <summary>The untyped counterpart to <see cref="ref"/>: a grain reference without the inferred typed API shape.</summary>
    let rawRef = FunctionalGrain.rawRef contract

namespace Chat.Server

open System
open Chat.Contracts
open Microsoft.Extensions.Logging
open Orleans.FSharp

/// <summary>The room grain's persisted state: message sequence counter, membership, and history.</summary>
type RoomState =
    { nextMessageId: int64
      members: Set<UserId>
      messages: ChatMessage list }

module Definition =

    /// <summary>The persistent-state handle for <see cref="RoomState"/>, stored as <c>"state"</c> under the <c>"Default"</c> provider.</summary>
    let roomState = PersistentState.create<RoomState> "state" "Default"

    /// <summary>The room grain's behavior: handlers for <c>join</c>, <c>say</c>, <c>history</c>, and <c>typing</c>.</summary>
    let roomDefinition =
        grainFor RoomApi.contract {
            defaultState (fun () ->
                { nextMessageId = 1L
                  members = Set.empty
                  messages = [] })

            stateFrom roomState
            collectionAge (TimeSpan.FromMinutes 30.0)

            handle
                (_.join)
                (fun context state userId ->
                    task {
                        let next =
                            { state with
                                members = Set.add userId state.members }

                        let storage = context.persistentState roomState
                        storage.State <- next
                        do! storage.WriteStateAsync()
                        return next, ()
                    })

            handle
                (_.say)
                (fun context state post ->
                    task {
                        if not (Set.contains post.author state.members) then
                            return state, Error NotAMember
                        elif String.IsNullOrWhiteSpace post.text then
                            return state, Error EmptyText
                        else
                            let message =
                                { author = post.author
                                  text = post.text
                                  sentAt = context.utcNow }

                            let id = state.nextMessageId

                            let next =
                                { state with
                                    nextMessageId = id + 1L
                                    messages = message :: state.messages }

                            let storage = context.persistentState roomState
                            storage.State <- next
                            do! storage.WriteStateAsync()
                            return next, Ok id
                    })

            handle
                (_.history)
                (fun _context state request ->
                    task {
                        return
                            state,
                            state.messages |> List.truncate (max 0 request.take) |> List.rev
                    })

            handle
                (_.typing)
                (fun context state typing ->
                    task {
                        context.logger.LogDebug("{User} typing={IsTyping}", typing.user, typing.isTyping)

                        return state, ()
                    })
        }
