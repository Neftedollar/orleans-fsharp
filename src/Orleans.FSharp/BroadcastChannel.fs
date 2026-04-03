namespace Orleans.FSharp.BroadcastChannel

open System.Threading.Tasks
open Orleans.BroadcastChannel

/// <summary>
/// A typed reference to an Orleans broadcast channel, containing the provider and channel identity.
/// Broadcast channels deliver messages to ALL subscribers (fan-out), unlike streams which target individual consumers.
/// </summary>
/// <typeparam name="'T">The type of messages on the broadcast channel.</typeparam>
type BroadcastChannelRef<'T> =
    {
        /// <summary>The Orleans broadcast channel provider that owns this channel.</summary>
        Provider: IBroadcastChannelProvider
        /// <summary>The unique identity of the broadcast channel (namespace + key).</summary>
        ChannelId: ChannelId
    }

/// <summary>
/// Functions for creating broadcast channel references and publishing messages.
/// Broadcast channels are one-way (publish-only from code); consumers are grains
/// that implement <c>IOnBroadcastChannelSubscribed</c> with <c>[ImplicitChannelSubscription]</c>.
/// </summary>
[<RequireQualifiedAccess>]
module BroadcastChannel =

    /// <summary>
    /// Gets a typed broadcast channel reference from a provider, namespace, and key.
    /// This is a purely local operation that does not contact the silo.
    /// </summary>
    /// <param name="provider">The Orleans broadcast channel provider.</param>
    /// <param name="ns">The channel namespace.</param>
    /// <param name="key">The channel key within the namespace.</param>
    /// <typeparam name="'T">The type of messages on the channel.</typeparam>
    /// <returns>A typed broadcast channel reference.</returns>
    let getChannel<'T> (provider: IBroadcastChannelProvider) (ns: string) (key: string) : BroadcastChannelRef<'T> =
        {
            Provider = provider
            ChannelId = ChannelId.Create(ns, key)
        }

    /// <summary>
    /// Publishes a message to all subscribers of the broadcast channel.
    /// </summary>
    /// <param name="channel">The broadcast channel reference to publish to.</param>
    /// <param name="message">The message to publish.</param>
    /// <typeparam name="'T">The type of messages on the channel.</typeparam>
    /// <returns>A Task that completes when the message has been accepted by the channel.</returns>
    let publish<'T> (channel: BroadcastChannelRef<'T>) (message: 'T) : Task<unit> =
        task {
            let writer = channel.Provider.GetChannelWriter<'T>(channel.ChannelId)
            do! writer.Publish(message)
        }
