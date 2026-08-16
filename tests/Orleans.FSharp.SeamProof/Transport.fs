/// Phase 0 seam proof — minimal spike versions of the fixed transport family
/// described in spec 003 "Fixed transport and wire protocol".
///
/// Nothing here is production code: it exists only to prove that the Orleans
/// 10.1.0 / 10.2.2 extension seams accept these shapes.
namespace Orleans.FSharp.SeamProof

open System
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Orleans.CodeGeneration
open Orleans.Serialization.Invocation

// ── Actor brands ────────────────────────────────────────────────────────────
// Phantom types; only their CLR identity matters.

type ProbeActor = private ProbeActor of unit
type OtherActor = private OtherActor of unit
type PeerActor = private PeerActor of unit

// ── Protocol token ──────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module ProtocolToken =

    /// SHA-256 of `grainType NUL version NUL operationId NUL direction`,
    /// version rendered as invariant ASCII decimal, direction lowercase.
    let compute (grainType: string) (version: int) (operationId: string) (direction: string) : byte[] =
        let text =
            String.Join(
                '\000',
                [| grainType
                   version.ToString(Globalization.CultureInfo.InvariantCulture)
                   operationId
                   direction |]
            )

        SHA256.HashData(Encoding.UTF8.GetBytes text)

    let request grainType version operationId = compute grainType version operationId "request"
    let reply grainType version operationId = compute grainType version operationId "reply"

    let toHex (bytes: byte[]) =
        Convert.ToHexString(bytes).ToLowerInvariant()

// ── Admission flags ─────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module AdmissionFlags =
    [<Literal>]
    let None = 0uy

    [<Literal>]
    let ReadOnly = 0x01uy

    [<Literal>]
    let OneWay = 0x02uy

    [<Literal>]
    let AlwaysInterleave = 0x04uy

    [<Literal>]
    let Reserved = 0xF8uy

// ── Public request metadata (call-filter surface) ───────────────────────────

type IFunctionalRequestMetadata =
    abstract GrainType: string
    abstract ContractVersion: int
    abstract OperationId: string
    abstract IsReadOnly: bool
    abstract IsOneWay: bool
    abstract IsAlwaysInterleave: bool
    abstract PayloadLength: int

// ── Fixed envelope / reply ──────────────────────────────────────────────────

[<Sealed>]
type FunctionalRequestEnvelope
    (
        grainType: string,
        contractVersion: int,
        operationId: string,
        protocolToken: byte[],
        admissionFlags: byte,
        payload: byte[]
    ) =

    member _.GrainType = grainType
    member _.ContractVersion = contractVersion
    member _.OperationId = operationId
    member _.ProtocolToken = protocolToken
    member _.AdmissionFlags = admissionFlags
    member _.Payload = payload

    interface IFunctionalRequestMetadata with
        member _.GrainType = grainType
        member _.ContractVersion = contractVersion
        member _.OperationId = operationId
        member _.IsReadOnly = admissionFlags &&& AdmissionFlags.ReadOnly <> 0uy
        member _.IsOneWay = admissionFlags &&& AdmissionFlags.OneWay <> 0uy
        member _.IsAlwaysInterleave = admissionFlags &&& AdmissionFlags.AlwaysInterleave <> 0uy
        member _.PayloadLength = if isNull payload then 0 else payload.Length

[<Sealed>]
type FunctionalReply(protocolToken: byte[], payload: byte[]) =
    member _.ProtocolToken = protocolToken
    member _.Payload = payload

// ── Target interfaces ───────────────────────────────────────────────────────

/// Non-generic dispatch seam — the actual invocation target.
type IFunctionalDispatchTarget =
    abstract DispatchAsync: envelope: FunctionalRequestEnvelope * cancellationToken: CancellationToken -> ValueTask<FunctionalReply>

/// Closed, actor-specific Orleans target interface. Supplies the manifest
/// interface identity; the non-generic seam above performs the invocation.
type IFunctionalGrainTarget<'Actor> =
    inherit IGrain
    abstract DispatchAsync: envelope: FunctionalRequestEnvelope * cancellationToken: CancellationToken -> ValueTask<FunctionalReply>

// ── Marker grain ────────────────────────────────────────────────────────────

/// Concrete manifest grain type. Public parameterless constructor so Orleans can
/// build its default activator during component configuration. Receiving a call
/// on this instance is a configuration error — the custom activator must have
/// replaced it.
type FunctionalGrainMarker<'Actor>() =
    inherit Grain()

    interface IFunctionalGrainTarget<'Actor> with
        member _.DispatchAsync(_envelope, _cancellationToken) =
            failwith "FunctionalGrainMarker received a call: the functional IGrainActivator was not installed."

    interface IRemindable with
        member _.ReceiveReminder(_reminderName, _status) =
            failwith "FunctionalGrainMarker received a reminder: the functional IGrainActivator was not installed."

// ── Fixed request ───────────────────────────────────────────────────────────

/// One request class carries every operation. `InvokeInner` calls the
/// non-generic dispatch seam; method metadata is reconstructed by the lifecycle.
[<Sealed>]
type FunctionalRequest(envelope: FunctionalRequestEnvelope, callerToken: CancellationToken) =
    inherit Request<FunctionalReply>()

    let mutable envelope = envelope
    let mutable target: IFunctionalDispatchTarget = Unchecked.defaultof<_>
    let mutable token = callerToken
    let mutable targetCts: CancellationTokenSource = null
    let mutable interfaceType: Type = null
    let mutable methodInfo: MethodInfo = null

    /// Caller-side metadata capture: closed target interface + its method.
    member this.SetCallerMetadata(closedInterfaceType: Type) =
        interfaceType <- closedInterfaceType
        methodInfo <- closedInterfaceType.GetMethod("DispatchAsync")

    member _.Envelope = envelope
    member _.HasTarget = not (isNull (box target))

    member _.ApplyOptions() =
        let flags = envelope.AdmissionFlags
        let mutable options = InvokeMethodOptions.None

        if flags &&& AdmissionFlags.ReadOnly <> 0uy then
            options <- options ||| InvokeMethodOptions.ReadOnly

        if flags &&& AdmissionFlags.OneWay <> 0uy then
            options <- options ||| InvokeMethodOptions.OneWay

        if flags &&& AdmissionFlags.AlwaysInterleave <> 0uy then
            options <- options ||| InvokeMethodOptions.AlwaysInterleave

        options

    override this.InvokeInner() = target.DispatchAsync(envelope, token)

    override _.GetTarget() = box target

    override _.SetTarget(holder: ITargetHolder) =
        let resolved = holder.GetTarget() :?> IFunctionalDispatchTarget
        target <- resolved
        // Resolve the single closed functional target interface from the actual target.
        let closed =
            resolved
                .GetType()
                .GetInterfaces()
            |> Array.tryFind (fun i ->
                i.IsGenericType
                && i.GetGenericTypeDefinition() = typedefof<IFunctionalGrainTarget<_>>)

        match closed with
        | Some t ->
            interfaceType <- t
            methodInfo <- t.GetMethod("DispatchAsync")
        | None -> ()

        // Target-local cancellation state; argument 1 becomes the target-local token.
        targetCts <- new CancellationTokenSource()
        token <- targetCts.Token

    override _.GetArgumentCount() = 2

    override _.GetArgument(index) =
        match index with
        | 0 -> box envelope
        | 1 -> box token
        | _ -> raise (ArgumentOutOfRangeException(nameof index))

    override _.SetArgument(index, value) =
        match index with
        | 0 ->
            match value with
            | :? FunctionalRequestEnvelope as e -> envelope <- e
            | _ -> raise (ArgumentException("Argument 0 must be a FunctionalRequestEnvelope."))
        | 1 ->
            match value with
            | :? CancellationToken as t -> token <- t
            | _ -> raise (ArgumentException("Argument 1 must be a CancellationToken."))
        | _ -> raise (ArgumentOutOfRangeException(nameof index))

    override _.GetMethodName() = "DispatchAsync"

    override _.GetInterfaceName() =
        if isNull interfaceType then
            typedefof<IFunctionalGrainTarget<_>>.FullName
        else
            interfaceType.FullName

    override this.GetActivityName() =
        String.Concat(this.GetInterfaceName(), "/", "DispatchAsync")

    override _.GetInterfaceType() =
        if isNull interfaceType then
            typedefof<IFunctionalGrainTarget<_>>
        else
            interfaceType

    override _.GetMethod() = methodInfo

    override _.IsCancellable =
        envelope.AdmissionFlags &&& AdmissionFlags.OneWay = 0uy

    override _.GetCancellationToken() = token

    override _.TryCancel() =
        if isNull targetCts then
            false
        else
            targetCts.Cancel()
            true

    override _.Dispose() =
        if not (isNull targetCts) then
            targetCts.Dispose()
            targetCts <- null

        target <- Unchecked.defaultof<_>

// ── Custom grain reference ──────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module FunctionalIds =
    /// The functional interface ID prefix reserved by spec 003.
    [<Literal>]
    let Prefix = "orleans.fsharp.functional/"

    /// Fixed internal Orleans interface version for this transport family.
    [<Literal>]
    let InterfaceVersion = 1us

    let interfaceId (grainType: string) = Prefix + grainType

    let grainInterfaceType (grainType: string) =
        GrainInterfaceType.Create(interfaceId grainType)

    let grainId (grainType: string) (key: string) =
        GrainId.Create(GrainType.Create grainType, key)

[<Sealed>]
type FunctionalGrainReference(shared: GrainReferenceShared, key: IdSpan) =
    inherit GrainReference(shared, key)

    /// Acknowledged send.
    member this.SendAsync(envelope: FunctionalRequestEnvelope, cancellationToken: CancellationToken) =
        let request = new FunctionalRequest(envelope, cancellationToken)
        request.SetCallerMetadata(typedefof<IFunctionalGrainTarget<_>>)
        request.AddInvokeMethodOptions(request.ApplyOptions())
        this.InvokeAsync<FunctionalReply>(request)

    /// One-way send: acknowledges only the local send path.
    member this.SendOneWay(envelope: FunctionalRequestEnvelope) =
        let request = new FunctionalRequest(envelope, CancellationToken.None)
        request.SetCallerMetadata(typedefof<IFunctionalGrainTarget<_>>)
        request.AddInvokeMethodOptions(request.ApplyOptions())
        this.Invoke(request)

    /// Acknowledged send carrying caller-supplied closed interface metadata.
    member this.SendAsyncTyped
        (closedInterfaceType: Type, envelope: FunctionalRequestEnvelope, cancellationToken: CancellationToken)
        =
        let request = new FunctionalRequest(envelope, cancellationToken)
        request.SetCallerMetadata(closedInterfaceType)
        request.AddInvokeMethodOptions(request.ApplyOptions())
        this.InvokeAsync<FunctionalReply>(request)
