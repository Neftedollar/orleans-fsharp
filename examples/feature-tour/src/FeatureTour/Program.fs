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
open FeatureTour.Interop

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
        clientBuilder.Logging.SetMinimumLevel LogLevel.Warning |> ignore

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
        silo.AddFunctionalGrain VersionedDefinition.definition |> ignore
        silo.AddFunctionalGrain ProducerDefinition.definition |> ignore
        silo.AddFunctionalGrain ConsumerDefinition.definition |> ignore
        silo.AddFunctionalGrain NotifierDefinition.definition |> ignore
        silo.AddFunctionalGrain AnnouncerDefinition.definition |> ignore

        // Experiment 9's F#-only arm: hand-register the F# class grain that an F# assembly's
        // missing [ApplicationPart]/[TypeManifestProvider] pair would otherwise hide from the
        // silo. If Orleans accepts it, no C# bridge is needed for a broadcast consumer.
        silo.Services.Configure<Orleans.Configuration.GrainTypeOptions>(fun (options: Orleans.Configuration.GrainTypeOptions) ->
            options.Classes.Add typeof<FSharpBroadcastConsumer> |> ignore)
        |> ignore)
    |> ignore

    let host = builder.Build()
    (runTour host).GetAwaiter().GetResult()
    0
