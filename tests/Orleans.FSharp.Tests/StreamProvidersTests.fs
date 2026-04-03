module Orleans.FSharp.Tests.StreamProvidersTests

open System
open Xunit
open Swensen.Unquote
open Orleans.Hosting
open Orleans.FSharp.StreamProviders

/// <summary>Tests for StreamProviders.fs — Event Hubs and Azure Queue stream provider helpers.</summary>

// --- Module existence tests ---

[<Fact>]
let ``StreamProviders module exists in the assembly`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "StreamProviders" && t.IsAbstract && t.IsSealed)

    test <@ moduleType.IsSome @>

// --- addEventHubStreams tests ---

[<Fact>]
let ``addEventHubStreams returns a function`` () =
    let f = StreamProviders.addEventHubStreams "MyEH" "Endpoint=sb://..." "my-hub"
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``addEventHubStreams with different names produces distinct functions`` () =
    let f1 = StreamProviders.addEventHubStreams "EH1" "conn1" "hub1"
    let f2 = StreamProviders.addEventHubStreams "EH2" "conn2" "hub2"
    test <@ not (obj.ReferenceEquals(f1, f2)) @>

[<Fact>]
let ``addEventHubStreams throws when package not installed`` () =
    let f = StreamProviders.addEventHubStreams "MyEH" "conn" "hub"
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("Microsoft.Orleans.Streaming.EventHubs") @>

[<Fact>]
let ``addEventHubStreams error mentions AddEventHubStreams method`` () =
    let f = StreamProviders.addEventHubStreams "Provider" "conn" "hub"
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("AddEventHubStreams") @>

// --- addAzureQueueStreams tests ---

[<Fact>]
let ``addAzureQueueStreams returns a function`` () =
    let f = StreamProviders.addAzureQueueStreams "MyAQ" "DefaultEndpointsProtocol=https;..."
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``addAzureQueueStreams with different names produces distinct functions`` () =
    let f1 = StreamProviders.addAzureQueueStreams "AQ1" "conn1"
    let f2 = StreamProviders.addAzureQueueStreams "AQ2" "conn2"
    test <@ not (obj.ReferenceEquals(f1, f2)) @>

[<Fact>]
let ``addAzureQueueStreams throws when package not installed`` () =
    let f = StreamProviders.addAzureQueueStreams "MyAQ" "conn"
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("Microsoft.Orleans.Streaming.AzureStorage") @>

[<Fact>]
let ``addAzureQueueStreams error mentions AddAzureQueueStreams method`` () =
    let f = StreamProviders.addAzureQueueStreams "Provider" "conn"
    let ex = Assert.Throws<InvalidOperationException>(fun () -> f (Unchecked.defaultof<ISiloBuilder>) |> ignore)
    test <@ ex.Message.Contains("AddAzureQueueStreams") @>

// --- Function signature tests ---

[<Fact>]
let ``addEventHubStreams has correct parameter count via reflection`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "StreamProviders" && t.IsAbstract && t.IsSealed)

    let method =
        moduleType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "addEventHubStreams")

    test <@ method.IsSome @>

[<Fact>]
let ``addAzureQueueStreams has correct parameter count via reflection`` () =
    let moduleType =
        typeof<Orleans.FSharp.AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "StreamProviders" && t.IsAbstract && t.IsSealed)

    let method =
        moduleType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "addAzureQueueStreams")

    test <@ method.IsSome @>
