using System.Globalization;

namespace Orleans.FSharp;

/// <summary>
/// Read-only view of one functional request, published to application call filters.
/// </summary>
/// <remarks>
/// This is the only part of the fixed transport that application code sees: the envelope,
/// the request, the reply, and the dispatch target stay internal to the runtime.
/// </remarks>
public interface IFunctionalRequestMetadata
{
    /// <summary>The explicit Orleans grain type of the addressed contract.</summary>
    string GrainType { get; }

    /// <summary>The application contract version carried by the request.</summary>
    int ContractVersion { get; }

    /// <summary>The stable ordinal wire ID of the invoked operation.</summary>
    string OperationId { get; }

    /// <summary>True when the operation was declared <c>readOnly</c>.</summary>
    bool IsReadOnly { get; }

    /// <summary>True when the operation was declared <c>oneWay</c>.</summary>
    bool IsOneWay { get; }

    /// <summary>True when the operation was declared <c>alwaysInterleave</c>.</summary>
    bool IsAlwaysInterleave { get; }

    /// <summary>The length in bytes of the serialized argument payload.</summary>
    int PayloadLength { get; }
}

/// <summary>
/// Transport limits for the functional grain runtime.
/// </summary>
public sealed class FunctionalGrainTransportOptions
{
    /// <summary>The default maximum payload size: 16 MiB.</summary>
    public const int DefaultMaxPayloadBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The largest serialized argument or reply payload this endpoint accepts or sends.
    /// Must be positive. Each endpoint enforces its own local value; Orleans' general
    /// message-size limit can be stricter.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = DefaultMaxPayloadBytes;
}

/// <summary>
/// The admission-flag byte carried by <see cref="FunctionalRequestEnvelope"/>.
/// </summary>
internal static class FunctionalAdmissionFlags
{
    /// <summary>No policy flags.</summary>
    public const byte None = 0x00;

    /// <summary>Bit 0 — Orleans read-only scheduling.</summary>
    public const byte ReadOnly = 0x01;

    /// <summary>Bit 1 — one-way delivery.</summary>
    public const byte OneWay = 0x02;

    /// <summary>Bit 2 — always-interleave admission.</summary>
    public const byte AlwaysInterleave = 0x04;

    /// <summary>Bits 3-7 — reserved; a set reserved bit invalidates the request.</summary>
    public const byte Reserved = 0xF8;

    /// <summary>True when the value sets at least one reserved bit.</summary>
    public static bool HasReservedBits(byte flags) => (flags & Reserved) != 0;
}

/// <summary>Shared diagnostic vocabulary of the fixed transport.</summary>
internal static class FunctionalTransportDiagnostics
{
    /// <summary>Prefix identifying the stage in every fixed-transport diagnostic.</summary>
    public const string Stage = "Orleans.FSharp functional transport";

    /// <summary>The exact length in bytes of a protocol token (a raw SHA-256 digest).</summary>
    public const int ProtocolTokenLength = 32;

    /// <summary>Raise a transport-stage diagnostic.</summary>
    public static InvalidOperationException Fail(string message) =>
        new(Stage + ": " + message);

    /// <summary>
    /// Longest accepted wire text. Both fields it guards — the grain type and the operation ID —
    /// are dotted identifiers chosen by the application: the grain type is the name handed to
    /// <c>GrainType.Create</c>, the operation ID is an API record field name. A fully qualified
    /// .NET name of that shape stays well inside 256 characters in practice, so 512 leaves two
    /// times headroom for the longest legitimate value while capping a hostile one at half a
    /// kilobyte instead of a whole payload's worth. Nothing else bounds these two fields: the
    /// payload limit covers only field 5.
    /// </summary>
    public const int MaxWireTextLength = 512;

    /// <summary>Validate a bounded, control-character-free, non-empty ordinal string field.</summary>
    /// <remarks>
    /// Both fields are echoed verbatim into diagnostics that reach the silo log and the remote
    /// caller, so a value carrying CR, LF, or any other C0 control character could forge log
    /// lines, and an unbounded one could bloat every exception it appears in. NUL is one of the
    /// characters this rejects.
    /// </remarks>
    public static void EnsureWireText(string? value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw Fail($"'{fieldName}' must be a non-empty string.");
        }

        if (value.Length > MaxWireTextLength)
        {
            throw Fail(
                $"'{fieldName}' must be at most {MaxWireTextLength.ToString(CultureInfo.InvariantCulture)} characters, but {value.Length.ToString(CultureInfo.InvariantCulture)} were supplied.");
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] < ' ')
            {
                throw Fail(
                    $"'{fieldName}' must not contain control characters, but one appears at index {index.ToString(CultureInfo.InvariantCulture)}.");
            }
        }
    }

    /// <summary>Validate a protocol token: non-null and exactly 32 bytes.</summary>
    public static void EnsureProtocolToken(byte[]? value, string fieldName)
    {
        if (value is null)
        {
            throw Fail($"'{fieldName}' must not be null.");
        }

        if (value.Length != ProtocolTokenLength)
        {
            throw Fail(
                $"'{fieldName}' must be exactly {ProtocolTokenLength} bytes, but {value.Length} bytes were supplied.");
        }
    }
}

/// <summary>
/// The fixed request data of one functional call. Immutable after construction: the
/// deserializing codec initializes an activated instance exactly once and every field is
/// validated at that point.
/// </summary>
internal sealed class FunctionalRequestEnvelope : IFunctionalRequestMetadata
{
    private string _grainType = string.Empty;
    private int _contractVersion;
    private string _operationId = string.Empty;
    private byte[] _protocolToken = [];
    private byte _admissionFlags;
    private byte[] _payload = [];
    private bool _initialized;

    /// <summary>Create an uninitialized instance for the deserialization activator.</summary>
    internal FunctionalRequestEnvelope()
    {
    }

    /// <summary>Create a complete, validated envelope.</summary>
    internal FunctionalRequestEnvelope(
        string grainType,
        int contractVersion,
        string operationId,
        byte[] protocolToken,
        byte admissionFlags,
        byte[] payload) =>
        Initialize(grainType, contractVersion, operationId, protocolToken, admissionFlags, payload);

    /// <summary>Field 0 — the explicit Orleans grain type.</summary>
    public string GrainType => _grainType;

    /// <summary>Field 1 — the application contract version.</summary>
    public int ContractVersion => _contractVersion;

    /// <summary>Field 2 — the stable ordinal operation ID.</summary>
    public string OperationId => _operationId;

    /// <summary>Field 3 — the raw SHA-256 request protocol token.</summary>
    public byte[] ProtocolToken => _protocolToken;

    /// <summary>Field 4 — the admission-flag byte.</summary>
    public byte AdmissionFlags => _admissionFlags;

    /// <summary>Field 5 — the serialized argument payload.</summary>
    public byte[] Payload => _payload;

    /// <inheritdoc />
    public bool IsReadOnly => (_admissionFlags & FunctionalAdmissionFlags.ReadOnly) != 0;

    /// <inheritdoc />
    public bool IsOneWay => (_admissionFlags & FunctionalAdmissionFlags.OneWay) != 0;

    /// <inheritdoc />
    public bool IsAlwaysInterleave => (_admissionFlags & FunctionalAdmissionFlags.AlwaysInterleave) != 0;

    /// <inheritdoc />
    public int PayloadLength => _payload.Length;

    /// <summary>
    /// Populate and validate every field exactly once. The deserializing codec calls this
    /// after it has read all six fields; a second call is a programming error.
    /// </summary>
    internal void Initialize(
        string grainType,
        int contractVersion,
        string operationId,
        byte[] protocolToken,
        byte admissionFlags,
        byte[] payload)
    {
        if (_initialized)
        {
            throw FunctionalTransportDiagnostics.Fail(
                "a request envelope is immutable after construction and cannot be initialized twice.");
        }

        FunctionalTransportDiagnostics.EnsureWireText(grainType, "grainType");

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

        _grainType = grainType;
        _contractVersion = contractVersion;
        _operationId = operationId;
        _protocolToken = protocolToken;
        _admissionFlags = admissionFlags;
        _payload = payload;
        _initialized = true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"FunctionalRequestEnvelope({_grainType}/{_operationId}, version = {_contractVersion}, flags = 0x{_admissionFlags:x2}, payload = {_payload.Length} bytes)");
}

/// <summary>
/// The fixed reply data of one functional call. Immutable after construction.
/// </summary>
internal sealed class FunctionalReply
{
    private byte[] _protocolToken = [];
    private byte[] _payload = [];
    private bool _initialized;

    /// <summary>Create an uninitialized instance for the deserialization activator.</summary>
    internal FunctionalReply()
    {
    }

    /// <summary>Create a complete, validated reply.</summary>
    internal FunctionalReply(byte[] protocolToken, byte[] payload) => Initialize(protocolToken, payload);

    /// <summary>Field 0 — the raw SHA-256 reply protocol token.</summary>
    public byte[] ProtocolToken => _protocolToken;

    /// <summary>Field 1 — the serialized reply payload.</summary>
    public byte[] Payload => _payload;

    /// <summary>Populate and validate both fields exactly once.</summary>
    internal void Initialize(byte[] protocolToken, byte[] payload)
    {
        if (_initialized)
        {
            throw FunctionalTransportDiagnostics.Fail(
                "a reply is immutable after construction and cannot be initialized twice.");
        }

        FunctionalTransportDiagnostics.EnsureProtocolToken(protocolToken, "protocolToken");

        if (payload is null)
        {
            throw FunctionalTransportDiagnostics.Fail("'payload' must not be null.");
        }

        _protocolToken = protocolToken;
        _payload = payload;
        _initialized = true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"FunctionalReply(payload = {_payload.Length} bytes)");
}

/// <summary>
/// The non-generic dispatch seam: the actual invocation target of every functional request.
/// </summary>
internal interface IFunctionalDispatchTarget
{
    /// <summary>Dispatch one fixed request on the activation.</summary>
    ValueTask<FunctionalReply> DispatchAsync(
        FunctionalRequestEnvelope envelope,
        CancellationToken cancellationToken);
}

/// <summary>
/// The closed, actor-specific Orleans target interface. It supplies the manifest interface
/// identity and the Orleans method metadata; <see cref="IFunctionalDispatchTarget"/> is the
/// interface the request actually invokes.
/// </summary>
/// <typeparam name="TActor">The application's actor brand.</typeparam>
internal interface IFunctionalGrainTarget<TActor> : IGrain
{
    /// <summary>Dispatch one fixed request on the activation.</summary>
    ValueTask<FunctionalReply> DispatchAsync(
        FunctionalRequestEnvelope envelope,
        CancellationToken cancellationToken);
}

/// <summary>
/// Exact-type payload serialization, as seen by the fixed transport. The implementation lives
/// in <c>Orleans.FSharp</c> (spec 003 project ownership: "payload codec"); the reference only
/// carries the injected instance so the layer above it can serialize an argument and
/// deserialize a reply as the descriptor's exact CLR type.
/// </summary>
internal interface IFunctionalPayloadCodec
{
    /// <summary>Serialize one value as its exact declared type into a fresh byte array.</summary>
    byte[] Serialize<T>(T value);

    /// <summary>Deserialize one value as its exact declared type.</summary>
    T Deserialize<T>(byte[] payload);
}
