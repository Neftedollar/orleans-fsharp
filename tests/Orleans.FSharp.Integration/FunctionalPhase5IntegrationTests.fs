/// <summary>
/// Spec 003 Phase 5 in a real single-silo cluster: collection age, declared reminders and
/// timers, the 4-stage deactivation order, and functional-context completion.
/// </summary>
module Orleans.FSharp.Integration.FunctionalPhase5IntegrationTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Configuration
open Orleans.Metadata
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp.Integration.FunctionalPhase5Fixture
open Xunit

let private poll (deadlineSeconds: float) (probe: unit -> bool) =
    task {
        let deadline = DateTime.UtcNow.AddSeconds deadlineSeconds
        let mutable satisfied = probe ()

        while not satisfied && DateTime.UtcNow < deadline do
            do! Task.Delay 100
            satisfied <- probe ()

        return satisfied
    }

/// <summary>
/// Reactivation only ever happens in response to an incoming call — Orleans' activation
/// collector only deactivates idle activations, it never proactively reactivates anything — so
/// detecting "the grain was collected and a later call reactivated it" requires each poll
/// iteration to actually CALL the grain, not merely inspect local state.
/// </summary>
let private pollByCalling (deadlineSeconds: float) (call: unit -> Task<'T>) (probe: 'T -> bool) =
    task {
        let deadline = DateTime.UtcNow.AddSeconds deadlineSeconds
        let! first = call ()
        let mutable satisfied = probe first

        while not satisfied && DateTime.UtcNow < deadline do
            do! Task.Delay 200
            let! observed = call ()
            satisfied <- probe observed

        return satisfied
    }

[<Collection("Phase5Cluster")>]
type FunctionalPhase5Tests(fixture: Phase5ClusterFixture) =

    let key (prefix: string) = $"{prefix}-{Guid.NewGuid():N}"

    // ──────────────────────────────────────────────────────────────────────────
    // Reminders
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// "Each successful activation reconciles declared reminders through
    /// RegisterOrUpdateReminder in declaration order... before activation completes." Two
    /// declared reminders on one grain both fire, proving reconciliation reached both — and the
    /// reminder context token is always CancellationToken.None, per the spec's token table.
    /// </remarks>
    [<Fact>]
    member _.``declared reminders reconcile at activation and fire with CancellationToken.None``() =
        task {
            let name = key "reminders"
            let grainId = fixture.ReminderId name
            let api = fixture.Reminder name

            do! api.touch ()

            let! fired =
                poll 30.0 (fun () ->
                    Phase5Probe.reminderTickCount grainId "tick-a" >= 1
                    && Phase5Probe.reminderTickCount grainId "tick-b" >= 1)

            Assert.True(fired, "both declared reminders must fire within the deadline")

            Assert.Equal(Some false, Phase5Probe.reminderTokenCouldCancel grainId "tick-a")
            Assert.Equal(Some false, Phase5Probe.reminderTokenCouldCancel grainId "tick-b")

            // Whole-state replacement: the counts observed through the API match what the
            // reminder hooks accumulated in state.
            let! tickA, tickB = api.ticks ()
            Assert.True(tickA >= 1)
            Assert.True(tickB >= 1)
        }

    /// <remarks>
    /// "Fixture: UseInMemoryReminderService; reminders survive deactivation + reactivation."
    /// </remarks>
    [<Fact>]
    member _.``a declared reminder survives deactivation and keeps firing on the next activation``() =
        task {
            let name = key "reminder-survive"
            let grainId = fixture.ReminderId name
            let api = fixture.Reminder name

            do! api.touch ()

            let! firedOnce = poll 30.0 (fun () -> Phase5Probe.reminderTickCount grainId "tick-a" >= 1)
            Assert.True(firedOnce, "the reminder must fire on the first activation")

            let activationsBefore = Phase5Probe.activationCount grainId
            do! api.goAway ()

            let! reactivated = poll 30.0 (fun () -> Phase5Probe.activationCount grainId > activationsBefore)
            Assert.True(reactivated, "the grain must reactivate after deactivateOnIdle")

            // The durable reminder registration survived the deactivation, was reconciled again
            // on the new activation (RegisterOrUpdateReminder is idempotent), and keeps firing.
            let tickCountAfterReactivation = Phase5Probe.reminderTickCount grainId "tick-a"

            let! keptFiring =
                poll 30.0 (fun () -> Phase5Probe.reminderTickCount grainId "tick-a" > tickCountAfterReactivation)

            Assert.True(keptFiring, "the reminder must keep firing after reactivation")
        }

    /// <remarks>
    /// <para>
    /// "Renamed/removed reminders: implement nothing automatic... a reminder declared once and
    /// then absent in a redeployed definition keeps firing into the unknown-name failure path
    /// (that IS the documented behavior motivating the migration)."
    /// </para>
    /// <para>
    /// <c>phase5.reminder.stale</c> declares no reminder at all — the redeployed definition. The
    /// durable registration a previous deployment made is recreated here through the stock
    /// <c>IReminderRegistry</c>, because that registration lives in the reminder table, not in
    /// the definition, and outlives the code that declared it. The reminder then fires into the
    /// unknown-name failure path on every period until the DOCUMENTED migration —
    /// <c>IReminderRegistry.GetReminder</c> + <c>UnregisterReminder</c> from
    /// <c>context.services</c> — removes it, which is what this test also exercises.
    /// </para>
    /// </remarks>
    [<Fact>]
    member _.``a reminder absent from the redeployed definition keeps failing until the documented unregister migration runs``
        ()
        =
        task {
            let name = key "stale-reminder"
            let grainId = fixture.StaleReminderId name
            let api = fixture.StaleReminder name

            do! api.ping ()

            let unknownFor (entries: string list) =
                entries
                |> List.filter (fun entry ->
                    entry.Contains(grainId, StringComparison.Ordinal)
                    && entry.Contains(GhostReminderName, StringComparison.Ordinal))

            Phase5LogCapture.clear ()
            do! api.registerGhost ()

            // The registration really is in the reminder table even though the definition never
            // declared it — this is the situation a redeploy leaves behind.
            let! registered = api.registeredNames ()
            Assert.Equal<string list>([ GhostReminderName ], registered)

            // Twice, not once: "keeps firing" is the whole point — the stale registration is
            // durable and nothing in the runtime retires it.
            let! failed =
                poll 30.0 (fun () ->
                    Phase5LogCapture.entriesContaining "received unknown reminder"
                    |> unknownFor
                    |> List.length >= 2)

            Assert.True(
                failed,
                "a reminder the redeployed definition no longer declares must keep firing into the unknown-name failure path"
            )

            // The documented migration: unregister the stale name explicitly. Nothing automatic
            // does this — the runtime cannot tell "renamed" from "temporarily undeployed".
            let! removed = api.unregisterGhost ()
            Assert.True(removed, "the stale reminder must still have been registered before the migration")

            let! remaining = api.registeredNames ()
            Assert.Empty(remaining)

            // Let any tick already in flight settle, then prove the failures have stopped.
            do! Task.Delay(Phase5Timing.ReminderPeriod + Phase5Timing.ReminderPeriod)
            Phase5LogCapture.clear ()
            do! Task.Delay(Phase5Timing.ReminderPeriod + Phase5Timing.ReminderPeriod + Phase5Timing.ReminderPeriod)

            Assert.Empty(Phase5LogCapture.entriesContaining "received unknown reminder" |> unknownFor)
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Timers
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// "Declared timers are created after successful activation... TimerHook = whole-state
    /// replacement under Interleave=false."
    /// </remarks>
    [<Fact>]
    member _.``a declared timer is created after activation and replaces whole state``() =
        task {
            let name = key "timer"
            let grainId = fixture.TimerId name
            let api = fixture.Timer name

            do! api.touch ()

            let! ticked = poll 15.0 (fun () -> Phase5Probe.timerTickCount grainId "poll" >= 3)
            Assert.True(ticked, "the declared timer must tick at least a few times")

            let! polls = api.snapshot ()
            Assert.True(polls >= 3, $"expected accumulated whole-state replacement, got {polls}")

            // "Timer hook: token from the Orleans timer callback" — unlike the reminder token,
            // which is always CancellationToken.None, the timer callback's token is a real,
            // cancellable one.
            Assert.Equal(Some true, Phase5Probe.timerTokenCouldCancel grainId "poll")
        }

    /// <remarks>
    /// <para>
    /// Task 5 only proved deactivation hooks ran "once"; the spec's required-test bullet says
    /// "execute once in the specified order": functional onDeactivate hook → lifecycle OnStop →
    /// timer disposal → IGrainActivator.DisposeInstance. Stage 2 is observed by
    /// <c>Phase5StopStageWitness</c> (a lifecycle stop callback subscribed at SetupState, which
    /// stop-stage reversal places after the stage invoking the functional hook); stages 3 and 4
    /// are opaque runtime internals with no typed observation point, so they are read from the
    /// runtime's own Debug-level trace lines, which is the only way to observe them at all.
    /// </para>
    /// <para>
    /// Every stage is attributed to THIS grain's identity, never recorded cluster-wide: the
    /// shared silo tears other activations down throughout (collection ages here are 1.5 s and
    /// 6 s), and every functional activation emits both disposal traces, so a stage-name-only
    /// probe would be satisfiable by an unrelated grain and would break "exactly once".
    /// </para>
    /// </remarks>
    [<Fact>]
    member _.``onDeactivate, lifecycle OnStop, timer disposal, and DisposeInstance run once and in order``
        ()
        =
        task {
            let name = key "order"
            let grainId = fixture.TimerId name
            let api = fixture.Timer name

            do! api.touch ()

            // The declared timer must really exist before deactivation, otherwise the timer-
            // disposal stage below would prove nothing about disposing anything.
            let! ticked = poll 15.0 (fun () -> Phase5Probe.timerTickCount grainId "poll" >= 1)
            Assert.True(ticked, "the declared timer must have ticked before the activation is torn down")

            Phase5Probe.resetStages ()
            Phase5LogCapture.clear ()

            do! api.goAway ()

            let stages = [ "deactivate-hook"; "lifecycle-stop"; "timers-disposed"; "dispose-instance" ]

            let! allObserved =
                poll 30.0 (fun () ->
                    stages
                    |> List.forall (fun stage -> Phase5Probe.stageTick stage grainId |> Option.isSome))

            Assert.True(allObserved, "all four deactivation-ordering stages must be observed for this grain")

            let hookTick = Phase5Probe.stageTick "deactivate-hook" grainId |> Option.get
            let stopTick = Phase5Probe.stageTick "lifecycle-stop" grainId |> Option.get
            let timersTick = Phase5Probe.stageTick "timers-disposed" grainId |> Option.get
            let disposeTick = Phase5Probe.stageTick "dispose-instance" grainId |> Option.get

            Assert.True(
                hookTick < stopTick,
                $"onDeactivate hook (tick {hookTick}) must run before the lifecycle stop stages (tick {stopTick})"
            )

            Assert.True(
                stopTick < timersTick,
                $"lifecycle OnStop (tick {stopTick}) must run before timer disposal (tick {timersTick})"
            )

            Assert.True(
                timersTick < disposeTick,
                $"timer disposal (tick {timersTick}) must run before DisposeInstance (tick {disposeTick})"
            )

            // Each stage exactly once, for this grain.
            for stage in stages do
                Assert.Equal(1, Phase5Probe.stageCount stage grainId)

            // The disposal stage is not vacuous: the runtime reports the handle count, and this
            // activation owned exactly the one declared timer. (The stage itself fires for every
            // functional activation, including those which never created a timer.)
            Assert.Equal(1, Phase5Probe.disposedTimerCount grainId)

            // "Timers are... disposed with the activation": no further tick arrives once the
            // activation is gone. No call is made here — a call would reactivate the grain and
            // create a fresh timer.
            let ticksAtDeactivation = Phase5Probe.timerTickCount grainId "poll"
            do! Task.Delay(Phase5Timing.TimerPeriod + Phase5Timing.TimerPeriod + TimeSpan.FromSeconds 1.0)

            Assert.Equal(ticksAtDeactivation, Phase5Probe.timerTickCount grainId "poll")
        }

    /// <remarks>
    /// "A timer with KeepAlive = false does not extend lifetime." collectionAge is short and the
    /// timer's own period is much shorter, so many ticks accumulate during the idle window; the
    /// grain must still be recollected and reactivate, proving the ticks alone were not counted
    /// as activity.
    /// </remarks>
    [<Fact>]
    member _.``a timer with KeepAlive=false does not extend collection lifetime``() =
        task {
            let name = key "keepalive-false"
            let grainId = fixture.TimerId name
            let api = fixture.Timer name

            do! api.touch ()
            let activationsBefore = Phase5Probe.activationCount grainId

            // Let several timer ticks accumulate, then wait past the HOST default collectionAge
            // (this grain type declares no collectionAge override, so the stock host default —
            // configured short in this fixture — governs it) with NO further calls at all, so
            // the ticks are the only thing that could keep it alive.
            let! _ = poll 5.0 (fun () -> Phase5Probe.timerTickCount grainId "poll" >= 5)
            do! Task.Delay(Phase5Timing.HostCollectionAge + Phase5Timing.CollectionQuantum + Phase5Timing.CollectionQuantum)

            // Reactivation only happens on a call, so the polling call here IS "a later call
            // [that] reactivates" — it is issued only after the idle window above has elapsed.
            let! recollected = pollByCalling 20.0 api.snapshot (fun _ -> Phase5Probe.activationCount grainId > activationsBefore)

            Assert.True(
                recollected,
                "a KeepAlive=false timer must not keep the activation alive past its collection age"
            )
        }

    /// <remarks>"An active timer configured with KeepAlive = true extends the activation's lifetime."</remarks>
    [<Fact>]
    member _.``a timer with KeepAlive=true extends collection lifetime``() =
        task {
            let name = key "keepalive-true"
            let grainId = fixture.TimerKeepAliveId name
            let api = fixture.TimerKeepAlive name

            do! api.touch ()
            let activationsBefore = Phase5Probe.activationCount grainId

            // Wait well past this grain type's own short collectionAge override, with no calls.
            // A KeepAlive=true timer ticking throughout must keep the SAME activation alive.
            //
            // Task-7 close-out A.7: this test's assertion is negative (no recollection observed),
            // so it only proves anything if the collector actually got a fair chance to sweep a
            // stale activation within the window. The collector runs every CollectionQuantum, and
            // sweeps are aligned to silo startup rather than to this test's `touch()` call, so the
            // worst-case first sweep after the grain becomes theoretically eligible can land
            // almost a full quantum later than the CollectionAge threshold alone suggests. The
            // previous window (CollectionAge + 2*Quantum) left only one quantum (0.5s) of margin
            // beyond that worst case — on a loaded CI runner a late collector sweep could silently
            // swallow the whole margin, and a REAL KeepAlive bug (the grain actually eligible for
            // collection) would then go undetected purely because the test didn't wait long enough
            // to see the collector catch up, not because KeepAlive worked. Six quanta of margin
            // (3s) makes that far less likely while keeping the test's total wall time bounded.
            do! Task.Delay(Phase5Timing.CollectionAge + (TimeSpan.FromTicks(Phase5Timing.CollectionQuantum.Ticks * 6L)))

            // The call itself is what proves it: if the activation HAD been collected, this call
            // would trigger a fresh one and bump the activation count.
            let! polls = api.snapshot ()
            Assert.Equal(activationsBefore, Phase5Probe.activationCount grainId)
            Assert.True(polls >= 5, $"the KeepAlive timer should have ticked repeatedly, got {polls}")
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Collection age
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// The primary proof that <c>collectionAge</c> is frozen into the well-known manifest
    /// property is the fast, deterministic unit test in FunctionalRuntimeTests (constructing
    /// <c>FunctionalGrainPropertiesProvider</c> directly, no cluster needed). This is the same
    /// fact confirmed against the real cluster manifest this fixture actually deploys.
    /// </remarks>
    [<Fact>]
    member _.``the collection grain type publishes idle-duration in the real cluster manifest``() : unit =
        let services = (fixture.Cluster.Silos.[0] :?> InProcessSiloHandle).SiloHost.Services
        let manifest = services.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest
        let grainType: GrainType = GrainType.Create Phase5GrainTypes.Collection

        match manifest.Grains.TryGetValue grainType with
        | true, properties ->
            let idleDuration =
                properties.Properties
                |> Seq.tryFind (fun pair -> pair.Key = WellKnownGrainTypeProperties.IdleDeactivationPeriod)
                |> Option.map (fun pair -> pair.Value)

            Assert.Equal(Some(Phase5Timing.CollectionAge.ToString()), idleDuration)
        | false, _ -> failwith $"grain type '{Phase5GrainTypes.Collection}' not found in the cluster manifest"

    /// <remarks>
    /// "An override produces stock collection eligibility and reactivation behavior... durable
    /// state reloads." Manifest presence/absence of the property itself is proven at the unit
    /// level (FunctionalRuntimeTests); this is the real end-to-end collection + reactivation +
    /// durable reload.
    /// </remarks>
    [<Fact>]
    member _.``a collectionAge override collects an idle activation and a later call reactivates with durable state``
        ()
        =
        task {
            let name = key "collection"
            let grainId = fixture.CollectionId name
            let api = fixture.Collection name

            do! api.writeNow "durable-marker"
            let activationsBefore = Phase5Probe.activationCount grainId

            // Idle past collectionAge + a couple of scan quanta, with no calls at all.
            do! Task.Delay(Phase5Timing.CollectionAge + Phase5Timing.CollectionQuantum + Phase5Timing.CollectionQuantum)

            // Reactivation only happens on a call, so the polling call here IS "a later call
            // [that] reactivates" — issued only after the idle window above has fully elapsed.
            let! reactivated =
                pollByCalling 20.0 api.snapshot (fun _ -> Phase5Probe.activationCount grainId > activationsBefore)

            Assert.True(reactivated, "an idle activation past its collectionAge override must be collected")

            // Durable state reloads: the reactivation loaded the explicitly written record.
            let! snapshot = api.snapshot ()
            Assert.Equal("durable-marker", snapshot)
        }

    /// <remarks>
    /// The ephemeral half of the same rule: "ephemeral state re-initializes." An in-memory-only
    /// mutation which was never written anywhere cannot survive a real recollection — only the
    /// SAME activation could still hold it — so observing the pristine initializer value after
    /// reactivation is exactly what "re-initializes" (as opposed to "reloads") means here.
    /// </remarks>
    [<Fact>]
    member _.``a collectionAge override on an ephemeral definition re-initializes state on reactivation``
        ()
        =
        task {
            let name = key "collection-ephemeral"
            let grainId = fixture.CollectionEphemeralId name
            let api = fixture.CollectionEphemeral name

            do! api.writeNow "in-memory-only"
            let! before = api.snapshot ()
            Assert.Equal("in-memory-only", before)

            let activationsBefore = Phase5Probe.activationCount grainId
            do! Task.Delay(Phase5Timing.CollectionAge + Phase5Timing.CollectionQuantum + Phase5Timing.CollectionQuantum)

            let! reactivated =
                pollByCalling 20.0 api.snapshot (fun _ -> Phase5Probe.activationCount grainId > activationsBefore)

            Assert.True(reactivated, "an idle ephemeral activation past its collectionAge override must be collected")

            let! after = api.snapshot ()
            Assert.Equal("", after)
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Context completion
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>"A custom application TimeProvider registered in DI is authoritative."</remarks>
    [<Fact>]
    member _.``a custom registered TimeProvider stays authoritative over utcNow``() =
        task {
            let fixedInstant = DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero)
            let api = fixture.Context(key "clock")

            settableTimeProvider.Set fixedInstant

            try
                let! observed = api.clock ()
                Assert.Equal(fixedInstant, observed)
            finally
                // Momentary override only: the same TimeProvider instance is registered for the
                // whole silo, and Orleans' own scheduling (activation collector, timers,
                // reminders) depends on it advancing in real time for the rest of this fixture.
                settableTimeProvider.Reset()
        }

    /// <remarks>
    /// "tryGetRequestContext / setRequestContext / removeRequestContext over Orleans
    /// RequestContext; values belong to the invocation." A value the caller sets before the call
    /// is visible inside the handler; a value the handler sets and removes within its own
    /// invocation is gone by the time it checks again, all within the same call.
    /// </remarks>
    [<Fact>]
    member _.``RequestContext values are visible to the handler and are invocation-scoped``() =
        task {
            let api = fixture.Context(key "reqctx")

            RequestContext.Set("phase5-probe", "caller-value")
            let! echoed = api.echoRequestContext ()
            RequestContext.Clear()

            Assert.Equal("caller-value", echoed)

            // A later call which never sets the key sees none — the caller's value did not leak
            // into activation-wide state.
            let! echoedAgain = api.echoRequestContext ()
            Assert.Equal("<none>", echoedAgain)

            let! before, after = api.roundTripRequestContext ()
            Assert.Equal("<none>", before)
            Assert.Equal("<none>", after)
        }

    /// <remarks>
    /// Spec required-test bullet: "concurrent contexts never leak cancellation or RequestContext
    /// values between activations/calls." Many concurrent calls, each caller setting a distinct
    /// value immediately before its own call, must each see only their own value back.
    /// </remarks>
    [<Fact>]
    member _.``concurrent calls never leak RequestContext values into each other``() =
        task {
            let api = fixture.Context(key "reqctx-concurrent")
            let concurrency = 24

            let call (index: int) =
                task {
                    let expected = $"probe-{index}"
                    RequestContext.Set("phase5-probe", expected)

                    try
                        let! observed = api.echoRequestContext ()
                        return expected, observed
                    finally
                        RequestContext.Clear()
                }

            let! results = Task.WhenAll [| for index in 0 .. concurrency - 1 -> call index |]

            for expected, observed in results do
                Assert.Equal(expected, observed)
        }

    /// <remarks>
    /// The other half of the same spec bullet: "concurrent contexts never leak cancellation...
    /// between activations/calls". Several read-only (hence interleaving) calls are genuinely in
    /// flight on ONE activation — gated on the handlers actually having entered, not on a sleep
    /// — when one caller's token is cancelled. Only that invocation's context token may fire;
    /// every sibling must run to completion.
    /// </remarks>
    [<Fact>]
    member _.``cancelling one concurrent call never cancels its siblings``() =
        task {
            let name = key "cancel-concurrent"
            let grainId = fixture.ContextId name
            let reference = fixture.ContextRaw name
            let siblings = 5

            use source = new CancellationTokenSource()
            let victim = reference.callCancellable (_.waitCancel) 20000 source.Token

            let siblingCalls =
                [| for _ in 1..siblings -> reference.callCancellable (_.waitCancel) 3000 CancellationToken.None |]

            let! allInFlight = poll 30.0 (fun () -> Phase5Probe.inFlightCount grainId >= siblings + 1)
            Assert.True(allInFlight, "every concurrent call must be in flight before the cancellation")

            source.Cancel()

            let! victimOutcome =
                task {
                    try
                        let! reply = victim
                        return reply
                    with :? OperationCanceledException ->
                        return "caller-cancelled"
                }

            // Either the caller observes cancellation or the target's cooperative reply arrives
            // first; both mean the victim's own token fired.
            Assert.True(
                victimOutcome = "cancelled" || victimOutcome = "caller-cancelled",
                $"the cancelled call must observe its own cancellation, got '{victimOutcome}'"
            )

            let! siblingOutcomes = Task.WhenAll siblingCalls

            for outcome in siblingOutcomes do
                Assert.Equal<string>("completed", outcome)

            // Judged from inside the handlers, which is the only place that can distinguish "the
            // target's own token fired" from "the caller stopped waiting": exactly one context
            // token was cancelled — the victim's — and every sibling ran to completion.
            let! settled =
                poll 30.0 (fun () ->
                    Phase5Probe.callOutcomeCount grainId "completed"
                    + Phase5Probe.callOutcomeCount grainId "cancelled" >= siblings + 1)

            Assert.True(settled, "every concurrent handler must have recorded an outcome")
            Assert.Equal(1, Phase5Probe.callOutcomeCount grainId "cancelled")
            Assert.Equal(siblings, Phase5Probe.callOutcomeCount grainId "completed")
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Tracing
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// "Logs and activities contain grain type, operation ID, version, and outcome; payload
    /// bytes and deserialized application values are excluded by default." Verified on both a
    /// successful and a failing call.
    /// </remarks>
    [<Fact>]
    member _.``dispatch tracing includes grain type, operation, version, grain id, and outcome``() =
        task {
            let name = key "tracing"
            let grainId = fixture.ContextId name
            let api = fixture.Context name
            Phase5LogCapture.clear ()

            let! _ = api.clock ()

            let successEntries = Phase5LogCapture.entriesContaining "Functional dispatch"
            Assert.True(successEntries.Length >= 1, "a successful call must be traced")

            Assert.True(
                successEntries
                |> List.exists (fun entry ->
                    entry.Contains(Phase5GrainTypes.Context, StringComparison.Ordinal)
                    && entry.Contains("operationId=clock", StringComparison.Ordinal)
                    && entry.Contains("version=1", StringComparison.Ordinal)
                    // Task-7 close-out A.5: a distinct "grainId=<exact GrainId>" token, not
                    // merely a substring `Contains` that a grain-type or operation token could
                    // also satisfy (e.g. a grain type name that happens to contain "grainId").
                    && entry.Contains($"grainId={grainId}", StringComparison.Ordinal)
                    && entry.Contains("outcome=success", StringComparison.Ordinal))
            )

            Phase5LogCapture.clear ()
            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.boom () :> Task)
            Assert.Contains("boom-for-tracing", error.ToString())

            let failureEntries = Phase5LogCapture.entriesContaining "Functional dispatch"
            Assert.True(failureEntries.Length >= 1, "a failing call must be traced too")

            Assert.True(
                failureEntries
                |> List.exists (fun entry ->
                    entry.Contains("operationId=boom", StringComparison.Ordinal)
                    && entry.Contains($"grainId={grainId}", StringComparison.Ordinal)
                    && entry.Contains("outcome=failed", StringComparison.Ordinal))
            )

            // Payload bytes and application values are excluded: the distinctive application
            // exception message must never appear in the dispatch trace line itself.
            Assert.False(
                failureEntries |> List.exists (fun entry -> entry.Contains("boom-for-tracing", StringComparison.Ordinal))
            )
        }

    /// <remarks>
    /// Task-7 close-out A.1: "Logs AND activities" — this half was unimplemented (log-only).
    /// Dispatch tags the ambient <c>Activity.Current</c>, when Orleans' own
    /// "Microsoft.Orleans.Runtime" instrumentation started one for the call, with exactly the
    /// five fields the spec names and nothing application-shaped. An <c>ActivityListener</c>
    /// forces Orleans to actually create+sample that Activity for the duration of this test —
    /// without a listener sampling the source, no Activity would exist to tag at all, which is
    /// what "when one exists" in the spec sentence is conditioned on. Captured activities are
    /// filtered to this call's exact grainId so concurrently-running tests' unrelated grain
    /// calls (which share the same process-wide ActivitySource) cannot contaminate the result.
    /// </remarks>
    [<Fact>]
    member _.``dispatch tags the ambient Activity with grain type, operation, version, grain id, and outcome``
        ()
        =
        task {
            let name = key "activity-tracing"
            let grainId = fixture.ContextId name
            let api = fixture.Context name

            let captured = System.Collections.Concurrent.ConcurrentBag<Activity>()

            use listener =
                new ActivityListener(
                    ShouldListenTo = (fun source -> source.Name = "Microsoft.Orleans.Runtime"),
                    Sample = (fun (_: byref<ActivityCreationOptions<ActivityContext>>) -> ActivitySamplingResult.AllData),
                    ActivityStopped = (fun activity -> captured.Add activity)
                )

            ActivitySource.AddActivityListener listener

            let! _ = api.clock ()
            let! _ = Assert.ThrowsAnyAsync<exn>(fun () -> api.boom () :> Task)

            let tag (activity: Activity) (name: string) =
                match activity.GetTagItem name with
                | null -> None
                | value -> Some(string value)

            let taggedFor (grainId: string) (operationId: string) =
                captured
                |> Seq.filter (fun activity ->
                    tag activity "grainId" = Some grainId && tag activity "operationId" = Some operationId)
                |> List.ofSeq

            let successActivities = taggedFor grainId "clock"

            Assert.True(
                successActivities.Length >= 1,
                $"expected at least one sampled Activity tagged for grainId={grainId} operationId=clock; captured {captured.Count} activities total"
            )

            Assert.True(
                successActivities
                |> List.forall (fun activity ->
                    tag activity "grainType" = Some Phase5GrainTypes.Context
                    && tag activity "version" = Some "1"
                    && tag activity "outcome" = Some "success")
            )

            let failureActivities = taggedFor grainId "boom"

            Assert.True(failureActivities.Length >= 1, "the failing call must be tagged too")
            Assert.True(failureActivities |> List.forall (fun activity -> tag activity "outcome" = Some "failed"))

            // Payload bytes and application values are excluded: the distinctive application
            // exception message must never appear as the value of one of the five tags THIS
            // dispatch code sets. Orleans' own AddActivityPropagation instrumentation records
            // the exception separately on the same Activity (e.g. an "exception.message" tag or
            // an ActivityEvent) when the call fails — that is Orleans' own diagnostic behavior,
            // not a leak from the functional runtime, and is out of scope for this assertion; only
            // the fixed key set the spec names is checked.
            let ownTagKeys = [ "grainType"; "operationId"; "version"; "grainId"; "outcome" ]

            Assert.False(
                failureActivities
                |> List.exists (fun activity ->
                    ownTagKeys |> List.exists (fun key -> tag activity key = Some "boom-for-tracing"))
            )
        }
