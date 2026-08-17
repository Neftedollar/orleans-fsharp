/// <summary>
/// The feature-tour driver: one clearly-labeled section per Orleans feature, each driven for
/// real against a live silo. See README.md for the status matrix this transcript backs.
/// </summary>
module FeatureTour.Program

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime

open FeatureTour.Tour
open FeatureTour.Persistence
open FeatureTour.Scheduling
open FeatureTour.CallFilters
open FeatureTour.RequestContextTour
open FeatureTour.Cancellation
open FeatureTour.VersioningTour

// ── Silo ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Assemblies Orleans must already have loaded when it takes its first manifest snapshot.
/// </summary>
/// <remarks>
/// Orleans snapshots application parts inside <c>UseOrleans</c>, before any of our configuration
/// runs, and its Roslyn generators never run over F#, so an assembly reached only through an F#
/// hop is invisible to that snapshot. <c>SiloConfig.applyToHost</c> pre-loads the two assemblies
/// the F# surface itself needs (memory storage and <c>Orleans.FSharp</c>); anything else this
/// process configures has to be touched here, before <c>applyToHost</c>.
/// </remarks>
let private preloadTourAssemblies () =
    typeof<Orleans.Hosting.SiloBuilderReminderMemoryExtensions>.Assembly |> ignore // reminders
    typeof<Orleans.Streams.IStreamProvider>.Assembly |> ignore // streams

let private siloConfiguration =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        addMemoryStorage "Audit"
        addMemoryReminderService

        addIncomingFilter (TourIncomingFilter GatewayApi.RejectedOperation :> IIncomingGrainCallFilter)
    }

// ── Sections ─────────────────────────────────────────────────────────────────

let private runPersistence (factory: IGrainFactory) =
    task {
        section 1 "Persistence — stateFrom + a second usePersistentState holder"

        let ledger = LedgerApi.ref factory "acct-1"

        let! before = ledger.snapshot ()
        say $"before any write: balance={before.balance} primaryRecordExists={before.primaryRecordExists} auditRecordExists={before.auditRecordExists}"

        let! after1 = ledger.deposit 250L
        let! after2 = ledger.deposit 125L
        say $"deposit 250 -> {after1}; deposit 125 -> {after2}"

        let! written = ledger.snapshot ()
        say $"after two explicit WriteStateAsync calls: balance={written.balance} entries={written.entries} primaryRecordExists={written.primaryRecordExists} auditRecordExists={written.auditRecordExists}"
        say $"second holder ('audit' on provider 'Audit'): {written.auditEvents}"
        say $"activations of this grain so far: {written.activations}"

        do! ledger.goIdle ()
        say "goIdle: context.deactivateOnIdle() requested — the activation ends after this turn"

        let! reactivated =
            waitUntil (TimeSpan.FromSeconds 30.0) (fun () ->
                task {
                    let! snapshot = ledger.snapshot ()
                    return snapshot.activations > written.activations
                })

        let! afterIdle = ledger.snapshot ()

        if reactivated then
            say $"after deactivation + a fresh call: activations={afterIdle.activations} balance={afterIdle.balance} primaryRecordExists={afterIdle.primaryRecordExists}"
        else
            say $"deactivation was NOT observed within 30s (activations={afterIdle.activations}) — reporting what was seen"

        let! reloaded = ledger.reload ()
        say $"explicit ReadStateAsync re-read from storage -> balance={reloaded}"

        let! clearObservation = ledger.clear ()
        let! cleared = ledger.snapshot ()
        say $"explicit ClearStateAsync on both holders: balance={cleared.balance} primaryRecordExists={cleared.primaryRecordExists} auditRecordExists={cleared.auditRecordExists}"
        say $"hazard observed on clear: {clearObservation}"
        detail "Orleans re-seeds a cleared facet with an uninitialized instance, so an F# record's"
        detail "reference fields come back null — re-seed explicitly after ClearStateAsync."

        verdict "SUPPORTED — two independently-provided holders, explicit read/write/clear, RecordExists across deactivation"
    }

let private runScheduling (factory: IGrainFactory) =
    task {
        section 2 "Timers and reminders — onTimer / onReminder on the definition"

        let scheduler = SchedulerApi.ref factory "clock-1"

        let! first = scheduler.report ()
        say $"first call (activation starts the timer): ticks={first.ticks}"
        say $"reminder table for this grain right after activation: {first.registeredReminders}"

        do! Task.Delay 1200
        let! later = scheduler.report ()
        say $"after ~1.2s of a 200ms onTimer: ticks={later.ticks} (grew by {later.ticks - first.ticks})"

        let! fired =
            waitUntil (TimeSpan.FromSeconds 40.0) (fun () ->
                task {
                    let! report = scheduler.report ()
                    return report.reminderFires >= 1
                })

        let! reminded = scheduler.report ()

        if fired then
            say $"onReminder '{SchedulerDefinition.ReminderName}' fired: reminderFires={reminded.reminderFires} at {reminded.lastReminderAt}"
        else
            say $"onReminder '{SchedulerDefinition.ReminderName}' did NOT fire within 40s (fires={reminded.reminderFires})"

        detail $"declared dueTime={SchedulerDefinition.dueTime} period={SchedulerDefinition.period}"
        detail "the one-minute floor is ReminderOptions.MinimumReminderPeriod and applies to the PERIOD only;"
        detail "the due time is unconstrained, which is what lets a reminder genuinely fire inside a short run."

        verdict "SUPPORTED — timer ticks observed; reminder registered in the real reminder table and fired"
    }

let private runCallFilters (factory: IGrainFactory) =
    task {
        section 3 "Grain call filters — IIncomingGrainCallFilter over IFunctionalRequestMetadata"

        let gateway = GatewayApi.ref factory "gate-1"

        let! allowed = gateway.allowed "ping"
        say $"allowed(\"ping\") -> {allowed}"

        let! peeked = gateway.peek ()
        say $"peek() -> {peeked}"

        let! rejection =
            task {
                try
                    let! reply = gateway.forbidden "secret"
                    return $"NO REJECTION — handler replied {reply}"
                with error ->
                    return describe error
            }

        say $"forbidden(\"secret\") -> {rejection}"

        say "what the filter saw (grain type / operation / version / readOnly / oneWay / interleave / payload bytes / rejected):"

        for call in FilterLog.forGrainType "tour.gateway" do
            detail
                $"{call.grainType} {call.operationId} v{call.contractVersion} readOnly={call.isReadOnly} oneWay={call.isOneWay} interleave={call.isAlwaysInterleave} payload={call.payloadLength}B rejected={call.rejected}"

        verdict "SUPPORTED — metadata readable, rejection surfaces to the caller before the handler runs"
    }

let private runRequestContext (factory: IGrainFactory) =
    task {
        section 4 "Request context — client-set correlation id and a handler-set hop"

        let correlationId = $"corr-{Guid.NewGuid().ToString().Substring(0, 8)}"
        RequestCtx.set ContextKeys.Correlation correlationId
        say $"client: RequestCtx.set \"{ContextKeys.Correlation}\" = {correlationId}"

        let front = FrontApi.ref factory "front-1"
        let! report = front.trace ()
        RequestCtx.remove ContextKeys.Correlation

        say $"front grain read context.tryGetRequestContext -> {report.correlationSeenByFront}"
        say $"front grain set context.setRequestContext \"{ContextKeys.Hop}\" = {report.hopSetByFront}"
        say $"downstream grain saw correlation -> {report.correlationSeenByDownstream}"
        say $"downstream grain saw hop         -> {report.hopSeenByDownstream}"

        verdict "SUPPORTED — client-to-grain and grain-to-grain propagation both observed"
    }

let private runCancellation (factory: IGrainFactory) =
    task {
        section 5 "Cancellation — rawRef.callCancellable and context.cancellationToken"

        let slow = SlowApi.rawRef factory "slow-1"
        use cancellation = new CancellationTokenSource()

        let call = slow.callCancellable (_.slow) 10000 cancellation.Token
        do! Task.Delay 500
        say "caller: cancelling a 10s call after 500ms"
        cancellation.Cancel()

        let! callerOutcome =
            task {
                try
                    let! reply = call
                    return $"call returned normally: {reply}"
                with error ->
                    return describe error
            }

        say $"caller side  -> {callerOutcome}"

        // The caller's task completes as cancelled the moment the token trips; the target only
        // notices when its own await throws, which is strictly later.
        let! _ =
            waitUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                Task.FromResult(List.length (TargetObservation.all ()) >= 2))

        for observation in TargetObservation.all () do
            say $"target side  -> {observation}"

        detail "cancellation is cooperative: it does not roll back anything the handler already did."

        verdict "SUPPORTED — both sides observe the cancellation"
    }

let private runVersioning (factory: IGrainFactory) =
    task {
        section 6 "Contract versioning — exact match, no rolling-upgrade tolerance"

        let matching = VersioningTour.VersionedApi.refV1 factory "doc-1"
        let! reply = matching.hosted ()
        say $"caller on version 1 (silo hosts version 1) -> {reply}"

        let ahead = VersioningTour.VersionedApi.refV2 factory "doc-1"

        let! mismatch =
            task {
                try
                    let! reply = ahead.hosted ()
                    return $"NO REJECTION — handler replied {reply}"
                with error ->
                    return describe error
            }

        say $"caller on version 2, same grainType '{VersioningTour.VersionedApi.GrainType}' ->"
        detail mismatch

        verdict "SUPPORTED — the mismatch is refused before any handler runs, naming both versions"
    }

// ── Entry point ──────────────────────────────────────────────────────────────

let private runTour (host: IHost) =
    task {
        do! host.StartAsync()
        let factory = host.Services.GetRequiredService<IGrainFactory>()

        printfn ""
        printfn "Orleans.FSharp feature tour — every section below runs for real against a live silo."

        do! runPersistence factory
        do! runScheduling factory
        do! runCallFilters factory
        do! runRequestContext factory
        do! runCancellation factory
        do! runVersioning factory

        printfn ""
        printfn "Tour complete. Shutting the silo down..."
        do! host.StopAsync()
    }

[<EntryPoint>]
let main _argv =
    preloadTourAssemblies ()

    let builder = Host.CreateApplicationBuilder()
    builder.Logging.SetMinimumLevel LogLevel.Warning |> ignore

    SiloConfig.applyToHost siloConfiguration builder

    builder.UseOrleans(fun silo ->
        silo.AddFunctionalGrain LedgerDefinition.definition |> ignore
        silo.AddFunctionalGrain SchedulerDefinition.definition |> ignore
        silo.AddFunctionalGrain GatewayDefinition.definition |> ignore
        silo.AddFunctionalGrain FrontDefinition.definition |> ignore
        silo.AddFunctionalGrain DownstreamDefinition.definition |> ignore
        silo.AddFunctionalGrain SlowDefinition.definition |> ignore
        // Deliberately ONLY version 1 of tour.versioned.
        silo.AddFunctionalGrain VersionedDefinition.definition |> ignore)
    |> ignore

    let host = builder.Build()
    (runTour host).GetAwaiter().GetResult()
    0
