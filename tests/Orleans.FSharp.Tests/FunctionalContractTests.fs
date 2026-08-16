/// <summary>
/// Contract metadata tests for spec 003 Phase 1: grain type, version, operation IDs, policy
/// validation, and key-operation cardinality.
/// </summary>
module Orleans.FSharp.Tests.FunctionalContractTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

type ChatActor = private ChatActor of unit

[<NoEquality; NoComparison>]
type ChatApi =
    { join: string -> Task<unit>
      say: string -> Task<int64>
      history: int -> Task<string list>
      typing: bool -> Task<unit> }

let private baseContract () =
    grainContract<ChatActor, string, ChatApi> () {
        grainType "chat.room"
        stringKey
    }

let private throws (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

// ──────────────────────────────────────────────────────────────────────────────
// Metadata defaults
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a contract keeps grain type, default version, and declaration order`` () =
    let contract = baseContract ()

    test <@ contract.GrainTypeName = "chat.room" @>
    test <@ contract.Version = 1 @>

    test
        <@
            contract.Operations |> Array.map (fun op -> op.FieldName) = [| "join"; "say"; "history"; "typing" |]
        @>

    test
        <@
            contract.Operations |> Array.map (fun op -> op.OperationId) = [| "join"; "say"; "history"; "typing" |]
        @>

[<Fact>]
let ``a contract records the exact argument and reply types`` () =
    let contract = baseContract ()

    test <@ contract.Operations.[1].ArgumentType = typeof<string> @>
    test <@ contract.Operations.[1].ReplyType = typeof<int64> @>
    test <@ contract.Operations.[2].ReplyType = typeof<string list> @>

[<Fact>]
let ``an explicit version is kept`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> () {
            grainType "chat.room"
            version 7
            stringKey
        }

    test <@ contract.Version = 7 @>

// ──────────────────────────────────────────────────────────────────────────────
// Grain type and version validation
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a missing grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () { stringKey } |> ignore)

    test <@ error.Message.Contains "requires exactly one 'grainType' operation" @>

[<Fact>]
let ``a blank grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "   "
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "non-blank" @>

[<Fact>]
let ``a NUL-containing grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat\000room"
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "NUL" @>

[<Fact>]
let ``a repeated grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                grainType "chat.other"
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "already set" @>

[<Fact>]
let ``a non-positive version fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                version 0
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "positive integer" @>

[<Fact>]
let ``a repeated version fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                version 2
                version 3
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "already set" @>

// ──────────────────────────────────────────────────────────────────────────────
// Key-operation cardinality
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a missing key operation fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () { grainType "chat.room" } |> ignore)

    test <@ error.Message.Contains "exactly one native or mapped key operation" @>

[<Fact>]
let ``a repeated key operation fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                stringKeyMapped id id
            }
            |> ignore)

    test <@ error.Message.Contains "conflicts with" @>

// ──────────────────────────────────────────────────────────────────────────────
// Operation IDs
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``an operation ID override replaces only that field's ID`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> () {
            grainType "chat.room"
            stringKey
            operationId "enter" (_.join)
        }

    test
        <@
            contract.Operations |> Array.map (fun op -> op.OperationId) = [| "enter"; "say"; "history"; "typing" |]
        @>

    test <@ (contract.TryFindOperation "enter").IsSome @>
    test <@ (contract.TryFindOperation "join").IsNone @>

[<Fact>]
let ``operation IDs are case-sensitive ordinal strings`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> () {
            grainType "chat.room"
            stringKey
            operationId "JOIN" (_.join)
        }

    test <@ (contract.TryFindOperation "JOIN").IsSome @>
    test <@ (contract.TryFindOperation "join").IsNone @>

[<Fact>]
let ``a duplicate final operation ID fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                operationId "say" (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "is used by both API field" @>

[<Fact>]
let ``a blank operation ID fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                operationId " " (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "non-blank wire ID" @>

[<Fact>]
let ``a NUL-containing operation ID fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                operationId "jo\000in" (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "NUL" @>

[<Fact>]
let ``a repeated operation ID override on one field fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                operationId "enter" (_.join)
                operationId "arrive" (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "'operationId' is applied more than once" @>

// ──────────────────────────────────────────────────────────────────────────────
// Policies
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``policies land on the selected fields only`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> () {
            grainType "chat.room"
            stringKey
            readOnly (_.history)
            oneWay (_.typing)
            alwaysInterleave (_.typing)
        }

    test <@ contract.Operations |> Array.map (fun op -> op.IsReadOnly) = [| false; false; true; false |] @>
    test <@ contract.Operations |> Array.map (fun op -> op.IsOneWay) = [| false; false; false; true |] @>

    test
        <@
            contract.Operations |> Array.map (fun op -> op.IsAlwaysInterleave) = [| false; false; false; true |]
        @>

[<Fact>]
let ``readOnly plus alwaysInterleave is accepted`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> () {
            grainType "chat.room"
            stringKey
            readOnly (_.history)
            alwaysInterleave (_.history)
        }

    test <@ contract.Operations.[2].IsReadOnly && contract.Operations.[2].IsAlwaysInterleave @>

[<Fact>]
let ``a repeated policy of the same kind on one field fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                readOnly (_.history)
                readOnly (_.history)
            }
            |> ignore)

    test <@ error.Message.Contains "'readOnly' is applied more than once" @>

[<Fact>]
let ``oneWay combined with readOnly fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                oneWay (_.typing)
                readOnly (_.typing)
            }
            |> ignore)

    test <@ error.Message.Contains "combines 'oneWay' with 'readOnly'" @>

[<Fact>]
let ``alwaysInterleave without readOnly or oneWay fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> () {
                grainType "chat.room"
                stringKey
                alwaysInterleave (_.say)
            }
            |> ignore)

    test <@ error.Message.Contains "without 'readOnly' or 'oneWay'" @>

/// <remarks>
/// The F# compiler already rejects a <c>oneWay</c> selector whose reply is not
/// <c>Task&lt;unit&gt;</c>, so this defensive contract-side check is exercised through the
/// internal draft state.
/// </remarks>
[<Fact>]
let ``oneWay on a non-unit reply is rejected defensively`` () =
    let shape = ApiShape.of'<ChatApi> ()

    let state: ContractDraftState<string> =
        { Shape = shape
          GrainTypeName = Some "chat.room"
          Version = None
          KeyCodec = Some KeyCodecs.stringKey
          ReadOnly = Set.empty
          OneWay = Set.ofList [ 1 ]
          AlwaysInterleave = Set.empty
          OperationIds = Map.empty }

    let draft = ContractDraft.withState<ChatActor, string, ChatApi> state
    let error = throws (fun () -> ContractDraft.run draft |> ignore)

    test <@ error.Message.Contains "requires API field 'say'" @>
    test <@ error.Message.Contains "Task<unit>" @>

// ──────────────────────────────────────────────────────────────────────────────
// Identity
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the grain identity combines the explicit grain type with the encoded key`` () =
    let contract = baseContract ()
    let grainId = contract.GrainIdOf "general"
    let grainTypeText = grainId.Type.ToString()
    let keyText = grainId.Key.ToString()

    test <@ grainTypeText = "chat.room" @>
    test <@ keyText = "general" @>
    test <@ contract.KeyOf grainId = "general" @>

[<Fact>]
let ``a changed grain type changes the grain identity`` () =
    let first = baseContract ()

    let second =
        grainContract<ChatActor, string, ChatApi> () {
            grainType "chat.lobby"
            stringKey
        }

    test <@ first.GrainIdOf "general" <> second.GrainIdOf "general" @>

[<Fact>]
let ``a changed key codec changes the grain identity`` () =
    let plain = baseContract ()

    let prefixed =
        grainContract<ChatActor, string, ChatApi> () {
            grainType "chat.room"
            stringKeyMapped (fun key -> "room:" + key) (fun native -> native.Substring 5)
        }

    test <@ plain.GrainIdOf "general" <> prefixed.GrainIdOf "general" @>
    test <@ (prefixed.GrainIdOf "general").Key.ToString() = "room:general" @>

[<Fact>]
let ``contract identity is independent of CLR and module names`` () =
    // Two contracts declared over different CLR API/actor types but the same explicit grain
    // type and key encoding address the same grain identity.
    let first = baseContract ()

    let second =
        grainContract<FunctionalShapeTests.SampleApi, string, FunctionalShapeTests.SampleApi> () {
            grainType "chat.room"
            stringKey
        }

    test <@ first.GrainIdOf "general" = second.GrainIdOf "general" @>
