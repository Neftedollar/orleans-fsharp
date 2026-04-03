namespace Orleans.FSharp.Streaming

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Orleans.Runtime
open Orleans.Streams
open FSharp.Control

/// <summary>
/// A typed reference to an Orleans stream, containing the stream provider and stream identity.
/// </summary>
/// <typeparam name="'T">The type of events on the stream.</typeparam>
type StreamRef<'T> =
    {
        /// <summary>The Orleans stream provider that owns this stream.</summary>
        Provider: IStreamProvider
        /// <summary>The unique identity of the stream (namespace + key).</summary>
        StreamId: StreamId
    }

/// <summary>
/// Represents an active stream subscription that can be unsubscribed.
/// </summary>
/// <typeparam name="'T">The type of events on the subscribed stream.</typeparam>
type StreamSubscription<'T> =
    {
        /// <summary>The underlying Orleans stream subscription handle.</summary>
        Handle: StreamSubscriptionHandle<'T>
    }

/// <summary>
/// Functions for creating, publishing to, subscribing to, and consuming Orleans streams
/// using idiomatic F# and TaskSeq for pull-based consumption.
/// </summary>
[<RequireQualifiedAccess>]
module Stream =

    /// <summary>
    /// Gets a typed stream reference from a stream provider, namespace, and key.
    /// This is a purely local operation that does not contact the silo.
    /// </summary>
    /// <param name="provider">The Orleans stream provider.</param>
    /// <param name="ns">The stream namespace.</param>
    /// <param name="key">The stream key within the namespace.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A typed stream reference.</returns>
    let getStream<'T> (provider: IStreamProvider) (ns: string) (key: string) : StreamRef<'T> =
        {
            Provider = provider
            StreamId = StreamId.Create(ns, key)
        }

    /// <summary>
    /// Publishes an event to a stream.
    /// </summary>
    /// <param name="stream">The stream reference to publish to.</param>
    /// <param name="event">The event to publish.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A Task that completes when the event has been accepted by the stream.</returns>
    let publish<'T> (stream: StreamRef<'T>) (event: 'T) : Task<unit> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            do! asyncStream.OnNextAsync(event)
        }

    /// <summary>
    /// Subscribes to a stream with a callback handler.
    /// The subscription is durable and persists beyond grain deactivation.
    /// </summary>
    /// <param name="stream">The stream reference to subscribe to.</param>
    /// <param name="handler">A function called for each event received on the stream.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A Task containing the stream subscription, which can be used to unsubscribe.</returns>
    let subscribe<'T> (stream: StreamRef<'T>) (handler: 'T -> Task<unit>) : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)

            let onNext =
                Func<'T, StreamSequenceToken, Task>(fun item _token ->
                    task { do! handler item })

            let! handle = asyncStream.SubscribeAsync(onNext)
            return { Handle = handle }
        }

    /// <summary>
    /// Consumes a stream as a TaskSeq (pull-based).
    /// Uses a bounded Channel with capacity 1000 to bridge the push-based Orleans subscription
    /// to a pull-based TaskSeq. BoundedChannelFullMode.Wait provides backpressure when
    /// the consumer falls behind the producer.
    /// </summary>
    /// <param name="stream">The stream reference to consume.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A TaskSeq that yields events from the stream. The sequence completes when
    /// the channel is completed by the stream's OnCompleted callback.</returns>
    let asTaskSeq<'T> (stream: StreamRef<'T>) : TaskSeq<'T> =
        let channelOptions =
            BoundedChannelOptions(1000, FullMode = BoundedChannelFullMode.Wait)

        let channel = Channel.CreateBounded<'T>(channelOptions)

        let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)

        let onNext =
            Func<'T, StreamSequenceToken, Task>(fun item _token ->
                task {
                    do! channel.Writer.WriteAsync(item)
                })

        let onError =
            Func<Exception, Task>(fun ex ->
                channel.Writer.Complete(ex)
                Task.CompletedTask)

        let onCompleted =
            Func<Task>(fun () ->
                channel.Writer.Complete()
                Task.CompletedTask)

        // Fire-and-forget subscription setup
        let _subscriptionTask =
            asyncStream.SubscribeAsync(onNext, onError, onCompleted)

        taskSeq {
            let reader = channel.Reader

            while! reader.WaitToReadAsync() do
                let mutable hasMore = true

                while hasMore do
                    match reader.TryRead() with
                    | true, item -> yield item
                    | false, _ -> hasMore <- false
        }

    /// <summary>
    /// Unsubscribes from a stream, stopping event delivery to the handler.
    /// </summary>
    /// <param name="sub">The stream subscription to cancel.</param>
    /// <typeparam name="'T">The type of events on the subscribed stream.</typeparam>
    /// <returns>A Task that completes when the unsubscription is processed.</returns>
    let unsubscribe<'T> (sub: StreamSubscription<'T>) : Task<unit> =
        task { do! sub.Handle.UnsubscribeAsync() }
