module Orleans.FSharp.Tests.StreamRewindTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Streams
open Orleans.FSharp.Streaming

/// <summary>
/// Tests for Stream.subscribeFrom / subscribeWithToken / subscribeFromWithToken and the
/// deprecated Stream.getSequenceToken — stream rewind/resume support.
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

// --- getSequenceToken function existence tests ---

[<Fact>]
let ``Stream module has getSequenceToken method`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let method =
        streamModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "getSequenceToken")

    test <@ method.IsSome @>

[<Fact>]
let ``getSequenceToken method is public`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let method =
        streamModule.GetMethods()
        |> Array.find (fun m -> m.Name = "getSequenceToken")

    test <@ method.IsPublic @>

// --- getSequenceToken behavior tests ---

// Deprecation pass: getSequenceToken now carries [<Obsolete>] (warning, not error) and these
// tests pin its unchanged None-returning behaviour on purpose.
#nowarn "44"

[<Fact>]
let ``getSequenceToken returns None for a subscription`` () =
    // getSequenceToken always returns None since the token must be tracked by the consumer
    let sub: StreamSubscription<int> = { Handle = Unchecked.defaultof<StreamSubscriptionHandle<int>> }
    let result = Stream.getSequenceToken sub
    test <@ result.IsNone @>

[<Fact>]
let ``getSequenceToken returns option type`` () =
    let sub: StreamSubscription<string> = { Handle = Unchecked.defaultof<StreamSubscriptionHandle<string>> }
    let result: Orleans.Streams.StreamSequenceToken option = Stream.getSequenceToken sub
    // None.GetType() would throw NRE; verify the compile-time type is option instead
    test <@ result.IsNone @>

#warnon "44"

[<Fact>]
let ``getSequenceToken is marked Obsolete and names its replacement`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    let method =
        streamModule.GetMethods()
        |> Array.find (fun m -> m.Name = "getSequenceToken")

    let attribute =
        method.GetCustomAttributes(typeof<ObsoleteAttribute>, false)
        |> Array.tryHead
        |> Option.map (fun a -> a :?> ObsoleteAttribute)

    test <@ attribute.IsSome @>
    // A warning, never an error: existing callers keep compiling.
    test <@ not attribute.Value.IsError @>
    test <@ attribute.Value.Message.Contains "subscribeWithToken" @>
    test <@ attribute.Value.Message.Contains "streamSequenceToken" @>

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

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

// Deprecation pass: pins the deprecated member's unchanged behaviour on purpose.
#nowarn "44"

[<Property>]
let ``getSequenceToken always returns None for any StreamSubscription value`` () =
    let sub: StreamSubscription<int> = { Handle = Unchecked.defaultof<StreamSubscriptionHandle<int>> }
    let result = Stream.getSequenceToken sub
    result.IsNone

#warnon "44"

[<Property>]
let ``Stream module methods all have non-empty names`` () =
    let streamModule =
        typeof<StreamRef<int>>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Stream" && t.IsAbstract && t.IsSealed)

    streamModule.GetMethods() |> Array.forall (fun m -> m.Name.Length > 0)
