/// <summary>
/// Compile fixtures for spec 003: the complete public authoring example from the
/// specification, compiled by the ordinary build. Nothing here is annotated — the whole point
/// is that <c>contract</c>, <c>ref</c>, <c>rawRef</c>, handlers, and bound calls infer their
/// complete concrete types.
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

    let ref = FunctionalGrain.ref contract
    let rawRef = FunctionalGrain.rawRef contract

/// <summary>
/// The spec's alternative application-owned bindings: same inference, key before factory.
/// </summary>
module RoomClient =
    let ref roomId factory = FunctionalGrain.ref RoomApi.contract factory roomId

    let rawRef roomId factory =
        FunctionalGrain.rawRef RoomApi.contract factory roomId

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

namespace Chat.Client

open Chat.Contracts
open Chat.Server
open Orleans
open Orleans.FSharp
open Orleans.Hosting

/// <summary>Registration and call sites from the spec, compiled but never executed here.</summary>
module Usage =

    let registerSilo (siloBuilder: ISiloBuilder) =
        siloBuilder.AddFunctionalGrain(Definition.roomDefinition) |> ignore

    let registerClient (clientBuilder: IClientBuilder) =
        clientBuilder.AddFunctionalGrainClient() |> ignore

    let externalClientCalls (client: IGrainFactory) =
        task {
            let lobby = RoomApi.ref client (RoomId.create "general")

            do! lobby.join (UserId.create "alice")

            let! result =
                lobby.say
                    { author = UserId.create "alice"
                      text = "Hello from F#" }

            let! recent = lobby.history { take = 20 }
            return result, recent
        }

    let insideAHandler (context: FunctionalGrainContext<RoomActor, RoomId>) (otherRoomId: RoomId) (userId: UserId) =
        task {
            let otherRoom = RoomApi.ref context.grainFactory otherRoomId
            do! otherRoom.join userId
        }

    let reorderedBindings (client: IGrainFactory) =
        task {
            let lobby = RoomClient.ref (RoomId.create "general") client
            let raw = RoomClient.rawRef (RoomId.create "general") client
            do! lobby.join (UserId.create "bob")
            return raw.key, raw.api
        }

    /// <remarks>
    /// F# inserts flexibility for the non-sealed <c>IGrainFactory</c> parameter, so the
    /// point-free bindings <c>RoomApi.ref</c> / <c>RoomApi.rawRef</c> stay generalized until a
    /// later use in the same file fixes the factory type. These two uses are what make the
    /// spec's unannotated module bindings compile; see FunctionalCompileFailureTests.
    /// </remarks>
    let pointFreeBindings (client: IGrainFactory) =
        task {
            let lobby = RoomApi.ref client (RoomId.create "general")
            let raw = RoomApi.rawRef client (RoomId.create "general")
            do! lobby.join (UserId.create "carol")
            return raw.key, raw.api
        }
