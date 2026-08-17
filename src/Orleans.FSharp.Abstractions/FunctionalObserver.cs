using System.Buffers;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Concurrency;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

// The notification envelope appears in the signature of a grain interface
// (IFunctionalObserverTarget.DispatchAsync), and Orleans validates at silo startup that every
// type referenced by a grain-interface signature has a serializer and a copier. That interface is
// NON-generic, so unlike IFunctionalGrainTarget<TActor> the validator can close and scan it in
// every process that merely loads this assembly — including one that hosts no functional grain at
// all. The assembly-level manifest provider therefore registers the explicit observer codecs
// wherever this assembly is loaded, so referencing Orleans.FSharp.Abstractions never breaks silo
// startup.
[assembly: Orleans.Serialization.Configuration.TypeManifestProvider(
    typeof(Orleans.FSharp.FunctionalObserverManifestProvider))]

namespace Orleans.FSharp;

/// <summary>
/// The fixed notification data of one push to a functional observer. Immutable after
/// construction: the deserializing codec initializes an activated instance exactly once and
/// every field is validated at that point.
/// </summary>
/// <remarks>
/// The mirror image of <see cref="FunctionalRequestEnvelope"/> for the observer direction. It is
/// public only because it appears in the signature of <see cref="IFunctionalObserverTarget"/>,
/// which Orleans' proxy generator must see; it is not part of the authoring surface.
/// </remarks>
public sealed class FunctionalNotificationEnvelope
{
    private string _observerType = string.Empty;
    private int _contractVersion;
    private string _operationId = string.Empty;
    private byte[] _protocolToken = [];
    private byte[] _payload = [];
    private bool _initialized;

    /// <summary>Create an uninitialized instance for the deserialization activator.</summary>
    public FunctionalNotificationEnvelope()
    {
    }

    /// <summary>Create a complete, validated envelope.</summary>
    public FunctionalNotificationEnvelope(
        string observerType,
        int contractVersion,
        string operationId,
        byte[] protocolToken,
        byte[] payload) =>
        Initialize(observerType, contractVersion, operationId, protocolToken, payload);

    /// <summary>Field 0 — the observer type, the observer-side analogue of the grain type.</summary>
    public string ObserverType => _observerType;

    /// <summary>Field 1 — the application contract version.</summary>
    public int ContractVersion => _contractVersion;

    /// <summary>Field 2 — the stable ordinal operation ID of the push operation.</summary>
    public string OperationId => _operationId;

    /// <summary>Field 3 — the raw SHA-256 notify-direction protocol token.</summary>
    public byte[] ProtocolToken => _protocolToken;

    /// <summary>Field 4 — the serialized message payload.</summary>
    public byte[] Payload => _payload;

    /// <summary>The length in bytes of the serialized message payload.</summary>
    public int PayloadLength => _payload.Length;

    /// <summary>
    /// Populate and validate every field exactly once. The deserializing codec calls this after
    /// it has read all five fields; a second call is a programming error.
    /// </summary>
    internal void Initialize(
        string observerType,
        int contractVersion,
        string operationId,
        byte[] protocolToken,
        byte[] payload)
    {
        if (_initialized)
        {
            throw FunctionalTransportDiagnostics.Fail(
                "a notification envelope is immutable after construction and cannot be initialized twice.");
        }

        FunctionalTransportDiagnostics.EnsureWireText(observerType, "observerType");

        if (contractVersion <= 0)
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"'contractVersion' must be a positive integer, but {contractVersion.ToString(CultureInfo.InvariantCulture)} was supplied.");
        }

        FunctionalTransportDiagnostics.EnsureWireText(operationId, "operationId");
        FunctionalTransportDiagnostics.EnsureProtocolToken(protocolToken, "protocolToken");

        if (payload is null)
        {
            throw FunctionalTransportDiagnostics.Fail("'payload' must not be null.");
        }

        _observerType = observerType;
        _contractVersion = contractVersion;
        _operationId = operationId;
        _protocolToken = protocolToken;
        _payload = payload;
        _initialized = true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"FunctionalNotificationEnvelope({_observerType} v{_contractVersion} {_operationId}, payload = {_payload.Length} bytes)");
}

/// <summary>
/// The single observer target interface of the functional runtime: every application observer,
/// of every brand, is delivered through this one interface.
/// </summary>
/// <remarks>
/// It is declared in C# for one reason: Orleans' proxy generators are Roslyn source generators
/// and never run over F#, so an F#-declared <see cref="IGrainObserver"/> has no proxy and
/// <c>CreateObjectReference</c> fails on it. One generated proxy here serves every observer an
/// application will ever declare, which is what keeps the authoring model codegen-free — exactly
/// as one fixed request serves every grain operation.
/// </remarks>
public interface IFunctionalObserverTarget : IGrainObserver
{
    /// <summary>
    /// Deliver one notification to the observed object.
    /// </summary>
    /// <remarks>
    /// <see cref="OneWayAttribute"/> is what makes delivery best-effort rather than acknowledged,
    /// and it is load-bearing rather than an optimisation. Without it the notifying grain awaits
    /// the observed object's completion, so one observer whose object reference has been released
    /// blocks that grain's handler until Orleans times the message out — thirty seconds, for a
    /// subscriber the application has already forgotten about. With it, the notifying handler
    /// completes as soon as the notification has entered the local send path, an observer that
    /// throws is reported only on its own side, and a dead reference costs nothing.
    /// </remarks>
    [OneWay]
    Task DispatchAsync(FunctionalNotificationEnvelope envelope);
}

/// <summary>
/// A typed, serializable handle to one client-hosted observer.
/// </summary>
/// <remarks>
/// <para>
/// The type parameters are phantom — they exist so an observer of one brand cannot be handed to
/// a grain expecting another, and neither appears on the wire. The wire form is exactly the
/// observer type name, the contract version, and the Orleans object reference.
/// </para>
/// <para>
/// A handle may be an operation's argument, or an element of a tuple argument, because Orleans
/// owns both of those shapes and routes each element to its own codec. It may NOT be a field of
/// an F# record, option, list or union argument: the F# binary codec owns those payloads whole
/// and has no codec for an Orleans object reference.
/// </para>
/// </remarks>
/// <typeparam name="TBrand">The application's observer brand.</typeparam>
/// <typeparam name="TApi">The observer's handler-record type.</typeparam>
public sealed class FunctionalObserverHandle<TBrand, TApi>
{
    /// <summary>Create a handle over a live object reference.</summary>
    /// <param name="observerType">The observer type this handle was created for.</param>
    /// <param name="contractVersion">The observer contract version.</param>
    /// <param name="target">The Orleans object reference of the observed object.</param>
    /// <param name="codec">The payload codec of the process holding this handle.</param>
    /// <param name="localObject">
    /// The locally hosted observed object, on the process that created the handle, and null on
    /// any process that received one over the wire.
    /// </param>
    internal FunctionalObserverHandle(
        string observerType,
        int contractVersion,
        IFunctionalObserverTarget target,
        IFunctionalPayloadCodec codec,
        object? localObject)
    {
        FunctionalTransportDiagnostics.EnsureWireText(observerType, "observerType");

        if (contractVersion <= 0)
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"'contractVersion' must be a positive integer, but {contractVersion.ToString(CultureInfo.InvariantCulture)} was supplied.");
        }

        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(codec);

        ObserverType = observerType;
        ContractVersion = contractVersion;
        Target = target;
        Codec = codec;
        LocalObject = localObject;
    }

    /// <summary>Field 0 — the observer type this handle was created for.</summary>
    public string ObserverType { get; }

    /// <summary>Field 1 — the observer contract version this handle was created for.</summary>
    public int ContractVersion { get; }

    /// <summary>Field 2 — the Orleans object reference of the observed object.</summary>
    public IFunctionalObserverTarget Target { get; }

    /// <summary>
    /// The exact-type payload codec of the process holding this handle. It is NOT part of the
    /// wire form: the creating process supplies its own, and the codec that deserializes a
    /// handle supplies the receiving process's. A handle is therefore always paired with the
    /// serializer of the process that is about to use it, on either side of the wire.
    /// </summary>
    internal IFunctionalPayloadCodec Codec { get; }

    /// <summary>
    /// The locally hosted observed object, kept alive by this handle. Not part of the wire form,
    /// and null on any process that received a handle rather than created one.
    /// </summary>
    /// <remarks>
    /// Orleans' object-reference table holds an observed object WEAKLY: nothing in Orleans keeps
    /// a client-hosted observer from being collected, and once it is, its reference is dead and
    /// pushes to it are dropped silently. Anchoring the object here makes the handle the thing
    /// that owns its lifetime, which is the lifetime an application can actually reason about —
    /// keep the handle, keep receiving.
    /// </remarks>
    internal object? LocalObject { get; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"FunctionalObserverHandle({ObserverType} v{ContractVersion})");
}

/// <summary>Explicit serializer for <see cref="FunctionalNotificationEnvelope"/>: fields 0-4.</summary>
internal sealed class FunctionalNotificationEnvelopeCodec : IFieldCodec<FunctionalNotificationEnvelope>
{
    private const string TypeName = nameof(FunctionalNotificationEnvelope);
    private const uint RequiredFields = 0b11111u;

    private readonly Type _codecFieldType = typeof(FunctionalNotificationEnvelope);
    private readonly IActivator<FunctionalNotificationEnvelope> _activator;

    /// <summary>Create the codec with the activator Orleans resolves for the envelope.</summary>
    public FunctionalNotificationEnvelopeCodec(IActivator<FunctionalNotificationEnvelope> activator) =>
        _activator = activator;

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        FunctionalNotificationEnvelope value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        StringCodec.WriteField(ref writer, 0U, value.ObserverType);
        Int32Codec.WriteField(ref writer, 1U, value.ContractVersion);
        StringCodec.WriteField(ref writer, 1U, value.OperationId);
        ByteArrayCodec.WriteField(ref writer, 1U, value.ProtocolToken);
        ByteArrayCodec.WriteField(ref writer, 1U, value.Payload);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public FunctionalNotificationEnvelope ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference<FunctionalNotificationEnvelope, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();

        var placeholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        var result = _activator.Create();
        ReferenceCodec.RecordObject(reader.Session, result, placeholder);

        var observerType = string.Empty;
        var contractVersion = 0;
        var operationId = string.Empty;
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
                    observerType = StringCodec.ReadValue(ref reader, header);
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

        result.Initialize(observerType, contractVersion, operationId, protocolToken, payload);
        return result;
    }
}

/// <summary>
/// Explicit serializer for <see cref="FunctionalObserverHandle{TBrand,TApi}"/>: fields 0-2, the
/// last of which is the Orleans object reference written by Orleans' own reference codec.
/// </summary>
/// <remarks>
/// Registered as an OPEN generic through the Orleans type manifest, not through the service
/// collection: MS DI's open-generic registration requires the service and implementation to have
/// equal arity, and <c>IFieldCodec&lt;T&gt;</c> has one type parameter while this codec has two.
/// Orleans' codec provider closes a manifest-registered generic codec over the field type's own
/// generic arguments instead, which is exactly the pairing this needs.
/// </remarks>
internal sealed class FunctionalObserverHandleCodec<TBrand, TApi> : IFieldCodec<FunctionalObserverHandle<TBrand, TApi>>
{
    private const string TypeName = "FunctionalObserverHandle";
    private const uint RequiredFields = 0b111u;

    private readonly Type _codecFieldType = typeof(FunctionalObserverHandle<TBrand, TApi>);
    private readonly IFieldCodec<IFunctionalObserverTarget> _targetCodec;
    private readonly IFunctionalPayloadCodec _payloadCodec;

    /// <summary>Create the handle codec over the Orleans codec of the observer reference.</summary>
    public FunctionalObserverHandleCodec(
        IFieldCodec<IFunctionalObserverTarget> targetCodec,
        IFunctionalPayloadCodec payloadCodec)
    {
        _targetCodec = targetCodec;
        _payloadCodec = payloadCodec;
    }

    /// <inheritdoc />
    public void WriteField<TBufferWriter>(
        ref Writer<TBufferWriter> writer,
        uint fieldIdDelta,
        Type expectedType,
        FunctionalObserverHandle<TBrand, TApi> value)
        where TBufferWriter : IBufferWriter<byte>
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
        {
            return;
        }

        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        StringCodec.WriteField(ref writer, 0U, value.ObserverType);
        Int32Codec.WriteField(ref writer, 1U, value.ContractVersion);
        _targetCodec.WriteField(ref writer, 1U, typeof(IFunctionalObserverTarget), value.Target);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public FunctionalObserverHandle<TBrand, TApi> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
        {
            return ReferenceCodec.ReadReference<FunctionalObserverHandle<TBrand, TApi>, TInput>(ref reader, field);
        }

        field.EnsureWireTypeTagDelimited();

        var placeholder = ReferenceCodec.CreateRecordPlaceholder(reader.Session);

        var observerType = string.Empty;
        var contractVersion = 0;
        IFunctionalObserverTarget? target = null;

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
                    observerType = StringCodec.ReadValue(ref reader, header);
                    break;
                case 1U:
                    contractVersion = Int32Codec.ReadValue(ref reader, header);
                    break;
                case 2U:
                    target = _targetCodec.ReadValue(ref reader, header);
                    break;
                default:
                    throw FunctionalWire.UnknownField(TypeName, id);
            }
        }

        if (seen != RequiredFields)
        {
            throw FunctionalWire.MissingFields(TypeName, seen, RequiredFields);
        }

        if (target is null)
        {
            throw FunctionalTransportDiagnostics.Fail(
                "an observer handle arrived without an object reference; the observed object cannot be reached.");
        }

        // A handle read off the wire never has a local object: the observed object lives in the
        // process that created the handle, and this one reaches it through the reference alone.
        var result = new FunctionalObserverHandle<TBrand, TApi>(
            observerType,
            contractVersion,
            target,
            _payloadCodec,
            localObject: null);
        ReferenceCodec.RecordObject(reader.Session, result, placeholder);
        return result;
    }
}

/// <summary>Activator for the deserializing notification-envelope codec.</summary>
internal sealed class FunctionalNotificationEnvelopeActivator : IActivator<FunctionalNotificationEnvelope>
{
    /// <inheritdoc />
    public FunctionalNotificationEnvelope Create() => new();
}

/// <summary>
/// Deep copier for the notification envelope. The envelope is immutable after construction and
/// its two byte arrays are never handed out for mutation, so a copy is the instance itself.
/// </summary>
internal sealed class FunctionalNotificationEnvelopeCopier : IDeepCopier<FunctionalNotificationEnvelope>
{
    /// <inheritdoc />
    public FunctionalNotificationEnvelope DeepCopy(FunctionalNotificationEnvelope input, CopyContext context) => input;
}

/// <summary>Deep copier for the observer handle, which is likewise immutable.</summary>
internal sealed class FunctionalObserverHandleCopier<TBrand, TApi> : IDeepCopier<FunctionalObserverHandle<TBrand, TApi>>
{
    /// <inheritdoc />
    public FunctionalObserverHandle<TBrand, TApi> DeepCopy(
        FunctionalObserverHandle<TBrand, TApi> input,
        CopyContext context) => input;
}

/// <summary>
/// The type filter of the observer transport: it claims the notification envelope and every
/// closed observer handle, and nothing else.
/// </summary>
internal sealed class FunctionalObserverTypeFilter : ITypeFilter
{
    /// <inheritdoc />
    public bool? IsTypeAllowed(Type type) =>
        FunctionalObserverSerialization.IsObserverTransportType(type) ? true : null;
}

/// <summary>
/// Publishes the explicit observer-transport serializers and copiers to every Orleans serializer
/// built in a process which loads this assembly.
/// </summary>
internal sealed class FunctionalObserverManifestProvider : TypeManifestProviderBase
{
    /// <inheritdoc />
    protected override void ConfigureInner(TypeManifestOptions config) =>
        FunctionalObserverSerialization.Configure(config);
}

/// <summary>Registration of the explicit observer-transport serialization.</summary>
internal static class FunctionalObserverSerialization
{
    /// <summary>True for the notification envelope and for any closed observer handle.</summary>
    public static bool IsObserverTransportType(Type type) =>
        type == typeof(FunctionalNotificationEnvelope)
        || (type.IsGenericType
            && !type.IsGenericTypeDefinition
            && type.GetGenericTypeDefinition() == typeof(FunctionalObserverHandle<,>));

    /// <summary>
    /// Register the explicit observer-transport serialization on a service collection. Repeated
    /// registration is idempotent.
    /// </summary>
    public static IServiceCollection AddFunctionalObserverTransport(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Configure<TypeManifestOptions>(Configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITypeFilter, FunctionalObserverTypeFilter>());
        return services;
    }

    /// <summary>Add the explicit codecs, copiers, and activators to a type manifest.</summary>
    public static void Configure(TypeManifestOptions config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Serializers.Add(typeof(FunctionalNotificationEnvelopeCodec));
        config.Serializers.Add(typeof(FunctionalObserverHandleCodec<,>));

        config.Copiers.Add(typeof(FunctionalNotificationEnvelopeCopier));
        config.Copiers.Add(typeof(FunctionalObserverHandleCopier<,>));

        config.Activators.Add(typeof(FunctionalNotificationEnvelopeActivator));
    }
}
