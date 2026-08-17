/// <summary>
/// Experiment 11 — implicit subscriptions on the functional runtime: a definition declares
/// <c>onStream</c> and <c>onBroadcast</c>, and an item published to the matching namespace
/// activates the grain the stream key names and runs the hook. Nothing calls the grain first.
/// </summary>
namespace FeatureTour.Implicit

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans.BroadcastChannel
open Orleans.Streams
open Orleans.FSharp
open Orleans.FSharp.BroadcastChannel
open Orleans.FSharp.Streaming
open FeatureTour.Interop
open FeatureTour.Streams

/// <summary>Namespaces the implicit-subscription section publishes on.</summary>
/// <remarks>
/// Deliberately its own namespaces, not experiment 7's or 9's: an implicit binding is published
/// for the whole grain TYPE, so re-using <c>tour-events</c> would make every explicit publish in
/// experiment 7 also activate an inbox grain and blur which section proved what.
/// </remarks>
[<RequireQualifiedAccess>]
module TourImplicit =
    /// <summary>The stream namespace the inbox definition declares with <c>onStream</c>.</summary>
    [<Literal>]
    let StreamNamespace = "tour-implicit-mail"

    /// <summary>The channel namespace the inbox definition declares with <c>onBroadcast</c>.</summary>
    [<Literal>]
    let ChannelNamespace = "tour-implicit-announcements"

    /// <summary>A namespace nothing declares, published to as the negative control.</summary>
    [<Literal>]
    let UndeclaredNamespace = "tour-implicit-nobody"

/// <summary>
/// What the hooks observed, recorded out of band so the driver can prove the grain was activated
/// BY the delivery: reading this log needs no call to the grain, and a call would activate the
/// grain itself and so destroy the very evidence the section is after.
/// </summary>
[<RequireQualifiedAccess>]
module ImplicitLog =
    let private entries = ConcurrentQueue<string>()

    /// <summary>Record one delivery as "&lt;kind&gt; &lt;grainKey&gt; = &lt;item&gt;".</summary>
    let add (kind: string) (grainKey: string) (item: string) = entries.Enqueue $"{kind} {grainKey} = {item}"

    /// <summary>Everything recorded so far, in arrival order.</summary>
    let all () = entries |> List.ofSeq

    /// <summary>How many entries start with the given prefix.</summary>
    let countOf (prefix: string) =
        entries
        |> Seq.filter (fun entry -> entry.StartsWith(prefix, StringComparison.Ordinal))
        |> Seq.length

// ── The implicitly subscribed grain ──────────────────────────────────────────

type InboxActor = private InboxActor of unit

/// <summary>What one inbox activation has accumulated.</summary>
type InboxState =
    { mail: string list
      announcements: string list
      activations: int }

/// <summary>The inbox's read-back shape, so the driver can print state after the fact.</summary>
type InboxSnapshot =
    { mail: string list
      announcements: string list
      activations: int
      cursor: string }

[<NoEquality; NoComparison>]
type InboxApi =
    { /// Reports what the delivery hooks accumulated, without changing anything.
      snapshot: unit -> Task<InboxSnapshot> }

[<RequireQualifiedAccess>]
module InboxApi =
    let contract =
        grainContract<InboxActor, string, InboxApi> () {
            grainType "tour.implicit.inbox"
            version 1
            stringKey

            readOnly (_.snapshot)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module InboxDefinition =

    /// <summary>What the last delivery reported about its stream cursor.</summary>
    let mutable private lastCursor = "none observed yet"

    let definition =
        grainFor InboxApi.contract {
            defaultState (fun () ->
                { mail = []
                  announcements = []
                  activations = 0 })

            // Activation counting is what proves the delivery ACTIVATED the grain: the first
            // delivery observes activations = 1 on a grain nothing had ever called.
            onActivate (fun _ state ->
                task {
                    return
                        { state with
                            activations = state.activations + 1 }
                })

            // The whole feature, in one operation. The item type comes from the hook, and the
            // grain that receives an item is the one whose key the STREAM key names.
            onStream TourStream.Provider TourImplicit.StreamNamespace (fun context state (item: string) ->
                task {
                    ImplicitLog.add "stream" context.key item

                    // The Orleans cursor of this item. It is Some only on a rewindable provider
                    // -- Orleans' memory streams are rewindable, so the tour prints a real
                    // sequence number here. The runtime never rewinds with it: a fresh activation
                    // resumes at the subscription's current position, and the token is exposed so
                    // an application can checkpoint or de-duplicate against it.
                    lastCursor <-
                        match context.streamSequenceToken with
                        | Some token -> $"{token.SequenceNumber}.{token.EventIndex}"
                        | None -> "none (this provider is not rewindable)"

                    return { state with mail = state.mail @ [ item ] }
                })

            // Broadcast channels ride the same machinery through the channel-subscriber seam.
            onBroadcast TourChannels.Provider TourImplicit.ChannelNamespace (fun context state (item: string) ->
                task {
                    ImplicitLog.add "broadcast" context.key item

                    return
                        { state with
                            announcements = state.announcements @ [ item ] }
                })

            handle (_.snapshot) (fun _ state () ->
                task {
                    return
                        state,
                        { mail = state.mail
                          announcements = state.announcements
                          activations = state.activations
                          cursor = lastCursor }
                })
        }

// ── The publisher ────────────────────────────────────────────────────────────

type MailerActor = private MailerActor of unit

[<NoEquality; NoComparison>]
type MailerApi =
    { /// Publishes one item onto (namespace, key) of the tour's stream provider.
      post: (string * string * string) -> Task<int>
      /// Publishes one item onto (namespace, key) of the tour's broadcast-channel provider.
      broadcast: (string * string * string) -> Task<int> }

[<RequireQualifiedAccess>]
module MailerApi =
    let contract =
        grainContract<MailerActor, string, MailerApi> () {
            grainType "tour.implicit.mailer"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module MailerDefinition =

    let definition =
        grainFor MailerApi.contract {
            defaultState (fun () -> 0)

            // The producer side is unchanged from experiment 7: an ordinary handler resolving a
            // keyed provider out of context.services. Implicit subscription changes only who
            // receives the item, never how it is published.
            handle (_.post) (fun context state ((streamNamespace, key, text): string * string * string) ->
                task {
                    let provider =
                        context.services.GetRequiredKeyedService<IStreamProvider> TourStream.Provider

                    let stream = Stream.getStream<string> provider streamNamespace key
                    do! Stream.publish stream text
                    return state + 1, state + 1
                })

            handle (_.broadcast) (fun context state ((channelNamespace, key, text): string * string * string) ->
                task {
                    let provider =
                        context.services.GetRequiredKeyedService<IBroadcastChannelProvider> TourChannels.Provider

                    let channel = BroadcastChannel.getChannel<string> provider channelNamespace key
                    do! BroadcastChannel.publish channel text
                    return state + 1, state + 1
                })
        }
