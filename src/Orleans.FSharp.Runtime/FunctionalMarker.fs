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

    /// <summary>Narrow wrapper for the protected <c>Grain.DeactivateOnIdle</c>.</summary>
    member this.DeactivateNow() = this.DeactivateOnIdle()

    /// <summary>Narrow wrapper for the protected <c>Grain.DelayDeactivation</c>.</summary>
    member this.DelayDeactivationFor(timeSpan: TimeSpan) = this.DelayDeactivation timeSpan

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
                this.OnDisposing()
