/// <summary>
/// Fixed-transport serialization tests for spec 003 Phase 2. Everything here round-trips
/// through a real Orleans <c>Serializer</c> configured with the explicit functional codecs:
/// numeric field IDs and exact wire types, required-field and duplicate-field rejection,
/// every valid admission-flag combination, reserved-flag rejection, the local copier contract,
/// and the argument surface of the fixed request.
/// </summary>
module Orleans.FSharp.Tests.FunctionalCodecTests

open System
open System.Buffers
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.CodeGeneration
open Orleans.Serialization
open Orleans.Serialization.Buffers
open Orleans.Serialization.Codecs
open Orleans.Serialization.Session
open Orleans.Serialization.Invocation
open Orleans.Serialization.WireProtocol
open Xunit
open Swensen.Unquote
open Orleans.FSharp

type CodecActor = private CodecActor of unit

let private services = lazy (FunctionalTransportHarness.buildServices false None)
let private serializer () = services.Value.GetRequiredService<Serializer>()
let private sessions () = (serializer ()).SessionPool
let private copier () = services.Value.GetRequiredService<DeepCopier>()

let private token = ProtocolToken.request "chat.room" 1 "join"
let private replyToken = ProtocolToken.reply "chat.room" 1 "join"
let private payload = [| 1uy; 2uy; 3uy; 4uy |]

let private envelope () =
    FunctionalRequestEnvelope("chat.room", 1, "join", token, AdmissionFlags.ReadOnly, payload)

// ──────────────────────────────────────────────────────────────────────────────
// Wire inspection helpers
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>The (field id, wire type) sequence of a serialized fixed-transport object.</summary>
let private walkFields (bytes: byte[]) =
    use session = sessions().GetSession()
    let mutable reader = Reader.Create(bytes, session)
    let mutable header = Unchecked.defaultof<Field>
    FieldHeaderCodec.ReadFieldHeader(&reader, &header)

    let observed = ResizeArray<uint32 * WireType>()
    let mutable id = 0u
    let mutable running = true

    while running do
        let mutable inner = Unchecked.defaultof<Field>
        FieldHeaderCodec.ReadFieldHeader(&reader, &inner)

        if inner.IsEndBaseOrEndObject then
            running <- false
        else
            id <- id + inner.FieldIdDelta
            observed.Add((id, inner.WireType))
            SkipFieldExtension.SkipField(&reader, inner)

    observed.ToArray()

/// <summary>Hand-write a complete envelope body, exactly the way the codec does.</summary>
let private handWriteEnvelope (grainType: string) (version: int) (operationId: string) (flags: byte) =
    use session = sessions().GetSession()
    let buffer = ArrayBufferWriter<byte>()
    let mutable writer = Writer.Create(buffer, session)
    let marker = obj ()

    if ReferenceCodec.TryWriteReferenceField(&writer, 0u, typeof<FunctionalRequestEnvelope>, marker) then
        failwith "a fresh object must not be written as a back-reference."

    writer.WriteStartObject(0u, typeof<FunctionalRequestEnvelope>, typeof<FunctionalRequestEnvelope>)
    StringCodec.WriteField(&writer, 0u, grainType)
    Int32Codec.WriteField(&writer, 1u, version)
    StringCodec.WriteField(&writer, 1u, operationId)
    ByteArrayCodec.WriteField(&writer, 1u, token)
    ByteCodec.WriteField(&writer, 1u, flags)
    ByteArrayCodec.WriteField(&writer, 1u, payload)
    writer.WriteEndObject()
    writer.Commit()
    buffer.WrittenSpan.ToArray()

/// <summary>Hand-write an envelope body which omits field 2 (the operation ID).</summary>
let private handWriteMissingOperationId () =
    use session = sessions().GetSession()
    let buffer = ArrayBufferWriter<byte>()
    let mutable writer = Writer.Create(buffer, session)
    let marker = obj ()
    ReferenceCodec.TryWriteReferenceField(&writer, 0u, typeof<FunctionalRequestEnvelope>, marker) |> ignore
    writer.WriteStartObject(0u, typeof<FunctionalRequestEnvelope>, typeof<FunctionalRequestEnvelope>)
    StringCodec.WriteField(&writer, 0u, "chat.room")
    Int32Codec.WriteField(&writer, 1u, 1)
    ByteArrayCodec.WriteField(&writer, 2u, token)
    ByteCodec.WriteField(&writer, 1u, 0uy)
    ByteArrayCodec.WriteField(&writer, 1u, payload)
    writer.WriteEndObject()
    writer.Commit()
    buffer.WrittenSpan.ToArray()

/// <summary>Hand-write an envelope body which repeats field 0.</summary>
let private handWriteDuplicateGrainType () =
    use session = sessions().GetSession()
    let buffer = ArrayBufferWriter<byte>()
    let mutable writer = Writer.Create(buffer, session)
    let marker = obj ()
    ReferenceCodec.TryWriteReferenceField(&writer, 0u, typeof<FunctionalRequestEnvelope>, marker) |> ignore
    writer.WriteStartObject(0u, typeof<FunctionalRequestEnvelope>, typeof<FunctionalRequestEnvelope>)
    StringCodec.WriteField(&writer, 0u, "chat.room")
    StringCodec.WriteField(&writer, 0u, "chat.rooms")
    writer.WriteEndObject()
    writer.Commit()
    buffer.WrittenSpan.ToArray()

/// <summary>Hand-write an envelope body whose field 1 carries a string instead of an int.</summary>
let private handWriteWrongVersionType () =
    use session = sessions().GetSession()
    let buffer = ArrayBufferWriter<byte>()
    let mutable writer = Writer.Create(buffer, session)
    let marker = obj ()
    ReferenceCodec.TryWriteReferenceField(&writer, 0u, typeof<FunctionalRequestEnvelope>, marker) |> ignore
    writer.WriteStartObject(0u, typeof<FunctionalRequestEnvelope>, typeof<FunctionalRequestEnvelope>)
    StringCodec.WriteField(&writer, 0u, "chat.room")
    // Field 1 is the only mistyped one: a string where the fixed layout requires an int32.
    // Every other field is written exactly as the layout specifies, so a failure can only be
    // caused by the wire type rather than by a missing or duplicated field.
    StringCodec.WriteField(&writer, 1u, "one")
    StringCodec.WriteField(&writer, 1u, "join")
    ByteArrayCodec.WriteField(&writer, 1u, token)
    ByteCodec.WriteField(&writer, 1u, 0uy)
    ByteArrayCodec.WriteField(&writer, 1u, payload)
    writer.WriteEndObject()
    writer.Commit()
    buffer.WrittenSpan.ToArray()

/// <summary>Hand-write an envelope body with a seventh, unknown field.</summary>
let private handWriteUnknownField () =
    use session = sessions().GetSession()
    let buffer = ArrayBufferWriter<byte>()
    let mutable writer = Writer.Create(buffer, session)
    let marker = obj ()
    ReferenceCodec.TryWriteReferenceField(&writer, 0u, typeof<FunctionalRequestEnvelope>, marker) |> ignore
    writer.WriteStartObject(0u, typeof<FunctionalRequestEnvelope>, typeof<FunctionalRequestEnvelope>)
    StringCodec.WriteField(&writer, 0u, "chat.room")
    Int32Codec.WriteField(&writer, 1u, 1)
    StringCodec.WriteField(&writer, 1u, "join")
    ByteArrayCodec.WriteField(&writer, 1u, token)
    ByteCodec.WriteField(&writer, 1u, 0uy)
    ByteArrayCodec.WriteField(&writer, 1u, payload)
    Int32Codec.WriteField(&writer, 1u, 7)
    writer.WriteEndObject()
    writer.Commit()
    buffer.WrittenSpan.ToArray()

let private readEnvelope (bytes: byte[]) =
    (serializer ()).Deserialize<FunctionalRequestEnvelope> bytes

// ──────────────────────────────────────────────────────────────────────────────
// Envelope layout
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the envelope round-trips every field through the real serializer`` () =
    let original = envelope ()
    let restored = readEnvelope ((serializer ()).SerializeToArray original)

    test <@ restored.GrainType = "chat.room" @>
    test <@ restored.ContractVersion = 1 @>
    test <@ restored.OperationId = "join" @>
    test <@ ProtocolToken.equal restored.ProtocolToken token @>
    test <@ restored.AdmissionFlags = AdmissionFlags.ReadOnly @>
    test <@ restored.Payload = payload @>
    test <@ not (obj.ReferenceEquals(restored, original)) @>

[<Fact>]
let ``the envelope uses field ids 0 to 5 with the specified wire types`` () =
    let bytes = (serializer ()).SerializeToArray(envelope ())

    let expected =
        [| 0u, WireType.LengthPrefixed // grainType : string
           1u, WireType.VarInt // contractVersion : int32
           2u, WireType.LengthPrefixed // operationId : string
           3u, WireType.LengthPrefixed // protocolToken : byte[]
           4u, WireType.VarInt // admissionFlags : byte
           5u, WireType.LengthPrefixed |] // payload : byte[]

    test <@ walkFields bytes = expected @>

[<Fact>]
let ``the hand-written fixed layout is byte-identical to the codec output`` () =
    // Pins the wire encoding itself: if the codec ever reordered fields, changed a field ID, or
    // switched a primitive codec, these two byte sequences would diverge.
    let produced = (serializer ()).SerializeToArray(envelope ())
    let handWritten = handWriteEnvelope "chat.room" 1 "join" AdmissionFlags.ReadOnly

    test <@ produced = handWritten @>

[<Fact>]
let ``the reply uses field ids 0 and 1`` () =
    let reply = FunctionalReply(replyToken, payload)
    let bytes = (serializer ()).SerializeToArray reply
    let restored = (serializer ()).Deserialize<FunctionalReply> bytes

    test <@ walkFields bytes = [| 0u, WireType.LengthPrefixed; 1u, WireType.LengthPrefixed |] @>
    test <@ ProtocolToken.equal restored.ProtocolToken replyToken @>
    test <@ restored.Payload = payload @>

[<Fact>]
let ``the request serializes exactly one field, the envelope`` () =
    let request = new FunctionalRequest(envelope (), CancellationToken.None)
    let bytes = (serializer ()).SerializeToArray request
    let restored = (serializer ()).Deserialize<FunctionalRequest> bytes

    test <@ walkFields bytes = [| 0u, WireType.TagDelimited |] @>
    test <@ restored.Envelope.OperationId = "join" @>
    test <@ restored.Envelope.GrainType = "chat.room" @>
    // Target and target-local cancellation state are never wire data.
    test <@ not restored.HasTarget @>
    test <@ not restored.HasTargetCancellation @>
    // Options are restored from the validated admission flags rather than sent.
    test <@ restored.Options = InvokeMethodOptions.ReadOnly @>

// ──────────────────────────────────────────────────────────────────────────────
// Required fields, duplicates, unknown fields, exact types
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a missing required field is rejected`` () =
    let error = Assert.Throws<InvalidOperationException>(fun () -> readEnvelope (handWriteMissingOperationId ()) |> ignore)

    test <@ error.Message.Contains "missing required wire fields" @>

[<Fact>]
let ``a repeated field is rejected`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () -> readEnvelope (handWriteDuplicateGrainType ()) |> ignore)

    test <@ error.Message.Contains "more than once" @>

[<Fact>]
let ``an unknown field is rejected`` () =
    let error = Assert.Throws<InvalidOperationException>(fun () -> readEnvelope (handWriteUnknownField ()) |> ignore)

    test <@ error.Message.Contains "unknown wire field" @>

[<Fact>]
let ``a field carrying the wrong wire type is rejected`` () =
    // Control: the same six fields with the correct wire types read back cleanly, so the
    // rejection below is caused by the mistyped field 1 alone.
    let control = readEnvelope (handWriteEnvelope "chat.room" 1 "join" 0uy)
    test <@ control.ContractVersion = 1 @>

    let error =
        Assert.ThrowsAny<exn>(fun () -> readEnvelope (handWriteWrongVersionType ()) |> ignore)

    // The failure comes from the Orleans field reader refusing the declared wire type, not
    // from one of the fixed-layout guards, and it names the concrete mismatch.
    test <@ error.GetType().Namespace.StartsWith "Orleans.Serialization" @>
    test <@ error.Message.Contains "specified in header" @>
    test <@ not (error.Message.Contains "missing required wire fields") @>
    test <@ not (error.Message.Contains "more than once") @>
    test <@ not (error.Message.Contains "unknown wire field") @>

// ──────────────────────────────────────────────────────────────────────────────
// Admission flags on the wire
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``every valid admission-flag combination round-trips`` () =
    for flags in 0uy .. 7uy do
        let original = FunctionalRequestEnvelope("chat.room", 1, "join", token, flags, payload)
        let restored = readEnvelope ((serializer ()).SerializeToArray original)

        test <@ restored.AdmissionFlags = flags @>
        test <@ restored.IsReadOnly = (flags &&& AdmissionFlags.ReadOnly <> 0uy) @>
        test <@ restored.IsOneWay = (flags &&& AdmissionFlags.OneWay <> 0uy) @>
        test <@ restored.IsAlwaysInterleave = (flags &&& AdmissionFlags.AlwaysInterleave <> 0uy) @>

[<Fact>]
let ``a set reserved bit invalidates the request on read`` () =
    for bit in 3 .. 7 do
        let flags = 1uy <<< bit
        let bytes = handWriteEnvelope "chat.room" 1 "join" flags

        let error = Assert.Throws<InvalidOperationException>(fun () -> readEnvelope bytes |> ignore)

        test <@ error.Message.Contains "reserved bit" @>

[<Fact>]
let ``reserved flags are refused when Orleans request options are restored`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () -> FunctionalRequest.OptionsFor 0x80uy |> ignore)

    test <@ error.Message.Contains "reserved bit" @>

[<Fact>]
let ``valid flags map onto the Orleans request options`` () =
    let combined = AdmissionFlags.OneWay ||| AdmissionFlags.AlwaysInterleave
    let expectedCombined = InvokeMethodOptions.OneWay ||| InvokeMethodOptions.AlwaysInterleave

    test <@ FunctionalRequest.OptionsFor AdmissionFlags.None = InvokeMethodOptions.None @>
    test <@ FunctionalRequest.OptionsFor AdmissionFlags.ReadOnly = InvokeMethodOptions.ReadOnly @>
    test <@ FunctionalRequest.OptionsFor AdmissionFlags.OneWay = InvokeMethodOptions.OneWay @>
    test <@ FunctionalRequest.OptionsFor AdmissionFlags.AlwaysInterleave = InvokeMethodOptions.AlwaysInterleave @>
    test <@ FunctionalRequest.OptionsFor combined = expectedCombined @>

// ──────────────────────────────────────────────────────────────────────────────
// Envelope construction validation
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the envelope rejects malformed field values at construction`` () =
    let cases: (unit -> FunctionalRequestEnvelope) list =
        [ fun () -> FunctionalRequestEnvelope("", 1, "join", token, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat\000room", 1, "join", token, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat.room", 0, "join", token, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat.room", -1, "join", token, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat.room", 1, "", token, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat.room", 1, "jo\000in", token, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat.room", 1, "join", null, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat.room", 1, "join", Array.zeroCreate 31, 0uy, payload)
          fun () -> FunctionalRequestEnvelope("chat.room", 1, "join", token, 0uy, null) ]

    cases
    |> List.iter (fun case -> Assert.Throws<InvalidOperationException>(fun () -> case () |> ignore) |> ignore)

[<Fact>]
let ``an initialized envelope cannot be initialized again`` () =
    let value = envelope ()

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            value.Initialize("chat.room", 1, "join", token, 0uy, payload))

    test <@ error.Message.Contains "immutable after construction" @>

[<Fact>]
let ``the reply rejects malformed field values at construction`` () =
    Assert.Throws<InvalidOperationException>(fun () -> FunctionalReply(null, payload) |> ignore) |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> FunctionalReply(Array.zeroCreate 8, payload) |> ignore)
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () -> FunctionalReply(replyToken, null) |> ignore) |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Local copier
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A dispatch target which answers with a fixed reply.</summary>
type private FakeTarget() =
    interface IFunctionalDispatchTarget with
        member _.DispatchAsync(_envelope, _cancellationToken) =
            ValueTask<FunctionalReply>(FunctionalReply(replyToken, payload))

    interface IFunctionalGrainTarget<CodecActor> with
        member _.DispatchAsync(_envelope, _cancellationToken) =
            ValueTask<FunctionalReply>(FunctionalReply(replyToken, payload))

/// <summary>The target holder Orleans hands to <c>SetTarget</c>.</summary>
type private FakeHolder(target: obj) =
    interface ITargetHolder with
        member _.GetTarget() = target
        member _.GetComponent(_componentType: Type) = target

let private closedInterface = typeof<IFunctionalGrainTarget<CodecActor>>
let private dispatchMethod = closedInterface.GetMethod "DispatchAsync"

[<Fact>]
let ``the local copier preserves the envelope, options, caller token, and caller metadata`` () =
    use source = new CancellationTokenSource()
    let original = new FunctionalRequest(envelope (), source.Token)
    original.SetCallerMetadata(closedInterface, dispatchMethod)
    original.ApplyAdmissionOptions()

    let copy = (copier ()).Copy original

    test <@ obj.ReferenceEquals(copy.Envelope, original.Envelope) @>
    test <@ copy.Options = InvokeMethodOptions.ReadOnly @>
    let callerTokenPreserved = copy.CallerToken.Equals source.Token
    let currentTokenPreserved = copy.GetCancellationToken().Equals source.Token
    test <@ callerTokenPreserved @>
    test <@ currentTokenPreserved @>
    test <@ obj.ReferenceEquals(copy.GetInterfaceType(), closedInterface) @>
    test <@ copy.GetMethod() = dispatchMethod @>
    test <@ copy.GetInterfaceName() = closedInterface.FullName @>
    test <@ not (obj.ReferenceEquals(copy, original)) @>

[<Fact>]
let ``the local copier clears the target and its cancellation state`` () =
    let original = new FunctionalRequest(envelope (), CancellationToken.None)
    original.SetCallerMetadata(closedInterface, dispatchMethod)
    original.SetTarget(FakeHolder(FakeTarget()))

    test <@ original.HasTarget @>
    test <@ original.HasTargetCancellation @>

    let copy = (copier ()).Copy original

    test <@ not copy.HasTarget @>
    test <@ not copy.HasTargetCancellation @>
    test <@ isNull (copy.GetTarget()) @>

[<Fact>]
let ``the envelope and reply copy as the immutable values they are`` () =
    let value = envelope ()
    let reply = FunctionalReply(replyToken, payload)

    test <@ obj.ReferenceEquals((copier ()).Copy value, value) @>
    test <@ obj.ReferenceEquals((copier ()).Copy reply, reply) @>

// ──────────────────────────────────────────────────────────────────────────────
// Request argument surface and cancellation
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the request exposes exactly two arguments`` () =
    use source = new CancellationTokenSource()
    let request = new FunctionalRequest(envelope (), source.Token)

    let argumentToken = (request.GetArgument 1 :?> CancellationToken).Equals source.Token
    test <@ request.GetArgumentCount() = 2 @>
    test <@ obj.ReferenceEquals(request.GetArgument 0, request.Envelope) @>
    test <@ argumentToken @>

[<Fact>]
let ``argument 0 accepts only the envelope type`` () =
    let request = new FunctionalRequest(envelope (), CancellationToken.None)
    let replacement = FunctionalRequestEnvelope("chat.room", 1, "say", token, 0uy, payload)

    request.SetArgument(0, replacement)
    test <@ obj.ReferenceEquals(request.Envelope, replacement) @>

    Assert.Throws<ArgumentException>(fun () -> request.SetArgument(0, box "not an envelope")) |> ignore
    Assert.Throws<ArgumentException>(fun () -> request.SetArgument(0, box CancellationToken.None)) |> ignore

[<Fact>]
let ``argument 1 accepts only a cancellation token`` () =
    use source = new CancellationTokenSource()
    let request = new FunctionalRequest(envelope (), CancellationToken.None)

    request.SetArgument(1, box source.Token)
    let replaced = request.GetCancellationToken().Equals source.Token
    test <@ replaced @>

    Assert.Throws<ArgumentException>(fun () -> request.SetArgument(1, box 42)) |> ignore
    Assert.Throws<ArgumentException>(fun () -> request.SetArgument(1, box (envelope ()))) |> ignore

[<Fact>]
let ``every other argument index is out of range`` () =
    let request = new FunctionalRequest(envelope (), CancellationToken.None)

    [ -1; 2; 3; Int32.MaxValue ]
    |> List.iter (fun index ->
        Assert.Throws<ArgumentOutOfRangeException>(fun () -> request.GetArgument index |> ignore) |> ignore

        Assert.Throws<ArgumentOutOfRangeException>(fun () -> request.SetArgument(index, box CancellationToken.None))
        |> ignore)

[<Fact>]
let ``an acknowledged request is cancellable and a one-way request is not`` () =
    let acknowledged =
        new FunctionalRequest(
            FunctionalRequestEnvelope("chat.room", 1, "join", token, AdmissionFlags.None, payload),
            CancellationToken.None
        )

    let oneWay =
        new FunctionalRequest(
            FunctionalRequestEnvelope("chat.room", 1, "typing", token, AdmissionFlags.OneWay, payload),
            CancellationToken.None
        )

    test <@ (acknowledged :> IInvokable).IsCancellable @>
    test <@ not (oneWay :> IInvokable).IsCancellable @>

[<Fact>]
let ``SetTarget installs the target, its closed interface metadata, and target-local cancellation`` () =
    use caller = new CancellationTokenSource()
    let request = new FunctionalRequest(envelope (), caller.Token)

    test <@ not (request.TryCancel()) @>

    let target = FakeTarget()
    request.SetTarget(FakeHolder target)

    test <@ obj.ReferenceEquals(request.GetTarget(), target) @>
    test <@ obj.ReferenceEquals(request.GetInterfaceType(), closedInterface) @>
    test <@ request.GetMethod() = dispatchMethod @>
    test <@ request.GetActivityName() = closedInterface.FullName + "/DispatchAsync" @>
    test <@ request.GetMethodName() = "DispatchAsync" @>

    // Argument 1 is now the target-local token, not the caller's.
    let targetToken = request.GetArgument 1 :?> CancellationToken
    let isCallerToken = targetToken.Equals caller.Token
    let targetCancelledBefore = targetToken.IsCancellationRequested
    test <@ not isCallerToken @>
    test <@ not targetCancelledBefore @>

    test <@ request.TryCancel() @>

    let targetCancelledAfter = (request.GetArgument 1 :?> CancellationToken).IsCancellationRequested
    let callerCancelled = caller.IsCancellationRequested
    test <@ targetCancelledAfter @>
    test <@ not callerCancelled @>

    request.Dispose()
    test <@ not request.HasTarget @>
    test <@ not request.HasTargetCancellation @>

[<Fact>]
let ``caller metadata refuses the open generic target interface`` () =
    let request = new FunctionalRequest(envelope (), CancellationToken.None)
    let openInterface = closedInterface.GetGenericTypeDefinition()

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            request.SetCallerMetadata(openInterface, openInterface.GetMethod "DispatchAsync"))

    test <@ error.Message.Contains "open generic" @>

[<Fact>]
let ``filter metadata is non-null before any target or caller metadata is stored`` () =
    let request = new FunctionalRequest(envelope (), CancellationToken.None)

    test <@ not (isNull (request.GetInterfaceType())) @>
    test <@ not (request.GetInterfaceType().ContainsGenericParameters) @>
    test <@ not (isNull (request.GetMethod())) @>
    test <@ not (String.IsNullOrEmpty(request.GetInterfaceName())) @>

// ──────────────────────────────────────────────────────────────────────────────
// Type filter
// ──────────────────────────────────────────────────────────────────────────────

[<NoEquality; NoComparison>]
type CodecApi = { ping: string -> Task<int> }

[<Fact>]
let ``the transport type filter claims exactly the three fixed types`` () =
    let filter = FunctionalTransportTypeFilter() :> ITypeFilter

    let claimed =
        [ typeof<FunctionalRequestEnvelope>
          typeof<FunctionalReply>
          typeof<FunctionalRequest> ]

    claimed
    |> List.iter (fun fixedType -> test <@ filter.IsTypeAllowed fixedType = Nullable true @>)

[<Fact>]
let ``the transport type filter claims no contract, facade, selector, or service type`` () =
    let filter = FunctionalTransportTypeFilter() :> ITypeFilter

    let contract =
        grainContract<CodecActor, string, CodecApi> () {
            grainType "codec.filter"
            stringKey
        }

    let neverClaimed: Type list =
        [ contract.GetType()
          typeof<CodecApi>
          typeof<GrainContract<CodecActor, string, CodecApi>>
          typeof<FunctionalGrainRef<CodecActor, string, CodecApi>>
          typeof<OperationSelector<CodecApi, string, int>>
          typeof<PersistentStateRef<int>>
          typeof<FunctionalGrainContext<CodecActor, string>>
          typeof<IServiceProvider>
          typeof<MethodInfo>
          typeof<Type>
          typeof<FunctionalTargetMetadata>
          typeof<CodecActor> ]

    neverClaimed
    |> List.iter (fun candidate -> test <@ filter.IsTypeAllowed candidate = Nullable() @>)

[<Fact>]
let ``a copy of a request without stored metadata keeps the fallback rather than promoting it`` () =
    let original = new FunctionalRequest(envelope (), CancellationToken.None)

    test <@ not original.HasCallFilterMetadata @>

    let copy = (copier ()).Copy original

    test <@ not copy.HasCallFilterMetadata @>
    test <@ obj.ReferenceEquals(copy.GetInterfaceType(), typeof<IFunctionalDispatchTarget>) @>

// ──────────────────────────────────────────────────────────────────────────────
// Availability without any functional registration
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the fixed transport types carry a serializer and a copier without any functional registration`` () =
    // Regression pin: the fixed types appear in the signature of a grain interface, so Orleans
    // refuses to start a silo unless every one of them has a serializer and a copier. Nothing
    // here calls the functional registration path.
    //
    // The codecs arrive through the assembly-level manifest provider, which Orleans finds by
    // scanning the assemblies of the process — so the abstractions assembly has to be LOADED
    // before the serializer is built. That is automatic in a process whose entry assembly
    // references the package; inside a test host it depends on what ran first, so force it
    // here rather than inherit it from another test.
    test <@ typeof<FunctionalRequestEnvelope>.Assembly.GetName().Name = "Orleans.FSharp.Abstractions" @>

    let collection = ServiceCollection()
    ServiceCollectionExtensions.AddSerializer(collection, Action<ISerializerBuilder>(fun _ -> ())) |> ignore
    use provider = collection.BuildServiceProvider()
    let codecProvider = provider.GetRequiredService<Orleans.Serialization.Serializers.ICodecProvider>()
    let codecs = codecProvider :> Orleans.Serialization.Serializers.IFieldCodecProvider
    let copiers = codecProvider :> Orleans.Serialization.Cloning.IDeepCopierProvider

    [ typeof<FunctionalRequestEnvelope>; typeof<FunctionalReply>; typeof<FunctionalRequest> ]
    |> List.iter (fun fixedType ->
        Assert.NotNull(codecs.GetCodec fixedType)
        Assert.NotNull(copiers.GetDeepCopier fixedType))

[<Fact>]
let ``the fixed request survives the dynamic type path Orleans uses for an invokable`` () =
    // A real message writes the invokable with its concrete type name, which the receiving
    // side resolves through the type filter. This is the path Phase 3 depends on.
    let request = new FunctionalRequest(envelope (), CancellationToken.None)
    let bytes = (serializer ()).SerializeToArray<obj>(box request)

    match (serializer ()).Deserialize<obj> bytes with
    | :? FunctionalRequest as restored ->
        test <@ restored.Envelope.OperationId = "join" @>
        test <@ restored.Envelope.GrainType = "chat.room" @>
        test <@ restored.Options = InvokeMethodOptions.ReadOnly @>
    | other -> failwith $"the request round-tripped as '{other.GetType().FullName}'."
