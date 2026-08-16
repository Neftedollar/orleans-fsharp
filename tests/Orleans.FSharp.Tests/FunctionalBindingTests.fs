/// <summary>
/// Binding and bound-call tests for spec 003 Phase 2, exercised over the in-memory transport:
/// what every bound field sends, the cached API instance, reply validation before
/// deserialization, serializer preflight at binding time, object-graph isolation across the
/// byte boundary, the caller-side payload boundaries, and the structural promises about the
/// hot path and serializer sessions.
/// </summary>
module Orleans.FSharp.Tests.FunctionalBindingTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Serialization
open Orleans.Serialization.Codecs
open Orleans.Serialization.Serializers
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Tests.FunctionalTransportHarness

type BindActor = private BindActor of unit

type Blob = { name: string; bytes: byte[] }

type Item =
    | Plain of string
    | Binary of byte[]

/// <summary>A mutable object, deliberately not an immutable F# value.</summary>
type Cursor() =
    member val Position = 0 with get, set

type Basket =
    { items: Item list
      tags: string option
      counter: int ref
      cursor: Cursor }

[<NoEquality; NoComparison>]
type BindApi =
    { join: string -> Task<unit>
      say: Blob -> Task<int64>
      history: int -> Task<string list>
      typing: bool -> Task<unit> }

let private contract =
    grainContract<BindActor, string, BindApi> () {
        grainType "bind.test"
        version 3
        stringKey

        operationId "chat" (_.say)
        readOnly (_.history)
        oneWay (_.typing)
        alwaysInterleave (_.typing)
    }

/// <summary>A contract whose argument type has no registered codec.</summary>
type Unserializable(value: int) =
    member _.Value = value

type PreflightActor = private PreflightActor of unit

[<NoEquality; NoComparison>]
type PreflightApi = { send: Unserializable -> Task<int> }

let private preflightContract =
    grainContract<PreflightActor, string, PreflightApi> () {
        grainType "bind.preflight"
        stringKey
    }

// ──────────────────────────────────────────────────────────────────────────────
// Fixtures
// ──────────────────────────────────────────────────────────────────────────────

let private newTarget (services: IServiceProvider) =
    let target = InMemoryTarget(services, "bind.test", 3)
    target.Handle<string, unit>("join", fun _ -> ())
    target.Handle<Blob, int64>("chat", fun blob -> int64 blob.bytes.Length)
    target.Handle<int, string list>("history", fun take -> [ for index in 1..take -> $"m{index}" ])
    target.Handle<bool, unit>("typing", fun _ -> ())
    target

/// <summary>A bound reference over a fresh in-memory transport.</summary>
let private bindWith (services: IServiceProvider) (target: InMemoryTarget) =
    let transport = InMemoryTransport(services, target.Dispatch)
    transport, FunctionalGrain.rawRef contract transport "general"

let private bind () =
    let services = buildServices true None
    let target = newTarget services
    let transport, reference = bindWith services target
    services, target, transport, reference

// ──────────────────────────────────────────────────────────────────────────────
// What a bound field sends
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``every bound field sends its operation, version, token, and flags`` () =
    let _services, _target, transport, reference = bind ()

    task {
        do! reference.api.join "alice"
        let! _ = reference.api.say { name = "hello"; bytes = [| 1uy; 2uy |] }
        let! _ = reference.api.history 2
        do! reference.api.typing true

        let sent = transport.Calls

        let expected =
            [| "join", AdmissionFlags.None
               "chat", AdmissionFlags.None
               "history", AdmissionFlags.ReadOnly
               "typing", AdmissionFlags.OneWay ||| AdmissionFlags.AlwaysInterleave |]

        test <@ sent |> Array.map (fun call -> call.Envelope.OperationId, call.Envelope.AdmissionFlags) = expected @>
        test <@ sent |> Array.forall (fun call -> call.Envelope.GrainType = "bind.test") @>
        test <@ sent |> Array.forall (fun call -> call.Envelope.ContractVersion = 3) @>

        test
            <@
                sent
                |> Array.forall (fun call ->
                    ProtocolToken.equal
                        call.Envelope.ProtocolToken
                        (ProtocolToken.request "bind.test" 3 call.Envelope.OperationId))
            @>

        // The one-way field acknowledges the local send only.
        test <@ sent |> Array.map (fun call -> call.IsOneWay) = [| false; false; false; true |] @>
    }

[<Fact>]
let ``a bound call addresses the encoded grain identity through the closed target interface`` () =
    let _services, _target, transport, reference = bind ()

    task {
        do! reference.api.join "alice"

        let call = transport.LastCall
        let expectedGrainId = contract.GrainIdOf "general"

        test <@ call.GrainId = expectedGrainId @>
        test <@ call.GrainId.Type.ToString() = "bind.test" @>
        test <@ call.GrainId.Key.ToString() = "general" @>
        test <@ call.Metadata.InterfaceId = "orleans.fsharp.functional/bind.test" @>
        test <@ call.Metadata.InterfaceType = typeof<IFunctionalGrainTarget<BindActor>> @>
        test <@ not call.Metadata.InterfaceType.ContainsGenericParameters @>
        test <@ call.Metadata.DispatchMethod.Name = "DispatchAsync" @>
        test <@ call.Metadata.DispatchMethod.DeclaringType = call.Metadata.InterfaceType @>
    }

[<Fact>]
let ``the operation id override reaches the wire`` () =
    let _services, _target, transport, reference = bind ()

    task {
        let! _ = reference.api.say { name = "x"; bytes = [||] }

        test <@ transport.LastCall.Envelope.OperationId = "chat" @>
    }

[<Fact>]
let ``the bound record is one cached instance whose fields are the preclosed closures`` () =
    let _services, _target, _transport, reference = bind ()

    let first = reference.api
    let second = reference.api

    test <@ obj.ReferenceEquals(first, second) @>
    test <@ obj.ReferenceEquals(first.join, second.join) @>
    test <@ obj.ReferenceEquals(first.say, second.say) @>

[<Fact>]
let ``ref returns the same record the raw reference exposes`` () =
    let services = buildServices true None
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)

    let raw = FunctionalGrain.rawRef contract transport "general"
    let api = FunctionalGrain.ref contract transport "general"

    // Two bindings are two records, but each binding's record is stable.
    test <@ not (obj.ReferenceEquals(raw.api, api)) @>
    test <@ obj.ReferenceEquals(raw.api, raw.api) @>

[<Fact>]
let ``selector-based calls reach the same bound closures`` () =
    let _services, _target, transport, reference = bind ()

    task {
        let! byField = reference.api.history 1
        let! bySelector = reference.call (_.history) 1
        let! byCancellable = reference.callCancellable (_.history) 1 CancellationToken.None

        test <@ byField = bySelector @>
        test <@ byField = byCancellable @>
        test <@ transport.Calls |> Array.forall (fun call -> call.Envelope.OperationId = "history") @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Payload bytes
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the request payload is exactly the serialized argument and nothing else`` () =
    let services, _target, transport, reference = bind ()
    let codec = payloadCodec services
    let argument = { name = "hello"; bytes = [| 9uy; 8uy; 7uy |] }

    task {
        let! _ = reference.api.say argument

        let sent = transport.LastCall.Envelope.Payload
        test <@ sent = codec.Serialize<Blob> argument @>

        // Nothing from the contract, the facade, or the runtime is in those bytes.
        let text = Text.Encoding.UTF8.GetString sent
        test <@ not (text.Contains "BindApi") @>
        test <@ not (text.Contains "GrainContract") @>
        test <@ not (text.Contains "bind.test") @>
    }

[<Fact>]
let ``each call allocates a fresh payload array`` () =
    let _services, _target, transport, reference = bind ()
    let argument = { name = "hello"; bytes = [| 1uy |] }

    task {
        let! _ = reference.api.say argument
        let! _ = reference.api.say argument

        let payloads = transport.Calls |> Array.map (fun call -> call.Envelope.Payload)

        test <@ payloads.[0] = payloads.[1] @>
        test <@ not (obj.ReferenceEquals(payloads.[0], payloads.[1])) @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Reply validation
// ──────────────────────────────────────────────────────────────────────────────

let private replyingTransport (services: IServiceProvider) (reply: FunctionalRequestEnvelope -> FunctionalReply) =
    InMemoryTransport(services, (fun _grainId envelope -> Task.FromResult(reply envelope)))

[<Fact>]
let ``a reply carrying the wrong protocol token is rejected before deserialization`` () =
    let services = buildServices true None
    let codec = payloadCodec services

    let transport =
        replyingTransport services (fun _ ->
            // A well-formed reply for a different operation, with a payload that would not
            // deserialize as the expected reply type either.
            FunctionalReply(ProtocolToken.reply "bind.test" 3 "history", codec.Serialize<string> "not an int64"))

    let reference = FunctionalGrain.rawRef contract transport "general"

    task {
        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                reference.api.say { name = "x"; bytes = [||] } :> Task)

        test <@ error.Message.Contains "protocol token" @>
        test <@ error.Message.Contains "chat" @>
        test <@ error.Message.Contains "bind.test" @>
    }

[<Fact>]
let ``a missing reply is rejected`` () =
    let services = buildServices true None

    let transport =
        InMemoryTransport(services, (fun _ _ -> Task.FromResult Unchecked.defaultof<FunctionalReply>))

    let reference = FunctionalGrain.rawRef contract transport "general"

    task {
        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                reference.api.say { name = "x"; bytes = [||] } :> Task)

        test <@ error.Message.Contains "returned no reply" @>
    }

[<Fact>]
let ``a valid reply token lets the typed deserialization run`` () =
    let services = buildServices true None
    let codec = payloadCodec services

    let transport =
        replyingTransport services (fun envelope ->
            // The right token for the right operation, but a payload of the wrong type.
            FunctionalReply(ProtocolToken.reply "bind.test" 3 envelope.OperationId, codec.Serialize<string> "nope"))

    let reference = FunctionalGrain.rawRef contract transport "general"

    task {
        let! error = Assert.ThrowsAnyAsync<exn>(fun () -> reference.api.say { name = "x"; bytes = [||] } :> Task)

        // The protocol check passed; the failure came from the typed reply deserialization.
        test <@ not (error.Message.Contains "protocol token") @>
    }

[<Fact>]
let ``an oversized reply is rejected at the caller reply boundary`` () =
    let services = buildServices true (Some 64)
    let codec = payloadCodec services

    let transport =
        replyingTransport services (fun envelope ->
            FunctionalReply(
                ProtocolToken.reply "bind.test" 3 envelope.OperationId,
                codec.Serialize<string list> [ String('x', 4096) ]
            ))

    let reference = FunctionalGrain.rawRef contract transport "general"

    task {
        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () -> reference.api.history 1 :> Task)

        test <@ error.Message.Contains "caller reply receive" @>
        test <@ error.Message.Contains "reply" @>
        test <@ error.Message.Contains "64" @>
        test <@ error.Message.Contains "history" @>
    }

[<Fact>]
let ``an oversized request is rejected at the caller send boundary before anything is sent`` () =
    let services = buildServices true (Some 64)
    let target = newTarget services
    let transport, reference = bindWith services target

    task {
        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                reference.api.say { name = String('x', 4096); bytes = [||] } :> Task)

        test <@ error.Message.Contains "caller request send" @>
        test <@ error.Message.Contains "request" @>
        test <@ error.Message.Contains "chat" @>
        test <@ transport.Calls = [||] @>
    }

[<Fact>]
let ``the silo boundaries are enforced by the same helper`` () =
    let services = buildServices true None
    let target = newTarget services
    target.RequestLimit <- 8
    let _transport, reference = bindWith services target

    task {
        let! requestError =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                reference.api.say { name = String('x', 512); bytes = [||] } :> Task)

        test <@ requestError.Message.Contains "silo request receive" @>

        let services = buildServices true None
        let target = newTarget services
        target.ReplyLimit <- 4
        let _transport, reference = bindWith services target

        let! replyError =
            Assert.ThrowsAsync<InvalidOperationException>(fun () -> reference.api.history 32 :> Task)

        test <@ replyError.Message.Contains "silo reply send" @>
    }

[<Fact>]
let ``a non-positive configured payload limit fails binding`` () =
    let services = buildServices true (Some 0)
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalGrain.rawRef contract transport "general" |> ignore)

    test <@ error.Message.Contains "MaxPayloadBytes" @>

// ──────────────────────────────────────────────────────────────────────────────
// Serializer preflight
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``binding fails when an argument type has no registered codec`` () =
    let services = buildServices false None
    let transport = InMemoryTransport(services, (fun _ _ -> failwith "no call must be made"))

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalGrain.rawRef preflightContract transport "general" |> ignore)

    test <@ error.Message.Contains "Unserializable" @>
    test <@ error.Message.Contains "bind.preflight" @>
    test <@ error.Message.Contains "send" @>
    test <@ error.InnerException :? CodecNotFoundException @>

[<Fact>]
let ``binding succeeds once the codec is registered`` () =
    let services = buildServices true None
    let transport = InMemoryTransport(services, (fun _ _ -> failwith "no call must be made"))

    // The F# binary codec claims POCO types, so preflight now resolves a codec.
    FunctionalGrain.rawRef preflightContract transport "general" |> ignore

/// <summary>An <c>ICodecProvider</c> which counts how often preflight asks it for a codec.</summary>
type private CountingCodecProvider(inner: ICodecProvider) =
    let mutable calls = 0

    member _.Calls = calls

    interface ICodecProvider with
        member _.Services = inner.Services

    interface IFieldCodecProvider with
        member _.GetCodec<'T>() = inner.GetCodec<'T>()
        member _.TryGetCodec<'T>() = inner.TryGetCodec<'T>()

        member _.GetCodec(fieldType: Type) =
            Interlocked.Increment &calls |> ignore
            inner.GetCodec fieldType

        member _.TryGetCodec(fieldType: Type) = inner.TryGetCodec fieldType

    interface IBaseCodecProvider with
        member _.GetBaseCodec<'T when 'T: not struct>() = inner.GetBaseCodec<'T>()

    interface IValueSerializerProvider with
        member _.GetValueSerializer<'T when 'T: struct and 'T :> ValueType and 'T: (new: unit -> 'T)>() =
            inner.GetValueSerializer<'T>()

    interface IActivatorProvider with
        member _.GetActivator<'T>() = inner.GetActivator<'T>()

    interface Orleans.Serialization.Cloning.IDeepCopierProvider with
        member _.GetDeepCopier<'T>() = inner.GetDeepCopier<'T>()
        member _.TryGetDeepCopier<'T>() = inner.TryGetDeepCopier<'T>()
        member _.GetDeepCopier(fieldType: Type) = inner.GetDeepCopier fieldType
        member _.TryGetDeepCopier(fieldType: Type) = inner.TryGetDeepCopier fieldType
        member _.GetBaseCopier<'T when 'T: not struct>() = inner.GetBaseCopier<'T>()

[<Fact>]
let ``preflight caches success per contract shape and serializer instance`` () =
    let services = buildServices true None
    let inner = services.GetRequiredService<ICodecProvider>()
    let counting = CountingCodecProvider inner
    let declared = contract.DeclaredTypes

    SerializerPreflight.ensure counting "bind.test" contract.ApiType declared
    let afterFirst = counting.Calls

    SerializerPreflight.ensure counting "bind.test" contract.ApiType declared
    SerializerPreflight.ensure counting "bind.test" contract.ApiType declared

    // Two types per operation on the first pass, nothing afterwards.
    test <@ afterFirst = declared.Length * 2 @>
    test <@ counting.Calls = afterFirst @>

    // A different serializer instance validates again.
    let other = CountingCodecProvider inner
    SerializerPreflight.ensure other "bind.test" contract.ApiType declared
    test <@ other.Calls = afterFirst @>

// ──────────────────────────────────────────────────────────────────────────────
// Object-graph isolation across the byte boundary
// ──────────────────────────────────────────────────────────────────────────────

type IsolationActor = private IsolationActor of unit

[<NoEquality; NoComparison>]
type IsolationApi =
    { echo: Basket -> Task<Basket>
      blob: byte[] -> Task<byte[]> }

let private isolationContract =
    grainContract<IsolationActor, string, IsolationApi> () {
        grainType "bind.isolation"
        stringKey
    }

[<Fact>]
let ``a local call isolates the argument graph exactly like a remote one`` () =
    let services = buildServices true None
    let target = InMemoryTarget(services, "bind.isolation", 1)
    let mutable observed = Unchecked.defaultof<Basket>

    target.Handle<Basket, Basket>(
        "echo",
        fun basket ->
            observed <- basket
            basket
    )

    target.Handle<byte[], byte[]>("blob", id)

    let transport = InMemoryTransport(services, target.Dispatch)
    let reference = FunctionalGrain.rawRef isolationContract transport "general"

    let bytes = [| 1uy; 2uy; 3uy |]
    let counter = ref 7

    let cursor = Cursor(Position = 3)

    let argument =
        { items = [ Plain "one"; Binary bytes ]
          tags = Some "tag"
          counter = counter
          cursor = cursor }

    task {
        let! reply = reference.api.echo argument

        // The target saw an independent graph.
        test <@ not (obj.ReferenceEquals(observed, argument)) @>
        test <@ not (obj.ReferenceEquals(observed.counter, counter)) @>
        test <@ observed.counter.Value = 7 @>

        test <@ not (obj.ReferenceEquals(observed.cursor, cursor)) @>
        test <@ observed.cursor.Position = 3 @>

        // Mutating the caller's argument after the call cannot reach the target's copy.
        bytes.[0] <- 99uy
        counter.Value <- 42
        cursor.Position <- 99

        let observedBytes =
            observed.items
            |> List.pick (function
                | Binary value -> Some value
                | Plain _ -> None)

        test <@ observedBytes = [| 1uy; 2uy; 3uy |] @>
        test <@ observed.counter.Value = 7 @>
        test <@ observed.cursor.Position = 3 @>

        // The reply is an independent graph too.
        test <@ not (obj.ReferenceEquals(reply, observed)) @>
        test <@ not (obj.ReferenceEquals(reply.counter, observed.counter)) @>

        reply.counter.Value <- 1000
        reply.cursor.Position <- 1000

        test <@ observed.counter.Value = 7 @>
        test <@ observed.cursor.Position = 3 @>
    }

[<Fact>]
let ``a byte array reply is a fresh array`` () =
    let services = buildServices true None
    let target = InMemoryTarget(services, "bind.isolation", 1)
    let mutable retained = [||]

    target.Handle<byte[], byte[]>(
        "blob",
        fun value ->
            retained <- value
            value
    )

    target.Handle<Basket, Basket>("echo", id)

    let transport = InMemoryTransport(services, target.Dispatch)
    let reference = FunctionalGrain.rawRef isolationContract transport "general"
    let argument = [| 1uy; 2uy |]

    task {
        let! reply = reference.api.blob argument

        test <@ not (obj.ReferenceEquals(reply, argument)) @>
        test <@ not (obj.ReferenceEquals(reply, retained)) @>

        reply.[0] <- 77uy
        argument.[1] <- 88uy

        test <@ retained = [| 1uy; 2uy |] @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// One-way and cancellation
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a one-way call completes on the local send`` () =
    let _services, _target, transport, reference = bind ()

    task {
        let call = reference.api.typing true
        do! call

        test <@ call.IsCompletedSuccessfully @>
        test <@ transport.LastCall.IsOneWay @>
        test <@ transport.LastCall.Envelope.IsOneWay @>
    }

[<Fact>]
let ``a one-way call with an already cancelled token is cancelled without sending`` () =
    let _services, _target, transport, reference = bind ()
    use source = new CancellationTokenSource()
    source.Cancel()

    let call = reference.callCancellable (_.typing) true source.Token

    test <@ call.IsCanceled @>
    test <@ transport.Calls = [||] @>

[<Fact>]
let ``an acknowledged call forwards the caller token to the transport`` () =
    let _services, _target, transport, reference = bind ()
    use source = new CancellationTokenSource()
    source.Cancel()

    task {
        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                reference.callCancellable (_.history) 1 source.Token :> Task)

        test <@ transport.Calls.Length = 1 @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Hot path and serializer sessions
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``sealing a contract is where reflection, selectors, and generic closing happen`` () =
    let counters = FunctionalInstrumentation.start ()

    try
        grainContract<PreflightActor, string, PreflightApi> () {
            grainType "bind.instrumented"
            stringKey
        }
        |> ignore

        // One shape build for the API type is only counted the first time it is reflected,
        // but the descriptor closures are closed for every contract.
        test <@ counters.GenericClosings > 0 @>
    finally
        FunctionalInstrumentation.stop ()

[<Fact>]
let ``a bound call performs no reflection, selector evaluation, or generic closing`` () =
    let services = buildServices true None
    let target = newTarget services
    let transport, reference = bindWith services target

    task {
        // Warm every operation first — the once-per-payload-type codec build is not what this
        // test is about — then observe only steady-state calls.
        do! reference.api.join "warm"
        let! _ = reference.api.say { name = "warm"; bytes = [| 0uy |] }
        let! _ = reference.api.history 1
        do! reference.api.typing false

        let counters = FunctionalInstrumentation.start ()

        try
            do! reference.api.join "alice"
            let! _ = reference.api.say { name = "x"; bytes = [| 1uy |] }
            let! _ = reference.api.history 2
            do! reference.api.typing true

            test <@ counters.ApiShapeBuilds = 0 @>
            test <@ counters.SelectorEvaluations = 0 @>
            test <@ counters.GenericClosings = 0 @>
            // The payload codecs count their own build and their own generic closings, so the
            // two assertions above cover the serialization path and not only the binding one:
            // `history` returns a string list, whose codec closed ListModule.OfArray per call
            // until that closing moved into the build step.
            test <@ counters.CodecBuilds = 0 @>
            test <@ counters.PayloadSerializations > 0 @>
            test <@ counters.PayloadDeserializations > 0 @>
        finally
            FunctionalInstrumentation.stop ()

        test <@ transport.Calls.Length = 8 @>
    }

/// <summary>A payload type nothing else in the suite serializes, so its codec is really cold.</summary>
type InstrumentationProbe = { probe: string }

[<Fact>]
let ``the codec path is what those counters watch`` () =
    // The counterweight to the test above: a cold codec really does register on the same two
    // counters, so their being zero in a warm window is a fact about the codec and not about
    // the instrumentation being blind to it.
    let counters = FunctionalInstrumentation.start ()

    try
        let cold =
            FSharpBinaryFormat.serialize (box [ { probe = "cold" } ]) typeof<InstrumentationProbe list>

        FSharpBinaryFormat.deserialize cold typeof<InstrumentationProbe list> |> ignore

        test <@ counters.CodecBuilds > 0 @>
        test <@ counters.GenericClosings > 0 @>

        let warmBuilds = counters.CodecBuilds
        let warmClosings = counters.GenericClosings

        for _ in 1..5 do
            FSharpBinaryFormat.deserialize cold typeof<InstrumentationProbe list> |> ignore

        test <@ counters.CodecBuilds = warmBuilds @>
        test <@ counters.GenericClosings = warmClosings @>
    finally
        FunctionalInstrumentation.stop ()

[<Fact>]
let ``binding evaluates no selector and closes no generic`` () =
    let services = buildServices true None
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)

    // Two things are per-CONTRACT, not per-binding, and both are lazy: this module's
    // `let` values (F# initialises a file's values on first access, which builds the four
    // module-level contracts and their API shapes) and `GrainContract.TargetMetadata`
    // (a `lazy` that closes the target interface generic once). Force both with a throwaway
    // binding, so what the counters see below is one ordinary binding of a sealed contract
    // rather than whatever an arbitrary xUnit ordering left warm.
    FunctionalGrain.rawRef contract transport "warm" |> ignore

    let counters = FunctionalInstrumentation.start ()

    try
        FunctionalGrain.rawRef contract transport "general" |> ignore

        test <@ counters.ApiShapeBuilds = 0 @>
        test <@ counters.SelectorEvaluations = 0 @>
        test <@ counters.GenericClosings = 0 @>
    finally
        FunctionalInstrumentation.stop ()

[<Fact>]
let ``a raw selector call resolves its selector exactly once`` () =
    let services = buildServices true None
    let target = newTarget services
    let _transport, reference = bindWith services target

    task {
        // Warm the reply codec first. `history` returns a string list, whose codec closes one
        // generic on its first build, and this test's claim is about the SELECTOR — leaving the
        // codec cold would make the closing count depend on xUnit ordering.
        let! _ = reference.call (_.history) 1

        let counters = FunctionalInstrumentation.start ()

        try
            let! _ = reference.call (_.history) 1

            test <@ counters.SelectorEvaluations = 1 @>
            test <@ counters.GenericClosings = 0 @>
            test <@ counters.CodecBuilds = 0 @>
        finally
            FunctionalInstrumentation.stop ()
    }

[<Fact>]
let ``concurrent bound calls never share a serializer session`` () =
    let services = buildServices true None
    let target = newTarget services
    let _transport, reference = bindWith services target

    task {
        do! reference.api.join "warm"

        let counters = FunctionalInstrumentation.start ()

        try
            // Enough parallelism to make sessions genuinely overlap, small enough not to
            // saturate the thread pool while the rest of the suite runs beside it.
            let calls =
                [| for index in 1..32 ->
                       Task.Run(fun () ->
                           task {
                               let! _ = reference.api.say { name = $"n{index}"; bytes = [| byte index |] }
                               return ()
                           }
                           :> Task) |]

            do! Task.WhenAll calls

            // Caller serialize + target deserialize + target serialize + caller deserialize.
            test <@ counters.SessionRentals = 32 * 4 @>
            test <@ counters.SessionConflicts = 0 @>
            test <@ counters.ActiveSessions.IsEmpty @>
        finally
            FunctionalInstrumentation.stop ()
    }

[<Fact>]
let ``the session-conflict detector reports a shared session`` () =
    // Guards the test above from being vacuous: the detector really does fire.
    let counters = FunctionalInstrumentation.start ()

    try
        let session = obj ()
        FunctionalInstrumentation.trackSessionRented session
        FunctionalInstrumentation.trackSessionRented session

        test <@ counters.SessionConflicts = 1 @>

        FunctionalInstrumentation.trackSessionReturned session
        test <@ counters.ActiveSessions.IsEmpty @>
    finally
        FunctionalInstrumentation.stop ()

// ──────────────────────────────────────────────────────────────────────────────
// Argument and reply shapes the public model documents
// ──────────────────────────────────────────────────────────────────────────────

type ShapeActor = private ShapeActor of unit

[<NoEquality; NoComparison>]
type ShapeApi =
    { ping: unit -> Task<unit>
      count: unit -> Task<int>
      lookup: string option -> Task<Result<int, string>> }

let private shapeContract =
    grainContract<ShapeActor, string, ShapeApi> () {
        grainType "bind.shape"
        stringKey
    }

[<Fact>]
let ``unit arguments, unit replies, options, and results cross the byte boundary`` () =
    let services = buildServices true None
    let target = InMemoryTarget(services, "bind.shape", 1)
    let mutable pinged = 0

    target.Handle<unit, unit>("ping", fun () -> pinged <- pinged + 1)
    target.Handle<unit, int>("count", fun () -> 7)

    target.Handle<string option, Result<int, string>>(
        "lookup",
        function
        | Some "known" -> Ok 1
        | Some other -> Error other
        | None -> Error "none"
    )

    let transport = InMemoryTransport(services, target.Dispatch)
    let reference = FunctionalGrain.rawRef shapeContract transport "general"

    task {
        do! reference.api.ping ()
        let! count = reference.api.count ()
        let! known = reference.api.lookup (Some "known")
        let! unknown = reference.api.lookup (Some "other")
        let! missing = reference.api.lookup None

        test <@ pinged = 1 @>
        test <@ count = 7 @>
        test <@ known = Ok 1 @>
        test <@ unknown = Error "other" @>
        test <@ missing = Error "none" @>
        test <@ transport.Calls.Length = 5 @>
    }
