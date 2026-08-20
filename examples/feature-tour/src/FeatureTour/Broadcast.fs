/// <summary>
/// Experiment 9 — broadcast channels: an F# functional handler as the producer, and the C#
/// implicit-subscription grain from the sibling interop project as the consumer.
/// </summary>
namespace FeatureTour.Broadcast

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.BroadcastChannel
open FeatureTour.Interop
open Orleans.FSharp
open Orleans.FSharp.BroadcastChannel

/// <summary>Out-of-band collector for the F#-only consumer arm (it has no reply path).</summary>
[<RequireQualifiedAccess>]
module BroadcastInbox =
    let private received = ConcurrentQueue<string>()

    /// <summary>Record one delivered message.</summary>
    let add (message: string) = received.Enqueue message

    /// <summary>Everything delivered so far, in arrival order.</summary>
    let all () = received |> List.ofSeq

/// <summary>
/// The F#-ONLY consumer arm of experiment 9: an ordinary F# class grain carrying
/// <c>[&lt;ImplicitChannelSubscription&gt;]</c> and implementing
/// <c>IOnBroadcastChannelSubscribed</c>, with no C# bridge assembly anywhere.
/// </summary>
/// <remarks>
/// The open question is discovery, not code generation. <c>IOnBroadcastChannelSubscribed</c> is
/// declared and code-generated inside Orleans' own assembly, so its proxy already exists; what
/// an F# assembly lacks is the <c>[ApplicationPart]</c> / <c>[TypeManifestProvider]</c> pair
/// Orleans' Roslyn generators would have emitted, so nothing ever tells the silo this class is a
/// grain. The tour registers it by hand through <c>GrainTypeOptions.Classes</c> and reports what
/// actually happens.
/// </remarks>
[<ImplicitChannelSubscription(TourChannels.Namespace, null)>]
type FSharpBroadcastConsumer() =
    inherit Grain()

    // IGrainWithStringKey is declared (and code-generated) inside Orleans itself, so implementing
    // it needs no C# project of ours — it is what tells Orleans this class is an addressable
    // grain at all.
    interface IGrainWithStringKey

    interface IOnBroadcastChannelSubscribed with
        member _.OnSubscribed(subscription: IBroadcastChannelSubscription) =
            subscription.Attach<string>(
                Func<string, Task>(fun item ->
                    BroadcastInbox.add item
                    Task.CompletedTask),
                Func<Exception, Task>(fun error ->
                    BroadcastInbox.add $"error: {error.Message}"
                    Task.CompletedTask)
            )

type AnnouncerActor = private AnnouncerActor of unit

[<NoEquality; NoComparison>]
type AnnouncerApi =
    { /// Publishes one announcement to every subscriber of the channel.
      announce: string -> Task<int>
      /// Reports how the channel-provider lookup went, without publishing.
      providerProbe: unit -> Task<string> }

[<RequireQualifiedAccess>]
module AnnouncerApi =
    let contract =
        grainContract<AnnouncerActor, string, AnnouncerApi> {
            grainType "tour.announcer"
            version 1
            stringKey

            readOnly (_.providerProbe)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module AnnouncerDefinition =

    /// <summary>
    /// The producer side needs nothing the functional runtime does not already give a handler:
    /// the broadcast-channel provider is a keyed service, exactly like a stream provider.
    /// </summary>
    let private channelProvider (services: IServiceProvider) =
        services.GetRequiredKeyedService<IBroadcastChannelProvider> TourChannels.Provider

    let definition =
        grainFor AnnouncerApi.contract {
            defaultState (fun () -> 0)

            handle
                (_.announce)
                (fun context state text ->
                    task {
                        let provider = channelProvider context.services

                        // The channel KEY selects which consumer grain key receives the publish
                        // under [ImplicitChannelSubscription]; the namespace selects which
                        // consumer type.
                        let channel =
                            BroadcastChannel.getChannel<string> provider TourChannels.Namespace context.key

                        do! BroadcastChannel.publish channel text
                        return state + 1, state + 1
                    })

            handle
                (_.providerProbe)
                (fun context state () ->
                    task {
                        let provider = channelProvider context.services
                        return state, provider.GetType().Name
                    })
        }
