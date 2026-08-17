namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// Activation-supplied services behind one invocation context. Phase 4 fills this record for
/// every request, hook, timer, and reminder callback.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalContextCore =
    {
        /// The Orleans identity of the activation.
        GrainId: GrainId
        /// The activation's grain factory.
        GrainFactory: IGrainFactory
        /// The activation's service provider.
        Services: IServiceProvider
        /// A scoped logger for the activation.
        Logger: ILogger
        /// The registered time provider.
        TimeProvider: TimeProvider
        /// The single <c>utcNow</c> value for this context, read once from
        /// <see cref="TimeProvider"/> at context creation. Every access through the public
        /// <c>utcNow</c> member returns this same frozen value, so two reads inside one callback
        /// can never observe different instants.
        UtcNow: DateTimeOffset
        /// The token selected by callback kind.
        CancellationToken: CancellationToken
        /// Wrapper for the protected Orleans deactivate-on-idle method.
        DeactivateOnIdle: unit -> unit
        /// Wrapper for the protected Orleans delay-deactivation method.
        DelayDeactivation: TimeSpan -> unit
        /// Typed lookup of an attached persistent state facet, boxed as <c>IPersistentState&lt;_&gt;</c>.
        ResolvePersistentState: PersistentStateDescriptor -> obj
    }

/// <summary>
/// The immutable per-invocation context supplied to every functional handler, lifecycle hook,
/// timer, and reminder callback.
/// </summary>
[<Sealed>]
type FunctionalGrainContext<'Actor, 'Key> internal (key: 'Key, core: FunctionalContextCore) =

    /// <summary>The domain key decoded once from the supplied grain identity.</summary>
    member _.key = key

    /// <summary>The Orleans identity of this activation.</summary>
    member _.grainId = core.GrainId

    /// <summary>The grain factory used to bind further references.</summary>
    member _.grainFactory = core.GrainFactory

    /// <summary>The activation service provider.</summary>
    member _.services = core.Services

    /// <summary>A logger scoped to this activation.</summary>
    member _.logger = core.Logger

    /// <summary>The registered time provider.</summary>
    member _.timeProvider = core.TimeProvider

    /// <summary>
    /// The instant this context was created, read once from
    /// <see cref="P:Orleans.FSharp.FunctionalGrainContext`2.timeProvider"/>. Stable for the whole
    /// callback: two reads of <c>utcNow</c> in the same handler, hook, timer, or reminder
    /// callback always agree.
    /// </summary>
    member _.utcNow = core.UtcNow

    /// <summary>The cancellation token selected by this callback kind.</summary>
    member _.cancellationToken = core.CancellationToken

    /// <summary>Request deactivation once the current turn completes.</summary>
    member _.deactivateOnIdle() = core.DeactivateOnIdle()

    /// <summary>Extend the activation's idle lifetime.</summary>
    member _.delayDeactivation(timeSpan: TimeSpan) = core.DelayDeactivation timeSpan

    /// <summary>Look up an attached persistent state facet by its logical descriptor.</summary>
    member _.persistentState<'State>(state: PersistentStateRef<'State>) : IPersistentState<'State> =
        if obj.ReferenceEquals(state, null) then
            fail DefinitionStage "persistentState requires a PersistentStateRef value."

        match core.ResolvePersistentState state.Descriptor with
        | :? IPersistentState<'State> as facet -> facet
        | _ ->
            let descriptor = state.Descriptor

            fail
                DefinitionStage
                $"no persistent state named '{descriptor.StateName}' with provider '{descriptor.ProviderName}' and stored type '{descriptor.StoredType.FullName}' is attached to this definition."

    /// <summary>Read a typed value from the Orleans request context.</summary>
    member _.tryGetRequestContext<'Value>(name: string) : 'Value option =
        match RequestContext.Get name with
        | null -> None
        | :? 'Value as value -> Some value
        | _ -> None

    /// <summary>Write a value into the Orleans request context.</summary>
    member _.setRequestContext<'Value> (name: string) (value: 'Value) : unit = RequestContext.Set(name, box value)

    /// <summary>Remove a value from the Orleans request context.</summary>
    member _.removeRequestContext(name: string) : unit = RequestContext.Remove name |> ignore

/// <summary>
/// A handler for one API operation. It receives the invocation context, the current primary
/// state, and the exact argument, and returns the replacement state with the exact reply.
/// </summary>
type Handler<'Actor, 'Key, 'State, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> 'Argument -> Task<'State * 'Reply>

/// <summary>An activation hook; its returned state is published in memory only.</summary>
type ActivateHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<'State>

/// <summary>A deactivation hook; it performs cleanup and returns no replacement state.</summary>
type DeactivateHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> -> DeactivationReason -> 'State -> Task<unit>

/// <summary>A reminder hook; whole-state replacement under ordinary Orleans scheduling.</summary>
type ReminderHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> -> 'State -> TickStatus -> Task<'State>

/// <summary>A timer hook; whole-state replacement under non-interleaving scheduling.</summary>
type TimerHook<'Actor, 'Key, 'State> = FunctionalGrainContext<'Actor, 'Key> -> 'State -> Task<'State>
