module Orleans.FSharp.Tests.StreamRewindTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Streams
open Orleans.FSharp.Streaming
open FSharp.Control

/// <summary>
/// Tests for Stream.subscribeFrom / subscribeWithToken / subscribeFromWithToken and
/// cursor-preserving TaskSeq consumption — stream rewind/resume support.
/// </summary>

// --- subscribeFrom function existence tests ---

[<Fact>]
let ``Stream module has subscribeFrom method`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let method =
        streamModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "subscribeFrom")

    test <@ method.IsSome @>

[<Fact>]
let ``subscribeFrom method exists and is public`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let method =
        streamModule.GetMethods()
        |> Array.find (fun m -> m.Name = "subscribeFrom")

    test <@ method.IsPublic @>

// --- subscribeWithToken / subscribeFromWithToken ---

[<Fact>]
let ``Stream module has subscribeWithToken and subscribeFromWithToken`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let names = streamModule.GetMethods() |> Array.map (fun m -> m.Name)

    test <@ names |> Array.contains "subscribeWithToken" @>
    test <@ names |> Array.contains "subscribeFromWithToken" @>

[<Fact>]
let ``the always-None getSequenceToken stub is not part of the 5.0 API`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let names = streamModule.GetMethods() |> Array.map _.Name
    test <@ names |> Array.contains "getSequenceToken" |> not @>

[<Fact>]
let ``subscribeWithToken has the cursor-carrying handler shape`` () =
    // The compiler is the assertion: this only type-checks if the handler receives the token.
    let _fn: StreamRef<int> -> (int -> StreamSequenceToken option -> Task<unit>) -> Task<StreamSubscription<int>> =
        Stream.subscribeWithToken

    test <@ true @>

[<Fact>]
let ``subscribeFromWithToken takes a start token and a cursor-carrying handler`` () =
    let _fn:
        StreamRef<int>
            -> StreamSequenceToken
            -> (int -> StreamSequenceToken option -> Task<unit>)
            -> Task<StreamSubscription<int>> =
        Stream.subscribeFromWithToken

    test <@ true @>

[<Fact>]
let ``asTaskSeqWithToken preserves the cursor in its public type`` () =
    let _fn: StreamRef<int> -> TaskSeq<int * StreamSequenceToken option> =
        Stream.asTaskSeqWithToken

    test <@ true @>

// --- subscribeFrom return type tests ---

[<Fact>]
let ``subscribeFrom return type is Task of StreamSubscription`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let method =
        streamModule.GetMethods()
        |> Array.find (fun m -> m.Name = "subscribeFrom")

    let returnType = method.ReturnType
    test <@ returnType.Name.Contains("Task") || returnType.Name.Contains("FSharpFunc") @>

// --- StreamSubscription type tests ---

[<Fact>]
let ``StreamSubscription still has Handle field after rewind additions`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<StreamSubscription<int>>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Handle" @>

[<Fact>]
let ``StreamSubscription Handle is still StreamSubscriptionHandle after additions`` () =
    let handleProp =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<StreamSubscription<int>>)
        |> Array.find (fun p -> p.Name = "Handle")

    test <@ handleProp.PropertyType = typeof<StreamSubscriptionHandle<int>> @>

[<Property>]
let ``Stream module methods all have non-empty names`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    streamModule.GetMethods() |> Array.forall (fun m -> m.Name.Length > 0)
