using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Runtime;
using Orleans.Serialization.WireProtocol;
using Orleans.Transactions;

// The fixed transport types appear in the signature of a grain interface
// (IFunctionalGrainTarget<TActor>.DispatchAsync), and Orleans validates at silo startup that
// every type referenced by a grain-interface signature has a serializer and a copier. The
// assembly-level manifest provider therefore registers the explicit codecs wherever this
// assembly is loaded, so referencing Orleans.FSharp.Abstractions never breaks silo startup.
[assembly: Orleans.Serialization.Configuration.TypeManifestProvider(
    typeof(Orleans.FSharp.FunctionalTransportManifestProvider))]

namespace Orleans.FSharp;

/// <summary>
/// Shared helpers for the hand-written fixed-transport codecs. The fixed layout requires every
/// listed field exactly once with its exact wire type, so the read loops track which field IDs
/// have been seen and reject duplicates, unknown IDs, and missing fields.
/// </summary>
internal static class FunctionalWire
{
    /// <summary>Reject a field ID that is not part of the fixed layout.</summary>
    public static InvalidOperationException UnknownField(string typeName, uint id) =>
        FunctionalTransportDiagnostics.Fail(
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{typeName}' received unknown wire field {id}; the fixed layout is closed."));

    /// <summary>Reject a field ID that appeared twice.</summary>
    public static InvalidOperationException DuplicateField(string typeName, uint id) =>
        FunctionalTransportDiagnostics.Fail(
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{typeName}' received wire field {id} more than once; every field is required exactly once."));

    /// <summary>Reject a payload whose required fields were not all present.</summary>
    public static InvalidOperationException MissingFields(string typeName, uint seen, uint required) =>
        FunctionalTransportDiagnostics.Fail(
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{typeName}' is missing required wire fields (mask 0x{required & ~seen:x2}); every field is required exactly once."));

    /// <summary>Mark one field as seen, rejecting a repeat.</summary>
    public static void MarkSeen(string typeName, ref uint seen, uint id)
    {
        var bit = 1u << (int)id;

        if ((seen & bit) != 0)
        {
            throw DuplicateField(typeName, id);
        }

        seen |= bit;
    }
}

/// <summary>Creates uninitialized envelopes for the deserializer.</summary>
internal sealed class FunctionalRequestEnvelopeActivator : IActivator<FunctionalRequestEnvelope>
{
    /// <inheritdoc />
    public FunctionalRequestEnvelope Create() => new();
}

/// <summary>Creates uninitialized replies for the deserializer.</summary>
internal sealed class FunctionalReplyActivator : IActivator<FunctionalReply>
{
    /// <inheritdoc />
    public FunctionalReply Create() => new();
}

/// <summary>Creates uninitialized requests for the deserializer.</summary>
internal sealed class FunctionalRequestActivator : IActivator<FunctionalRequest>
{
    /// <inheritdoc />
    public FunctionalRequest Create() => new();
}

/// <summary>
/// Creates uninitialized transactional requests for the deserializer. The two constructor
/// arguments are the ones <c>TransactionRequestBase</c> declares with
/// <c>[GeneratedActivatorConstructor]</c>, resolved from the container of the receiving process
/// exactly as Orleans' own generated invokable activators resolve them.
/// </summary>
internal sealed class FunctionalTransactionRequestActivator : IActivator<FunctionalTransactionRequest>
{
    private readonly Serializer<OrleansTransactionAbortedException> _exceptionSerializer;
    private readonly IServiceProvider _services;

    /// <summary>Create the activator from the transaction machinery's own dependencies.</summary>
    public FunctionalTransactionRequestActivator(
        Serializer<OrleansTransactionAbortedException> exceptionSerializer,
        IServiceProvider services)
    {
        _exceptionSerializer = exceptionSerializer;
        _services = services;
    }

    /// <inheritdoc />
    public FunctionalTransactionRequest Create() => new(_exceptionSerializer, _services);
}

/// <summary>Creates uninitialized streaming requests for the deserializer.</summary>
internal sealed class FunctionalStreamRequestActivator : IActivator<FunctionalStreamRequest>
{
    /// <inheritdoc />
    public FunctionalStreamRequest Create() => new();
}

/// <summary>
/// Explicit serializer for <see cref="FunctionalRequestEnvelope"/>: fields 0-5 in the exact
/// wire order and wire types fixed by the specification.
/// </summary>
internal sealed class FunctionalRequestEnvelopeCodec : IFieldCodec<FunctionalRequestEnvelope>
{
    private const string TypeName = nameof(FunctionalRequestEnvelope);
    private const uint RequiredFields = 0b111111u;

    private readonly Type _codecFieldType = typeof(FunctionalRequestEnvelope);
    private readonly IActivator<FunctionalRequestEnvelope> _activator;

    /// <summary>Create the codec with the activator Orleans resolves for the envelope.</summary>
    public FunctionalRequestEnvelopeCodec(IActivator<FunctionalRequestEnvelope> activator) =>
        _activator = activator;

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        FunctionalRequestEnvelope value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        StringCodec.WriteField(ref writer, 0U, value.GrainType);
        Int32Codec.WriteField(ref writer, 1U, value.ContractVersion);
        StringCodec.WriteField(ref writer, 1U, value.OperationId);
        ByteArrayCodec.WriteField(ref writer, 1U, value.ProtocolToken);
        ByteCodec.WriteField(ref writer, 1U, value.AdmissionFlags);
        ByteArrayCodec.WriteField(ref writer, 1U, value.Payload);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public FunctionalRequestEnvelope ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference<FunctionalRequestEnvelope, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();

        var placeholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        var result = _activator.Create();
        ReferenceCodec.RecordObject(reader.Session, result, placeholder);

        var grainType = string.Empty;
        var contractVersion = 0;
        var operationId = string.Empty;
        byte[] protocolToken = [];
        byte admissionFlags = 0;
        byte[] payload = [];

        var seen = 0u;
        var id = 0u;
        Field header = default;

        while (true)
        {
            reader.ReadFieldHeader(ref header);

            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            id += header.FieldIdDelta;
            FunctionalWire.MarkSeen(TypeName, ref seen, id);

            switch (id)
            {
                case 0U:
                    grainType = StringCodec.ReadValue(ref reader, header);
                    break;
                case 1U:
                    contractVersion = Int32Codec.ReadValue(ref reader, header);
                    break;
                case 2U:
                    operationId = StringCodec.ReadValue(ref reader, header);
                    break;
                case 3U:
                    protocolToken = ByteArrayCodec.ReadValue(ref reader, header);
                    break;
                case 4U:
                    admissionFlags = ByteCodec.ReadValue(ref reader, header);
                    break;
                case 5U:
                    payload = ByteArrayCodec.ReadValue(ref reader, header);
                    break;
                default:
                    throw FunctionalWire.UnknownField(TypeName, id);
            }
        }

        if (seen != RequiredFields)
        {
            throw FunctionalWire.MissingFields(TypeName, seen, RequiredFields);
        }

        if (FunctionalAdmissionFlags.HasReservedBits(admissionFlags))
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"the admission flags 0x{admissionFlags:x2} set a reserved bit (mask 0x{FunctionalAdmissionFlags.Reserved:x2}); the request is invalid.");
        }

        result.Initialize(grainType, contractVersion, operationId, protocolToken, admissionFlags, payload);
        return result;
    }
}

/// <summary>Explicit serializer for <see cref="FunctionalReply"/>: fields 0-1.</summary>
internal sealed class FunctionalReplyCodec : IFieldCodec<FunctionalReply>
{
    private const string TypeName = nameof(FunctionalReply);
    private const uint RequiredFields = 0b11u;

    private readonly Type _codecFieldType = typeof(FunctionalReply);
    private readonly IActivator<FunctionalReply> _activator;

    /// <summary>Create the codec with the activator Orleans resolves for the reply.</summary>
    public FunctionalReplyCodec(IActivator<FunctionalReply> activator) => _activator = activator;

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        FunctionalReply value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        ByteArrayCodec.WriteField(ref writer, 0U, value.ProtocolToken);
        ByteArrayCodec.WriteField(ref writer, 1U, value.Payload);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public FunctionalReply ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference<FunctionalReply, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();

        var placeholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        var result = _activator.Create();
        ReferenceCodec.RecordObject(reader.Session, result, placeholder);

        byte[] protocolToken = [];
        byte[] payload = [];

        var seen = 0u;
        var id = 0u;
        Field header = default;

        while (true)
        {
            reader.ReadFieldHeader(ref header);

            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            id += header.FieldIdDelta;
            FunctionalWire.MarkSeen(TypeName, ref seen, id);

            switch (id)
            {
                case 0U:
                    protocolToken = ByteArrayCodec.ReadValue(ref reader, header);
                    break;
                case 1U:
                    payload = ByteArrayCodec.ReadValue(ref reader, header);
                    break;
                default:
                    throw FunctionalWire.UnknownField(TypeName, id);
            }
        }

        if (seen != RequiredFields)
        {
            throw FunctionalWire.MissingFields(TypeName, seen, RequiredFields);
        }

        result.Initialize(protocolToken, payload);
        return result;
    }
}

/// <summary>
/// Explicit serializer for <see cref="FunctionalRequest"/>: exactly one serialized field, the
/// envelope. Method metadata, options, target, and cancellation state are never wire data.
/// </summary>
internal sealed class FunctionalRequestCodec : IFieldCodec<FunctionalRequest>
{
    private const string TypeName = nameof(FunctionalRequest);
    private const uint RequiredFields = 0b1u;

    private readonly Type _codecFieldType = typeof(FunctionalRequest);
    private readonly ICodecProvider _codecProvider;
    private readonly IActivator<FunctionalRequest> _activator;
    private IFieldCodec<FunctionalRequestEnvelope>? _envelopeCodec;

    /// <summary>Create the codec with the shared codec provider and the request activator.</summary>
    public FunctionalRequestCodec(ICodecProvider codecProvider, IActivator<FunctionalRequest> activator)
    {
        _codecProvider = codecProvider;
        _activator = activator;
    }

    private IFieldCodec<FunctionalRequestEnvelope> EnvelopeCodec =>
        _envelopeCodec ??= _codecProvider.GetCodec<FunctionalRequestEnvelope>();

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        FunctionalRequest value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (value is null)
        {
            ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
            return;
        }

        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        EnvelopeCodec.WriteField(ref writer, 0U, typeof(FunctionalRequestEnvelope), value.Envelope);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public FunctionalRequest ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference<FunctionalRequest, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);

        var result = _activator.Create();
        FunctionalRequestEnvelope? envelope = null;

        var seen = 0u;
        var id = 0u;
        Field header = default;

        while (true)
        {
            reader.ReadFieldHeader(ref header);

            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            id += header.FieldIdDelta;
            FunctionalWire.MarkSeen(TypeName, ref seen, id);

            switch (id)
            {
                case 0U:
                    envelope = EnvelopeCodec.ReadValue(ref reader, header);
                    break;
                default:
                    throw FunctionalWire.UnknownField(TypeName, id);
            }
        }

        if (seen != RequiredFields || envelope is null)
        {
            throw FunctionalWire.MissingFields(TypeName, seen, RequiredFields);
        }

        result.SetEnvelope(envelope);

        // The received request carries its scheduling flags in the envelope; the Orleans
        // request options are restored from those validated flags rather than sent on the wire.
        result.ApplyAdmissionOptions();
        return result;
    }
}

/// <summary>
/// Explicit serializer for <see cref="FunctionalTransactionRequest"/>: the three fields Orleans'
/// own <c>TransactionRequestBase</c> declares, written by Orleans' own base codec, then the same
/// single derived field the non-transactional request has — the envelope.
/// </summary>
/// <remarks>
/// The base segment is deliberately not hand-written. <c>TransactionRequestBase</c> is
/// <c>[GenerateSerializer]</c>, so Orleans generates <c>IBaseCodec&lt;TransactionRequestBase&gt;</c>
/// inside <c>Orleans.Transactions</c> and every generated transactional invokable's codec resolves
/// exactly that service and calls it first, followed by <c>WriteEndBase()</c>. Doing the same here
/// means the transaction fields — including <c>TransactionInfo</c>, whose shape is Orleans'
/// business and has already changed once (<c>UseExclusiveLock</c> is newer than the checked-in
/// 10.1.0 API baseline) — are always written by the version of Orleans that owns them.
/// </remarks>
internal sealed class FunctionalTransactionRequestCodec : IFieldCodec<FunctionalTransactionRequest>
{
    private const string TypeName = nameof(FunctionalTransactionRequest);
    private const uint RequiredFields = 0b1u;

    private readonly Type _codecFieldType = typeof(FunctionalTransactionRequest);
    private readonly ICodecProvider _codecProvider;
    private readonly IActivator<FunctionalTransactionRequest> _activator;
    private readonly IBaseCodec<TransactionRequestBase> _baseCodec;
    private IFieldCodec<FunctionalRequestEnvelope>? _envelopeCodec;

    /// <summary>Create the codec with the shared codec provider and the request activator.</summary>
    public FunctionalTransactionRequestCodec(
        ICodecProvider codecProvider,
        IActivator<FunctionalTransactionRequest> activator)
    {
        _codecProvider = codecProvider;
        _activator = activator;
        _baseCodec = codecProvider.GetBaseCodec<TransactionRequestBase>();
    }

    private IFieldCodec<FunctionalRequestEnvelope> EnvelopeCodec =>
        _envelopeCodec ??= _codecProvider.GetCodec<FunctionalRequestEnvelope>();

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        FunctionalTransactionRequest value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (value is null)
        {
            ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
            return;
        }

        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        _baseCodec.Serialize(ref writer, value);
        writer.WriteEndBase();
        EnvelopeCodec.WriteField(ref writer, 0U, typeof(FunctionalRequestEnvelope), value.Envelope);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public FunctionalTransactionRequest ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference<FunctionalTransactionRequest, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);

        var result = _activator.Create();
        _baseCodec.Deserialize(ref reader, result);

        FunctionalRequestEnvelope? envelope = null;

        var seen = 0u;
        var id = 0u;
        Field header = default;

        while (true)
        {
            reader.ReadFieldHeader(ref header);

            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            id += header.FieldIdDelta;
            FunctionalWire.MarkSeen(TypeName, ref seen, id);

            switch (id)
            {
                case 0U:
                    envelope = EnvelopeCodec.ReadValue(ref reader, header);
                    break;
                default:
                    throw FunctionalWire.UnknownField(TypeName, id);
            }
        }

        if (seen != RequiredFields || envelope is null)
        {
            throw FunctionalWire.MissingFields(TypeName, seen, RequiredFields);
        }

        result.SetEnvelope(envelope);

        // The received request carries its scheduling flags and its transaction option in the
        // envelope; both are restored from those validated flags rather than trusted from the
        // wire copy Orleans' base codec also carries.
        result.ApplyAdmissionOptions();
        return result;
    }
}

/// <summary>
/// Explicit serializer for <see cref="FunctionalStreamRequest"/>: the one field Orleans'
/// <c>AsyncEnumerableRequest&lt;T&gt;</c> declares (<c>MaxBatchSize</c>), written by Orleans' own
/// base codec, then the same single derived field the other two request shapes have — the envelope.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="FunctionalTransactionRequestCodec"/>: <c>AsyncEnumerableRequest</c>
/// is <c>[GenerateSerializer]</c>, so Orleans emits
/// <c>IBaseCodec&lt;AsyncEnumerableRequest&lt;T&gt;&gt;</c> inside <c>Orleans.Core.Abstractions</c>
/// and every generated <c>IAsyncEnumerable</c> invokable's codec resolves exactly that service and
/// calls it first, followed by <c>WriteEndBase()</c>. The batch size is therefore always written by
/// the version of Orleans that owns it.
/// </remarks>
internal sealed class FunctionalStreamRequestCodec : IFieldCodec<FunctionalStreamRequest>
{
    private const string TypeName = nameof(FunctionalStreamRequest);
    private const uint RequiredFields = 0b1u;

    private readonly Type _codecFieldType = typeof(FunctionalStreamRequest);
    private readonly ICodecProvider _codecProvider;
    private readonly IActivator<FunctionalStreamRequest> _activator;
    private readonly IBaseCodec<AsyncEnumerableRequest<FunctionalReply>> _baseCodec;
    private IFieldCodec<FunctionalRequestEnvelope>? _envelopeCodec;

    /// <summary>Create the codec with the shared codec provider and the request activator.</summary>
    public FunctionalStreamRequestCodec(
        ICodecProvider codecProvider,
        IActivator<FunctionalStreamRequest> activator)
    {
        _codecProvider = codecProvider;
        _activator = activator;
        _baseCodec = codecProvider.GetBaseCodec<AsyncEnumerableRequest<FunctionalReply>>();
    }

    private IFieldCodec<FunctionalRequestEnvelope> EnvelopeCodec =>
        _envelopeCodec ??= _codecProvider.GetCodec<FunctionalRequestEnvelope>();

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        FunctionalStreamRequest value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (value is null)
        {
            ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
            return;
        }

        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        _baseCodec.Serialize(ref writer, value);
        writer.WriteEndBase();
        EnvelopeCodec.WriteField(ref writer, 0U, typeof(FunctionalRequestEnvelope), value.Envelope);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public FunctionalStreamRequest ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference<FunctionalStreamRequest, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);

        var result = _activator.Create();
        _baseCodec.Deserialize(ref reader, result);

        FunctionalRequestEnvelope? envelope = null;

        var seen = 0u;
        var id = 0u;
        Field header = default;

        while (true)
        {
            reader.ReadFieldHeader(ref header);

            if (header.IsEndBaseOrEndObject)
            {
                break;
            }

            id += header.FieldIdDelta;
            FunctionalWire.MarkSeen(TypeName, ref seen, id);

            switch (id)
            {
                case 0U:
                    envelope = EnvelopeCodec.ReadValue(ref reader, header);
                    break;
                default:
                    throw FunctionalWire.UnknownField(TypeName, id);
            }
        }

        if (seen != RequiredFields || envelope is null)
        {
            throw FunctionalWire.MissingFields(TypeName, seen, RequiredFields);
        }

        result.SetEnvelope(envelope);
        result.ValidateAdmissionFlags();
        return result;
    }
}

/// <summary>
/// Local copier for <see cref="FunctionalRequestEnvelope"/>. The envelope is immutable after
/// construction, so a local copy shares it.
/// </summary>
internal sealed class FunctionalRequestEnvelopeCopier : IDeepCopier<FunctionalRequestEnvelope>
{
    /// <inheritdoc />
    public FunctionalRequestEnvelope DeepCopy(FunctionalRequestEnvelope input, CopyContext context) => input;
}

/// <summary>
/// Local copier for <see cref="FunctionalReply"/>. The reply is immutable after construction,
/// so a local copy shares it.
/// </summary>
internal sealed class FunctionalReplyCopier : IDeepCopier<FunctionalReply>
{
    /// <inheritdoc />
    public FunctionalReply DeepCopy(FunctionalReply input, CopyContext context) => input;
}

/// <summary>
/// Local copier for <see cref="FunctionalRequest"/>. It preserves the envelope, the dynamic
/// request options, the caller's cancellation state, and the caller-side method metadata, and
/// resets the target and the target-local cancellation resources.
/// </summary>
internal sealed class FunctionalRequestCopier : IDeepCopier<FunctionalRequest>
{
    /// <inheritdoc />
    public FunctionalRequest DeepCopy(FunctionalRequest input, CopyContext context)
    {
        if (input is null)
        {
            return null!;
        }

        if (context.TryGetCopy<FunctionalRequest>(input, out var existing) && existing is not null)
        {
            return existing;
        }

        var copy = new FunctionalRequest(input.Envelope, input.CallerToken);
        context.RecordCopy(input, copy);
        copy.AddInvokeMethodOptions(input.Options);

        // Only real caller-side metadata is carried over: the fallback the request reports
        // until metadata is stored must not be promoted into a stored value by a copy.
        if (input.HasCallFilterMetadata)
        {
            copy.SetCallerMetadata(input.GetInterfaceType(), input.GetMethod());
        }

        return copy;
    }
}

/// <summary>
/// Local copier for <see cref="FunctionalTransactionRequest"/>. It preserves everything the
/// non-transactional copier preserves and additionally lets Orleans' own base copier carry the
/// transaction option and the forked <c>TransactionInfo</c> across the copy — a local call never
/// serializes the request, so without this a same-silo participant would join no transaction.
/// </summary>
internal sealed class FunctionalTransactionRequestCopier : IDeepCopier<FunctionalTransactionRequest>
{
    private readonly Serializer<OrleansTransactionAbortedException> _exceptionSerializer;
    private readonly IServiceProvider _services;
    private readonly IBaseCopier<TransactionRequestBase> _baseCopier;

    /// <summary>Create the copier with the request dependencies and Orleans' transaction base copier.</summary>
    public FunctionalTransactionRequestCopier(
        ICodecProvider codecProvider,
        Serializer<OrleansTransactionAbortedException> exceptionSerializer,
        IServiceProvider services)
    {
        _exceptionSerializer = exceptionSerializer;
        _services = services;
        _baseCopier = codecProvider.GetBaseCopier<TransactionRequestBase>();
    }

    /// <inheritdoc />
    public FunctionalTransactionRequest DeepCopy(FunctionalTransactionRequest input, CopyContext context)
    {
        if (input is null)
        {
            return null!;
        }

        if (context.TryGetCopy<FunctionalTransactionRequest>(input, out var existing) && existing is not null)
        {
            return existing;
        }

        var copy = new FunctionalTransactionRequest(
            _exceptionSerializer,
            _services,
            input.Envelope,
            input.CallerToken);

        context.RecordCopy(input, copy);

        _baseCopier.DeepCopy(input, copy, context);
        copy.AddInvokeMethodOptions(input.Options);

        // Only real caller-side metadata is carried over: the fallback the request reports
        // until metadata is stored must not be promoted into a stored value by a copy.
        if (input.HasCallFilterMetadata)
        {
            copy.SetCallerMetadata(input.GetInterfaceType(), input.GetMethod());
        }

        return copy;
    }
}

/// <summary>
/// Local copier for <see cref="FunctionalStreamRequest"/>. It preserves the envelope and lets
/// Orleans' own base copier carry <c>MaxBatchSize</c> across the copy, which is what a same-silo
/// enumeration needs: a local call never serializes the request, so without this every local
/// stream would silently fall back to the default batch size.
/// </summary>
internal sealed class FunctionalStreamRequestCopier : IDeepCopier<FunctionalStreamRequest>
{
    private readonly IBaseCopier<AsyncEnumerableRequest<FunctionalReply>> _baseCopier;

    /// <summary>Create the copier with Orleans' async-enumerable base copier.</summary>
    public FunctionalStreamRequestCopier(ICodecProvider codecProvider) =>
        _baseCopier = codecProvider.GetBaseCopier<AsyncEnumerableRequest<FunctionalReply>>();

    /// <inheritdoc />
    public FunctionalStreamRequest DeepCopy(FunctionalStreamRequest input, CopyContext context)
    {
        if (input is null)
        {
            return null!;
        }

        if (context.TryGetCopy<FunctionalStreamRequest>(input, out var existing) && existing is not null)
        {
            return existing;
        }

        var copy = new FunctionalStreamRequest(input.Envelope, input.CallerToken);
        context.RecordCopy(input, copy);
        _baseCopier.DeepCopy(input, copy, context);

        // Only real caller-side metadata is carried over: the fallback the request reports until
        // metadata is stored must not be promoted into a stored value by a copy.
        if (input.HasCallFilterMetadata)
        {
            copy.SetCallerMetadata(input.GetInterfaceType(), input.GetMethod());
        }

        return copy;
    }
}

/// <summary>
/// The type filter of the fixed transport: it claims exactly the five fixed transport types
/// and nothing else. Contracts, API records, selectors, reflection metadata, persistent-state
/// descriptors, and services are never claimed and therefore never enter request bytes through
/// this filter.
/// </summary>
internal sealed class FunctionalTransportTypeFilter : ITypeFilter
{
    /// <inheritdoc />
    public bool? IsTypeAllowed(Type type) =>
        FunctionalTransportSerialization.IsFixedTransportType(type) ? true : null;
}

/// <summary>
/// Publishes the explicit fixed-transport serializers, copiers, and activators to every
/// Orleans serializer built in a process which loads this assembly.
/// </summary>
internal sealed class FunctionalTransportManifestProvider : TypeManifestProviderBase
{
    /// <inheritdoc />
    protected override void ConfigureInner(TypeManifestOptions config) =>
        FunctionalTransportSerialization.Configure(config);
}

/// <summary>
/// Registration of the explicit fixed-transport serializers, copiers, and activators.
/// </summary>
internal static class FunctionalTransportSerialization
{
    /// <summary>True for exactly the five fixed transport types.</summary>
    public static bool IsFixedTransportType(Type type) =>
        type == typeof(FunctionalRequestEnvelope)
        || type == typeof(FunctionalReply)
        || type == typeof(FunctionalRequest)
        || type == typeof(FunctionalTransactionRequest)
        || type == typeof(FunctionalStreamRequest);

    /// <summary>
    /// Register the explicit fixed-transport serialization on a serializer builder. Repeated
    /// registration is idempotent.
    /// </summary>
    public static ISerializerBuilder AddFunctionalTransport(ISerializerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddFunctionalTransport(builder.Services);
        return builder;
    }

    /// <summary>
    /// Register the explicit fixed-transport serialization on a service collection. Repeated
    /// registration is idempotent.
    /// </summary>
    public static IServiceCollection AddFunctionalTransport(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<TypeManifestOptions>(Configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITypeFilter, FunctionalTransportTypeFilter>());
        return services;
    }

    /// <summary>Add the explicit codecs, copiers, and activators to a type manifest.</summary>
    public static void Configure(TypeManifestOptions config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Serializers.Add(typeof(FunctionalRequestEnvelopeCodec));
        config.Serializers.Add(typeof(FunctionalReplyCodec));
        config.Serializers.Add(typeof(FunctionalRequestCodec));
        config.Serializers.Add(typeof(FunctionalTransactionRequestCodec));
        config.Serializers.Add(typeof(FunctionalStreamRequestCodec));

        config.Copiers.Add(typeof(FunctionalRequestEnvelopeCopier));
        config.Copiers.Add(typeof(FunctionalReplyCopier));
        config.Copiers.Add(typeof(FunctionalRequestCopier));
        config.Copiers.Add(typeof(FunctionalTransactionRequestCopier));
        config.Copiers.Add(typeof(FunctionalStreamRequestCopier));

        config.Activators.Add(typeof(FunctionalRequestEnvelopeActivator));
        config.Activators.Add(typeof(FunctionalReplyActivator));
        config.Activators.Add(typeof(FunctionalRequestActivator));
        config.Activators.Add(typeof(FunctionalTransactionRequestActivator));
        config.Activators.Add(typeof(FunctionalStreamRequestActivator));
    }
}
