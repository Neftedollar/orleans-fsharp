module Orleans.FSharp.Tests.StreamingTests

open System
open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Runtime
open Orleans.Streams
open Orleans.Providers.Streams.Common
open Orleans.FSharp
open Orleans.FSharp.Streaming
open FSharp.Control

// --- StreamRef construction tests ---

/// <summary>Fake stream provider for unit testing.</summary>
type FakeStreamProvider() =
    interface IStreamProvider with
        member _.Name = "FakeProvider"
        member _.IsRewindable = false
        member _.GetStream<'T>(streamId: StreamId) : IAsyncStream<'T> =
            Unchecked.defaultof<IAsyncStream<'T>>

[<AllowNullLiteral>]
type CapturingIntStreamProxy() =
    inherit DispatchProxy()

    let mutable publishedBatch = []
    let mutable publishedToken: StreamSequenceToken option = None
    let mutable observer: IAsyncObserver<int> option = None
    let mutable completions = 0
    let mutable failure: exn option = None

    member _.PublishedBatch = publishedBatch
    member _.PublishedToken = publishedToken
    member _.Completions = completions
    member _.Failure = failure

    override _.Invoke(targetMethod: MethodInfo, arguments: obj array) =
        match targetMethod.Name with
        | "SubscribeAsync" ->
            match arguments.[0] with
            | :? IAsyncObserver<int> as registered ->
                observer <- Some registered

                Task.FromResult(Unchecked.defaultof<StreamSubscriptionHandle<int>>)
                :> obj
            | unsupported ->
                raise (
                    NotSupportedException(
                        $"unexpected SubscribeAsync observer in forwarding test: {unsupported.GetType().FullName}"
                    )
                )
        | "OnNextBatchAsync" ->
            publishedBatch <- (arguments.[0] :?> IEnumerable<int>) |> Seq.toList
            publishedToken <- arguments.[1] :?> StreamSequenceToken |> Option.ofObj
            Task.CompletedTask :> obj
        | "OnCompletedAsync" ->
            completions <- completions + 1

            match observer with
            | Some registered -> registered.OnCompletedAsync() :> obj
            | None -> Task.CompletedTask :> obj
        | "OnErrorAsync" ->
            let cause = arguments.[0] :?> exn
            failure <- Some cause

            match observer with
            | Some registered -> registered.OnErrorAsync cause :> obj
            | None -> Task.CompletedTask :> obj
        | memberName ->
            raise (NotSupportedException($"unexpected IAsyncStream member in forwarding test: {memberName}"))

type CapturingIntStreamProvider(stream: IAsyncStream<int>) =
    interface IStreamProvider with
        member _.Name = "CapturingProvider"
        member _.IsRewindable = true

        member _.GetStream<'T>(_: StreamId) : IAsyncStream<'T> =
            if typeof<'T> <> typeof<int> then
                invalidOp $"capturing provider only supports int, requested {typeof<'T>.FullName}"

            unbox<IAsyncStream<'T>> (box stream)

[<Fact>]
let ``getStream stores StreamId with correct namespace and key`` () =
    let provider = FakeStreamProvider() :> IStreamProvider
    let ref = Stream.getStream<int> provider "test-ns" "test-key"
    let expectedId = StreamId.Create("test-ns", "test-key")
    test <@ ref.StreamId = expectedId @>

[<Fact>]
let ``getStream stores the stream provider`` () =
    let provider = FakeStreamProvider() :> IStreamProvider
    let ref = Stream.getStream<int> provider "ns" "key"
    test <@ obj.ReferenceEquals(ref.Provider, provider) @>

[<Fact>]
let ``getStream preserves namespace in StreamId`` () =
    let provider = FakeStreamProvider() :> IStreamProvider
    let ref = Stream.getStream<string> provider "my-namespace" "my-key"
    let expectedId = StreamId.Create("my-namespace", "my-key")
    test <@ ref.StreamId = expectedId @>

[<Fact>]
let ``getStream with different types produces StreamRef of correct generic type`` () =
    let provider = FakeStreamProvider() :> IStreamProvider
    let intRef = Stream.getStream<int> provider "ns" "k"
    let strRef = Stream.getStream<string> provider "ns" "k"
    // Verify they are different types at the type level
    test <@ intRef.GetType().GenericTypeArguments.[0] = typeof<int> @>
    test <@ strRef.GetType().GenericTypeArguments.[0] = typeof<string> @>

[<Fact>]
let ``publishBatchFrom forwards the exact token to Orleans`` () =
    task {
        let asyncStream = DispatchProxy.Create<IAsyncStream<int>, CapturingIntStreamProxy>()
        let capture = asyncStream :?> CapturingIntStreamProxy
        let provider = CapturingIntStreamProvider(asyncStream) :> IStreamProvider
        let streamRef = Stream.getStream<int> provider "token-ns" "token-key"
        let token = EventSequenceToken(42L, 3) :> StreamSequenceToken

        do! Stream.publishBatchFrom streamRef token [ 1; 2; 3 ]

        test <@ capture.PublishedBatch = [ 1; 2; 3 ] @>
        test <@ capture.PublishedToken |> Option.exists (fun actual -> obj.ReferenceEquals(actual, token)) @>
    }

[<Fact>]
let ``complete reaches the public subscription completion handler`` () =
    task {
        let asyncStream = DispatchProxy.Create<IAsyncStream<int>, CapturingIntStreamProxy>()
        let capture = asyncStream :?> CapturingIntStreamProxy
        let provider = CapturingIntStreamProvider(asyncStream) :> IStreamProvider
        let streamRef = Stream.getStream<int> provider "terminal-ns" "complete"
        let mutable completed = false

        let handlers =
            StreamHandlers.create (fun _ -> Task.FromResult())
            |> StreamHandlers.withCompletion (fun () ->
                task {
                    completed <- true
                })

        let! _ = Stream.subscribeHandlers streamRef handlers
        do! Stream.complete streamRef

        test <@ capture.Completions = 1 @>
        test <@ completed @>
    }

[<Fact>]
let ``fail reaches the public subscription with the exact exception`` () =
    task {
        let asyncStream = DispatchProxy.Create<IAsyncStream<int>, CapturingIntStreamProxy>()
        let capture = asyncStream :?> CapturingIntStreamProxy
        let provider = CapturingIntStreamProvider(asyncStream) :> IStreamProvider
        let streamRef = Stream.getStream<int> provider "terminal-ns" "failure"
        let mutable received: exn option = None
        let cause = InvalidOperationException "terminal failure"

        let handlers =
            StreamHandlers.create (fun _ -> Task.FromResult())
            |> StreamHandlers.withError (fun error ->
                task {
                    received <- Some error
                })

        let! _ = Stream.subscribeHandlers streamRef handlers
        do! Stream.fail streamRef cause

        test <@ capture.Failure |> Option.exists (fun actual -> obj.ReferenceEquals(actual, cause)) @>
        test <@ received |> Option.exists (fun actual -> obj.ReferenceEquals(actual, cause)) @>
    }

// --- Type signature tests ---

[<Fact>]
let ``publish function exists in Stream module`` () =
    // F# modules compile to static classes; find the Stream module via the assembly
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    test <@ streamModule.IsSome @>

    let publishMethod =
        streamModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "publish")

    test <@ publishMethod.IsSome @>

[<Fact>]
let ``StreamRef is a record type`` () =
    test <@ Microsoft.FSharp.Reflection.FSharpType.IsRecord(typeof<StreamRef<int>>) @>

[<Fact>]
let ``StreamRef has Provider and StreamId fields`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<StreamRef<int>>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Provider" @>
    test <@ fields |> Array.contains "StreamId" @>

[<Fact>]
let ``StreamSubscription has Handle field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<StreamSubscription<int>>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Handle" @>

[<Fact>]
let ``StreamSubscription Handle is StreamSubscriptionHandle<'T>`` () =
    let handleProp =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<StreamSubscription<int>>)
        |> Array.find (fun p -> p.Name = "Handle")

    test <@ handleProp.PropertyType = typeof<StreamSubscriptionHandle<int>> @>

[<Fact>]
let ``item handlers receive tokens errors and completion`` () =
    task {
        let received = ResizeArray<int * StreamSequenceToken option>()
        let errors = ResizeArray<string>()
        let mutable completed = false

        let handlers =
            StreamHandlers.withToken (fun item token ->
                task {
                    received.Add(item, token)
                })
            |> StreamHandlers.withError (fun error ->
                task {
                    errors.Add error.Message
                })
            |> StreamHandlers.withCompletion (fun () ->
                task {
                    completed <- true
                })

        let observer = FunctionalAsyncObserver handlers :> IAsyncObserver<int>
        do! observer.OnNextAsync(7, null)
        do! observer.OnErrorAsync(InvalidOperationException "broken")
        do! observer.OnCompletedAsync()

        test <@ received |> Seq.toList = [ 7, None ] @>
        test <@ errors |> Seq.toList = [ "broken" ] @>
        test <@ completed @>
    }

[<Fact>]
let ``batch handlers preserve batch order and terminal callbacks`` () =
    task {
        let batches = ResizeArray<StreamBatchItem<int> array>()
        let mutable error: exn option = None
        let mutable completed = false

        let handlers =
            StreamBatchHandlers.create (fun batch ->
                task {
                    batches.Add batch
                })
            |> StreamBatchHandlers.withError (fun cause ->
                task {
                    error <- Some cause
                })
            |> StreamBatchHandlers.withCompletion (fun () ->
                task {
                    completed <- true
                })

        let observer = FunctionalAsyncBatchObserver handlers :> IAsyncBatchObserver<int>

        let items =
            [ SequentialItem<int>(3, null); SequentialItem<int>(4, null) ]
            |> ResizeArray
            :> IList<SequentialItem<int>>

        let failure = ApplicationException "batch failed"
        do! observer.OnNextAsync items
        do! observer.OnErrorAsync failure
        do! observer.OnCompletedAsync()

        test <@ batches.Count = 1 @>
        test <@ batches.[0] |> Array.map _.Item = [| 3; 4 |] @>
        test <@ batches.[0] |> Array.forall (fun item -> item.Token.IsNone) @>
        test <@ error = Some failure @>
        test <@ completed @>
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

let private fakeProvider = FakeStreamProvider() :> IStreamProvider

[<Property>]
let ``getStream stores correct StreamId for any namespace and key`` (ns: NonNull<string>) (key: NonNull<string>) =
    let ref = Stream.getStream<int> fakeProvider ns.Get key.Get
    let expected = StreamId.Create(ns.Get, key.Get)
    ref.StreamId = expected

[<Property>]
let ``getStream preserves provider reference for any inputs`` (ns: NonNull<string>) (key: NonNull<string>) =
    let ref = Stream.getStream<string> fakeProvider ns.Get key.Get
    obj.ReferenceEquals(ref.Provider, fakeProvider)

[<Property>]
let ``getStream with same namespace and key produces equal StreamId for any inputs`` (ns: NonNull<string>) (key: NonNull<string>) =
    let ref1 = Stream.getStream<int> fakeProvider ns.Get key.Get
    let ref2 = Stream.getStream<int> fakeProvider ns.Get key.Get
    ref1.StreamId = ref2.StreamId

[<Property>]
let ``getStream with different keys produces different StreamIds`` (ns: NonNull<string>) (key1: NonNull<string>) (key2: NonNull<string>) =
    // Skip degenerate case where keys happen to be equal
    key1.Get = key2.Get ||
        (let ref1 = Stream.getStream<int> fakeProvider ns.Get key1.Get
         let ref2 = Stream.getStream<int> fakeProvider ns.Get key2.Get
         ref1.StreamId <> ref2.StreamId)
