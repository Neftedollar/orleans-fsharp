module Orleans.FSharp.Tests.StreamingTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Runtime
open Orleans.Streams
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
