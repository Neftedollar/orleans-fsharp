namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.BroadcastChannel
open Orleans.Metadata
open Orleans.Runtime
open Orleans.Streams.Core
open Orleans.Transactions.Abstractions
open Orleans.FSharp.FunctionalDiagnostics
open Orleans.FSharp.FunctionalSiloDiagnostics

/// <summary>
/// The functional activation target of one actor brand: the Orleans grain instance every
/// functional call, hook, timer, and reminder runs on.
/// </summary>
/// <remarks>
/// It is a class rather than an object expression because the streaming variant below has to add
/// two interfaces to exactly this member set, and an object expression cannot be extended.
/// </remarks>
/// <typeparam name="TActor">The actor brand of the hosted definition.</typeparam>
type internal FunctionalGrainTarget<'Actor>
    (env: FunctionalTargetEnvironment, grainContext: IGrainContext, grainRuntime: IGrainRuntime) =
    inherit FunctionalGrainTargetBase(grainContext, grainRuntime)

    override _.OnDisposing() = ()

    override _.OnActivating(cancellationToken) =
        FunctionalLifecycle.activate env cancellationToken

    override _.OnDeactivating(reason, cancellationToken) =
        FunctionalLifecycle.deactivate env reason cancellationToken

    interface IFunctionalDispatchTarget with
        member _.DispatchAsync(envelope, cancellationToken) =
            FunctionalDispatch.dispatch env envelope cancellationToken

        // Spec 004 item 6. The enumerator is lazy: nothing is validated, no context is built and
        // no handler runs until Orleans pulls the first item. See FunctionalStreamEnumerator.
        member _.DispatchStream(envelope, cancellationToken) =
            new FunctionalStreamEnumerator(env, envelope, cancellationToken) :> IAsyncEnumerator<FunctionalReply>

    interface IFunctionalGrainTarget<'Actor> with
        member _.DispatchAsync(envelope, cancellationToken) =
            FunctionalDispatch.dispatch env envelope cancellationToken

    interface IRemindable with
        member _.ReceiveReminder(reminderName: string, status: TickStatus) : Task =
            match env.Definition.TryFindReminder reminderName with
            | Some hostedReminder ->
                task {
                    let scope =
                        FunctionalStateScope(env.Definition.GrainTypeName, $"onReminder:{reminderName}", true)

                    try
                        // "Reminder hook: CancellationToken.None, because
                        // IRemindable.ReceiveReminder supplies no token."
                        let core =
                            FunctionalContextFactory.core env CancellationToken.None scope

                        let! next = hostedReminder.Adapter.Invoke(env.Key, core, env.State.Current, status)

                        env.State.Publish next
                    finally
                        scope.Expire()
                }
                :> Task
            | None ->
                env.Logger.LogError(
                    "Grain {GrainId} of functional grain type {GrainType} received unknown reminder {ReminderName}",
                    grainContext.GrainId,
                    env.Definition.GrainTypeName,
                    reminderName
                )

                Task.FromException(
                    InvalidOperationException(
                        $"{StartupStage}: grain '{grainContext.GrainId}' of grain type '{env.Definition.GrainTypeName}' received reminder '{reminderName}', which the hosted definition does not declare."
                    )
                )

/// <summary>
/// The activation target of a definition which declares <c>onStream</c> or <c>onBroadcast</c>
/// hooks. It adds exactly the two interfaces Orleans probes the grain instance for when it
/// installs a stream or broadcast consumer extension.
/// </summary>
/// <remarks>
/// The two interfaces are on a separate type, used only when the definition declares at least one
/// implicit subscription, deliberately. <c>StreamConsumerGrainContextAction</c> eagerly binds a
/// <c>StreamConsumerExtension</c> to every activation whose instance implements
/// <c>IStreamSubscriptionObserver</c>, and <c>SiloStreamProviderRuntime.BindExtension</c> throws
/// for a stateless worker — so implementing the interface unconditionally would fail the
/// activation of every stateless-worker functional grain on a silo with streaming configured.
/// </remarks>
/// <typeparam name="TActor">The actor brand of the hosted definition.</typeparam>
[<Sealed>]
type internal FunctionalStreamingGrainTarget<'Actor>
    (env: FunctionalTargetEnvironment, grainContext: IGrainContext, grainRuntime: IGrainRuntime) =
    inherit FunctionalGrainTarget<'Actor>(env, grainContext, grainRuntime)

    interface IStreamSubscriptionObserver with
        member _.OnSubscribed(handleFactory: IStreamSubscriptionHandleFactory) =
            FunctionalStreams.onStreamSubscribed env handleFactory

    interface IOnBroadcastChannelSubscribed with
        member _.OnSubscribed(subscription: IBroadcastChannelSubscription) =
            FunctionalStreams.onChannelSubscribed env subscription

/// <summary>
/// The functional grain activator of one actor brand. It resolves <c>IGrainRuntime</c> from the
/// activation services, builds the target as an F# object expression over
/// <see cref="T:Orleans.FSharp.FunctionalGrainTargetBase"/> implementing the exact closed
/// <c>IFunctionalGrainTarget&lt;'Actor&gt;</c>, the non-generic dispatch seam, and
/// <c>IRemindable</c>, and verifies that the target really carries the supplied activation
/// context before returning it.
/// </summary>
/// <typeparam name="TActor">The actor brand of the hosted definition.</typeparam>
[<Sealed>]
type internal FunctionalGrainActivator<'Actor>(definition: FunctionalHostedDefinition) =

    interface IGrainActivator with
        member _.CreateInstance(grainContext: IGrainContext) : obj =
            let services = grainContext.ActivationServices
            let grainRuntime = services.GetRequiredService<IGrainRuntime>()
            let grainFactory = services.GetRequiredService<IGrainFactory>()
            let codec = services.GetRequiredService<FunctionalPayloadCodec>()

            let timeProvider =
                match services.GetService typeof<TimeProvider> with
                | :? TimeProvider as provider -> provider
                | _ -> TimeProvider.System

            let logger =
                services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger($"Orleans.FSharp.Functional.{definition.GrainTypeName}")

            // The domain key is decoded once per activation. The primary state itself is created
            // in OnActivateAsync (activation step 3), never here, so a definition without a
            // durable record and one with it follow the same ordering.
            let key = definition.DecodeKey grainContext.GrainId

            // Step 1 of the activation order: every attached facet is created here,
            // synchronously and before the target is returned, so each one subscribes to the
            // activation lifecycle in time for the stock GrainLifecycleStage.SetupState load.
            // IPersistentStateFactory.Create refuses to run once the lifecycle has started.
            let facets =
                if definition.Facets.Length = 0 then
                    Array.empty
                else
                    let stateFactory = services.GetRequiredService<IPersistentStateFactory>()

                    definition.Facets
                    |> Array.map (fun blueprint ->
                        { Blueprint = blueprint
                          Instance = blueprint.Create stateFactory grainContext })

            // Step 1b, same reasoning and the same moment: Orleans' transactional states subscribe
            // to GrainLifecycleStage.SetupState from inside ITransactionalStateFactory.Create
            // (TransactionalStateFactory.Create calls state.Participate(context.ObservableLifecycle)),
            // so they too must exist before the lifecycle starts.
            //
            // Unlike IPersistentStateFactory.Create, the transactional factory takes NO grain
            // context: TransactionalStateFactory reads IGrainContextAccessor.GrainContext, which is
            // RuntimeContext.Current. Orleans runs IGrainActivator.CreateInstance from
            // ActivationData.Start, which runs as a work item on the activation's own scheduler
            // (WorkItemGroup.Execute wraps every work item in RuntimeContext.SetExecutionContext),
            // so the ambient context IS this activation. That is a real invariant of Orleans'
            // scheduler rather than a property of our code, so it is checked rather than assumed:
            // a silent mismatch would build every transactional facet against the wrong grain.
            let transactionalFacets =
                if definition.TransactionalFacets.Length = 0 then
                    Array.empty
                else
                    let contextAccessor = services.GetRequiredService<IGrainContextAccessor>()
                    let ambient = contextAccessor.GrainContext

                    if not (obj.ReferenceEquals(ambient, grainContext)) then
                        let ambientId =
                            if isNull (box ambient) then
                                "<none>"
                            else
                                string ambient.GrainId

                        fail
                            StartupStage
                            $"the functional activation of grain type '{definition.GrainTypeName}' cannot create its transactional state: Orleans resolves a transactional facet against the ambient grain context (IGrainContextAccessor.GrainContext), which is '{ambientId}' here rather than this activation's '{grainContext.GrainId}'."

                    let transactionalFactory =
                        services.GetRequiredService<ITransactionalStateFactory>()

                    definition.TransactionalFacets
                    |> Array.map (fun blueprint ->
                        { Blueprint = blueprint
                          Instance = blueprint.Create transactionalFactory
                          Initial = blueprint.Initialize key })

            // Spec 004 item 3, step 1b of the activation order: a journaled definition's log-view
            // adaptor is installed at GrainLifecycleStage.SetupState -- the same stage Orleans'
            // own LogConsistentGrain installs at, and the same stage a persistent-state facet
            // loads at -- so the journal is replayed before the activation serves anything.
            //
            // The subscription is made HERE, synchronously inside CreateInstance, for the same
            // reason the onLifecycle hooks below are: it is before any lifecycle stage of this
            // activation has started, so it is registered in time for every one of them.
            // Implementing ILifecycleParticipant<IGrainLifecycle> on the target would work too
            // (ActivationData.SetGrainInstance calls Participate on any instance that implements
            // it, whichever activator produced it), but it would put a second, differently-ordered
            // subscription mechanism on the same type.
            let journalHost =
                match definition.Journal with
                | None -> None
                | Some blueprint ->
                    let host =
                        FunctionalJournalHost(
                            blueprint,
                            definition.GrainTypeName,
                            grainContext,
                            codec :> IFunctionalPayloadCodec,
                            logger,
                            key
                        )

                    grainContext.ObservableLifecycle.Subscribe(
                        $"Orleans.FSharp.Functional.{definition.GrainTypeName}.journal.setup",
                        GrainLifecycleStage.SetupState,
                        Func<CancellationToken, Task>(fun _ ->
                            host.Install()
                            Task.CompletedTask),
                        Func<CancellationToken, Task>(fun _ -> host.DeactivateAsync())
                    )
                    |> ignore

                    grainContext.ObservableLifecycle.Subscribe(
                        $"Orleans.FSharp.Functional.{definition.GrainTypeName}.journal.preActivate",
                        GrainLifecycleStage.Activate - 1,
                        Func<CancellationToken, Task>(fun _ -> host.PreActivateAsync())
                    )
                    |> ignore

                    grainContext.ObservableLifecycle.Subscribe(
                        $"Orleans.FSharp.Functional.{definition.GrainTypeName}.journal.replay",
                        GrainLifecycleStage.Activate + 1,
                        Func<CancellationToken, Task>(fun _ -> host.ReplayAsync())
                    )
                    |> ignore

                    Some host

            let mutable deactivate = fun () -> ()
            let mutable delay = fun (_: TimeSpan) -> ()
            let mutable registerReminder = fun (_: string) (_: TimeSpan) (_: TimeSpan) -> Unchecked.defaultof<Task<IGrainReminder>>
            let mutable createTimer = fun (_: CancellationToken -> Task) (_: GrainTimerCreationOptions) -> ()

            let activationState =
                FunctionalActivationState(definition, facets, transactionalFacets)

            match journalHost with
            | Some host -> activationState.AttachJournal(host :> IFunctionalJournalAccess)
            | None -> ()

            let env =
                { Definition = definition
                  GrainContext = grainContext
                  Services = services
                  GrainFactory = grainFactory
                  Logger = logger
                  TimeProvider = timeProvider
                  Codec = codec
                  MaxPayloadBytes = FunctionalTransportConfiguration.maxPayloadBytes services
                  Key = key
                  State = activationState
                  DeactivateOnIdle = fun () -> deactivate ()
                  DelayDeactivation = fun timeSpan -> delay timeSpan
                  RegisterReminder = fun name dueTime period -> registerReminder name dueTime period
                  CreateTimer = fun callback options -> createTimer callback options }

            // A definition with no implicit subscription gets the plain target, so Orleans never
            // probes it as a stream or broadcast consumer -- see FunctionalStreamingGrainTarget's
            // remarks for why that separation is load-bearing rather than cosmetic.
            let target: FunctionalGrainTarget<'Actor> =
                if definition.StreamBindings.Length = 0 then
                    new FunctionalGrainTarget<'Actor>(env, grainContext, grainRuntime)
                else
                    new FunctionalStreamingGrainTarget<'Actor>(env, grainContext, grainRuntime)

            deactivate <- fun () -> target.DeactivateNow()
            delay <- fun timeSpan -> target.DelayDeactivationFor timeSpan
            registerReminder <- fun name dueTime period -> target.RegisterReminderNow(name, dueTime, period)
            createTimer <- fun callback options -> target.CreateTrackedTimer(callback, options) |> ignore

            target.OnTimerDisposalError <-
                fun error ->
                    logger.LogError(
                        error,
                        "Disposing a timer of grain type {GrainType} on {GrainId} failed: {Message}",
                        definition.GrainTypeName,
                        grainContext.GrainId,
                        error.Message
                    )

            // Deactivation ordering, made independently observable: this fires once every
            // declared timer of this activation has been attempted for disposal, which is the
            // "timer disposal" stage between the remaining Orleans stop stages and DisposeInstance
            // observing Dispose returning.
            target.OnTimersDisposed <-
                fun disposed ->
                    logger.LogDebug(
                        "Functional timers of grain type {GrainType} disposed: {TimerCount} handle(s) for grain {GrainId}",
                        definition.GrainTypeName,
                        disposed,
                        grainContext.GrainId
                    )

            if not (obj.ReferenceEquals((target :> IGrainBase).GrainContext, grainContext)) then
                fail
                    StartupStage
                    $"the functional activation target of grain type '{definition.GrainTypeName}' did not receive the supplied IGrainContext."

            // "onLifecycle" hooks: subscribed directly on the Orleans-supplied observable
            // lifecycle, exactly the seam persistent-state facets already use to load at
            // SetupState (this method's own step-1 comment above) and the seam the classic
            // grain{} CE's onLifecycleStage uses (Orleans.FSharp.Runtime.GrainDiscovery.fs). This
            // subscription itself runs here, synchronously, during CreateInstance -- i.e. before
            // any lifecycle stage of THIS activation starts -- so it is registered in time for
            // every stage, including First. The callback body only runs later, once Orleans
            // actually reaches that stage, by which point 'target' and every mutable wired above
            // (deactivate, delay, ...) are already assigned; env's closures resolve to them
            // correctly because env captures the mutable bindings, not a snapshot of their value
            // at this point. allowsMutation=false: a lifecycle hook carries no state (see
            // LifecycleHook's remarks) and does not participate in state publication, so its
            // persistent-state facades permit reads but reject the setter and storage calls, the
            // same rule an interleaved read-only handler call gets. Verified ordering (an
            // integration probe, not an assumption -- see FunctionalPlacementIntegrationTests.fs):
            // First, SetupState, and Last all fire before OnActivateAsync -- and therefore before
            // FunctionalLifecycle.activate's state initialization, onActivate hook, reminders,
            // and timers -- runs at all. OnActivateAsync is not gated by the numbered Activate
            // stage; it is a separate step Orleans runs after the whole numbered stage sequence
            // (First..Last) has completed.
            for stage, adapter in definition.LifecycleHooks do
                grainContext.ObservableLifecycle.Subscribe(
                    $"Orleans.FSharp.Functional.{definition.GrainTypeName}.onLifecycle.{stage}",
                    LifecycleStage.toOrleansStage stage,
                    Func<CancellationToken, Task>(fun cancellationToken ->
                        task {
                            let scope =
                                FunctionalStateScope(definition.GrainTypeName, $"onLifecycle:{stage}", false)

                            try
                                let core = FunctionalContextFactory.core env cancellationToken scope
                                do! adapter.Invoke(env.Key, core)
                            finally
                                scope.Expire()
                        }
                        :> Task)
                )
                |> ignore

            box target

        member _.DisposeInstance(grainContext: IGrainContext, instance: obj) =
            match instance with
            | :? IDisposable as disposable ->
                disposable.Dispose()

                // Deactivation ordering, made independently observable: DisposeInstance observing
                // Dispose() return is the last of the four ordered stages (onDeactivate hook →
                // lifecycle OnStop → timer disposal → DisposeInstance).
                match grainContext.ActivationServices.GetService typeof<ILoggerFactory> with
                | :? ILoggerFactory as loggerFactory ->
                    loggerFactory
                        .CreateLogger("Orleans.FSharp.Functional.DisposeInstance")
                        .LogDebug(
                            "Functional DisposeInstance completed for grain {GrainId}",
                            grainContext.GrainId
                        )
                | _ -> ()

                ValueTask.CompletedTask
            | _ -> ValueTask.CompletedTask

/// <summary>
/// Installs the functional <c>IGrainActivator</c> on every registered functional grain type and
/// leaves every other grain type untouched.
/// </summary>
[<Sealed>]
type internal FunctionalConfigureGrainTypeComponents(registry: FunctionalGrainRegistry) =

    /// <summary>Close the functional activator over one definition's actor brand.</summary>
    static member CreateActivator(definition: FunctionalHostedDefinition) : IGrainActivator =
        let closed =
            typedefof<FunctionalGrainActivator<_>>.MakeGenericType [| definition.ActorType |]

        match Activator.CreateInstance(closed, [| box definition |]) with
        | :? IGrainActivator as activator -> activator
        | _ ->
            fail
                StartupStage
                $"the functional grain activator for grain type '{definition.GrainTypeName}' could not be created."

    interface IConfigureGrainTypeComponents with
        member _.Configure(grainType: GrainType, _properties: GrainProperties, shared: GrainTypeSharedContext) =
            match registry.TryByGrainType(grainType.ToString()) with
            | Some entry ->
                shared.SetComponent<IGrainActivator>(
                    FunctionalConfigureGrainTypeComponents.CreateActivator entry.Definition
                )
            | None -> ()
