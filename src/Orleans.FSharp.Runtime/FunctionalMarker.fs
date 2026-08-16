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

    /// <summary>How often this target has actually been disposed; exactly one after teardown.</summary>
    member _.DisposalCount = Volatile.Read(&disposals)

    /// <summary>Activation-local cleanup, run exactly once by <c>IGrainActivator.DisposeInstance</c>.</summary>
    abstract OnDisposing: unit -> unit

    default _.OnDisposing() = ()

    interface IDisposable with
        member this.Dispose() =
            if Interlocked.Increment(&disposals) = 1 then
                this.OnDisposing()
