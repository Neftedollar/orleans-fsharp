/// <summary>
/// Spec 004 item 1, end to end: an item published to a declared namespace activates the
/// functional grain whose identity the stream key names, and reaches the declared hook.
/// </summary>
module Orleans.FSharp.Integration.FunctionalStreamIntegrationTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Metadata
open Orleans.Runtime
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalStreamFixture
open Swensen.Unquote
open Xunit

/// <summary>A key nothing else in the suite uses, so deliveries never cross tests.</summary>
let private freshKey (prefix: string) = $"{prefix}-{Guid.NewGuid():N}"

/// <summary>Every exception in a fault tree, however many aggregates it is wrapped in.</summary>
let rec private flatten (error: exn) : exn list =
    match error with
    | null -> []
    | :? AggregateException as aggregate ->
        aggregate :: (aggregate.InnerExceptions |> Seq.collect flatten |> List.ofSeq)
    | _ -> error :: flatten error.InnerException

let private oneSecond = TimeSpan.FromSeconds 1.0
let private deliveryTimeout = TimeSpan.FromSeconds 30.0

// ──────────────────────────────────────────────────────────────────────────────
// Single silo
// ──────────────────────────────────────────────────────────────────────────────

[<Collection("FunctionalStreamSingleSilo")>]
type SingleSiloTests(fixture: FunctionalStreamSingleSiloFixture) =

    /// <remarks>
    /// The Step-0 gate: nothing here calls the grain first. Publishing the item is the ONLY
    /// interaction, so a green assertion means Orleans resolved the functional grain type as an
    /// implicit subscriber from the published manifest binding, activated it, and the activation
    /// accepted the delivery into application code.
    /// </remarks>
    [<Fact>]
    member _.``an item published to a declared namespace activates the grain and reaches the hook``() =
        task {
            let key = freshKey "single"
            let probe = $"{StreamNames.Items}|{key}"

            do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, "hello")

            do!
                fixture.WaitFor(
                    "the implicit delivery to reach the onStream hook",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            let grain = sinkRef fixture.Client key
            let! items = grain.items ()
            test <@ items = [ "hello" ] @>
        }

    /// <remarks>
    /// The published binding names a namespace, so a second declared namespace on the same
    /// provider needs its own binding group and its own hook — with its own item type.
    /// </remarks>
    [<Fact>]
    member _.``a second declared namespace routes to its own hook at its own item type``() =
        task {
            let key = freshKey "second-ns"

            do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, "text")
            do! fixture.Publish(StreamNames.Provider, StreamNames.Numbers, key, 41)

            do!
                fixture.WaitFor(
                    "both declared namespaces to deliver",
                    deliveryTimeout,
                    fun () ->
                        StreamProbe.count $"{StreamNames.Items}|{key}" = 1
                        && StreamProbe.count $"{StreamNames.Numbers}|{key}" = 1
                )

            let grain = sinkRef fixture.Client key
            let! items = grain.items ()
            let! numbers = grain.numbers ()
            test <@ items = [ "text" ] @>
            test <@ numbers = [ 41 ] @>
        }

    /// <remarks>
    /// Every delivery publishes its returned state exactly like a handler return, so a
    /// three-item sequence accumulates in order rather than each delivery seeing initial state.
    /// </remarks>
    [<Fact>]
    member _.``successive deliveries see the state the previous delivery published``() =
        task {
            let key = freshKey "accumulate"
            let probe = $"{StreamNames.Items}|{key}"

            for item in [ "a"; "b"; "c" ] do
                do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, item)

            do!
                fixture.WaitFor(
                    "three deliveries",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 3
                )

            let grain = sinkRef fixture.Client key
            let! items = grain.items ()
            test <@ items = [ "a"; "b"; "c" ] @>
        }

    /// <remarks>
    /// The negative half of the binding: a namespace no definition declares publishes no binding
    /// group, so Orleans finds no implicit subscriber, activates nothing, and no hook runs. The
    /// assertion is on the probe rather than on grain state, because reading grain state would
    /// itself activate the grain.
    /// </remarks>
    [<Fact>]
    member _.``an undeclared namespace delivers to nothing``() =
        task {
            let key = freshKey "unsubscribed"

            do! fixture.Publish(StreamNames.Provider, StreamNames.Unsubscribed, key, "ignored")
            // A declared-namespace publish on the same key, awaited to completion, is the
            // synchronisation point: once IT has been delivered, the undeclared one has had at
            // least as long to arrive and demonstrably did not.
            do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, "delivered")

            do!
                fixture.WaitFor(
                    "the declared namespace to deliver",
                    deliveryTimeout,
                    fun () -> StreamProbe.count $"{StreamNames.Items}|{key}" = 1
                )

            test <@ StreamProbe.count $"{StreamNames.Unsubscribed}|{key}" = 0 @>

            let grain = sinkRef fixture.Client key
            let! items = grain.items ()
            test <@ items = [ "delivered" ] @>
        }

    /// <remarks>
    /// Orleans' implicit-subscription binding names a namespace and NOT a provider, so a
    /// declared namespace published on an undeclared provider still routes to this grain type.
    /// The runtime matches (provider, namespace) and leaves such an item undelivered — this test
    /// pins that decision, which is the one place the runtime is deliberately stricter than the
    /// binding it publishes.
    /// </remarks>
    [<Fact>]
    member _.``a declared namespace on an undeclared provider does not reach the hook``() =
        task {
            let key = freshKey "other-provider"

            do! fixture.Publish(StreamNames.OtherProvider, StreamNames.Items, key, "wrong-provider")
            do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, "right-provider")

            do!
                fixture.WaitFor(
                    "the declared provider to deliver",
                    deliveryTimeout,
                    fun () -> StreamProbe.count $"{StreamNames.Items}|{key}" >= 1
                )

            // Give the wrong-provider item the same wall-clock budget once more before asserting.
            do! Task.Delay oneSecond

            let grain = sinkRef fixture.Client key
            let! items = grain.items ()
            test <@ items = [ "right-provider" ] @>
        }

    /// <remarks>
    /// What stock Orleans does with a failing implicit delivery, measured rather than assumed:
    /// <c>PersistentStreamPullingAgent</c> retries the same item with backoff for up to
    /// <c>MaxEventDeliveryTime</c>, and its <c>ErrorProtocol</c> never faults an implicit
    /// subscription — so the retry eventually succeeds and later items still arrive.
    /// </remarks>
    [<Fact>]
    member _.``a throwing hook is retried by Orleans and the subscription survives``() =
        task {
            let key = freshKey "poison"
            let probe = $"{StreamNames.Poison}|{key}"
            StreamProbe.poison.Arm("retry-me", 1)

            do! fixture.Publish(StreamNames.Provider, StreamNames.Poison, key, "retry-me")

            do!
                fixture.WaitFor(
                    "the retried delivery to succeed",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            // Entered at least twice: once for the throw, once for the retry that succeeded.
            test <@ StreamProbe.attempts.["retry-me"] >= 2 @>

            // The state of the failed attempt was never published: only the accepted item is there.
            let grain = poisonRef fixture.Client key
            let! accepted = grain.accepted ()
            test <@ accepted = [ "retry-me" ] @>

            // The implicit subscription is not faulted by the failure: the next item arrives.
            do! fixture.Publish(StreamNames.Provider, StreamNames.Poison, key, "after-failure")

            do!
                fixture.WaitFor(
                    "the item after the failure",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 2
                )

            let! acceptedAfter = grain.accepted ()
            test <@ acceptedAfter = [ "retry-me"; "after-failure" ] @>
        }

    /// <remarks>
    /// The broadcast arm of the same machinery: <c>IOnBroadcastChannelSubscribed</c> instead of
    /// <c>IStreamSubscriptionObserver</c>, a channel binding instead of a stream binding, and the
    /// grain activated by the publish itself.
    /// </remarks>
    [<Fact>]
    member _.``a broadcast publish activates the grain and reaches the onBroadcast hook``() =
        task {
            let key = freshKey "channel"
            let probe = $"{StreamNames.Channel}|{key}"

            do! fixture.PublishChannel(StreamNames.Channel, key, "broadcast")

            do!
                fixture.WaitFor(
                    "the implicit channel delivery to reach the onBroadcast hook",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            let grain = sinkRef fixture.Client key
            let! items = grain.channelItems ()
            test <@ items = [ "broadcast" ] @>
        }

    /// <remarks>
    /// The observer this runtime attaches lives on the ACTIVATION, inside Orleans'
    /// per-activation <c>StreamConsumerExtension</c> — so a deactivation throws it away. Nothing
    /// re-registers it on the way back: the next item simply finds an extension with no observer
    /// again and Orleans calls <c>OnSubscribed</c> again. This test is what proves that round trip
    /// rather than assuming it, and it also pins that the ephemeral state really restarts.
    /// </remarks>
    [<Fact>]
    member _.``an implicit delivery re-activates a grain that has since been deactivated``() =
        task {
            let key = freshKey "reactivate"
            let probe = $"{StreamNames.Items}|{key}"

            do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, "before")

            do!
                fixture.WaitFor(
                    "the first implicit delivery",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            let grain = sinkRef fixture.Client key
            let! before = grain.items ()
            test <@ before = [ "before" ] @>

            do! grain.goIdle ()

            do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, "after")

            do!
                fixture.WaitFor(
                    "the delivery that had to re-activate the grain",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 2
                )

            // Ephemeral state: the fresh activation started from the initializer, so the earlier
            // item is gone. That is what makes this a genuine re-activation and not a survivor.
            let! after = grain.items ()
            test <@ after = [ "after" ] @>
        }

    /// <remarks>
    /// The cluster-wide regression guard. Orleans' <c>StreamConsumerGrainContextAction</c> binds a
    /// <c>StreamConsumerExtension</c> to every activation whose instance implements
    /// <c>IStreamSubscriptionObserver</c>, and <c>SiloStreamProviderRuntime.BindExtension</c>
    /// throws "cannot be bound to a Stateless Worker" — so had the functional target implemented
    /// that interface unconditionally, every stateless-worker functional grain on a streaming silo
    /// would have stopped activating. This grain declares no implicit subscription and is hosted
    /// on the same streaming silo as the ones that do.
    /// </remarks>
    [<Fact>]
    member _.``a stateless-worker functional grain still activates on a streaming silo``() =
        task {
            let worker = workerRef fixture.Client "worker-1"
            let! first = worker.work ()
            let! second = worker.work ()

            test <@ first <> "" @>
            test <@ second <> "" @>
        }

    /// <remarks>
    /// <para>
    /// The silent-loss path Orleans opens on the broadcast side, and its fix, in Orleans' OWN
    /// default delivery mode. An item whose runtime type is not the hook's is never routed to the
    /// hook: <c>BroadcastChannelConsumerExtension.Callback&lt;T&gt;.OnPublished</c> sends it to
    /// the subscription's ERROR callback as an <c>InvalidCastException</c>. Completing that
    /// callback quietly — the obvious thing to write — lets the extension go on to
    /// <c>EmitItemDelivered</c>, so the item vanishes with NO signal anywhere: not a fault, not
    /// even a log. The runtime faults that callback instead.
    /// </para>
    /// <para>
    /// <c>BroadcastChannelOptions.FireAndForgetDelivery</c> defaults to <b>true</b>, so in this
    /// mode <c>BroadcastChannelWriter.PublishToSubscriber</c> swallows the fault after logging it
    /// at <c>Error</c> — that log is the whole signal, and this test is what proves it exists.
    /// The awaited mode is the next test.
    /// </para>
    /// </remarks>
    [<Fact>]
    member _.``a wrong-typed broadcast item is logged rather than silently dropped (fire-and-forget)``() =
        task {
            let key = freshKey "channel-mistyped-faf"
            let probe = $"{StreamNames.Channel}|{key}"

            // The declared onBroadcast hook of this namespace takes a string; publish an int.
            // Orleans' default mode does not surface it to the publisher, so this must NOT throw.
            do! fixture.PublishChannel<int>(StreamNames.Channel, key, 7)

            do!
                fixture.WaitFor(
                    "the silo to log the failed fire-and-forget delivery",
                    deliveryTimeout,
                    fun () ->
                        FunctionalClusterFixture.LogCapture.entries
                        |> Seq.exists (fun entry ->
                            entry.Category.Contains "BroadcastChannelWriter"
                            && entry.Error.Contains "Int32"
                            && entry.Error.Contains "System.String")
                )

            // The hook was never entered, and nothing was published into the state.
            test <@ StreamProbe.count probe = 0 @>

            // The subscription survives: a correctly-typed publish on the same channel arrives.
            do! fixture.PublishChannel(StreamNames.Channel, key, "well-typed")

            do!
                fixture.WaitFor(
                    "the correctly-typed publish after the mismatch",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            let grain = sinkRef fixture.Client key
            let! items = grain.channelItems ()
            test <@ items = [ "well-typed" ] @>
        }

    /// <remarks>
    /// The same mismatch on a provider configured with <c>FireAndForgetDelivery = false</c>, where
    /// <c>BroadcastChannelWriter.PublishToSubscriber</c> rethrows and <c>Publish</c> collects the
    /// subscriber faults into an <c>AggregateException</c>. This is the half that makes the fix
    /// observable to application code rather than only to a log reader.
    /// </remarks>
    [<Fact>]
    member _.``a wrong-typed broadcast item faults the publish when delivery is awaited``() =
        task {
            let key = freshKey "channel-mistyped-awaited"
            let probe = $"{StreamNames.AwaitedChannel}|{key}"

            let! error =
                Assert.ThrowsAnyAsync<exn>(fun () ->
                    fixture.PublishChannelOn<int>(
                        StreamNames.AwaitedChannelProvider,
                        StreamNames.AwaitedChannel,
                        key,
                        7
                    )
                    :> Task)

            let messages = flatten error |> List.map (fun entry -> entry.Message)

            // The mismatch reaches the publisher, and it names BOTH types.
            test <@ messages |> List.exists (fun message -> message.Contains "Int32") @>
            test <@ messages |> List.exists (fun message -> message.Contains "System.String") @>

            test <@ StreamProbe.count probe = 0 @>

            // The subscription survives the fault.
            do!
                fixture.PublishChannelOn(
                    StreamNames.AwaitedChannelProvider,
                    StreamNames.AwaitedChannel,
                    key,
                    "well-typed"
                )

            do!
                fixture.WaitFor(
                    "the correctly-typed publish after the fault",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            let grain = sinkRef fixture.Client key
            let! items = grain.channelItems ()
            test <@ items = [ "well-typed" ] @>
        }

    /// <remarks>
    /// The trap this feature could most easily have shipped with, and its fix, both measured.
    /// Orleans routes an implicit delivery to <c>GrainId.Create(grainType, streamId.Key)</c> —
    /// the stream key bytes verbatim — so the stream key must be the grain key in the
    /// CONTRACT's encoding. For an <c>int64Key</c> contract that is Orleans'
    /// <c>GrainIdKeyExtensions.CreateIntegerKey</c> HEXADECIMAL form, while
    /// <c>StreamId.Create(ns, 42L)</c> writes DECIMAL. The naive publish therefore lands on a
    /// different grain — silently, and one whose key decodes as 0x42 = 66.
    /// <c>FunctionalGrain.streamId</c> asks the contract instead, so it cannot drift.
    /// </remarks>
    [<Fact>]
    member _.``FunctionalGrain.streamId addresses the contract's own key encoding``() =
        task {
            // 0x2A = 42 decimal; the two encodings differ for every value above 9.
            let key = 42L
            let decoyKey = 0x42L // what the naive decimal StreamId.Create(ns, 42L) really addresses

            do!
                fixture.PublishTo(
                    StreamNames.Provider,
                    FunctionalGrain.streamId counterContract StreamNames.Counters key,
                    7
                )

            do!
                fixture.WaitFor(
                    "the correctly-keyed delivery",
                    deliveryTimeout,
                    fun () -> StreamProbe.count $"{StreamNames.Counters}|{key}" = 1
                )

            let grain = counterRef fixture.Client key
            let! seen = grain.seen ()
            test <@ seen = [ 7 ] @>

            // The naive overload: same namespace, same numeric key, different bytes.
            do! fixture.PublishTo(StreamNames.Provider, StreamId.Create(StreamNames.Counters, key), 9)

            do!
                fixture.WaitFor(
                    "the naively-keyed delivery to land somewhere",
                    deliveryTimeout,
                    fun () -> StreamProbe.count $"{StreamNames.Counters}|{decoyKey}" = 1
                )

            // It did NOT reach grain 42 — it reached grain 66, the hex reading of "42".
            let! stillSeven = grain.seen ()
            test <@ stillSeven = [ 7 ] @>

            let decoy = counterRef fixture.Client decoyKey
            let! decoySeen = decoy.seen ()
            test <@ decoySeen = [ 9 ] @>
        }

    /// <remarks>
    /// The manifest half, read back off the live silo: the grain manifest of a definition with
    /// two stream declarations and one channel declaration carries three binding groups under
    /// the keys Orleans' own <c>AttributeGrainBindingsProvider</c> writes, and a definition with
    /// no declaration carries none.
    /// </remarks>
    [<Fact>]
    member _.``the live grain manifest carries one binding group per declared namespace``() =
        let services = fixture.Cluster.GetSiloServiceProvider fixture.Cluster.Primary.SiloAddress

        let manifest =
            services.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

        let properties =
            manifest.Grains.[GrainType.Create StreamGrainTypes.Sink].Properties

        let bindingValue (key: string) =
            match properties.TryGetValue key with
            | true, value -> value
            | _ -> failwith $"the manifest carries no property '{key}'"

        let groups =
            properties.Keys
            |> Seq.filter (fun key -> key.StartsWith("binding.", StringComparison.Ordinal))
            |> Seq.map (fun key -> key.Substring(0, key.IndexOf('.', "binding.".Length)))
            |> Seq.distinct
            |> Seq.sort
            |> Seq.toList

        test <@ groups = [ "binding.attr-1"; "binding.attr-2"; "binding.attr-3"; "binding.attr-4" ] @>
        test <@ bindingValue "binding.attr-1.type" = "stream" @>
        test <@ bindingValue "binding.attr-1.pattern" = $"namespace:{StreamNames.Items}" @>
        test <@ bindingValue "binding.attr-2.type" = "stream" @>
        test <@ bindingValue "binding.attr-2.pattern" = $"namespace:{StreamNames.Numbers}" @>
        test <@ bindingValue "binding.attr-3.type" = "broadcast-channel" @>
        test <@ bindingValue "binding.attr-3.channel-pattern" = $"namespace:{StreamNames.Channel}" @>
        test <@ bindingValue "binding.attr-4.type" = "broadcast-channel" @>
        test <@ bindingValue "binding.attr-4.channel-pattern" = $"namespace:{StreamNames.AwaitedChannel}" @>

// ──────────────────────────────────────────────────────────────────────────────
// Two silos
// ──────────────────────────────────────────────────────────────────────────────

[<Collection("FunctionalStreamCluster")>]
type ClusterTests(fixture: FunctionalStreamClusterFixture) =

    /// <remarks>
    /// The same delivery across a real silo boundary. The pulling agents are spread over both
    /// silos by the consistent-ring balancer and Orleans places the implicitly activated grain
    /// wherever it likes, so the delivery generally crosses silos; the test asserts the item
    /// arrived and reports which silo ran the hook rather than pinning one.
    /// </remarks>
    [<Fact>]
    member _.``implicit delivery reaches the grain on whichever silo Orleans places it``() =
        task {
            let key = freshKey "cluster"
            let probe = $"{StreamNames.Items}|{key}"

            do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, "across-silos")

            do!
                fixture.WaitFor(
                    "the implicit delivery to reach the onStream hook",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            let hostingSilo = StreamProbe.silos.[probe]
            let siloNames = fixture.Cluster.Silos |> Seq.map (fun silo -> silo.Name) |> Seq.toList
            test <@ List.contains hostingSilo siloNames @>

            let grain = sinkRef fixture.Client key
            let! items = grain.items ()
            test <@ items = [ "across-silos" ] @>
        }

    /// <remarks>
    /// Many keys at once: every one of them must reach its own activation, which is the property
    /// that makes implicit subscriptions worth having (one stream key per entity, no explicit
    /// subscribe step anywhere).
    /// </remarks>
    [<Fact>]
    member _.``every stream key reaches its own activation``() =
        task {
            let prefix = freshKey "fanout"
            let keys = [ for index in 1..8 -> $"{prefix}-{index}" ]

            for key in keys do
                do! fixture.Publish(StreamNames.Provider, StreamNames.Items, key, $"item-{key}")

            do!
                fixture.WaitFor(
                    "every key to deliver",
                    deliveryTimeout,
                    fun () -> keys |> List.forall (fun key -> StreamProbe.count $"{StreamNames.Items}|{key}" = 1)
                )

            for key in keys do
                let grain = sinkRef fixture.Client key
                let! items = grain.items ()
                test <@ items = [ $"item-{key}" ] @>
        }

    /// <remarks>
    /// The binding has to survive cluster-manifest GOSSIP, not just local publication. It carries
    /// a <b>null</b> <c>streamid-mapper</c> value (exactly what an undecorated
    /// <c>[ImplicitStreamSubscription]</c> publishes), and
    /// <c>ImplicitStreamSubscriberTable.BuildCache</c> throws
    /// <c>KeyNotFoundException("… is missing a "streamid-mapper" value")</c> if the KEY is
    /// absent — so a gossip path that dropped null-valued properties would break implicit
    /// delivery on every silo but the one that published the binding. This asserts the key is
    /// present in every silo's view of every silo's grain manifest.
    /// </remarks>
    [<Fact>]
    member _.``the stream binding survives cluster-manifest propagation to every silo``() =
        let expectedKeys =
            [ "binding.attr-1.type"
              "binding.attr-1.pattern"
              "binding.attr-1.streamid-mapper"
              "binding.attr-2.type"
              "binding.attr-2.pattern"
              "binding.attr-2.streamid-mapper"
              "binding.attr-3.type"
              "binding.attr-3.channel-pattern"
              "binding.attr-3.channelid-mapper"
              "binding.attr-4.type"
              "binding.attr-4.channel-pattern"
              "binding.attr-4.channelid-mapper" ]

        let manifests = fixture.ClusterManifests
        test <@ List.length manifests = 2 @>

        for observingSilo, manifest in manifests do
            test <@ manifest.Silos.Count = 2 @>

            for pair in manifest.Silos do
                let hostingSilo = string pair.Key

                let properties =
                    pair.Value.Grains.[GrainType.Create StreamGrainTypes.Sink].Properties

                let missing =
                    expectedKeys |> List.filter (fun key -> not (properties.ContainsKey key))

                // Names both silos in the failure message: which view lost which key matters.
                test <@ (observingSilo, hostingSilo, missing) = (observingSilo, hostingSilo, []) @>

                // The mapper value really is null — the exact shape the reference attribute
                // publishes — and it round-tripped through gossip as such.
                test <@ isNull properties.["binding.attr-1.streamid-mapper"] @>

    /// <remarks>The broadcast arm on the two-silo cluster.</remarks>
    [<Fact>]
    member _.``a broadcast publish reaches the grain across the cluster``() =
        task {
            let key = freshKey "cluster-channel"
            let probe = $"{StreamNames.Channel}|{key}"

            do! fixture.PublishChannel(StreamNames.Channel, key, "cluster-broadcast")

            do!
                fixture.WaitFor(
                    "the implicit channel delivery",
                    deliveryTimeout,
                    fun () -> StreamProbe.count probe = 1
                )

            let grain = sinkRef fixture.Client key
            let! items = grain.channelItems ()
            test <@ items = [ "cluster-broadcast" ] @>
        }
