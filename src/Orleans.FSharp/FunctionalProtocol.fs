namespace Orleans.FSharp

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The protocol token of one operation and direction: the raw SHA-256 digest of
/// <c>grainType NUL version NUL operationId NUL direction</c> in UTF-8, where the version is
/// invariant ASCII decimal without sign or leading zero and the direction is the lowercase
/// literal <c>request</c> or <c>reply</c>.
/// </summary>
/// <remarks>
/// The digest detects descriptor misrouting. Argument and reply compatibility is governed by
/// the exact registered CLR types and the contract version, not by this token.
/// </remarks>
[<RequireQualifiedAccess>]
module internal ProtocolToken =

    /// <summary>The exact length in bytes of a protocol token.</summary>
    [<Literal>]
    let Length = 32

    /// <summary>The lowercase direction literal of a request.</summary>
    [<Literal>]
    let RequestDirection = "request"

    /// <summary>The lowercase direction literal of a reply.</summary>
    [<Literal>]
    let ReplyDirection = "reply"

    /// <summary>
    /// The lowercase direction literal of an observer notification.
    /// </summary>
    /// <remarks>
    /// A notification token cannot collide with a grain-operation token even when an observer
    /// type and a grain type share a name and an operation ID: the direction is part of the
    /// hashed preimage, and "notify" is neither "request" nor "reply", so the three preimages
    /// differ in their final NUL-separated field and hash to different digests. A collision
    /// would need a SHA-256 preimage collision, not a naming coincidence. What the token detects
    /// is the same thing it detects for grains — a notification routed to the wrong descriptor.
    /// </remarks>
    [<Literal>]
    let NotifyDirection = "notify"

    /// <summary>
    /// The lowercase direction literal of a server-streaming request. Spec 004 item 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why streaming gets its own direction rather than reusing <c>request</c>.</b> The token is
    /// what detects descriptor misrouting, and "the same operation ID at the same version changed
    /// from unary to streaming" is exactly a misrouting the reused direction could not detect. Under
    /// <c>acceptsVersions (BackwardCompatible n)</c> a host admits an older caller's version and
    /// then compares against the token it computes for <b>that</b> version — so a v1 caller who
    /// believes <c>watch</c> returns <c>Task&lt;_&gt;</c> would present exactly the token a v1
    /// unary <c>watch</c> has, and a host that now streams <c>watch</c> would accept it. With a
    /// separate direction the two preimages differ in their final NUL-separated field and the call
    /// is rejected by name.
    /// </para>
    /// <para>
    /// <b>Wire compatibility.</b> Purely additive: the preimage of every existing
    /// <c>request</c>/<c>reply</c>/<c>notify</c> token is byte-for-byte what spec 003 and Phases
    /// A-E computed, so no already-deployed pair changes its digests. A peer that predates
    /// streaming never computes these two digests at all, and the outcome it gets for a streaming
    /// operation — a token mismatch — is the correct one.
    /// </para>
    /// </remarks>
    [<Literal>]
    let StreamRequestDirection = "stream-request"

    /// <summary>
    /// The lowercase direction literal of one item of a server-streaming reply. Every item carries
    /// it, so a stream whose descriptor was misrouted is rejected on the first item rather than
    /// deserialized as the wrong type.
    /// </summary>
    [<Literal>]
    let StreamItemDirection = "stream-item"

    /// <summary>Compute the token for one grain type, version, operation, and direction.</summary>
    let compute (grainType: string) (version: int) (operationId: string) (direction: string) : byte[] =
        let text =
            String.Join(
                '\000',
                [| grainType
                   version.ToString(CultureInfo.InvariantCulture)
                   operationId
                   direction |]
            )

        SHA256.HashData(Encoding.UTF8.GetBytes text)

    /// <summary>The request-direction token.</summary>
    let request grainType version operationId =
        compute grainType version operationId RequestDirection

    /// <summary>The reply-direction token.</summary>
    let reply grainType version operationId =
        compute grainType version operationId ReplyDirection

    /// <summary>The notify-direction token of one observer type, version, and push operation.</summary>
    let notify observerType version operationId =
        compute observerType version operationId NotifyDirection

    /// <summary>The stream-request-direction token of one server-streaming operation.</summary>
    let streamRequest grainType version operationId =
        compute grainType version operationId StreamRequestDirection

    /// <summary>The stream-item-direction token of one server-streaming operation.</summary>
    let streamItem grainType version operationId =
        compute grainType version operationId StreamItemDirection

    /// <summary>Render a token as lowercase hexadecimal for diagnostics.</summary>
    let toHex (token: byte[]) =
        if isNull token then
            "<null>"
        else
            Convert.ToHexString(token).ToLowerInvariant()

    /// <summary>True when two tokens are byte-identical.</summary>
    let equal (left: byte[]) (right: byte[]) =
        not (isNull left)
        && not (isNull right)
        && left.Length = Length
        && right.Length = Length
        && MemoryExtensions.SequenceEqual(ReadOnlySpan<byte>(left), ReadOnlySpan<byte>(right))

/// <summary>The admission-flag byte of the fixed request envelope.</summary>
[<RequireQualifiedAccess>]
module internal AdmissionFlags =

    /// <summary>No policy flags.</summary>
    [<Literal>]
    let None = 0uy

    /// <summary>Bit 0 — read-only scheduling.</summary>
    [<Literal>]
    let ReadOnly = 0x01uy

    /// <summary>Bit 1 — one-way delivery.</summary>
    [<Literal>]
    let OneWay = 0x02uy

    /// <summary>Bit 2 — always-interleave admission.</summary>
    [<Literal>]
    let AlwaysInterleave = 0x04uy

    /// <summary>
    /// Bits 3-5 — the transaction field: <c>0</c> for a non-transactional operation, otherwise
    /// the <c>Orleans.TransactionOption</c> value plus one. Code <c>7</c> is unassigned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The option travels in the admission byte rather than beside it because dispatch already
    /// compares that byte against the hosted descriptor as a whole (spec 003's normative
    /// validation order). Carrying the transaction policy there makes a caller and a host that
    /// disagree about whether an operation is transactional — or about which option it uses — a
    /// rejected request rather than a silently non-transactional call.
    /// </para>
    /// <para>
    /// The six option values and their numeric encodings are identical on Orleans 10.1.0 and
    /// 10.2.2 (verified by reflection over both <c>Orleans.Transactions.dll</c> assemblies:
    /// <c>Suppress=0, CreateOrJoin=1, Create=2, Join=3, Supported=4, NotAllowed=5</c>), so
    /// <c>value + 1</c> is a stable encoding across the supported range.
    /// </para>
    /// </remarks>
    [<Literal>]
    let TransactionMask = 0x38uy

    /// <summary>The bit position of the low bit of <see cref="TransactionMask"/>.</summary>
    [<Literal>]
    let TransactionShift = 3

    /// <summary>The one code in <see cref="TransactionMask"/> that no transaction option uses.</summary>
    [<Literal>]
    let UnassignedTransactionCode = 7uy

    /// <summary>Bits 6-7 — reserved; a set reserved bit invalidates the request.</summary>
    [<Literal>]
    let Reserved = 0xC0uy

    /// <summary>Encode one transaction option into the transaction field.</summary>
    let encodeTransaction (option: Orleans.TransactionOption) =
        (byte (int option + 1)) <<< TransactionShift

    /// <summary>Compose the flag byte from the three policy decisions and the transaction option.</summary>
    let compose
        (isReadOnly: bool)
        (isOneWay: bool)
        (isAlwaysInterleave: bool)
        (transaction: Orleans.TransactionOption option)
        =
        (if isReadOnly then ReadOnly else None)
        ||| (if isOneWay then OneWay else None)
        ||| (if isAlwaysInterleave then AlwaysInterleave else None)
        ||| (match transaction with
             | Some option -> encodeTransaction option
             | Option.None -> None)

    /// <summary>True when the value sets at least one reserved bit.</summary>
    let hasReserved (flags: byte) = flags &&& Reserved <> None

    /// <summary>The raw transaction code carried in bits 3-5.</summary>
    let transactionCode (flags: byte) = (flags &&& TransactionMask) >>> TransactionShift

    /// <summary>True when the operation declares a transaction option.</summary>
    let isTransactional (flags: byte) = flags &&& TransactionMask <> None

    /// <summary>True unless bits 3-5 carry the one unassigned code.</summary>
    let hasValidTransactionField (flags: byte) =
        transactionCode flags <> UnassignedTransactionCode

    /// <summary>Decode the transaction field; <c>None</c> for a non-transactional operation.</summary>
    let tryTransactionOption (flags: byte) : Orleans.TransactionOption option =
        let code = transactionCode flags

        if code = 0uy || code = UnassignedTransactionCode then
            Option.None
        else
            Some(enum<Orleans.TransactionOption> (int code - 1))

/// <summary>The reserved Orleans identifiers of the functional transport family.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalIds =

    /// <summary>The functional interface ID prefix.</summary>
    [<Literal>]
    let Prefix = "orleans.fsharp.functional/"

    /// <summary>The fixed internal Orleans interface version of this transport family.</summary>
    [<Literal>]
    let InterfaceVersion = 1us

    /// <summary>The functional interface ID of one explicit grain type.</summary>
    let interfaceId (grainType: string) = Prefix + grainType

    /// <summary>The stable actor-specific <c>GrainInterfaceType</c> of one explicit grain type.</summary>
    let grainInterfaceType (grainType: string) =
        GrainInterfaceType.Create(interfaceId grainType)

/// <summary>The eight boundaries at which the payload limit is enforced.</summary>
type internal PayloadBoundary =
    /// The caller serialized an argument and is about to send it.
    | CallerRequestSend
    /// The silo received a request and is about to dispatch it.
    | SiloRequestReceive
    /// The silo serialized a reply and is about to send it.
    | SiloReplySend
    /// The caller received a reply and is about to deserialize it.
    | CallerReplyReceive
    /// The caller serialized a notification message and is about to push it.
    | CallerNotifySend
    /// The observer received a notification and is about to dispatch it.
    | ObserverReceive
    /// The silo serialized one item of a server-streaming reply and is about to yield it.
    | SiloStreamItemSend
    /// The caller received one item of a server-streaming reply and is about to deserialize it.
    | CallerStreamItemReceive

    /// <summary>The wire direction this boundary belongs to.</summary>
    member this.Direction =
        match this with
        | CallerRequestSend
        | SiloRequestReceive -> ProtocolToken.RequestDirection
        | SiloReplySend
        | CallerReplyReceive -> ProtocolToken.ReplyDirection
        | CallerNotifySend
        | ObserverReceive -> ProtocolToken.NotifyDirection
        | SiloStreamItemSend
        | CallerStreamItemReceive -> ProtocolToken.StreamItemDirection

    /// <summary>The stable diagnostic name of this boundary.</summary>
    member this.Name =
        match this with
        | CallerRequestSend -> "caller request send"
        | SiloRequestReceive -> "silo request receive"
        | SiloReplySend -> "silo reply send"
        | CallerReplyReceive -> "caller reply receive"
        | CallerNotifySend -> "caller notify send"
        | ObserverReceive -> "observer receive"
        | SiloStreamItemSend -> "silo stream item send"
        | CallerStreamItemReceive -> "caller stream item receive"

    /// <summary>
    /// The diagnostic label of the entity the boundary is scoped to: a grain type for the four
    /// request/reply boundaries, an observer type for the two notification boundaries.
    /// </summary>
    member this.OwnerLabel =
        match this with
        | CallerRequestSend
        | SiloRequestReceive
        | SiloReplySend
        | CallerReplyReceive -> "grain type"
        | CallerNotifySend
        | ObserverReceive -> "observer type"
        | SiloStreamItemSend
        | CallerStreamItemReceive -> "grain type"

/// <summary>
/// Payload-limit enforcement. Every endpoint enforces its own local configuration; Orleans'
/// general message-size limit can be stricter. Diagnostics carry the owner type (grain type or
/// observer type, per boundary), operation ID, direction, actual size, and local limit, and
/// never the payload contents.
/// </summary>
[<RequireQualifiedAccess>]
module internal PayloadLimit =

    /// <summary>Reject a non-positive configured limit.</summary>
    let validateLimit (maxPayloadBytes: int) =
        if maxPayloadBytes <= 0 then
            fail
                TransportStage
                $"FunctionalGrainTransportOptions.MaxPayloadBytes must be positive, but {maxPayloadBytes} is configured."

        maxPayloadBytes

    /// <summary>
    /// Enforce the local limit at one boundary. <paramref name="ownerType"/> is the grain type
    /// for the four request/reply boundaries and the observer type for the two notification
    /// boundaries.
    /// </summary>
    let ensure
        (boundary: PayloadBoundary)
        (ownerType: string)
        (operationId: string)
        (actualBytes: int)
        (maxPayloadBytes: int)
        =
        if actualBytes > maxPayloadBytes then
            fail
                TransportStage
                $"the {boundary.Direction} payload of operation '{operationId}' on {boundary.OwnerLabel} '{ownerType}' is {actualBytes} bytes, which exceeds the local limit of {maxPayloadBytes} bytes at the {boundary.Name} boundary."
