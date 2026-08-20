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

/// <summary>
/// A fixture whose sole field name is 513 characters -- one past the fixed transport's
/// MaxWireTextLength (512) -- used only to exercise the derived-operation-ID length check a
/// hand-written 'operationId' override can never reach, since there is nowhere to write a length
/// violation as a string literal for an override: the field NAME itself has to be long.
/// </summary>
[<NoEquality; NoComparison>]
type LongFieldApi = { ``aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`` : string -> Task<unit> }

/// <summary>The exact field name declared above, for the test that names it in a diagnostic.</summary>
module LongFieldApi =
    [<Literal>]
    let longFieldName = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

let private baseContract () =
    grainContract<ChatActor, string, ChatApi> {
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
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            version 7
            stringKey
        }

    test <@ contract.Version = 7 @>

// ──────────────────────────────────────────────────────────────────────────────
// Grain type and version validation
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``an omitted grain type derives the actor brand's CLR simple name`` () =
    let contract =
        grainContract<Orleans.FSharp.Tests.GrainTypeDerivation.DerivableActor, string, ChatApi> { stringKey }

    test <@ contract.GrainTypeName = "DerivableActor" @>

/// <remarks>
/// <c>ChatActor</c> is declared inside this file's own top-level module, so it is a CLR-nested
/// type (see <c>GrainTypeDerivationFixtures.fs</c>) -- a convenient, already-present fixture for
/// the "nested brand" half of the derivation rule.
/// </remarks>
[<Fact>]
let ``a missing grain type on a nested actor brand fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> { stringKey } |> ignore)

    test <@ error.Message.Contains "nested" @>
    test <@ error.Message.Contains "explicit 'grainType'" @>

[<Fact>]
let ``a missing grain type on a generic actor brand fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<Orleans.FSharp.Tests.GrainTypeDerivation.GenericActor<int>, string, ChatApi> {
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "generic" @>
    test <@ error.Message.Contains "explicit 'grainType'" @>

/// <remarks>
/// Exercises the DERIVED half of grain-type validation specifically: the explicit-'grainType'
/// tests below go through GrainContractBuilder.GrainType, a different code path in
/// ContractDraft.run from the one that validates an omitted 'grainType''s derived name. The fixed
/// transport's own 512-character bound applies to a CLR simple name exactly as it does to a
/// hand-written string literal, so an actor brand this long fails contract construction the same
/// way an over-length explicit 'grainType' does (see below).
/// </remarks>
[<Fact>]
let ``an over-length derived grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<Orleans.FSharp.Tests.GrainTypeDerivation.``bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb``, string, ChatApi> { stringKey }
            |> ignore)

    test <@ error.Message.Contains "the 'grainType' derived from actor brand" @>
    test <@ error.Message.Contains "at most 512 characters" @>

[<Fact>]
let ``a blank grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "   "
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "non-blank" @>

[<Fact>]
let ``a NUL-containing grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat\000room"
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "control character" @>

[<Fact>]
let ``a newline-carrying grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat\nroom"
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "control character" @>

[<Fact>]
let ``an over-length grain type fails contract construction with the transport's own bound`` () =
    let tooLong = String.replicate 513 "a"

    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType tooLong
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "at most 512 characters" @>
    test <@ error.Message.Contains "513 were supplied" @>

[<Fact>]
let ``a grain type at exactly the transport's bound is accepted`` () =
    let atLimit = String.replicate 512 "a"

    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType atLimit
            stringKey
        }

    test <@ contract.GrainTypeName = atLimit @>

/// <remarks>
/// Verified with <c>dotnet fsi</c> before writing this test: an F# double-backtick identifier
/// cannot carry a raw control character (FS3563 rejects both a literal newline and a literal
/// carriage return inside <c>``...``</c>), but it CAN carry 600 ordinary characters. The
/// control-character half of "an F# double-backtick field name can carry unusual characters" is
/// therefore not constructible through source syntax; the length half is, and this is that case.
/// </remarks>
[<Fact>]
let ``an over-length derived operation ID from a double-backtick field name fails contract construction, naming the field`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, LongFieldApi> {
                grainType "chat.room"
                stringKey
            }
            |> ignore)

    test <@ error.Message.Contains "the operation ID defaulted from API field" @>
    test <@ error.Message.Contains LongFieldApi.longFieldName @>
    test <@ error.Message.Contains "at most 512 characters" @>

[<Fact>]
let ``a repeated grain type fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> { grainType "chat.room" } |> ignore)

    test <@ error.Message.Contains "exactly one native or mapped key operation" @>

[<Fact>]
let ``a repeated key operation fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
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
        grainContract<ChatActor, string, ChatApi> {
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
        grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                operationId "jo\000in" (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "control character" @>

[<Fact>]
let ``a newline-carrying operation ID fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                operationId "jo\nin" (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "control character" @>

[<Fact>]
let ``an over-length operation ID fails contract construction with the transport's own bound`` () =
    let tooLong = String.replicate 513 "a"

    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                operationId tooLong (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "at most 512 characters" @>
    test <@ error.Message.Contains "513 were supplied" @>

[<Fact>]
let ``an operation ID at exactly the transport's bound is accepted`` () =
    let atLimit = String.replicate 512 "a"

    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            stringKey
            operationId atLimit (_.join)
        }

    test <@ (contract.TryFindOperation atLimit).IsSome @>

[<Fact>]
let ``a repeated operation ID override on one field fails contract construction`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
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
        grainContract<ChatActor, string, ChatApi> {
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
        grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
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
            grainContract<ChatActor, string, ChatApi> {
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
          AcceptedVersions = None
          IsReentrant = false
          MayInterleave = None
          KeyCodec = Some KeyCodecs.stringKey
          ReadOnly = Set.empty
          OneWay = Set.ofList [ 1 ]
          AlwaysInterleave = Set.empty
          Transactions = Map.empty
          SinceVersions = Map.empty
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
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.lobby"
            stringKey
        }

    test <@ first.GrainIdOf "general" <> second.GrainIdOf "general" @>

[<Fact>]
let ``a changed key codec changes the grain identity`` () =
    let plain = baseContract ()

    let prefixed =
        grainContract<ChatActor, string, ChatApi> {
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
        grainContract<FunctionalShapeTests.SampleApi, string, FunctionalShapeTests.SampleApi> {
            grainType "chat.room"
            stringKey
        }

    test <@ first.GrainIdOf "general" = second.GrainIdOf "general" @>

// ──────────────────────────────────────────────────────────────────────────────
// Spec 004 item 5 -- reentrancy variants
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a contract is not reentrant and declares no interleave predicate by default`` () =
    let contract = baseContract ()

    test <@ not contract.IsReentrant @>
    test <@ contract.MayInterleave.IsNone @>

[<Fact>]
let ``reentrant marks the whole contract`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            stringKey
            reentrant
        }

    test <@ contract.IsReentrant @>

[<Fact>]
let ``mayInterleave stores the declared predicate`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            stringKey
            mayInterleave (fun metadata -> metadata.OperationId = "history")
        }

    test <@ contract.MayInterleave.IsSome @>

[<Fact>]
let ``reentrant is rejected twice`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                reentrant
                reentrant
            }
            |> ignore)

    test <@ error.Message.Contains "'reentrant' is declared more than once" @>

[<Fact>]
let ``mayInterleave is rejected twice`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                mayInterleave (fun _ -> true)
                mayInterleave (fun _ -> false)
            }
            |> ignore)

    test <@ error.Message.Contains "'mayInterleave' is declared more than once" @>

[<Fact>]
let ``mayInterleave rejects a null predicate`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                mayInterleave (Unchecked.defaultof<IFunctionalRequestMetadata -> bool>)
            }
            |> ignore)

    test <@ error.Message.Contains "'mayInterleave' requires a predicate" @>

[<Fact>]
let ``reentrant and mayInterleave are mutually exclusive`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                reentrant
                mayInterleave (fun _ -> true)
            }
            |> ignore)

    test <@ error.Message.Contains "declares both 'reentrant' and 'mayInterleave'" @>

[<Fact>]
let ``reentrant rejects alwaysInterleave on an operation`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                reentrant
                readOnly (_.history)
                alwaysInterleave (_.history)
            }
            |> ignore)

    test <@ error.Message.Contains "uses 'alwaysInterleave' on a contract declared 'reentrant'" @>

[<Fact>]
let ``mayInterleave rejects alwaysInterleave on an operation`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                mayInterleave (fun _ -> true)
                readOnly (_.history)
                alwaysInterleave (_.history)
            }
            |> ignore)

    test <@ error.Message.Contains "uses 'alwaysInterleave' on a contract declared 'mayInterleave'" @>

/// <remarks>
/// The ruling that 'readOnly' and 'oneWay' survive 'reentrant': neither is only a scheduling
/// flag in this runtime, so neither is made redundant by whole-grain reentrancy.
/// </remarks>
[<Fact>]
let ``reentrant keeps readOnly and oneWay legal`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            stringKey
            reentrant
            readOnly (_.history)
            oneWay (_.typing)
        }

    test <@ contract.IsReentrant @>
    test <@ contract.Operations |> Array.exists (fun op -> op.FieldName = "history" && op.IsReadOnly) @>
    test <@ contract.Operations |> Array.exists (fun op -> op.FieldName = "typing" && op.IsOneWay) @>

// ──────────────────────────────────────────────────────────────────────────────
// Spec 004 item 7 -- version-tolerant contracts
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a contract accepts its own version exactly by default`` () =
    let contract = baseContract ()

    test <@ contract.AcceptedVersions = Exact @>
    test <@ contract.MinAcceptedVersion = 1 @>
    test <@ contract.Operations |> Array.forall (fun op -> op.SinceVersion = 1) @>

[<Fact>]
let ``acceptsVersions BackwardCompatible lowers the admitted floor`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            version 4
            stringKey
            acceptsVersions (BackwardCompatible 2)
        }

    test <@ contract.AcceptedVersions = BackwardCompatible 2 @>
    test <@ contract.MinAcceptedVersion = 2 @>

[<Fact>]
let ``acceptsVersions is rejected twice`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 4
                stringKey
                acceptsVersions (BackwardCompatible 2)
                acceptsVersions Exact
            }
            |> ignore)

    test <@ error.Message.Contains "'acceptsVersions' is already set" @>

[<Fact>]
let ``a non-positive backward-compatible floor is rejected`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 4
                stringKey
                acceptsVersions (BackwardCompatible 0)
            }
            |> ignore)

    test <@ error.Message.Contains "requires a positive minimum version" @>

[<Fact>]
let ``a backward-compatible floor above the contract version is rejected`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 2
                stringKey
                acceptsVersions (BackwardCompatible 3)
            }
            |> ignore)

    test <@ error.Message.Contains "would admit no request at all" @>

[<Fact>]
let ``sinceVersion is recorded on the selected operation only`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            version 4
            stringKey
            acceptsVersions (BackwardCompatible 2)
            sinceVersion 4 (_.typing)
        }

    let sinceOf name =
        contract.Operations |> Array.find (fun op -> op.FieldName = name) |> fun op -> op.SinceVersion

    test <@ sinceOf "typing" = 4 @>
    test <@ sinceOf "join" = 1 @>

[<Fact>]
let ``sinceVersion is rejected twice on one operation`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 4
                stringKey
                acceptsVersions (BackwardCompatible 2)
                sinceVersion 3 (_.typing)
                sinceVersion 4 (_.typing)
            }
            |> ignore)

    test <@ error.Message.Contains "'sinceVersion' is applied more than once" @>

[<Fact>]
let ``a non-positive sinceVersion is rejected`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 4
                stringKey
                acceptsVersions (BackwardCompatible 2)
                sinceVersion 0 (_.typing)
            }
            |> ignore)

    test <@ error.Message.Contains "must be a positive integer" @>

[<Fact>]
let ``a sinceVersion above the contract version is rejected`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 4
                stringKey
                acceptsVersions (BackwardCompatible 2)
                sinceVersion 5 (_.typing)
            }
            |> ignore)

    test <@ error.Message.Contains "is above the contract version 4" @>

/// <remarks>
/// The uniform "dead declaration" rule: a sinceVersion at or below the lowest admitted version
/// can never reject anything. Under the default Exact policy that is every legal value, which is
/// exactly the mistake of declaring sinceVersion and forgetting acceptsVersions.
/// </remarks>
[<Fact>]
let ``a sinceVersion that can never reject is refused under the default policy`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 4
                stringKey
                sinceVersion 4 (_.typing)
            }
            |> ignore)

    test <@ error.Message.Contains "can never reject a call" @>
    test <@ error.Message.Contains "'acceptsVersions Exact' policy admits version 4 only" @>

[<Fact>]
let ``a sinceVersion at the backward-compatible floor is refused`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                version 4
                stringKey
                acceptsVersions (BackwardCompatible 3)
                sinceVersion 3 (_.typing)
            }
            |> ignore)

    test <@ error.Message.Contains "can never reject a call" @>
    test <@ error.Message.Contains "admits versions 3 through 4" @>

/// <remarks>
/// Admission-only, at the contract layer: a wider policy changes neither the stable operation
/// IDs nor the grain identity, so nothing about storage or routing moves when it is declared.
/// </remarks>
[<Fact>]
let ``a version policy changes neither operation IDs nor grain identity`` () =
    let strict =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            version 4
            stringKey
        }

    let tolerant =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            version 4
            stringKey
            acceptsVersions (BackwardCompatible 1)
        }

    let idsOf (contract: GrainContract<ChatActor, string, ChatApi>) =
        contract.Operations |> Array.map (fun op -> op.OperationId)

    let flagsOf (contract: GrainContract<ChatActor, string, ChatApi>) =
        contract.Operations |> Array.map (fun op -> op.AdmissionFlags)

    test <@ idsOf strict = idsOf tolerant @>
    test <@ strict.GrainIdOf "general" = tolerant.GrainIdOf "general" @>
    test <@ flagsOf strict = flagsOf tolerant @>

// ──────────────────────────────────────────────────────────────────────────────
// Spec 004 item 2 — transactional operations
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``transactional stores the declared option and encodes it in the admission byte`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            stringKey
            transactional Orleans.TransactionOption.CreateOrJoin (_.join)
            transactional Orleans.TransactionOption.Supported (_.say)
        }

    let join = (contract.TryFindField "join").Value
    let say = (contract.TryFindField "say").Value
    let history = (contract.TryFindField "history").Value

    test <@ join.Transaction = Some Orleans.TransactionOption.CreateOrJoin @>
    test <@ say.Transaction = Some Orleans.TransactionOption.Supported @>
    test <@ history.Transaction = None @>

    test <@ AdmissionFlags.tryTransactionOption join.AdmissionFlags = Some Orleans.TransactionOption.CreateOrJoin @>
    test <@ AdmissionFlags.tryTransactionOption say.AdmissionFlags = Some Orleans.TransactionOption.Supported @>
    test <@ not (AdmissionFlags.isTransactional history.AdmissionFlags) @>

[<Fact>]
let ``transaction-scoped is exactly Create, CreateOrJoin, and Join`` () =
    // The same three options for which Orleans' own TransactionRequestBase.IsTransactionRequired
    // is true. Supported forwards a caller's context but starts none, so it is not scoped.
    let scopedFor (option: Orleans.TransactionOption) =
        let contract =
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                transactional option (_.join)
            }

        let join = (contract.TryFindField "join").Value
        join.IsTransactionScoped, join.CanCarryTransaction

    test <@ scopedFor Orleans.TransactionOption.Create = (true, true) @>
    test <@ scopedFor Orleans.TransactionOption.CreateOrJoin = (true, true) @>
    test <@ scopedFor Orleans.TransactionOption.Join = (true, true) @>
    test <@ scopedFor Orleans.TransactionOption.Supported = (false, true) @>
    test <@ scopedFor Orleans.TransactionOption.Suppress = (false, false) @>
    test <@ scopedFor Orleans.TransactionOption.NotAllowed = (false, false) @>

[<Fact>]
let ``transactional is rejected twice on one operation`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                transactional Orleans.TransactionOption.Create (_.join)
                transactional Orleans.TransactionOption.Join (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "'transactional' is applied more than once" @>

[<Fact>]
let ``transactional rejects an undefined option value`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                transactional (enum<Orleans.TransactionOption> 99) (_.join)
            }
            |> ignore)

    test <@ error.Message.Contains "undefined Orleans.TransactionOption value 99" @>

[<Fact>]
let ``transactional rejects oneWay`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                oneWay (_.typing)
                transactional Orleans.TransactionOption.CreateOrJoin (_.typing)
            }
            |> ignore)

    test <@ error.Message.Contains "combines 'transactional' with 'oneWay'" @>

[<Fact>]
let ``transactional rejects alwaysInterleave`` () =
    let error =
        throws (fun () ->
            grainContract<ChatActor, string, ChatApi> {
                grainType "chat.room"
                stringKey
                readOnly (_.history)
                alwaysInterleave (_.history)
                transactional Orleans.TransactionOption.CreateOrJoin (_.history)
            }
            |> ignore)

    test <@ error.Message.Contains "combines 'transactional' with 'alwaysInterleave'" @>

[<Fact>]
let ``transactional composes with readOnly`` () =
    let contract =
        grainContract<ChatActor, string, ChatApi> {
            grainType "chat.room"
            stringKey
            readOnly (_.history)
            transactional Orleans.TransactionOption.CreateOrJoin (_.history)
        }

    let history = (contract.TryFindField "history").Value

    test <@ history.IsReadOnly @>
    test <@ history.IsTransactionScoped @>

    let expected =
        AdmissionFlags.ReadOnly
        ||| AdmissionFlags.encodeTransaction Orleans.TransactionOption.CreateOrJoin

    test <@ history.AdmissionFlags = expected @>
