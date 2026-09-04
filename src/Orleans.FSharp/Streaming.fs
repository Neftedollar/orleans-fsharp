namespace Orleans.FSharp.Streaming

open System
open System.Collections.Generic
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

/// <summary>Callbacks for item-by-item Orleans stream delivery.</summary>
[<NoEquality; NoComparison>]
type StreamHandlers<'T> =
    {
        /// <summary>Handle one item and its optional provider sequence token.</summary>
        OnNext: 'T -> StreamSequenceToken option -> Task<unit>
        /// <summary>Handle terminal stream failure.</summary>
        OnError: exn -> Task<unit>
        /// <summary>Handle normal stream completion.</summary>
        OnCompleted: unit -> Task<unit>
    }

/// <summary>Construction and immutable customization of item stream callbacks.</summary>
[<RequireQualifiedAccess>]
module StreamHandlers =

    let private completed = Task.FromResult()

    /// <summary>Create callbacks from an item handler, ignoring sequence tokens.</summary>
    let create (onNext: 'T -> Task<unit>) : StreamHandlers<'T> =
        if obj.ReferenceEquals(onNext, null) then
            nullArg (nameof onNext)

        { OnNext = fun item _ -> onNext item
          OnError = fun _ -> completed
          OnCompleted = fun () -> completed }

    /// <summary>Create callbacks whose item handler also receives the sequence token.</summary>
    let withToken (onNext: 'T -> StreamSequenceToken option -> Task<unit>) : StreamHandlers<'T> =
        if obj.ReferenceEquals(onNext, null) then
            nullArg (nameof onNext)

        { OnNext = onNext
          OnError = fun _ -> completed
          OnCompleted = fun () -> completed }

    /// <summary>Replace the terminal error callback.</summary>
    let withError (onError: exn -> Task<unit>) (handlers: StreamHandlers<'T>) =
        if obj.ReferenceEquals(onError, null) then
            nullArg (nameof onError)

        { handlers with OnError = onError }

    /// <summary>Replace the normal completion callback.</summary>
    let withCompletion (onCompleted: unit -> Task<unit>) (handlers: StreamHandlers<'T>) =
        if obj.ReferenceEquals(onCompleted, null) then
            nullArg (nameof onCompleted)

        { handlers with OnCompleted = onCompleted }

/// <summary>One immutable item delivered inside an Orleans stream batch.</summary>
[<NoEquality; NoComparison>]
type StreamBatchItem<'T> =
    {
        /// <summary>The delivered application item.</summary>
        Item: 'T
        /// <summary>The provider sequence token for this item, when available.</summary>
        Token: StreamSequenceToken option
    }

/// <summary>Callbacks for batch-oriented Orleans stream delivery.</summary>
[<NoEquality; NoComparison>]
type StreamBatchHandlers<'T> =
    {
        /// <summary>Handle one ordered batch. The array is a detached immutable-by-convention snapshot.</summary>
        OnNextBatch: StreamBatchItem<'T> array -> Task<unit>
        /// <summary>Handle terminal stream failure.</summary>
        OnError: exn -> Task<unit>
        /// <summary>Handle normal stream completion.</summary>
        OnCompleted: unit -> Task<unit>
    }

/// <summary>Construction and immutable customization of batch stream callbacks.</summary>
[<RequireQualifiedAccess>]
module StreamBatchHandlers =

    let private completed = Task.FromResult()

    /// <summary>Create batch callbacks with no-op terminal handlers.</summary>
    let create (onNextBatch: StreamBatchItem<'T> array -> Task<unit>) : StreamBatchHandlers<'T> =
        if obj.ReferenceEquals(onNextBatch, null) then
            nullArg (nameof onNextBatch)

        { OnNextBatch = onNextBatch
          OnError = fun _ -> completed
          OnCompleted = fun () -> completed }

    /// <summary>Replace the terminal error callback.</summary>
    let withError (onError: exn -> Task<unit>) (handlers: StreamBatchHandlers<'T>) =
        if obj.ReferenceEquals(onError, null) then
            nullArg (nameof onError)

        { handlers with OnError = onError }

    /// <summary>Replace the normal completion callback.</summary>
    let withCompletion (onCompleted: unit -> Task<unit>) (handlers: StreamBatchHandlers<'T>) =
        if obj.ReferenceEquals(onCompleted, null) then
            nullArg (nameof onCompleted)

        { handlers with OnCompleted = onCompleted }

[<Sealed>]
type internal FunctionalAsyncObserver<'T>(handlers: StreamHandlers<'T>) =
    interface IAsyncObserver<'T> with
        member _.OnNextAsync(item, token) = handlers.OnNext item (Option.ofObj token) :> Task
        member _.OnErrorAsync(error) = handlers.OnError error :> Task
        member _.OnCompletedAsync() = handlers.OnCompleted() :> Task

[<Sealed>]
type internal FunctionalAsyncBatchObserver<'T>(handlers: StreamBatchHandlers<'T>) =
    interface IAsyncBatchObserver<'T> with
        member _.OnNextAsync(items: IList<SequentialItem<'T>>) =
            items
            |> Seq.map (fun item ->
                { Item = item.Item
                  Token = Option.ofObj item.Token })
            |> Seq.toArray
            |> handlers.OnNextBatch
            :> Task

        member _.OnErrorAsync(error) = handlers.OnError error :> Task
        member _.OnCompletedAsync() = handlers.OnCompleted() :> Task

[<RequireQualifiedAccess>]
module internal StreamTaskSeqCleanup =

    let private cleanupLater
        (subscriptionTask: Task<'Subscription>)
        (unsubscribe: 'Subscription -> Task)
        =
        task {
            try
                let! subscription = subscriptionTask
                do! unsubscribe subscription
            with
            | _ ->
                // DisposeAsync has already returned, so late provider failures cannot be
                // propagated to its caller. Observing them here prevents an unobserved fault.
                ()
        }

    let dispose
        (lifetime: CancellationTokenSource)
        (subscriptionTask: Task<'Subscription>)
        (unsubscribe: 'Subscription -> Task)
        : Task =
        task {
            try
                lifetime.Cancel()

                if not (isNull subscriptionTask) then
                    if subscriptionTask.IsCompleted then
                        try
                            let! subscription = subscriptionTask
                            do! unsubscribe subscription
                        with
                        | _ when subscriptionTask.IsCanceled || subscriptionTask.IsFaulted -> ()
                    else
                        // SubscribeAsync has no cancellation-token overload. Do not let a
                        // provider which never completes it hang enumerator disposal forever;
                        // if it eventually succeeds, remove that late subscription then.
                        cleanupLater subscriptionTask unsubscribe |> ignore
            finally
                lifetime.Dispose()
        }

[<Sealed>]
type private StreamTaskSeqEnumerator<'T, 'Item>
    (stream: StreamRef<'T>,
     cancellationToken: CancellationToken,
     project: 'T -> StreamSequenceToken option -> 'Item) =
    let channelOptions =
        BoundedChannelOptions(1000, FullMode = BoundedChannelFullMode.Wait)

    let channel = Channel.CreateBounded<'Item>(channelOptions)
    let lifetime = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
    let lifetimeToken = lifetime.Token
    let mutable subscriptionTask: Task<StreamSubscriptionHandle<'T>> = null
    let mutable current = Unchecked.defaultof<'Item>
    let mutable disposed = 0

    let ensureSubscribed () =
        if isNull subscriptionTask then
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)

            let onNext =
                Func<'T, StreamSequenceToken, Task>(fun item token ->
                    task {
                        try
                            let projected = project item (Option.ofObj token)
                            do! channel.Writer.WriteAsync(projected, lifetimeToken).AsTask()
                        with
                        | :? OperationCanceledException when lifetimeToken.IsCancellationRequested -> ()
                        | :? ChannelClosedException -> ()
                    })

            let onError =
                Func<Exception, Task>(fun error ->
                    channel.Writer.TryComplete(error) |> ignore
                    Task.CompletedTask)

            let onCompleted =
                Func<Task>(fun () ->
                    channel.Writer.TryComplete() |> ignore
                    Task.CompletedTask)

            subscriptionTask <- asyncStream.SubscribeAsync(onNext, onError, onCompleted)

        subscriptionTask

    let moveNext () =
        task {
            if Volatile.Read(&disposed) <> 0 then
                return false
            else
                let! _ = ensureSubscribed ()
                let reader = channel.Reader
                let mutable waiting = true
                let mutable moved = false

                while waiting do
                    let! available = reader.WaitToReadAsync(lifetimeToken).AsTask()

                    if not available then
                        waiting <- false
                    else
                        match reader.TryRead() with
                        | true, item ->
                            current <- item
                            moved <- true
                            waiting <- false
                        | false, _ -> ()

                return moved
        }

    let dispose () =
        task {
            if Interlocked.Exchange(&disposed, 1) = 0 then
                channel.Writer.TryComplete() |> ignore
                do!
                    StreamTaskSeqCleanup.dispose lifetime subscriptionTask (fun subscription ->
                        subscription.UnsubscribeAsync())
        }

    interface IAsyncEnumerator<'Item> with
        member _.Current = current
        member _.MoveNextAsync() = ValueTask<bool>(moveNext ())
        member _.DisposeAsync() = ValueTask(dispose ())

[<Sealed>]
type private StreamTaskSeqEnumerable<'T, 'Item>
    (stream: StreamRef<'T>, project: 'T -> StreamSequenceToken option -> 'Item) =
    interface IAsyncEnumerable<'Item> with
        member _.GetAsyncEnumerator(cancellationToken) =
            new StreamTaskSeqEnumerator<'T, 'Item>(stream, cancellationToken, project)
            :> IAsyncEnumerator<'Item>

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

    /// <summary>Publish one provider-native batch with no explicit starting token.</summary>
    let publishBatch<'T> (stream: StreamRef<'T>) (events: seq<'T>) : Task<unit> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            do! asyncStream.OnNextBatchAsync(events, null)
        }

    /// <summary>Publish one provider-native batch starting at an explicit sequence token.</summary>
    let publishBatchFrom<'T>
        (stream: StreamRef<'T>)
        (token: StreamSequenceToken)
        (events: seq<'T>)
        : Task<unit> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            do! asyncStream.OnNextBatchAsync(events, token)
        }

    /// <summary>Notify subscribers that the stream completed normally.</summary>
    let complete<'T> (stream: StreamRef<'T>) : Task<unit> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            do! asyncStream.OnCompletedAsync()
        }

    /// <summary>Notify subscribers that the stream terminated with an error.</summary>
    let fail<'T> (stream: StreamRef<'T>) (error: exn) : Task<unit> =
        if isNull error then
            nullArg (nameof error)

        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            do! asyncStream.OnErrorAsync(error)
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
    /// Subscribes to a stream with a handler that also receives each event's sequence token.
    /// This is how you obtain a cursor to checkpoint and later hand to <c>subscribeFrom</c>:
    /// the token belongs to the delivered event, so store it after the event is processed and
    /// resume from it.
    /// </summary>
    /// <remarks>
    /// The token is <c>Some</c> on a rewindable provider — Orleans' in-memory streams and Event
    /// Hubs both are — and <c>None</c> on a provider that supplies no cursor, matching how
    /// <c>context.streamSequenceToken</c> reports it on the functional grain runtime.
    /// The subscription is durable and persists beyond grain deactivation.
    /// </remarks>
    /// <param name="stream">The stream reference to subscribe to.</param>
    /// <param name="handler">
    /// A function called for each event with the event and its sequence token.
    /// </param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A Task containing the stream subscription, which can be used to unsubscribe.</returns>
    let subscribeWithToken<'T>
        (stream: StreamRef<'T>)
        (handler: 'T -> StreamSequenceToken option -> Task<unit>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)

            let onNext =
                Func<'T, StreamSequenceToken, Task>(fun item token ->
                    task { do! handler item (Option.ofObj token) })

            let! handle = asyncStream.SubscribeAsync(onNext)
            return { Handle = handle }
        }

    /// <summary>Subscribe with first-class next/error/completion callbacks.</summary>
    let subscribeHandlers<'T>
        (stream: StreamRef<'T>)
        (handlers: StreamHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<'T>
            let! handle = asyncStream.SubscribeAsync(observer)
            return { Handle = handle }
        }

    /// <summary>
    /// Subscribe with server-side filter data. The named provider must have an Orleans
    /// <c>IStreamFilter</c> registered; the filter receives this opaque string for every item.
    /// </summary>
    let subscribeFiltered<'T>
        (stream: StreamRef<'T>)
        (filterData: string)
        (handlers: StreamHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<'T>
            let! handle = asyncStream.SubscribeAsync(observer, null, filterData)
            return { Handle = handle }
        }

    /// <summary>Subscribe with provider-native batch delivery and terminal callbacks.</summary>
    let subscribeBatch<'T>
        (stream: StreamRef<'T>)
        (handlers: StreamBatchHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let observer = FunctionalAsyncBatchObserver handlers :> IAsyncBatchObserver<'T>
            let! handle = asyncStream.SubscribeAsync(observer)
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
    /// the channel is completed by the stream's OnCompleted callback. Disposing the enumerator
    /// unsubscribes the underlying Orleans subscription, including when consumption stops early.</returns>
    let asTaskSeq<'T> (stream: StreamRef<'T>) : TaskSeq<'T> =
        new StreamTaskSeqEnumerable<'T, 'T>(stream, fun item _ -> item) :> IAsyncEnumerable<'T>

    /// <summary>
    /// Consumes a stream as a pull-based TaskSeq while preserving every item's provider sequence
    /// token. Save a delivered token after processing its item and pass it to
    /// <c>subscribeFromWithToken</c> when resuming from that checkpoint.
    /// </summary>
    /// <param name="stream">The stream reference to consume.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>
    /// A TaskSeq of item/token pairs. The token is <c>Some</c> for providers which expose cursors
    /// and <c>None</c> otherwise. Subscription, backpressure, cancellation, and disposal semantics
    /// are identical to <c>asTaskSeq</c>.
    /// </returns>
    let asTaskSeqWithToken<'T>
        (stream: StreamRef<'T>)
        : TaskSeq<'T * StreamSequenceToken option> =
        new StreamTaskSeqEnumerable<'T, 'T * StreamSequenceToken option>(stream, fun item token -> item, token)
        :> IAsyncEnumerable<'T * StreamSequenceToken option>

    /// <summary>
    /// Subscribes to a stream starting from a specific sequence token (rewind/resume).
    /// This allows resuming event processing from a previously checkpointed position.
    /// Rewindable providers only — Orleans' in-memory streams and Event Hubs both are; a
    /// provider that is not rewindable rejects the token.
    /// </summary>
    /// <remarks>
    /// Use <c>subscribeWithToken</c> to obtain the cursor this takes, or
    /// <c>subscribeFromWithToken</c> to resume and keep checkpointing in one step.
    /// </remarks>
    /// <param name="stream">The stream reference to subscribe to.</param>
    /// <param name="token">The sequence token to start consuming from.</param>
    /// <param name="handler">A function called for each event received on the stream.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A Task containing the stream subscription, which can be used to unsubscribe.</returns>
    let subscribeFrom<'T>
        (stream: StreamRef<'T>)
        (token: StreamSequenceToken)
        (handler: 'T -> Task<unit>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)

            let onNext =
                Func<'T, StreamSequenceToken, Task>(fun item _token ->
                    task { do! handler item })

            let! handle = asyncStream.SubscribeAsync(onNext, token)
            return { Handle = handle }
        }

    /// <summary>
    /// Subscribes from a checkpoint <b>and</b> keeps handing the handler each event's own
    /// sequence token, so the consumer can go on checkpointing after a rewind.
    /// </summary>
    /// <remarks>
    /// The delivered token is <c>Some</c> on a rewindable provider and <c>None</c> on a provider
    /// that supplies no cursor, exactly as in <c>subscribeWithToken</c>.
    /// </remarks>
    /// <param name="stream">The stream reference to subscribe to.</param>
    /// <param name="token">The sequence token to start consuming from.</param>
    /// <param name="handler">
    /// A function called for each event with the event and its sequence token.
    /// </param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A Task containing the stream subscription, which can be used to unsubscribe.</returns>
    let subscribeFromWithToken<'T>
        (stream: StreamRef<'T>)
        (token: StreamSequenceToken)
        (handler: 'T -> StreamSequenceToken option -> Task<unit>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)

            let onNext =
                Func<'T, StreamSequenceToken, Task>(fun item itemToken ->
                    task { do! handler item (Option.ofObj itemToken) })

            let! handle = asyncStream.SubscribeAsync(onNext, token)
            return { Handle = handle }
        }

    /// <summary>Resume item delivery from a token while preserving error and completion callbacks.</summary>
    let subscribeFromHandlers<'T>
        (stream: StreamRef<'T>)
        (token: StreamSequenceToken)
        (handlers: StreamHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<'T>
            let! handle = asyncStream.SubscribeAsync(observer, token, null)
            return { Handle = handle }
        }

    /// <summary>Resume filtered item delivery from a token.</summary>
    let subscribeFromFiltered<'T>
        (stream: StreamRef<'T>)
        (token: StreamSequenceToken)
        (filterData: string)
        (handlers: StreamHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<'T>
            let! handle = asyncStream.SubscribeAsync(observer, token, filterData)
            return { Handle = handle }
        }

    /// <summary>Resume provider-native batch delivery from a token.</summary>
    let subscribeBatchFrom<'T>
        (stream: StreamRef<'T>)
        (token: StreamSequenceToken)
        (handlers: StreamBatchHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let observer = FunctionalAsyncBatchObserver handlers :> IAsyncBatchObserver<'T>
            let! handle = asyncStream.SubscribeAsync(observer, token)
            return { Handle = handle }
        }

    /// <summary>
    /// Unsubscribes from a stream, stopping event delivery to the handler.
    /// </summary>
    /// <param name="sub">The stream subscription to cancel.</param>
    /// <typeparam name="'T">The type of events on the subscribed stream.</typeparam>
    /// <returns>A Task that completes when the unsubscription is processed.</returns>
    let unsubscribe<'T> (sub: StreamSubscription<'T>) : Task<unit> =
        task { do! sub.Handle.UnsubscribeAsync() }

    /// <summary>
    /// Gets all active subscriptions for a stream.
    /// </summary>
    /// <param name="stream">The stream reference to query.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A Task containing a list of active stream subscriptions.</returns>
    let getSubscriptions<'T> (stream: StreamRef<'T>) : Task<StreamSubscription<'T> list> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let! handles = asyncStream.GetAllSubscriptionHandles()

            return
                handles
                |> Seq.map (fun h -> { Handle = h })
                |> Seq.toList
        }

    /// <summary>Reattach item callbacks to one durable subscription.</summary>
    let resume<'T>
        (sub: StreamSubscription<'T>)
        (handlers: StreamHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<'T>
            let! handle = sub.Handle.ResumeAsync(observer, null)
            return { Handle = handle }
        }

    /// <summary>Reattach item callbacks to one durable subscription from an explicit token.</summary>
    let resumeFrom<'T>
        (sub: StreamSubscription<'T>)
        (token: StreamSequenceToken)
        (handlers: StreamHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<'T>
            let! handle = sub.Handle.ResumeAsync(observer, token)
            return { Handle = handle }
        }

    /// <summary>Reattach batch callbacks to one durable subscription.</summary>
    let resumeBatch<'T>
        (sub: StreamSubscription<'T>)
        (handlers: StreamBatchHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let observer = FunctionalAsyncBatchObserver handlers :> IAsyncBatchObserver<'T>
            let! handle = sub.Handle.ResumeAsync(observer, null)
            return { Handle = handle }
        }

    /// <summary>Reattach batch callbacks from an explicit token.</summary>
    let resumeBatchFrom<'T>
        (sub: StreamSubscription<'T>)
        (token: StreamSequenceToken)
        (handlers: StreamBatchHandlers<'T>)
        : Task<StreamSubscription<'T>> =
        task {
            let observer = FunctionalAsyncBatchObserver handlers :> IAsyncBatchObserver<'T>
            let! handle = sub.Handle.ResumeAsync(observer, token)
            return { Handle = handle }
        }

    /// <summary>
    /// Resume all existing subscriptions for a stream with a new handler.
    /// Useful after grain reactivation to reattach handlers to durable subscriptions.
    /// </summary>
    /// <param name="stream">The stream reference whose subscriptions to resume.</param>
    /// <param name="handler">The handler function to attach to each subscription.</param>
    /// <typeparam name="'T">The type of events on the stream.</typeparam>
    /// <returns>A Task that completes when all subscriptions have been resumed.</returns>
    let resumeAll<'T> (stream: StreamRef<'T>) (handler: 'T -> Task<unit>) : Task<unit> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let! handles = asyncStream.GetAllSubscriptionHandles()

            let onNext =
                Func<'T, StreamSequenceToken, Task>(fun item _token ->
                    task { do! handler item })

            for handle in handles do
                let! _ = handle.ResumeAsync(onNext)
                ()
        }

    /// <summary>Resume every durable subscription with item/error/completion callbacks.</summary>
    let resumeAllHandlers<'T> (stream: StreamRef<'T>) (handlers: StreamHandlers<'T>) : Task<unit> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let! handles = asyncStream.GetAllSubscriptionHandles()

            for handle in handles do
                let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<'T>
                let! _ = handle.ResumeAsync(observer, null)
                ()
        }

    /// <summary>Resume every durable subscription with provider-native batch callbacks.</summary>
    let resumeAllBatch<'T> (stream: StreamRef<'T>) (handlers: StreamBatchHandlers<'T>) : Task<unit> =
        task {
            let asyncStream = stream.Provider.GetStream<'T>(stream.StreamId)
            let! handles = asyncStream.GetAllSubscriptionHandles()

            for handle in handles do
                let observer = FunctionalAsyncBatchObserver handlers :> IAsyncBatchObserver<'T>
                let! _ = handle.ResumeAsync(observer, null)
                ()
        }
