namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Runtime

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
[<Sealed>]
type FunctionalGrainMarker<'Actor>() =
    inherit Grain()

    interface IFunctionalGrainTarget<'Actor> with
        member _.DispatchAsync(_envelope: FunctionalRequestEnvelope, _cancellationToken: CancellationToken) =
            raise (
                FunctionalTransportDiagnostics.Fail
                    $"the manifest marker for actor brand '{typeof<'Actor>.FullName}' received a call, which means the functional grain activator was not installed on this silo."
            )

    interface IRemindable with
        member _.ReceiveReminder(reminderName: string, _status: TickStatus) =
            raise (
                FunctionalTransportDiagnostics.Fail
                    $"the manifest marker for actor brand '{typeof<'Actor>.FullName}' received reminder '{reminderName}', which means the functional grain activator was not installed on this silo."
            )

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
    member this.DelayDeactivationFor(timeSpan: TimeSpan) = this.DelayDeactivation timeSpan

    /// <summary>
    /// Register a durable reminder through the stock Orleans reminder extension. This activation
    /// must implement <c>IRemindable</c>, which every functional target does.
    /// </summary>
    member this.RegisterReminderNow(reminderName: string, dueTime: TimeSpan, period: TimeSpan) =
        GrainReminderExtensions.RegisterOrUpdateReminder(this, reminderName, dueTime, period)

    /// <summary>
    /// Create one declared timer through the stock Orleans timer extension and track its handle
    /// for guaranteed disposal, regardless of whatever else activation-local cleanup does.
    /// </summary>
    member this.CreateTrackedTimer
        (callback: CancellationToken -> Task, options: GrainTimerCreationOptions)
        : IGrainTimer =
        let handle = GrainBaseExtensions.RegisterGrainTimer(this, Func<CancellationToken, Task> callback, options)
        lock timersGate (fun () -> timers.Add handle)
        handle

    /// <summary>
    /// Dispose every timer created for this activation. Every handle is attempted even when an
    /// earlier one throws while disposing, and the caller decides how failures are reported.
    /// </summary>
    member private _.DisposeTimers(onError: exn -> unit) =
        let snapshot = lock timersGate (fun () -> timers.ToArray())

        for timer in snapshot do
            try
                timer.Dispose()
            with error ->
                onError error

    /// <summary>
    /// Observes a timer-disposal failure. Set once by the activator right after construction, so
    /// a disposal failure reaches the same scoped logger every other functional diagnostic uses;
    /// defaults to a silent no-op so this base type has no hard logging dependency of its own.
    /// </summary>
    member val internal OnTimerDisposalError: exn -> unit = ignore with get, set

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
    abstract OnActivating: CancellationToken -> Task

    default _.OnActivating(_cancellationToken: CancellationToken) = Task.CompletedTask

    /// <summary>The functional half of deactivation, run before the lifecycle stop stages.</summary>
    abstract OnDeactivating: DeactivationReason * CancellationToken -> Task

    default _.OnDeactivating(_reason: DeactivationReason, _cancellationToken: CancellationToken) = Task.CompletedTask

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
    member private this.StockDeactivateAsync(reason: DeactivationReason, cancellationToken: CancellationToken) : Task =
        base.OnDeactivateAsync(reason, cancellationToken)

    interface IDisposable with
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
