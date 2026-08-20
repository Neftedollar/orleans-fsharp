/// <summary>
/// Spec 004 item 6: the contract-, definition- and facade-level rules of a server-streaming
/// operation. Everything here runs without a cluster; the wire behaviour is proven by the Phase F
/// integration suite.
/// </summary>
module Orleans.FSharp.Tests.FunctionalStreamingContractTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open FSharp.Control
open Xunit
open Swensen.Unquote
open Orleans.FSharp

type StreamActor = private StreamActor of unit

/// <summary>A contract mixing the two field kinds, so nothing here is about a streaming-only API.</summary>
[<NoEquality; NoComparison>]
type MixedApi =
    { post: string -> Task<int64>
      watch: int -> IAsyncEnumerable<string>
      notify: string -> Task<unit> }

/// <summary>A range that is neither <c>Task&lt;_&gt;</c> nor <c>IAsyncEnumerable&lt;_&gt;</c>.</summary>
[<NoEquality; NoComparison>]
type SyncSequenceApi = { walk: int -> seq<string> }

/// <summary>An empty stream, for definitions whose handler bodies are beside the point.</summary>
let private noItems<'T> () : IAsyncEnumerable<'T> = taskSeq { () }

let private throws (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

let private mixedContract () =
    grainContract<StreamActor, string, MixedApi> {
        grainType "stream.mixed"
        stringKey
    }

/// <summary>
/// A draft built directly, so the rules that the F# types already make unreachable from the
/// computation expression can still be exercised. Every admission policy takes an
/// <c>OperationSelector</c>, whose range is <c>Task&lt;'Reply&gt;</c>, so none of them accepts
/// <c>_.watch</c> — which is the first line of defence, and the reason these are defensive tests.
/// </summary>
let private draftWith
    (readOnly: Set<int>)
    (oneWay: Set<int>)
    (alwaysInterleave: Set<int>)
    (transactions: Map<int, Orleans.TransactionOption>)
    =
    let state: ContractDraftState<string> =
        { Shape = ApiShape.of'<MixedApi> ()
          GrainTypeName = Some "stream.mixed"
          Version = None
          AcceptedVersions = None
          IsReentrant = false
          MayInterleave = None
          KeyCodec = Some KeyCodecs.stringKey
          ReadOnly = readOnly
          OneWay = oneWay
          AlwaysInterleave = alwaysInterleave
          Transactions = transactions
          SinceVersions = Map.empty
          OperationIds = Map.empty }

    ContractDraft.withState<StreamActor, string, MixedApi> state

/// The declaration index of the streaming field.
let private watchIndex = 1

// ──────────────────────────────────────────────────────────────────────────────
// Shape recognition
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a streaming field is recognized structurally and carries its item type`` () =
    let shape = ApiShape.of'<MixedApi> ()

    test <@ shape.Operations |> Array.map (fun operation -> operation.FieldName) = [| "post"; "watch"; "notify" |] @>
    test <@ shape.Operations |> Array.map (fun operation -> operation.IsStreaming) = [| false; true; false |] @>
    test <@ shape.Operations.[watchIndex].ArgumentType = typeof<int> @>
    test <@ shape.Operations.[watchIndex].ReplyType = typeof<string> @>

[<Fact>]
let ``a synchronous sequence range is still rejected, and the diagnostic names both shapes`` () =
    let error = throws (fun () -> ApiShape.of'<SyncSequenceApi> () |> ignore)

    test <@ error.Message.Contains "'Argument -> Task<'Reply>" @>
    test <@ error.Message.Contains "'Argument -> IAsyncEnumerable<'Item>" @>

[<Fact>]
let ``a streaming selector resolves the streaming field`` () =
    let contract = mixedContract ()
    let operation = contract.ResolveStream("test", (fun api -> api.watch))

    test <@ operation.FieldName = "watch" @>
    test <@ operation.IsStreaming @>

// ──────────────────────────────────────────────────────────────────────────────
// Protocol tokens
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a streaming operation carries the two streaming token directions`` () =
    let contract = mixedContract ()
    let watch = contract.Operations.[watchIndex]
    let post = contract.Operations.[0]

    test <@ watch.RequestToken = ProtocolToken.streamRequest "stream.mixed" 1 "watch" @>
    test <@ watch.ReplyToken = ProtocolToken.streamItem "stream.mixed" 1 "watch" @>
    test <@ post.RequestToken = ProtocolToken.request "stream.mixed" 1 "post" @>
    test <@ post.ReplyToken = ProtocolToken.reply "stream.mixed" 1 "post" @>

/// <summary>
/// The reason the two new directions exist. If a streaming operation reused <c>request</c>, then a
/// caller that still believes an operation is unary would present exactly the digest a host that
/// has since made it streaming expects for that caller's version, and the call would be admitted.
/// </summary>
[<Fact>]
let ``a streaming token cannot be confused with the unary token of the same operation`` () =
    let streamRequest = ProtocolToken.streamRequest "g" 3 "watch"
    let streamItem = ProtocolToken.streamItem "g" 3 "watch"
    let unaryRequest = ProtocolToken.request "g" 3 "watch"
    let unaryReply = ProtocolToken.reply "g" 3 "watch"
    let notify = ProtocolToken.notify "g" 3 "watch"

    let all = [ streamRequest; streamItem; unaryRequest; unaryReply; notify ]

    test <@ all |> List.forall (fun token -> token.Length = ProtocolToken.Length) @>
    test <@ all |> List.map ProtocolToken.toHex |> List.distinct |> List.length = 5 @>

[<Fact>]
let ``a streaming operation carries no admission flags`` () =
    let contract = mixedContract ()
    test <@ contract.Operations.[watchIndex].AdmissionFlags = AdmissionFlags.None @>

// ──────────────────────────────────────────────────────────────────────────────
// Sealing rejections, one per composed policy
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``readOnly on a streaming field is rejected`` () =
    let draft = draftWith (Set.ofList [ watchIndex ]) Set.empty Set.empty Map.empty
    let error = throws (fun () -> ContractDraft.run draft |> ignore)

    test <@ error.Message.Contains "'readOnly' on a streaming operation" @>
    test <@ error.Message.Contains "AlwaysInterleave" @>

[<Fact>]
let ``oneWay on a streaming field is rejected`` () =
    let draft = draftWith Set.empty (Set.ofList [ watchIndex ]) Set.empty Map.empty
    let error = throws (fun () -> ContractDraft.run draft |> ignore)

    test <@ error.Message.Contains "combines 'oneWay' with a streaming reply" @>

[<Fact>]
let ``alwaysInterleave on a streaming field is rejected`` () =
    let draft = draftWith Set.empty Set.empty (Set.ofList [ watchIndex ]) Map.empty
    let error = throws (fun () -> ContractDraft.run draft |> ignore)

    test <@ error.Message.Contains "'alwaysInterleave' on a streaming operation" @>

[<Fact>]
let ``transactional on a streaming field is rejected`` () =
    let draft =
        draftWith Set.empty Set.empty Set.empty (Map.ofList [ watchIndex, Orleans.TransactionOption.CreateOrJoin ])

    let error = throws (fun () -> ContractDraft.run draft |> ignore)

    test <@ error.Message.Contains "combines 'transactional' with a streaming reply" @>

/// <summary>
/// The mutation check for all four: the same draft with the flags on a UNARY field is accepted, so
/// the rejections above are about the streaming field and not about the draft being malformed.
/// </summary>
[<Fact>]
let ``the same policies on a unary field are accepted`` () =
    let draft =
        draftWith (Set.ofList [ 0 ]) (Set.ofList [ 2 ]) (Set.ofList [ 0 ]) Map.empty

    let contract = ContractDraft.run draft

    test <@ contract.Operations.[0].IsReadOnly @>
    test <@ contract.Operations.[2].IsOneWay @>
    test <@ not contract.Operations.[watchIndex].IsReadOnly @>

// ──────────────────────────────────────────────────────────────────────────────
// The two operations that DO compose
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``operationId and sinceVersion apply to a streaming field`` () =
    let contract =
        grainContract<StreamActor, string, MixedApi> {
            grainType "stream.mixed"
            version 3
            acceptsVersions (BackwardCompatible 1)
            stringKey
            operationId "watch-v2" (fun api -> api.watch)
            sinceVersion 2 (fun api -> api.watch)
        }

    let watch = contract.Operations.[watchIndex]

    test <@ watch.OperationId = "watch-v2" @>
    test <@ watch.SinceVersion = 2 @>
    test <@ watch.RequestToken = ProtocolToken.streamRequest "stream.mixed" 3 "watch-v2" @>

[<Fact>]
let ``a dead sinceVersion on a streaming field is rejected exactly as on a unary one`` () =
    let error =
        throws (fun () ->
            grainContract<StreamActor, string, MixedApi> {
                grainType "stream.mixed"
                version 2
                stringKey
                sinceVersion 2 (fun api -> api.watch)
            }
            |> ignore)

    test <@ error.Message.Contains "can never reject a call" @>

// ──────────────────────────────────────────────────────────────────────────────
// Definition rules
// ──────────────────────────────────────────────────────────────────────────────

let private streamingDefinition () =
    grainFor (mixedContract ()) {
        defaultState (fun () -> 0)
        handle (_.post) (fun _ state (_: string) -> task { return state, 1L })
        handleStream (_.watch) (fun _ _ (_: int) -> noItems<string> ())
        handle (_.notify) (fun _ state (_: string) -> task { return state, () })
    }

[<Fact>]
let ``a definition with a streaming handler seals`` () =
    let definition = streamingDefinition ()
    test <@ definition.GrainTypeName = "stream.mixed" @>

[<Fact>]
let ``a missing streaming handler is reported by field name`` () =
    let error =
        throws (fun () ->
            grainFor (mixedContract ()) {
                defaultState (fun () -> 0)
                handle (_.post) (fun _ state (_: string) -> task { return state, 1L })
                handle (_.notify) (fun _ state (_: string) -> task { return state, () })
            }
            |> ignore)

    test <@ error.Message.Contains "no handler for API field(s) watch" @>

[<Fact>]
let ``a repeated streaming handler is rejected`` () =
    let error =
        throws (fun () ->
            grainFor (mixedContract ()) {
                defaultState (fun () -> 0)
                handle (_.post) (fun _ state (_: string) -> task { return state, 1L })
                handleStream (_.watch) (fun _ _ (_: int) -> noItems<string> ())
                handleStream (_.watch) (fun _ _ (_: int) -> noItems<string> ())
                handle (_.notify) (fun _ state (_: string) -> task { return state, () })
            }
            |> ignore)

    test <@ error.Message.Contains "already has a handler" @>

/// <summary>
/// Spec 004 item 6. An open enumeration lives in one activation's grain extension and every
/// <c>MoveNext</c> has to find it there; a stateless worker routes each message to whichever local
/// worker is free, on whichever silo the caller reached.
/// </summary>
[<Fact>]
let ``statelessWorker combined with a streaming operation is rejected`` () =
    let error =
        throws (fun () ->
            grainFor (mixedContract ()) {
                defaultState (fun () -> 0)
                statelessWorker 4
                handle (_.post) (fun _ state (_: string) -> task { return state, 1L })
                handleStream (_.watch) (fun _ _ (_: int) -> noItems<string> ())
                handle (_.notify) (fun _ state (_: string) -> task { return state, () })
            }
            |> ignore)

    test <@ error.Message.Contains "combines 'statelessWorker' with the streaming API field 'watch'" @>
    test <@ error.Message.Contains "placement PreferLocal" @>

/// <summary>The mutation check: the same definition with a stock placement strategy seals.</summary>
[<Fact>]
let ``an explicit placement strategy combined with a streaming operation is accepted`` () =
    let definition =
        grainFor (mixedContract ()) {
            defaultState (fun () -> 0)
            placement PreferLocal
            handle (_.post) (fun _ state (_: string) -> task { return state, 1L })
            handleStream (_.watch) (fun _ _ (_: int) -> noItems<string> ())
            handle (_.notify) (fun _ state (_: string) -> task { return state, () })
        }

    test <@ definition.GrainTypeName = "stream.mixed" @>

// ──────────────────────────────────────────────────────────────────────────────
// Facade rules
// ──────────────────────────────────────────────────────────────────────────────

type IGoodFacade =
    abstract Watch: count: int -> IAsyncEnumerable<string>

type IWrongReturnFacade =
    abstract Watch: count: int -> Task<string>

/// <summary>
/// The factory is the unconfigured harness one on purpose: a rule that fired only after the
/// reference was bound would fail with the transport's diagnostic instead of the facade's, so the
/// message asserted here is proof the return rule ran first.
/// </summary>
[<Fact>]
let ``a facade member over a streaming operation must return IAsyncEnumerable`` () =
    let contract = mixedContract ()

    let wrong =
        throws (fun () ->
            FunctionalGrainInterop.For<IWrongReturnFacade>(
                contract,
                FunctionalTransportHarness.UnconfiguredFactory(),
                box "k"
            )
            |> ignore)

    test <@ wrong.Message.Contains "is a streaming operation and requires 'IAsyncEnumerable<System.String>'" @>

    // The right shape gets past that rule and fails later, on binding — which is what shows the
    // rule accepted it rather than being skipped.
    let accepted =
        throws (fun () ->
            FunctionalGrainInterop.For<IGoodFacade>(
                contract,
                FunctionalTransportHarness.UnconfiguredFactory(),
                box "k"
            )
            |> ignore)

    test <@ not (accepted.Message.Contains "is a streaming operation and requires") @>
