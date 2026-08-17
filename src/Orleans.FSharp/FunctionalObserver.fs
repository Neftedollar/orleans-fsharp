namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// An immutable push-operation descriptor sealed by observer-contract construction.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalPushOperation =
    {
        /// Zero-based API-record field index.
        Index: int
        /// The source record-field name, which is also the wire operation ID.
        OperationId: string
        /// The message type this push carries.
        MessageType: Type
        /// The precomputed notify-direction protocol token.
        NotifyToken: byte[]
        /// Deserialize one payload as the exact message type and invoke the handler field.
        Invoke: Func<IFunctionalPayloadCodec, obj, byte[], Task>
    }

/// <summary>
/// The typed push-delivery factory. Its generic method is closed once per push descriptor while
/// the observer contract is sealed, so delivering a notification never closes a generic.
/// </summary>
[<AbstractClass; Sealed>]
type internal PushAdapterFactory =

    /// <summary>Build the deserialize-and-invoke adapter of one message type.</summary>
    static member Create<'Msg>() : Func<IFunctionalPayloadCodec, obj, byte[], Task> =
        Func<IFunctionalPayloadCodec, obj, byte[], Task>(fun codec handler payload ->
            let message = codec.Deserialize<'Msg> payload
            (unbox<'Msg -> Task<unit>> handler) message :> Task)

/// <summary>Preclosing of the typed push-delivery factory.</summary>
[<RequireQualifiedAccess>]
module internal PushAdapter =

    let private createMethod =
        match
            typeof<PushAdapterFactory>
                .GetMethod(
                    "Create",
                    Reflection.BindingFlags.Static
                    ||| Reflection.BindingFlags.Public
                    ||| Reflection.BindingFlags.NonPublic
                )
        with
        | null -> fail ContractStage "the typed push-delivery factory 'PushAdapterFactory.Create' was not found."
        | method -> method

    /// <summary>Close the adapter over one message type, once, at sealing time.</summary>
    let precompute (messageType: Type) : Func<IFunctionalPayloadCodec, obj, byte[], Task> =
        FunctionalInstrumentation.countGenericClosing ()

        createMethod.MakeGenericMethod([| messageType |]).Invoke(null, [||])
        :?> Func<IFunctionalPayloadCodec, obj, byte[], Task>

/// <summary>
/// A sealed observer contract: the observer type, the contract version, the reflected handler
/// record shape, and one immutable push descriptor per field in declaration order.
/// </summary>
[<Sealed>]
type ObserverContract<'Brand, 'Api>
    internal (observerTypeName: string, version: int, shape: ApiShape, operations: FunctionalPushOperation[]) =

    /// <summary>The observer type: the observer-side analogue of the grain type.</summary>
    member _.ObserverTypeName = observerTypeName

    /// <summary>The application contract version.</summary>
    member _.Version = version

    /// <summary>The reflected handler-record shape.</summary>
    member internal _.Shape = shape

    /// <summary>The push descriptors, in handler-record declaration order.</summary>
    member internal _.Operations = operations

    /// <summary>The handler-record type this contract was reflected from.</summary>
    member internal _.ApiType = shape.ApiType

    /// <summary>Every message type this contract puts on the wire.</summary>
    member internal _.MessageTypes =
        operations |> Array.map (fun operation -> operation.MessageType)

    /// <summary>Resolve one selector to its push descriptor.</summary>
    member internal _.Resolve(entry: string, selector: OperationSelector<'Api, 'Msg, unit>) =
        operations.[(ApiShape.resolve shape entry selector).Index]

/// <summary>The mutable draft of an observer contract under construction.</summary>
[<Sealed>]
type ObserverContractDraft<'Brand, 'Api> internal (observerTypeName: string option, version: int option) =
    member internal _.ObserverTypeName = observerTypeName
    member internal _.Version = version

/// <summary>
/// The <c>observerContract</c> computation expression, mirroring <c>grainContract</c>: the
/// observer type, the contract version, and nothing else. A push operation's wire ID is always
/// its handler-record field name — sender and receiver derive it from the same record type, so
/// there is no override to keep in step between them.
/// </summary>
[<Sealed>]
type ObserverContractBuilder<'Brand, 'Api> internal () =

    /// <summary>Start an empty draft.</summary>
    member _.Yield(_: unit) : ObserverContractDraft<'Brand, 'Api> = ObserverContractDraft<'Brand, 'Api>(None, None)

    /// <summary>Validate and seal the draft into an immutable observer contract.</summary>
    member _.Run(draft: ObserverContractDraft<'Brand, 'Api>) : ObserverContract<'Brand, 'Api> =
        let observerTypeName =
            match draft.ObserverTypeName with
            | Some value -> value
            | None ->
                // The brand's simple CLR name, on exactly the terms grainContract derives from.
                let brand = typeof<'Brand>

                if brand.IsGenericType then
                    fail
                        ContractStage
                        $"the observer brand '{brand.FullName}' is a generic type, so its CLR name cannot supply a derived 'observerType'. Declare an explicit 'observerType' for this contract."

                if brand.IsNested then
                    fail
                        ContractStage
                        $"the observer brand '{brand.FullName}' is a nested type (declared inside another type or inside an F# 'module' rather than a 'namespace'), so its CLR name cannot supply a derived 'observerType'. Declare an explicit 'observerType' for this contract."

                brand.Name

        let version = draft.Version |> Option.defaultValue 1
        let shape = ApiShape.of'<'Api> ()

        let operations =
            shape.Operations
            |> Array.map (fun field ->
                // An observer never returns data. Reusing ApiShape means the shape rules are the
                // grain rules; this is the one rule that is observer-specific.
                if field.ReplyType <> typeof<unit> then
                    fail
                        ContractStage
                        $"the push operation '{field.FieldName}' of observer type '{observerTypeName}' returns Task<{field.ReplyType.FullName}>, but an observer never returns data. Every push operation must have the shape 'Msg -> Task<unit>."

                { Index = field.Index
                  OperationId = field.FieldName
                  MessageType = field.ArgumentType
                  NotifyToken = ProtocolToken.notify observerTypeName version field.FieldName
                  Invoke = PushAdapter.precompute field.ArgumentType })

        ObserverContract<'Brand, 'Api>(observerTypeName, version, shape, operations)

    /// <summary>Set the explicit observer type; defaults to the brand's simple CLR name.</summary>
    [<CustomOperation("observerType")>]
    member _.ObserverType(state: ObserverContractDraft<'Brand, 'Api>, value: string) =
        if isBlank value then
            fail ContractStage "'observerType' requires a non-blank value."

        if containsNul value then
            fail ContractStage $"'observerType' value '{value}' must not contain a NUL character."

        match state.ObserverTypeName with
        | Some existing ->
            fail ContractStage $"'observerType' is already set to '{existing}'; it is allowed at most once."
        | None -> ObserverContractDraft<'Brand, 'Api>(Some value, state.Version)

    /// <summary>Set the application contract version; defaults to <c>1</c>.</summary>
    [<CustomOperation("version")>]
    member _.Version(state: ObserverContractDraft<'Brand, 'Api>, value: int) =
        if value <= 0 then
            fail ContractStage $"'version' must be a positive integer, but {value} was supplied."

        match state.Version with
        | Some existing -> fail ContractStage $"'version' is already set to {existing}; it is allowed at most once."
        | None -> ObserverContractDraft<'Brand, 'Api>(state.ObserverTypeName, Some value)

/// <summary>
/// The object hosted by the subscribing process. It revalidates every notification the way the
/// grain target revalidates a request, then invokes the matching handler-record field.
/// </summary>
/// <remarks>
/// Delivery is best-effort and one-way-like: a handler that throws is reported to the local
/// logger sink and never propagated back to the notifying grain, which matches Orleans' own
/// observer semantics. A rejected ENVELOPE — wrong observer type, version, operation or token —
/// is a protocol fault rather than an application fault and does propagate, because it means the
/// two sides disagree about the contract.
/// </remarks>
[<Sealed>]
type internal FunctionalObserverObject<'Brand, 'Api>
    (contract: ObserverContract<'Brand, 'Api>, handlers: 'Api, codec: IFunctionalPayloadCodec, onError: exn -> unit) =

    let fields = contract.Shape.Operations |> Array.map (fun field -> field.Index)

    let handlerField (index: int) =
        // The handler record is the application's own value: read the field it declared.
        let properties =
            FSharp.Reflection.FSharpType.GetRecordFields(
                contract.ApiType,
                Reflection.BindingFlags.Public
            )

        properties.[index].GetValue(box handlers)

    let handlerValues = fields |> Array.map handlerField

    interface IFunctionalObserverTarget with
        member _.DispatchAsync(envelope: FunctionalNotificationEnvelope) : Task =
            if envelope.ObserverType <> contract.ObserverTypeName then
                fail
                    TransportStage
                    $"the observer hosts '{contract.ObserverTypeName}' but received a notification for '{envelope.ObserverType}'."

            if envelope.ContractVersion <> contract.Version then
                fail
                    TransportStage
                    $"the observer hosts '{contract.ObserverTypeName}' version {contract.Version} but received version {envelope.ContractVersion}."

            let operation =
                contract.Operations
                |> Array.tryFind (fun candidate -> candidate.OperationId = envelope.OperationId)

            match operation with
            | None ->
                fail
                    TransportStage
                    $"the observer '{contract.ObserverTypeName}' hosts no push operation '{envelope.OperationId}'."
            | Some push ->
                if not (ProtocolToken.equal envelope.ProtocolToken push.NotifyToken) then
                    fail
                        TransportStage
                        $"the notification for '{envelope.OperationId}' on observer '{contract.ObserverTypeName}' carried an unexpected protocol token."

                task {
                    try
                        do! push.Invoke.Invoke(codec, handlerValues.[push.Index], envelope.Payload)
                    with cause ->
                        // Best-effort: a failing observer must never fail the notifying handler.
                        onError cause
                }
                :> Task

/// <summary>
/// Creating, notifying, and releasing functional observers: push to a client-hosted object
/// without any application code generation.
/// </summary>
[<RequireQualifiedAccess>]
module FunctionalObserver =

    /// <summary>Resolve the payload codec of a subscribing process.</summary>
    let private codecOf (services: IServiceProvider) =
        match services.GetService typeof<IFunctionalPayloadCodec> with
        | :? IFunctionalPayloadCodec as codec -> codec
        | _ ->
            fail
                BindingStage
                $"creating a functional observer requires the functional transport. {FunctionalTransportSource.Guidance}"

    /// <summary>Declare every message type of a contract as a top-level payload type.</summary>
    let private preflight (services: IServiceProvider) (contract: ObserverContract<'Brand, 'Api>) =
        let provider = SerializerPreflight.providerOf services contract.ObserverTypeName

        for operation in contract.Operations do
            SerializerPreflight.checkType
                provider
                contract.ObserverTypeName
                "message"
                $"push operation '{operation.OperationId}'"
                operation.MessageType

    /// <summary>
    /// Wrap a handler record in a client-hosted observer object and return a typed, serializable
    /// handle to it. The handle is an ordinary operation argument.
    /// </summary>
    let createFrom
        (contract: ObserverContract<'Brand, 'Api>)
        (services: IServiceProvider)
        (handlers: 'Api)
        : FunctionalObserverHandle<'Brand, 'Api> =
        if obj.ReferenceEquals(handlers, null) then
            fail ContractStage $"the handler record of observer type '{contract.ObserverTypeName}' must not be null."

        let factory =
            match services.GetService typeof<IGrainFactory> with
            | :? IGrainFactory as factory -> factory
            | _ ->
                fail
                    BindingStage
                    $"creating a functional observer for '{contract.ObserverTypeName}' requires an Orleans grain factory in the supplied services."

        let codec = codecOf services
        preflight services contract

        let logger =
            match services.GetService typeof<Microsoft.Extensions.Logging.ILoggerFactory> with
            | :? Microsoft.Extensions.Logging.ILoggerFactory as factory ->
                Some(factory.CreateLogger $"Orleans.FSharp.FunctionalObserver.{contract.ObserverTypeName}")
            | _ -> None

        let onError (cause: exn) =
            match logger with
            | Some log ->
                Microsoft.Extensions.Logging.LoggerExtensions.LogError(
                    log,
                    cause,
                    "A functional observer handler of '{ObserverType}' threw; the notification is dropped.",
                    contract.ObserverTypeName
                )
            | None -> ()

        let target =
            FunctionalObserverObject<'Brand, 'Api>(contract, handlers, codec, onError) :> IFunctionalObserverTarget

        let reference =
            try
                factory.CreateObjectReference<IFunctionalObserverTarget> target
            with cause ->
                failCause
                    BindingStage
                    $"creating the Orleans object reference for observer type '{contract.ObserverTypeName}' failed."
                    cause

        FunctionalObserverHandle<'Brand, 'Api>(
            contract.ObserverTypeName,
            contract.Version,
            reference,
            codec,
            box target
        )

    /// <summary>Wrap a handler record and return a typed handle, from an Orleans cluster client.</summary>
    let create (contract: ObserverContract<'Brand, 'Api>) (client: IClusterClient) (handlers: 'Api) =
        if isNull (box client) then
            fail
                BindingStage
                $"creating a functional observer for '{contract.ObserverTypeName}' requires an Orleans cluster client."

        createFrom contract client.ServiceProvider handlers

    /// <summary>
    /// Push one message to the observed object.
    /// </summary>
    /// <remarks>
    /// The returned task completes when the notification has entered the local send path, not
    /// when the observed object has handled it: <c>IFunctionalObserverTarget.DispatchAsync</c> is
    /// one-way. Delivery is therefore best-effort, which is Orleans' own observer semantics — an
    /// observer that throws is logged on ITS side and never reported here, and an observer whose
    /// object reference has been released costs the notifying handler nothing.
    /// </remarks>
    let notify
        (handle: FunctionalObserverHandle<'Brand, 'Api>)
        (selector: OperationSelector<'Api, 'Msg, unit>)
        (message: 'Msg)
        : Task<unit> =
        if isNull (box handle) then
            fail TransportStage "notifying a functional observer requires a handle."

        let shape = ApiShape.of'<'Api> ()
        let field = ApiShape.resolve shape "notify" selector

        let envelope =
            FunctionalNotificationEnvelope(
                handle.ObserverType,
                handle.ContractVersion,
                field.FieldName,
                ProtocolToken.notify handle.ObserverType handle.ContractVersion field.FieldName,
                handle.Codec.Serialize<'Msg> message
            )

        task { do! handle.Target.DispatchAsync envelope }

    /// <summary>
    /// Release the object reference behind a handle; delivery through it stops.
    /// </summary>
    /// <remarks>
    /// Idempotent, and deliberately so: releasing a reference Orleans no longer knows about is
    /// the normal outcome of a second release, of a client that has already been torn down, and
    /// of an observed object that was collected before the application got round to unsubscribing.
    /// A cleanup call in a <c>finally</c> must not turn any of those into a failure — the
    /// post-condition ("nothing is delivered through this handle any more") already holds.
    /// </remarks>
    let unsubscribe (factory: IGrainFactory) (handle: FunctionalObserverHandle<'Brand, 'Api>) : unit =
        if isNull (box factory) then
            fail BindingStage "releasing a functional observer requires an Orleans grain factory."

        if not (isNull (box handle)) then
            try
                factory.DeleteObjectReference<IFunctionalObserverTarget> handle.Target
            with :? ArgumentException ->
                // "Reference is not associated with a local object" — already released or
                // collected. The reference is dead either way, which is what was asked for.
                ()

/// <summary>
/// A fan-out set of observer handles with time-based liveness, the functional runtime's
/// equivalent of <c>FSharpObserverManager</c>.
/// </summary>
/// <remarks>
/// <para>
/// The manager is a MUTABLE object. Holding one in a functional grain's state is the documented
/// exception to the immutable-state rule, and it carries the same caveat the specification states
/// for deep mutation: the runtime publishes state by returning it from a handler, so a mutation
/// performed in place is visible immediately and is NOT part of any state write. A manager must
/// therefore never be attached to a persistent state type — object references do not survive an
/// activation, let alone a storage round-trip.
/// </para>
/// <para>
/// Liveness is by expiry, not by liveness probing: a subscription that is not refreshed within
/// the configured window is dropped on the next notification or the next explicit sweep. A
/// handle whose observed object has gone away simply stops being refreshed.
/// </para>
/// </remarks>
[<Sealed>]
type FunctionalObserverManager<'Brand, 'Api>(expiry: TimeSpan) =

    let entries =
        ConcurrentDictionary<IFunctionalObserverTarget, FunctionalObserverHandle<'Brand, 'Api> * DateTimeOffset ref>()

    do
        if expiry <= TimeSpan.Zero then
            fail
                ContractStage
                $"a functional observer manager requires a positive expiry, but {expiry} was supplied."

    /// <summary>The liveness window a subscription must be refreshed within.</summary>
    member _.Expiry = expiry

    /// <summary>The number of subscriptions which have not yet expired.</summary>
    member this.Count =
        this.RemoveExpired()
        entries.Count

    /// <summary>Add a handle, or refresh the one already present. Idempotent per observed object.</summary>
    member _.Subscribe(handle: FunctionalObserverHandle<'Brand, 'Api>) =
        if isNull (box handle) then
            fail TransportStage "subscribing to a functional observer manager requires a handle."

        let now = DateTimeOffset.UtcNow

        entries.AddOrUpdate(
            handle.Target,
            (fun _ -> handle, ref now),
            (fun _ (existing, seen) ->
                seen.Value <- now
                existing, seen)
        )
        |> ignore

    /// <summary>Remove one handle. Returns true when it was present.</summary>
    member _.Unsubscribe(handle: FunctionalObserverHandle<'Brand, 'Api>) : bool =
        if isNull (box handle) then
            false
        else
            fst (entries.TryRemove handle.Target)

    /// <summary>Drop every subscription whose last refresh is older than the expiry.</summary>
    member _.RemoveExpired() =
        let deadline = DateTimeOffset.UtcNow - expiry

        for pair in entries do
            let _, seen = pair.Value

            if seen.Value < deadline then
                entries.TryRemove pair.Key |> ignore

    /// <summary>Forget every subscription.</summary>
    member _.Clear() = entries.Clear()

    /// <summary>
    /// Push one message to every live subscription. Expired subscriptions are dropped first;
    /// an observer that fails to accept the send is dropped and never reported to the caller.
    /// </summary>
    member this.Notify (selector: OperationSelector<'Api, 'Msg, unit>) (message: 'Msg) : Task<unit> =
        this.RemoveExpired()

        task {
            for pair in entries do
                let handle, _ = pair.Value

                try
                    do! FunctionalObserver.notify handle selector message
                with _ ->
                    // A send that the local path refuses means the object reference is gone.
                    entries.TryRemove pair.Key |> ignore
        }

[<AutoOpen>]
module ObserverContractBuilders =

    /// <summary>
    /// The <c>observerContract</c> computation expression: seal one observer type's push
    /// operations from a handler record whose every field is <c>'Msg -&gt; Task&lt;unit&gt;</c>.
    /// </summary>
    let observerContract<'Brand, 'Api> () = ObserverContractBuilder<'Brand, 'Api>()
