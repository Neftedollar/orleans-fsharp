namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Concurrency
open Orleans.Runtime
open Orleans.Serialization.Invocation
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The process-wide table behind the <c>mayInterleave</c> callback, keyed by the closed
/// interleaving marker type of each grain type that declares one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a table and not an ordinary field.</b> Orleans reaches a per-message interleave
/// predicate in exactly one way: <c>MayInterleaveConfiguratorProvider</c> reads the
/// <c>may-interleave-predicate</c> grain property, reflects a method of that name off the
/// <b>grain class</b>, and wraps it. The component it stores it in
/// (<c>GrainCanInterleave</c>) and the interface it stores it as (<c>IMayInterleavePredicate</c>)
/// are both internal to <c>Orleans.Runtime</c>, so nothing outside Orleans can install one
/// directly. A <b>static</b> callback is the only usable shape:
/// <c>MayInterleaveStaticPredicate</c> discards the grain instance, and the instanced form binds
/// <c>instance as TGrainClass</c> — which is always <c>null</c> here, because the functional
/// activation instance is <c>FunctionalGrainTarget&lt;'Actor&gt;</c> and the grain class is the
/// marker. A static callback has no <c>this</c> to carry the definition, so the definition is
/// looked up by the identity the callback does have: its own closed marker type.
/// </para>
/// <para>
/// Entries are written while the silo's service collection is being configured — before any
/// activation exists — and are never removed. The table is keyed by the CLOSED marker type, which
/// is derived from the actor brand alone, so it is process-wide rather than per-silo. Two silos in
/// one process registering the same definition therefore meet on the same key: re-registering the
/// SAME grain type name is an idempotent overwrite (an in-process silo restart re-seals the
/// definition and produces a fresh closure for the same grain type — latest wins is correct), while
/// a SECOND grain type name on one actor brand is rejected outright. Each silo has its own
/// <c>FunctionalGrainRegistry</c>, which rejects that collision within a silo but cannot see across
/// them; a silent overwrite here would leave one live grain type consulting another definition's
/// predicate.
/// </para>
/// </remarks>
[<RequireQualifiedAccess>]
module internal FunctionalInterleave =

    /// <summary>The public method name every interleaving marker exposes.</summary>
    [<Literal>]
    let CallbackName = "MayInterleave"

    /// <summary>One grain type's bound <c>mayInterleave</c> predicate, keyed elsewhere by its closed marker type.</summary>
    [<ReferenceEquality>]
    type internal Registration =
        { /// The grain type name the predicate is bound to.
          GrainTypeName: string
          /// The declared interleave predicate itself.
          Predicate: IFunctionalRequestMetadata -> bool }

    let private table = ConcurrentDictionary<Type, Registration>()

    /// <summary>
    /// Serializes the read-then-write below. Registration is a cold configuration-time path, so a
    /// lock costs nothing and makes "detect the conflict and claim the slot" a single atomic step;
    /// <see cref="M:Orleans.FSharp.FunctionalInterleave.tryFind"/> stays lock-free.
    /// </summary>
    let private gate = obj ()

    /// <summary>
    /// Bind one grain type's declared predicate to its closed marker type.
    /// </summary>
    /// <returns>
    /// <c>None</c> when the binding was installed (a first registration, or an idempotent
    /// overwrite by the same grain type name); <c>Some existingGrainTypeName</c> when the marker
    /// type is already bound to a DIFFERENT grain type, in which case nothing is written and the
    /// caller raises the configuration diagnostic — this module cannot, because the
    /// silo-registration stage vocabulary is declared further down the compile order.
    /// </returns>
    /// <param name="markerType">The closed marker CLR type to bind the predicate to.</param>
    /// <param name="grainTypeName">The grain type name the caller is registering.</param>
    /// <param name="predicate">The declared <c>mayInterleave</c> predicate.</param>
    let register (markerType: Type) (grainTypeName: string) (predicate: IFunctionalRequestMetadata -> bool) =
        lock gate (fun () ->
            match table.TryGetValue markerType with
            | true, existing when not (String.Equals(existing.GrainTypeName, grainTypeName, StringComparison.Ordinal)) ->
                Some existing.GrainTypeName
            | _ ->
                table.[markerType] <-
                    { GrainTypeName = grainTypeName
                      Predicate = predicate }

                None)

    /// <summary>The registration of one closed marker type, if it has one.</summary>
    /// <param name="markerType">The closed marker CLR type to look up.</param>
    let tryFind (markerType: Type) =
        match table.TryGetValue markerType with
        | true, registration -> Some registration
        | _ -> None

    /// <summary>
    /// Adapt Orleans' <c>IInvokable</c> callback to the declared metadata predicate. Every arm
    /// that cannot reach a declared predicate answers <c>false</c> — "do not interleave" is the
    /// spec-003 default and the only safe answer for a message this definition cannot identify.
    /// </summary>
    /// <remarks>
    /// Argument 0 of a functional request is the <c>FunctionalRequestEnvelope</c>, which is the
    /// <c>IFunctionalRequestMetadata</c> the predicate is declared over — so the predicate sees
    /// the protocol fields and NEVER the argument payload, which is not deserialized until
    /// dispatch admits the request. That is spec 003's protocol-before-payload invariant, held
    /// intact on a path Orleans runs before dispatch is even reached.
    /// </remarks>
    /// <param name="markerType">The closed marker CLR type whose registration to consult.</param>
    /// <param name="request">The Orleans invocation being considered for interleaving.</param>
    /// <exception cref="System.InvalidOperationException">The registered <c>mayInterleave</c> predicate threw while evaluating <paramref name="request"/>.</exception>
    let evaluate (markerType: Type) (request: IInvokable) : bool =
        // The argument count is checked BEFORE argument 0 is read, and it is not defensive
        // decoration: Orleans' code generator emits no GetArgument override at all for a method
        // with no parameters (InvokableGenerator.GenerateGetArgumentMethod returns null for
        // Parameters.Length = 0), so such an invokable inherits RequestBase.GetArgument, which
        // THROWS ArgumentOutOfRangeException("The request has zero arguments"). A functional
        // activation receives plenty of those -- a grain extension's parameterless method, for
        // instance IStreamConsumerExtension.GetAllSubscriptionHandles() on a definition that also
        // declares onStream -- and this callback is invoked for every message queued to a busy
        // activation, whatever its shape. Reading argument 0 unconditionally would turn each one
        // into a thrown predicate and therefore into a transient rejection of the incoming call.
        if isNull (box request) || request.GetArgumentCount() = 0 then
            false
        else
            match tryFind markerType with
            | None -> false
            | Some registration ->
                match request.GetArgument 0 with
                | :? IFunctionalRequestMetadata as metadata when
                    String.Equals(metadata.GrainType, registration.GrainTypeName, StringComparison.Ordinal)
                    ->
                    try
                        registration.Predicate metadata
                    with cause ->
                        // Orleans logs and rethrows a failing predicate, and the message is then
                        // rejected to its caller as transient (ActivationData.MayInvokeRequest ->
                        // the message loop's catch -> RejectMessage). That behaviour is kept --
                        // swallowing an application fault here would hide it and silently change
                        // the activation's concurrency -- but the exception is wrapped so the
                        // rejection names the grain type, the operation, and the stage.
                        failCause
                            TransportStage
                            $"the 'mayInterleave' predicate of grain type '{registration.GrainTypeName}' failed while deciding whether operation '{metadata.OperationId}' may interleave."
                            cause
                | _ -> false

/// <summary>
/// The concrete manifest grain type of one actor brand.
/// </summary>
/// <remarks>
/// <para>
/// This type exists so Orleans can build its default activator while configuring the grain
/// type's components; the functional <c>IGrainActivator</c> replaces the instance before any
/// call is delivered, so a call arriving on a marker instance means the functional activator
/// was not installed on this silo.
/// </para>
/// <para>
/// It is infrastructure, not application surface: applications never name it. It is public only
/// because Orleans constructs it through <c>ActivatorUtilities</c> while configuring grain-type
/// components, and it needs no Orleans code generation — its manifest entry comes from the
/// functional <c>GrainTypeOptions</c> post-configure rather than from assembly discovery.
/// </para>
/// </remarks>
/// <typeparam name="TActor">The application's actor brand.</typeparam>
type FunctionalGrainMarker<'Actor>() =
    inherit Grain()

    interface IFunctionalGrainTarget<'Actor> with
        /// <inheritdoc/>
        /// <exception cref="System.InvalidOperationException">Always thrown: a call reaching the manifest marker means the functional grain activator was not installed on this silo.</exception>
        member _.DispatchAsync(_envelope: FunctionalRequestEnvelope, _cancellationToken: CancellationToken) =
            raise (
                FunctionalTransportDiagnostics.Fail
                    $"the manifest marker for actor brand '{typeof<'Actor>.FullName}' received a call, which means the functional grain activator was not installed on this silo."
            )

    interface IRemindable with
        /// <inheritdoc/>
        /// <exception cref="System.InvalidOperationException">Always thrown: a reminder reaching the manifest marker means the functional grain activator was not installed on this silo.</exception>
        member _.ReceiveReminder(reminderName: string, _status: TickStatus) =
            raise (
                FunctionalTransportDiagnostics.Fail
                    $"the manifest marker for actor brand '{typeof<'Actor>.FullName}' received reminder '{reminderName}', which means the functional grain activator was not installed on this silo."
            )

/// <summary>
/// The manifest grain type of an actor brand whose contract declares <c>mayInterleave</c>. It
/// adds exactly what Orleans needs to find a per-message interleave predicate on a grain class:
/// the <c>[MayInterleave]</c> attribute and the public static callback it names.
/// </summary>
/// <remarks>
/// <para>
/// It is a separate type, used only for those grain types, for the same reason
/// <c>FunctionalStreamingGrainTarget</c> is: the attribute is not inert.
/// <c>AttributeGrainPropertiesProvider</c> writes the <c>may-interleave-predicate</c> property
/// for every grain class carrying it, and <c>MayInterleaveConfiguratorProvider</c> then installs
/// a predicate for every grain type whose properties contain that key — so putting the attribute
/// on the shared marker would give every functional grain type in the cluster an interleave
/// predicate it never asked for.
/// </para>
/// <para>
/// This is also why <c>reentrant</c> does NOT get a marker of its own: <c>[Reentrant]</c>
/// contributes a grain property and nothing else, so publishing that attribute's own
/// <c>Populate</c> output from the registry's properties provider is complete fidelity.
/// <c>[MayInterleave]</c> additionally names a method Orleans reflects off the grain class, which
/// a published property alone cannot supply.
/// </para>
/// </remarks>
/// <typeparam name="TActor">The application's actor brand.</typeparam>
[<Sealed>]
[<MayInterleave(FunctionalInterleave.CallbackName)>]
type FunctionalInterleavingGrainMarker<'Actor>() =
    inherit FunctionalGrainMarker<'Actor>()

    /// <summary>
    /// The callback <c>[MayInterleave]</c> names. Orleans reflects it off this closed marker type
    /// and calls it for the incoming request and, separately, for the request currently
    /// executing; it must return quickly and must not block.
    /// </summary>
    /// <param name="request">
    /// The Orleans invocation being considered. For a functional call this is the fixed
    /// <c>FunctionalRequest</c>, whose argument 0 is the request envelope.
    /// </param>
    static member MayInterleave(request: IInvokable) : bool =
        FunctionalInterleave.evaluate typeof<FunctionalInterleavingGrainMarker<'Actor>> request

/// <summary>
/// The base every functional activation target derives from. It hands the Orleans-supplied
/// activation context and runtime to <see cref="T:Orleans.Grain"/>, exposes narrow internal
/// wrappers for the protected deactivation members, and disposes exactly once.
/// </summary>
[<AbstractClass>]
type internal FunctionalGrainTargetBase(grainContext: IGrainContext, grainRuntime: IGrainRuntime) =
    inherit Grain(grainContext, grainRuntime)

    let mutable disposals = 0

    /// <summary>
    /// The <c>IGrainTimer</c> handles of every declared timer created for this activation, so
    /// they can be disposed exactly once, deterministically, as activation-local cleanup.
    /// </summary>
    let timers = ResizeArray<IGrainTimer>()
    let timersGate = obj ()

    /// <summary>Narrow wrapper for the protected <c>Grain.DeactivateOnIdle</c>.</summary>
    member this.DeactivateNow() = this.DeactivateOnIdle()

    /// <summary>Narrow wrapper for the protected <c>Grain.DelayDeactivation</c>.</summary>
    /// <param name="timeSpan">How long to delay deactivation by.</param>
    member this.DelayDeactivationFor(timeSpan: TimeSpan) = this.DelayDeactivation timeSpan

    /// <summary>
    /// Register a durable reminder through the stock Orleans reminder extension. This activation
    /// must implement <c>IRemindable</c>, which every functional target does.
    /// </summary>
    /// <param name="reminderName">The name of the reminder to register or update.</param>
    /// <param name="dueTime">The time to wait before the first tick.</param>
    /// <param name="period">The period between subsequent ticks.</param>
    member this.RegisterReminderNow(reminderName: string, dueTime: TimeSpan, period: TimeSpan) =
        GrainReminderExtensions.RegisterOrUpdateReminder(this, reminderName, dueTime, period)

    /// <summary>
    /// Create one declared timer through the stock Orleans timer extension and track its handle
    /// for guaranteed disposal, regardless of whatever else activation-local cleanup does.
    /// </summary>
    /// <param name="callback">The timer callback.</param>
    /// <param name="options">The Orleans timer creation options (due time, period).</param>
    member this.CreateTrackedTimer
        (callback: CancellationToken -> Task, options: GrainTimerCreationOptions)
        : IGrainTimer =
        let handle = GrainBaseExtensions.RegisterGrainTimer(this, Func<CancellationToken, Task> callback, options)
        lock timersGate (fun () -> timers.Add handle)
        handle

    /// <summary>
    /// Dispose every timer created for this activation. Every handle is attempted even when an
    /// earlier one throws while disposing, and the caller decides how failures are reported. Once
    /// every handle has been attempted, <see cref="P:Orleans.FSharp.FunctionalGrainTargetBase.OnTimersDisposed"/> fires exactly once, which is
    /// what makes "timer disposal" a distinct, independently observable stage of deactivation
    /// ordering rather than an implementation detail invisible outside this type.
    /// </summary>
    /// <param name="onError">Called for each timer whose <c>Dispose</c> throws, with the caught exception.</param>
    member private this.DisposeTimers(onError: exn -> unit) =
        let snapshot = lock timersGate (fun () -> timers.ToArray())

        for timer in snapshot do
            try
                timer.Dispose()
            with error ->
                onError error

        this.OnTimersDisposed snapshot.Length

    /// <summary>
    /// Observes a timer-disposal failure. Set once by the activator right after construction, so
    /// a disposal failure reaches the same scoped logger every other functional diagnostic uses;
    /// defaults to a silent no-op so this base type has no hard logging dependency of its own.
    /// </summary>
    member val internal OnTimerDisposalError: exn -> unit = ignore with get, set

    /// <summary>
    /// Fires once, after every timer of this activation has been attempted for disposal (whether
    /// or not any individual disposal failed). This is the deactivation-ordering "timer disposal"
    /// stage: it runs inside <c>Dispose</c>'s <c>finally</c>, strictly after the functional
    /// <c>onDeactivate</c> hook and the remaining Orleans stop stages have already completed, and
    /// strictly before <c>IGrainActivator.DisposeInstance</c> observes <c>Dispose</c> returning.
    /// It receives how many <c>IGrainTimer</c> handles this activation actually owned, so an
    /// observer can tell "disposed the two declared timers" from "there was nothing to dispose"
    /// — the stage fires for every functional activation, including those with no declared
    /// timers at all.
    /// </summary>
    member val internal OnTimersDisposed: int -> unit = ignore with get, set

    /// <summary>
    /// How often this target has actually been disposed: <c>0</c> before teardown and exactly
    /// <c>1</c> afterwards, however many times <c>Dispose</c> is called.
    /// </summary>
    member _.DisposalCount = Volatile.Read(&disposals)

    /// <summary>Activation-local cleanup, run exactly once by <c>IGrainActivator.DisposeInstance</c>.</summary>
    abstract OnDisposing: unit -> unit

    default _.OnDisposing() = ()

    /// <summary>
    /// The functional half of activation, run by Orleans after the stock
    /// <c>GrainLifecycleStage.SetupState</c> load and before activation completes.
    /// </summary>
    /// <param name="_cancellationToken">Cancellation token for the activation.</param>
    abstract OnActivating: CancellationToken -> Task

    default _.OnActivating(_cancellationToken: CancellationToken) = Task.CompletedTask

    /// <summary>The functional half of deactivation, run before the lifecycle stop stages.</summary>
    /// <param name="_reason">Why the grain is being deactivated.</param>
    /// <param name="_cancellationToken">Cancellation token for the deactivation.</param>
    abstract OnDeactivating: DeactivationReason * CancellationToken -> Task

    default _.OnDeactivating(_reason: DeactivationReason, _cancellationToken: CancellationToken) = Task.CompletedTask

    /// <summary>Runs the stock Orleans activation, then the functional <c>OnActivating</c> hook.</summary>
    /// <param name="cancellationToken">Cancellation token for the activation.</param>
    /// <remarks>
    /// <para>
    /// The stock <c>Grain</c> implementation is awaited as well rather than replaced, so a
    /// future Orleans version which gives it a body keeps working. It is called first on the way
    /// in and last on the way out, which keeps the functional deactivation hook ahead of
    /// everything Orleans does when stopping.
    /// </para>
    /// <para>
    /// A failing functional deactivation hook propagates immediately, so the stock deactivation
    /// — a no-op on Orleans 10.1.0 and 10.2.2 — is skipped. That is deliberate: the hook failure
    /// must reach the Orleans stop lifecycle unaltered, and swallowing it to run a no-op would
    /// be the wrong trade.
    /// </para>
    /// </remarks>
    override this.OnActivateAsync(cancellationToken: CancellationToken) =
        let stock = base.OnActivateAsync cancellationToken

        if stock.IsCompletedSuccessfully then
            this.OnActivating cancellationToken
        else
            task {
                do! stock
                do! this.OnActivating cancellationToken
            }
            :> Task

    /// <summary>Runs the functional <c>OnDeactivating</c> hook, then the stock Orleans deactivation.</summary>
    /// <param name="reason">Why the grain is being deactivated.</param>
    /// <param name="cancellationToken">Cancellation token for the deactivation.</param>
    override this.OnDeactivateAsync(reason: DeactivationReason, cancellationToken: CancellationToken) =
        let functional = this.OnDeactivating(reason, cancellationToken)

        if functional.IsCompletedSuccessfully then
            base.OnDeactivateAsync(reason, cancellationToken)
        else
            let stock () = this.StockDeactivateAsync(reason, cancellationToken)

            task {
                do! functional
                do! stock ()
            }
            :> Task

    /// <summary>The stock <c>Grain</c> deactivation, reachable from inside a closure.</summary>
    /// <param name="reason">Why the grain is being deactivated.</param>
    /// <param name="cancellationToken">Cancellation token for the deactivation.</param>
    member private this.StockDeactivateAsync(reason: DeactivationReason, cancellationToken: CancellationToken) : Task =
        base.OnDeactivateAsync(reason, cancellationToken)

    interface IDisposable with
        /// <inheritdoc/>
        member this.Dispose() =
            if Interlocked.Exchange(&disposals, 1) = 0 then
                // Deactivation ordering: the functional onDeactivate hook and the remaining
                // Orleans stop stages (lifecycle OnStop) have already run by the time Orleans
                // calls IGrainActivator.DisposeInstance, which reaches here. Timer disposal is
                // activation-local cleanup and must happen even when OnDisposing itself throws,
                // so it runs in `finally` rather than merely after OnDisposing returns.
                try
                    this.OnDisposing()
                finally
                    this.DisposeTimers(this.OnTimerDisposalError)
