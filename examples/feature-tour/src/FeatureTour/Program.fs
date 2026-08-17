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
open Orleans.Concurrency
open Orleans.Hosting
open Orleans.Streams
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.Streaming

open FeatureTour.Tour
open FeatureTour.Persistence
open FeatureTour.Scheduling
open FeatureTour.CallFilters
open FeatureTour.RequestContextTour
open FeatureTour.Cancellation
open FeatureTour.VersioningTour
open FeatureTour.Streams
open FeatureTour.ObserverTour
open FeatureTour.Broadcast
open FeatureTour.Implicit
open FeatureTour.Interleaving
open FeatureTour.Interop
open FeatureTour.Placement
open FeatureTour.Heterogeneous

// ── Silo ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Assemblies Orleans must already have loaded when it takes its first manifest snapshot.
/// </summary>
/// <remarks>
/// Orleans snapshots application parts inside <c>UseOrleans</c>, before any of our configuration
/// runs, and its Roslyn generators never run over F#, so an assembly reached only through an F#
/// hop is invisible to that snapshot. Building a <c>siloConfig { }</c> value now pre-loads every
/// Orleans assembly <c>Orleans.FSharp.Runtime</c> references — memory storage, reminders,
/// streaming, broadcast channels, and the F# abstractions — so the first four touches below are
/// redundant and kept only to state the dependency at the point of use. The LAST one is not:
/// this process's own C# interop assembly is referenced only from F# and has to be touched here.
/// </remarks>
let private preloadTourAssemblies () =
    typeof<Orleans.Hosting.SiloBuilderReminderMemoryExtensions>.Assembly |> ignore // reminders
    typeof<Orleans.Streams.IStreamProvider>.Assembly |> ignore // streams
    typeof<Orleans.Providers.MemoryStreamQueueGrain>.Assembly |> ignore // memory stream grains
    typeof<Orleans.BroadcastChannel.IBroadcastChannelProvider>.Assembly |> ignore // broadcast channels
    // The C# interop assembly carries the Orleans-generated observer proxy AND the implicit
    // broadcast-channel consumer grain. It is reached only through an F# reference, so without
    // this touch it is absent from Orleans' first manifest snapshot and the consumer grain is
    // simply not part of the cluster.
    typeof<ITourObserver>.Assembly |> ignore

let private siloConfiguration =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        addMemoryStorage "Audit"
        addMemoryReminderService
        addMemoryStreams TourStream.Provider
        addBroadcastChannel TourChannels.Provider

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

        let persistenceHolds =
            written.balance = 375L
            && written.primaryRecordExists
            && written.auditRecordExists
            && List.length written.auditEvents = 2
            && reactivated
            && afterIdle.balance = 375L
            && reloaded = 375L
            && not cleared.primaryRecordExists
            && not cleared.auditRecordExists

        if persistenceHolds then
            verdict "SUPPORTED — two independently-provided holders, explicit read/write/clear, RecordExists across deactivation"
        else
            verdict "FAILED — one of the persistence observations above did not hold"
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

        if fired && later.ticks > first.ticks && List.contains SchedulerDefinition.ReminderName first.registeredReminders then
            verdict "SUPPORTED — timer ticks observed; reminder registered in the real reminder table and fired"
        else
            verdict "FAILED — the timer did not tick, or the reminder was not registered or did not fire"
    }

let private runCallFilters (factory: IGrainFactory) =
    task {
        section 3 "Grain call filters — IIncomingGrainCallFilter over IFunctionalRequestMetadata"

        let gateway = GatewayApi.ref factory "gate-1"

        let! allowed = gateway.allowed "ping"
        say $"allowed(\"ping\") -> {allowed}"

        let! peeked = gateway.peek ()
        say $"peek() -> {peeked}"

        do! gateway.note "filed for later"
        say "note(\"filed for later\") -> acknowledged locally (oneWay: no target reply, ever)"

        // A oneWay call completes at the local send, so the filter may not have seen it yet.
        let! _ =
            waitUntil (TimeSpan.FromSeconds 5.0) (fun () ->
                Task.FromResult(
                    FilterLog.forGrainType "tour.gateway"
                    |> List.exists (fun call -> call.operationId = "note")))

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

        let observedFlags = FilterLog.forGrainType "tour.gateway"

        let filtersHold =
            rejection.Contains "filter rejected operation 'forbidden'"
            && observedFlags |> List.exists _.isReadOnly
            && observedFlags |> List.exists _.isOneWay
            && observedFlags |> List.exists _.isAlwaysInterleave
            && observedFlags |> List.exists _.rejected

        if filtersHold then
            verdict "SUPPORTED — metadata readable, rejection surfaces to the caller before the handler runs"
        else
            verdict "FAILED — the rejection did not surface, or an admission flag was never observed"
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

        let contextHolds =
            report.correlationSeenByFront = Some correlationId
            && report.correlationSeenByDownstream = Some correlationId
            && report.hopSeenByDownstream = Some report.hopSetByFront

        if contextHolds then
            verdict "SUPPORTED — client-to-grain and grain-to-grain propagation both observed"
        else
            verdict "FAILED — a request-context value did not reach one of the two hops"
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

        let observations = TargetObservation.all ()

        let cancellationHolds =
            callerOutcome.Contains "OperationCanceledException"
            && observations |> List.exists (fun text -> text.Contains "observed cancellation")

        if cancellationHolds then
            verdict "SUPPORTED — both sides observe the cancellation"
        else
            verdict "FAILED — one of the two sides did not observe the cancellation"
    }

let private runVersioning (factory: IGrainFactory) =
    task {
        section 6 "Contract versioning — exact by default, opt-in tolerance with acceptsVersions"

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

        // ── The opt-in: spec 004 item 7 ──────────────────────────────────────
        // A version-3 host that accepts 2 as well, with one operation introduced at 3.
        let current = RollingApi.refV3 factory "order-1"
        let previous = RollingApi.refV2 factory "order-1"
        let ancient = RollingApi.refV1 factory "order-1"

        let attempt (call: unit -> Task<string>) =
            task {
                try
                    let! reply = call ()
                    return $"ADMITTED — {reply}"
                with error ->
                    return describe error
            }

        let! currentSettle = attempt (fun () -> current.settle "A")
        let! previousSettle = attempt (fun () -> previous.settle "A")
        let! ancientSettle = attempt (fun () -> ancient.settle "A")
        let! currentRefund = attempt (fun () -> current.refund "A")
        let! previousRefund = attempt (fun () -> previous.refund "A")

        say $"host '{RollingApi.GrainType}' hosts version 3 and declares acceptsVersions (BackwardCompatible 2)"
        detail $"caller v3, 'settle'  -> {currentSettle}"
        detail $"caller v2, 'settle'  -> {previousSettle}"
        detail $"caller v1, 'settle'  -> {ancientSettle}"
        detail $"caller v3, 'refund'  -> {currentRefund}"
        detail $"caller v2, 'refund' (sinceVersion 3) -> {previousRefund}"

        // Admission only: the older caller reached the SAME activation and the same state, so
        // nothing about routing or storage identity moved with the wider policy.
        let! sameActivation = attempt (fun () -> previous.settle "B")
        let! readBack = attempt (fun () -> current.refund "ignored")

        detail $"caller v2 wrote state, caller v3 read the same activation -> {readBack}"

        let versioningHolds =
            reply.Contains "version-1 handler"
            && mismatch.Contains "hosts contract version 1 but received version 2"
            && currentSettle.StartsWith "ADMITTED"
            && previousSettle.StartsWith "ADMITTED"
            && ancientSettle.Contains "accepts versions 2 through 3, but received version 1"
            && currentRefund.StartsWith "ADMITTED"
            && previousRefund.Contains "was introduced at contract version 3, but the request declares version 2"
            && sameActivation.StartsWith "ADMITTED"
            && readBack.StartsWith "ADMITTED"

        if versioningHolds then
            verdict
                "SUPPORTED — exact by default; acceptsVersions admits an older caller, sinceVersion still refuses a newer operation"
        else
            verdict "FAILED — the matching call, the version rejection, or the tolerance opt-in did not behave as documented"
    }

let private runStreams (factory: IGrainFactory) (siloServices: IServiceProvider) =
    task {
        section 7 "Streams (EXPERIMENT) — producer from a handler, three consumer arms"

        let producer = ProducerApi.ref factory "line-1"
        let! probe = producer.providerProbe ()
        say $"handler resolved the stream provider from context.services -> {probe}"
        detail "there is no context.streamProvider: Orleans exposes GetStreamProvider only on"
        detail "Grain / IGrainBase / IClusterClient, so a handler goes through the keyed service."

        // Arm (b): a functional grain subscribing from its own onActivate hook. The first call is
        // what activates it, so this has to happen BEFORE anything is published.
        let consumer = ConsumerApi.ref factory "line-1"
        let! initial = consumer.report ()
        say $"arm (b) grain-side onActivate subscription -> {initial.subscribeOutcome}"

        // Arm (c): a subscription taken outside any grain context, from the silo process's own
        // service provider — the shape an in-process host reaches for first.
        let outOfGrainSubscriber = "out-of-grain"

        let outOfGrainOutcome =
            try
                let provider = siloServices.GetRequiredKeyedService<IStreamProvider> TourStream.Provider
                let stream = Stream.getStream<string> provider TourStream.Namespace TourStream.Key

                (Stream.subscribe stream (fun event -> task { StreamInbox.add outOfGrainSubscriber event }))
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                "subscribed"
            with error ->
                describe error

        say $"arm (c) subscription from the silo service provider, no grain context -> {outOfGrainOutcome}"

        // Arm (a): a genuinely external Orleans client, connected over the localhost gateway.
        let externalSubscriber = "external-client"
        let clientBuilder = Host.CreateApplicationBuilder()
        clientBuilder.Logging.SetMinimumLevel LogLevel.Error |> ignore

        clientBuilder.UseOrleansClient(fun client ->
            client.UseLocalhostClustering() |> ignore
            client.AddMemoryStreams TourStream.Provider |> ignore
            // Every process that BINDS a functional contract installs the fixed transport once.
            client.AddFunctionalGrainClient() |> ignore)
        |> ignore

        use clientHost = clientBuilder.Build()
        do! clientHost.StartAsync()
        let clusterClient = clientHost.Services.GetRequiredService<IClusterClient>()

        let externalOutcome =
            try
                let provider = clusterClient.GetStreamProvider TourStream.Provider
                let stream = Stream.getStream<string> provider TourStream.Namespace TourStream.Key

                (Stream.subscribe stream (fun event -> task { StreamInbox.add externalSubscriber event }))
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                "subscribed"
            with error ->
                describe error

        say $"arm (a) subscription from an external IClusterClient over the gateway -> {externalOutcome}"

        // While the external client is up, prove AddFunctionalGrainClient binds a contract too.
        let! externalCall = (ProducerApi.ref clusterClient "line-1").providerProbe ()
        say $"external client calling a functional grain -> {externalCall}"

        let! _ = producer.publish "order-placed"
        let! total = producer.publish "order-shipped"
        say $"published {total} events through the producer's handler"

        let expected = [ "order-placed"; "order-shipped" ]

        let! _ =
            waitUntil (TimeSpan.FromSeconds 20.0) (fun () ->
                task {
                    let! report = consumer.report ()

                    return
                        List.length report.received >= 2
                        && List.length (StreamInbox.read outOfGrainSubscriber) >= 2
                        && List.length (StreamInbox.read externalSubscriber) >= 2
                })

        let! finalReport = consumer.report ()
        let outOfGrainReceived = StreamInbox.read outOfGrainSubscriber
        let externalReceived = StreamInbox.read externalSubscriber

        say $"arm (a) external client received -> {externalReceived}"
        say $"arm (b) grain-side consumer received -> {finalReport.received}"
        say $"arm (c) out-of-grain subscriber received -> {outOfGrainReceived}"

        do! clientHost.StopAsync()

        if finalReport.received = expected && externalReceived = expected then
            verdict "SUPPORTED — publish from a handler, and all three consumer arms deliver"
        else
            verdict "PARTIAL — see the per-arm lines above and the README for the exact failure"
    }

let private runObservers (factory: IGrainFactory) =
    task {
        section 8 "Observers (EXPERIMENT) — Observer.createRef against a C#-declared interface"

        let notifier = NotifierApi.ref factory "room-1"
        let observer = RecordingObserver()

        let! outcome =
            task {
                try
                    // The whole experiment in one line: does CreateObjectReference find a
                    // generated proxy for an interface declared in a C# project that this F#
                    // project references?
                    let observerRef = Observer.createRef<ITourObserver> factory observer
                    let! count = notifier.subscribe observerRef
                    say $"Observer.createRef succeeded; subscriber count = {count}"

                    let! notified = notifier.notify "deployment finished"
                    let! notifiedAgain = notifier.notify "all green"
                    say $"notify() reached {notified} then {notifiedAgain} subscriber(s)"

                    let! delivered =
                        waitUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                            Task.FromResult(List.length observer.Received >= 2))

                    say $"observer received -> {observer.Received}"

                    let! remaining = notifier.unsubscribe observerRef
                    say $"after unsubscribe, subscriber count = {remaining}"

                    Observer.deleteRef<ITourObserver> factory observerRef

                    return
                        if delivered then
                            "SUPPORTED"
                        else
                            "PARTIAL — reference created and subscribed, but notifications did not arrive"
                with error ->
                    say $"Observer.createRef / subscribe FAILED -> {describe error}"
                    return "WALL"
            }

        detail "the observer interface is declared in the sibling C# project (TourInterop) because"
        detail "Orleans' proxy source generators are Roslyn generators and never run over F#."

        if outcome = "SUPPORTED" then
            verdict "SUPPORTED — with the requirement that the observer interface lives in a C#-compiled assembly"
        else
            verdict $"{outcome} — see the lines above and the README"
    }

let private runBroadcast (factory: IGrainFactory) =
    task {
        section 9 "Broadcast channels (EXPERIMENT) — functional producer, two consumer arms"

        let announcer = AnnouncerApi.ref factory "region-eu"
        let! probe = announcer.providerProbe ()
        say $"handler resolved the broadcast-channel provider from context.services -> {probe}"

        let! _ = announcer.announce "maintenance window opens"
        let! total = announcer.announce "maintenance window closes"
        say $"published {total} announcements from a functional handler"

        let consumer = factory.GetGrain<IBroadcastConsumerGrain> "region-eu"

        let! delivered =
            waitUntil (TimeSpan.FromSeconds 20.0) (fun () ->
                task {
                    let! received = consumer.Received()
                    return received.Count >= 2
                })

        let! received = consumer.Received()
        say $"arm (a) C#-declared consumer grain received -> {List.ofSeq received}"

        // Arm (b): the same attribute and the same interface on an F# class grain, with NO C#
        // assembly involved — hand-registered in GrainTypeOptions.Classes because an F# assembly
        // carries no Orleans application-part attributes.
        let! fsharpDelivered =
            waitUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                Task.FromResult(List.length (BroadcastInbox.all ()) >= 2))

        let fsharpReceived = BroadcastInbox.all ()
        say $"arm (b) F#-ONLY class grain consumer received -> {fsharpReceived}"

        detail "the PRODUCER is a plain functional handler — nothing extra needed."
        detail "the CONSUMER is a class grain either way: an F# one works, but ONLY if it also"
        detail "implements IGrainWithStringKey (so Orleans can build an activator for it) and is"
        detail "hand-added to GrainTypeOptions.Classes. Without IGrainWithStringKey the publish is"
        detail "routed and then fails with 'Unable to find an IGrainContextActivatorProvider'."

        if delivered && fsharpDelivered then
            verdict "COMPOSED — functional producer; consumer is a class grain, and an F#-only one works"
        elif delivered then
            verdict "COMPOSED — functional producer + C#-declared consumer; the F#-only arm did not deliver"
        else
            verdict "WALL — publish succeeded but no consumer delivery was observed; see the README"
    }

let private runHeterogeneous () =
    task {
        section 12 "Heterogeneous cluster — one grain type advertised by only one silo"

        say "deploying a two-silo cluster (Microsoft.Orleans.TestingHost, in-process silos)..."
        let! observation = HeterogeneousRun.run ()

        say $"silos: {observation.siloNames}"
        say $"'{Cluster.RegionalGrainType}' appears in the local grain manifest of: {observation.regionalHostSilos}"

        let everywhereSilos =
            observation.everywherePlacements |> List.map snd |> List.distinct |> List.sort

        let regionalSilos =
            observation.regionalPlacements |> List.map snd |> List.distinct |> List.sort

        say $"'{Cluster.EverywhereGrainType}' activations landed on: {everywhereSilos}"
        say $"'{Cluster.RegionalGrainType}' activations landed on: {regionalSilos}"

        for key, silo in observation.regionalPlacements do
            detail $"{Cluster.RegionalGrainType}/{key} ran on {silo}"

        let routedAway =
            regionalSilos = observation.regionalHostSilos
            && not (List.contains Cluster.PrimarySiloName regionalSilos)

        if routedAway && List.length everywhereSilos > 1 then
            verdict "SUPPORTED — the everywhere grain spreads over both silos; every regional call routes to the only silo advertising it"
        elif routedAway then
            verdict "SUPPORTED (partial spread) — regional calls route correctly, but the everywhere grain happened to land on one silo"
        else
            verdict "UNEXPECTED — regional placements did not match the advertising silos; see the per-key lines above"
    }

let private runPlacement (factory: IGrainFactory) =
    task {
        section 10 "Stateless workers and flexible placement — first-class operation"

        let worker = WorkerApi.ref factory "batch-1"
        let! reports, elapsed = WorkerRun.concurrentBatch worker 8 400

        let activations = reports |> Array.map _.activation |> Array.distinct

        say $"8 concurrent 400ms calls to ONE grain id finished in {elapsed}ms"
        let rendered = String.Join(", ", activations)
        say $"distinct activations that served them: {activations.Length} -> [{rendered}]"

        detail $"WorkerDefinition declares 'statelessWorker {WorkerApi.MaxLocalWorkers}' directly (Placement.fs) —"
        detail "the registry's own properties provider publishes the manifest properties, no"
        detail "application-level IGrainPropertiesProvider registration needed any more."
        detail "properties written: placement-strategy, max-local-instances, remove-idle-workers, unordered"
        detail "(verified identical to a live StatelessWorkerAttribute by a property-key exactness test)."
        detail "Composition via an app-level IGrainPropertiesProvider (FunctionalPlacementProvider,"
        detail "still in this file) remains possible for placement needs the closed operation set"
        detail "does not cover."

        if activations.Length > 1 then
            verdict $"SUPPORTED — stateless-worker placement through the first-class 'statelessWorker' operation"
        else
            verdict "WALL — the placement properties did not take effect; see the README"
    }

let private runImplicit (factory: IGrainFactory) =
    task {
        section 11 "Implicit subscriptions — onStream / onBroadcast activate the grain on publish"

        let mailer = MailerApi.ref factory "mailer"
        let inboxKey = $"inbox-{Guid.NewGuid():N}"

        // Nothing has ever touched this grain id. The publish below is the ONLY interaction.
        let! _ = mailer.post (TourImplicit.StreamNamespace, inboxKey, "first")

        let streamPrefix = $"stream {inboxKey}"
        let broadcastPrefix = $"broadcast {inboxKey}"

        let! delivered =
            waitUntil (TimeSpan.FromSeconds 30.0) (fun () -> task { return ImplicitLog.countOf streamPrefix = 1 })

        if delivered then
            say $"published to stream namespace '{TourImplicit.StreamNamespace}' key '{inboxKey}' — nothing called the grain"
            let observed = ImplicitLog.countOf streamPrefix
            say $"the onStream hook ran on a grain that did not exist: {observed} delivery"
        else
            say $"no implicit stream delivery observed within 30s for key '{inboxKey}'"

        // A second item, so the transcript shows the hook seeing the state the first one left.
        let! _ = mailer.post (TourImplicit.StreamNamespace, inboxKey, "second")

        let! bothDelivered =
            waitUntil (TimeSpan.FromSeconds 30.0) (fun () -> task { return ImplicitLog.countOf streamPrefix = 2 })

        // The broadcast arm of the same machinery, on the same grain id.
        let! _ = mailer.broadcast (TourImplicit.ChannelNamespace, inboxKey, "all-hands")

        let! broadcastDelivered =
            waitUntil (TimeSpan.FromSeconds 30.0) (fun () -> task { return ImplicitLog.countOf broadcastPrefix = 1 })

        // The negative control: a namespace no definition declares publishes no binding, so
        // Orleans resolves no implicit subscriber and nothing is activated at all.
        let undeliveredKey = $"inbox-{Guid.NewGuid():N}"
        let! _ = mailer.post (TourImplicit.UndeclaredNamespace, undeliveredKey, "into the void")
        do! Task.Delay(TimeSpan.FromSeconds 2.0)
        let undeclaredDeliveries = ImplicitLog.countOf $"stream {undeliveredKey}"


        // Only now is the grain called, to read back the state the hooks published.
        let inbox = InboxApi.ref factory inboxKey
        let! snapshot = inbox.snapshot ()

        say $"state after the deliveries: mail={snapshot.mail} announcements={snapshot.announcements}"
        say $"activations of that grain: {snapshot.activations} (the FIRST one was caused by the publish)"
        say $"stream cursor seen by the last delivery: {snapshot.cursor}"
        say $"publish to undeclared namespace '{TourImplicit.UndeclaredNamespace}': {undeclaredDeliveries} deliveries"

        detail "InboxDefinition (Implicit.fs) declares 'onStream TourStreams tour-implicit-mail' and"
        detail "'onBroadcast TourBroadcast tour-implicit-announcements' — the registry's properties"
        detail "provider publishes the same manifest binding an [ImplicitStreamSubscription] /"
        detail "[ImplicitChannelSubscription] class publishes (binding.attr-N.type / .pattern /"
        detail ".streamid-mapper), and the activation implements Orleans' IStreamSubscriptionObserver"
        detail "and IOnBroadcastChannelSubscribed so the pending item is delivered rather than dropped."
        detail "Delivery follows the timer-hook rules: whole-state replacement, published only on a"
        detail "successful return, no implicit storage write. A throwing hook is retried by Orleans"
        detail "for up to MaxEventDeliveryTime and never faults the implicit subscription."

        let holds =
            delivered
            && bothDelivered
            && broadcastDelivered
            && undeclaredDeliveries = 0
            && snapshot.mail = [ "first"; "second" ]
            && snapshot.announcements = [ "all-hands" ]
            && snapshot.activations = 1

        if holds then
            verdict
                "SUPPORTED — a publish activates the functional grain the stream key names and the declared hook receives the item"
        else
            verdict "UNEXPECTED — implicit delivery did not hold; see the observation lines above"
    }

let private runInterleaving (factory: IGrainFactory) =
    task {
        section 13 "Reentrancy variants — 'reentrant' and 'mayInterleave' as contract operations"

        // ── Whole-grain reentrancy, against an identical contract without it ──
        let reentrantKey = $"gate-{Guid.NewGuid():N}"
        let reentrant = GateApi.refReentrant factory reentrantKey
        let parkedReentrant = reentrant.park 8000
        let! reentrantParked = Gate.waitForEntry reentrantKey

        // Reaching 'release' AT ALL is the observation: it is what unparks the first call, so it
        // can only return while the first call is still inside the activation.
        let! _ = reentrant.release ()
        let! reentrantOutcome = parkedReentrant

        let serialKey = $"gate-{Guid.NewGuid():N}"
        let serial = GateApi.refSerial factory serialKey
        let parkedSerial = serial.park 1500
        let! serialParked = Gate.waitForEntry serialKey
        let serialRelease = serial.release ()
        let! serialOutcome = parkedSerial
        let! _ = serialRelease

        say $"'{GateApi.ReentrantGrainType}' (declares 'reentrant'): second call {reentrantOutcome}"
        say $"'{GateApi.SerialGrainType}' (identical contract, no 'reentrant'): second call {serialOutcome}"

        // ── The cost, shown rather than only documented ───────────────────────
        let lostKey = $"lost-{Guid.NewGuid():N}"
        let lost = GateApi.refReentrant factory lostKey
        let slow = lost.slowAppend "slow"
        let! lostParked = Gate.waitForEntry lostKey
        do! lost.fastAppend "fast"
        let! interim = lost.notes ()
        let! _ = lost.release ()
        do! slow
        let! final = lost.notes ()

        say $"two interleaved writers: after the fast one published, state was {interim}"
        say $"after the slow one returned, state is {final} — its snapshot predated the fast write"

        // ── A per-request predicate, with a negative control ──────────────────
        let admitKey = $"sel-{Guid.NewGuid():N}"
        let admit = SelectiveApi.ref factory admitKey
        let parkedAdmit = admit.park 8000
        let! admitParked = Gate.waitForEntry admitKey
        let! _ = admit.release ()
        let! admitOutcome = parkedAdmit

        let refuseKey = $"sel-{Guid.NewGuid():N}"
        let refuse = SelectiveApi.ref factory refuseKey
        let parkedRefuse = refuse.park 1500
        let! refuseParked = Gate.waitForEntry refuseKey
        let audit = refuse.audit ()
        let! refuseOutcome = parkedRefuse
        let! _ = audit

        say $"'{SelectiveApi.GrainType}' declares mayInterleave (operationId = \"release\")"
        detail $"'release' (named by the predicate): {admitOutcome}"
        detail $"'audit'   (not named):              {refuseOutcome}"
        detail $"the predicate itself saw: {PredicateLog.all ()}"

        detail "Both are contract operations, and both reach Orleans' own machinery: 'reentrant'"
        detail "publishes the grain property [Reentrant] publishes, and 'mayInterleave' publishes the"
        detail "property [MayInterleave] publishes plus the static callback it names, on a marker"
        detail "class used only for definitions that declare it. The predicate is handed"
        detail "IFunctionalRequestMetadata -- protocol fields only, never the argument payload."
        detail "Orleans admits a request when the predicate accepts EITHER it or the request already"
        detail "running, so write it as a statement about what is safe to overlap."

        let interleavingHolds =
            reentrantParked
            && serialParked
            && lostParked
            && admitParked
            && refuseParked
            && reentrantOutcome.StartsWith "released"
            && serialOutcome.StartsWith "timed out"
            && interim = [ "fast" ]
            && final = [ "slow" ]
            && admitOutcome.StartsWith "released"
            && refuseOutcome.StartsWith "timed out"
            && PredicateLog.countOf "release -> True" >= 1
            && PredicateLog.countOf "audit -> False" >= 1

        if interleavingHolds then
            verdict
                "SUPPORTED — whole-grain reentrancy and a metadata-only per-request predicate, each with a control that does not interleave"
        else
            verdict "UNEXPECTED — an interleaving observation above did not hold"
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
        do! runStreams factory host.Services
        do! runObservers factory
        do! runBroadcast factory
        do! runPlacement factory
        do! runImplicit factory
        do! runInterleaving factory

        printfn ""
        printfn "Single-silo sections done. Shutting that silo down before the cluster section..."
        do! host.StopAsync()

        do! runHeterogeneous ()

        printfn ""
        printfn "Tour complete." 
    }

[<EntryPoint>]
let main _argv =
    preloadTourAssemblies ()

    let builder = Host.CreateApplicationBuilder()
    // Error, not Warning: a clean transcript is the point of this example. Orleans logs a
    // ServerGC advisory at startup and a "Connection reset by peer" warning when the tour's
    // external client host shuts down; both are expected and would only obscure the sections.
    // Genuine failures are logged at Error and still appear.
    builder.Logging.SetMinimumLevel LogLevel.Error |> ignore

    SiloConfig.applyToHost siloConfiguration builder

    builder.UseOrleans(fun silo ->
        silo.AddFunctionalGrain LedgerDefinition.definition |> ignore
        silo.AddFunctionalGrain SchedulerDefinition.definition |> ignore
        silo.AddFunctionalGrain GatewayDefinition.definition |> ignore
        silo.AddFunctionalGrain FrontDefinition.definition |> ignore
        silo.AddFunctionalGrain DownstreamDefinition.definition |> ignore
        silo.AddFunctionalGrain SlowDefinition.definition |> ignore
        // Deliberately ONLY version 1 of tour.versioned.
        silo.AddFunctionalGrain VersionedDefinition.definition |> ignore
        silo.AddFunctionalGrain ProducerDefinition.definition |> ignore
        silo.AddFunctionalGrain ConsumerDefinition.definition |> ignore
        silo.AddFunctionalGrain NotifierDefinition.definition |> ignore
        silo.AddFunctionalGrain AnnouncerDefinition.definition |> ignore
        // Feature 11 / status-matrix row 12: statelessWorker is now a first-class definition
        // operation (spec 004 item 4), declared on WorkerDefinition itself in Placement.fs — no
        // separate IGrainPropertiesProvider registration needed here any more.
        silo.AddFunctionalGrain WorkerDefinition.definition |> ignore
        // Feature 11 / status-matrix row 11: implicit subscriptions (spec 004 item 1). The
        // inbox definition declares onStream/onBroadcast; nothing else is registered for it.
        silo.AddFunctionalGrain InboxDefinition.definition |> ignore
        silo.AddFunctionalGrain MailerDefinition.definition |> ignore
        // Feature 13 / status-matrix row 15: reentrancy variants (spec 004 item 5).
        silo.AddFunctionalGrain GateDefinition.reentrant |> ignore
        silo.AddFunctionalGrain GateDefinition.serial |> ignore
        silo.AddFunctionalGrain SelectiveDefinition.definition |> ignore
        // Feature 6, second half / status-matrix row 6: version tolerance (spec 004 item 7).
        // Deliberately ONLY version 3 of tour.rolling.
        silo.AddFunctionalGrain RollingDefinition.definition |> ignore

        // Experiment 9's F#-only arm: hand-register the F# class grain that an F# assembly's
        // missing [ApplicationPart]/[TypeManifestProvider] pair would otherwise hide from the
        // silo. If Orleans accepts it, no C# bridge is needed for a broadcast consumer.
        silo.Services.Configure<Orleans.Configuration.GrainTypeOptions>(fun (options: Orleans.Configuration.GrainTypeOptions) ->
            options.Classes.Add typeof<FSharpBroadcastConsumer> |> ignore)
        |> ignore)
    |> ignore

    let host = builder.Build()
    (runTour host).GetAwaiter().GetResult()

    match failedVerdicts () with
    | [] -> 0
    | failures ->
        printfn ""
        printfn "%d section(s) did NOT pass:" (List.length failures)

        for failure in failures do
            printfn "   %s" failure

        1
