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

[<Struct>]
type UserId = private UserId of string

[<RequireQualifiedAccess>]
module UserId =
    let create value = UserId value
    let value (UserId value) = value

[<Struct>]
type RoomId = private RoomId of string

[<RequireQualifiedAccess>]
module RoomId =
    let create value = RoomId value
    let value (RoomId value) = value

type PostMessage = { author: UserId; text: string }

type ChatMessage =
    { author: UserId
      text: string
      sentAt: DateTimeOffset }

type HistoryRequest = { take: int }

type Typing = { user: UserId; isTyping: bool }

type PostError =
    | NotAMember
    | EmptyText

type RoomActor = private RoomActor of unit

[<NoEquality; NoComparison>]
type RoomApi =
    { join: UserId -> Task<unit>
      say: PostMessage -> Task<Result<int64, PostError>>
      history: HistoryRequest -> Task<ChatMessage list>
      typing: Typing -> Task<unit> }

[<RequireQualifiedAccess>]
module RoomApi =
    let contract =
        grainContract<RoomActor, RoomId, RoomApi> () {
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
    let rawRef = FunctionalGrain.rawRef contract

namespace Chat.Server

open System
open Chat.Contracts
open Microsoft.Extensions.Logging
open Orleans.FSharp

type RoomState =
    { nextMessageId: int64
      members: Set<UserId>
      messages: ChatMessage list }

module Definition =

    let roomState = PersistentState.create<RoomState> "state" "Default"

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
