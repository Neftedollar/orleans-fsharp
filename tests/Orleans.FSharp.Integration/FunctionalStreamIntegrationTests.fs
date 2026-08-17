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

        test <@ groups = [ "binding.attr-1"; "binding.attr-2"; "binding.attr-3" ] @>
        test <@ bindingValue "binding.attr-1.type" = "stream" @>
        test <@ bindingValue "binding.attr-1.pattern" = $"namespace:{StreamNames.Items}" @>
        test <@ bindingValue "binding.attr-2.type" = "stream" @>
        test <@ bindingValue "binding.attr-2.pattern" = $"namespace:{StreamNames.Numbers}" @>
        test <@ bindingValue "binding.attr-3.type" = "broadcast-channel" @>
        test <@ bindingValue "binding.attr-3.channel-pattern" = $"namespace:{StreamNames.Channel}" @>

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
