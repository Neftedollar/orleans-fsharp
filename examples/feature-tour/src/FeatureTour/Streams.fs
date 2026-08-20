/// <summary>
/// Experiment 7 — Orleans streams from the functional runtime: a producer that resolves the
/// stream provider out of <c>context.services</c>, a grain-side consumer that subscribes from
/// <c>onActivate</c>, and an out-of-grain subscription attempt for comparison.
/// </summary>
namespace FeatureTour.Streams

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.Streams
open Orleans.FSharp
open Orleans.FSharp.Streaming

/// <summary>Stream identifiers shared by the producer, the consumer and the driver.</summary>
[<RequireQualifiedAccess>]
module TourStream =
    /// <summary>The memory stream provider registered on the silo.</summary>
    [<Literal>]
    let Provider = "TourStreams"

    /// <summary>The stream namespace the tour publishes on.</summary>
    [<Literal>]
    let Namespace = "tour-events"

    /// <summary>The single stream key the tour uses.</summary>
    [<Literal>]
    let Key = "line-1"

/// <summary>
/// Where a stream callback puts what it received.
/// </summary>
/// <remarks>
/// A stream callback fires outside the handler pipeline, so it has no state to return — the
/// functional runtime's whole-state-replacement contract does not extend to it. An out-of-band
/// collector keyed by subscriber name is the honest shape; putting a mutable buffer inside the
/// grain's state record would violate the immutable-state guidance the runtime relies on.
/// </remarks>
[<RequireQualifiedAccess>]
module StreamInbox =
    let private inboxes = ConcurrentDictionary<string, ConcurrentQueue<string>>()

    /// <summary>Append one received event for the named subscriber.</summary>
    let add (subscriber: string) (event: string) =
        (inboxes.GetOrAdd(subscriber, fun _ -> ConcurrentQueue<string>())).Enqueue event

    /// <summary>Everything the named subscriber has received, in arrival order.</summary>
    let read (subscriber: string) =
        match inboxes.TryGetValue subscriber with
        | true, queue -> queue |> List.ofSeq
        | _ -> []

// ── Producer ─────────────────────────────────────────────────────────────────

type ProducerActor = private ProducerActor of unit

[<NoEquality; NoComparison>]
type ProducerApi =
    { /// Publishes one event onto the tour's stream and replies with the running count.
      publish: string -> Task<int>
      /// Reports how the provider lookup went, without publishing.
      providerProbe: unit -> Task<string> }

[<RequireQualifiedAccess>]
module ProducerApi =
    let contract =
        grainContract<ProducerActor, string, ProducerApi> {
            grainType "tour.stream.producer"
            version 1
            stringKey

            readOnly (_.providerProbe)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module ProducerDefinition =

    /// <summary>
    /// The reach a functional handler has to a named stream provider. There is no
    /// <c>context.streamProvider</c>: Orleans only exposes <c>GetStreamProvider</c> as an
    /// extension on <c>Grain</c> / <c>IGrainBase</c> / <c>IClusterClient</c>, none of which a
    /// functional handler is. The provider is a KEYED service, so <c>context.services</c>
    /// reaches it directly.
    /// </summary>
    let private streamProvider (services: IServiceProvider) =
        services.GetRequiredKeyedService<IStreamProvider> TourStream.Provider

    let definition =
        grainFor ProducerApi.contract {
            defaultState (fun () -> 0)

            handle
                (_.publish)
                (fun context state text ->
                    task {
                        let provider = streamProvider context.services
                        let stream = Stream.getStream<string> provider TourStream.Namespace TourStream.Key
                        do! Stream.publish stream text
                        return state + 1, state + 1
                    })

            handle
                (_.providerProbe)
                (fun context state () ->
                    task {
                        let provider = streamProvider context.services
                        return state, $"{provider.GetType().Name} named '{provider.Name}'"
                    })
        }

// ── Grain-side consumer ──────────────────────────────────────────────────────

type ConsumerActor = private ConsumerActor of unit

/// <summary>What the grain-side consumer reports about its own subscription.</summary>
type ConsumerReport =
    { subscribeOutcome: string
      received: string list }

[<NoEquality; NoComparison>]
type ConsumerApi =
    { /// Activates the consumer (which subscribes) and reports what it has seen.
      report: unit -> Task<ConsumerReport> }

[<RequireQualifiedAccess>]
module ConsumerApi =
    let contract =
        grainContract<ConsumerActor, string, ConsumerApi> {
            grainType "tour.stream.consumer"
            version 1
            stringKey

            readOnly (_.report)
        }

    let ref = FunctionalGrain.ref contract

    /// <summary>The inbox name the grain-side consumer writes to.</summary>
    [<Literal>]
    let Subscriber = "grain-side"

[<RequireQualifiedAccess>]
module ConsumerDefinition =

    let definition =
        grainFor ConsumerApi.contract {
            defaultState (fun () -> "onActivate has not run")

            // The experiment: can a functional activation take a real, explicit stream
            // subscription from its lifecycle hook? Stream extensions bind to the stock
            // IGrainContext, which a functional activation has — but "should" is not "does",
            // so the outcome is captured either way and printed by the driver.
            onActivate (fun context _state ->
                task {
                    try
                        let provider =
                            context.services.GetRequiredKeyedService<IStreamProvider> TourStream.Provider

                        let stream = Stream.getStream<string> provider TourStream.Namespace TourStream.Key

                        let! _subscription =
                            Stream.subscribe stream (fun event ->
                                task { StreamInbox.add ConsumerApi.Subscriber event })

                        return "subscribed from onActivate"
                    with error ->
                        return $"subscribe FAILED: {error.GetType().Name}: {error.Message}"
                })

            handle
                (_.report)
                (fun _context state () ->
                    task {
                        return
                            state,
                            { subscribeOutcome = state
                              received = StreamInbox.read ConsumerApi.Subscriber }
                    })
        }
