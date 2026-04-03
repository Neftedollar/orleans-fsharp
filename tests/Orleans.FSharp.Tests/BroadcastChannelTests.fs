module Orleans.FSharp.Tests.BroadcastChannelTests

open Xunit
open Swensen.Unquote
open Orleans.BroadcastChannel
open Orleans.FSharp.BroadcastChannel

// --- Fake provider for unit testing ---

/// <summary>Fake broadcast channel provider for unit testing.</summary>
type FakeBroadcastChannelProvider() =
    interface IBroadcastChannelProvider with
        member _.GetChannelWriter<'T>(channelId: ChannelId) : IBroadcastChannelWriter<'T> =
            Unchecked.defaultof<IBroadcastChannelWriter<'T>>

// --- BroadcastChannelRef construction tests ---

[<Fact>]
let ``getChannel stores ChannelId with correct namespace and key`` () =
    let provider = FakeBroadcastChannelProvider() :> IBroadcastChannelProvider
    let ref = BroadcastChannel.getChannel<int> provider "test-ns" "test-key"
    let expectedId = ChannelId.Create("test-ns", "test-key")
    test <@ ref.ChannelId = expectedId @>

[<Fact>]
let ``getChannel stores the provider`` () =
    let provider = FakeBroadcastChannelProvider() :> IBroadcastChannelProvider
    let ref = BroadcastChannel.getChannel<int> provider "ns" "key"
    test <@ obj.ReferenceEquals(ref.Provider, provider) @>

[<Fact>]
let ``getChannel preserves namespace in ChannelId`` () =
    let provider = FakeBroadcastChannelProvider() :> IBroadcastChannelProvider
    let ref = BroadcastChannel.getChannel<string> provider "my-namespace" "my-key"
    let expectedId = ChannelId.Create("my-namespace", "my-key")
    test <@ ref.ChannelId = expectedId @>

[<Fact>]
let ``getChannel with different types produces BroadcastChannelRef of correct generic type`` () =
    let provider = FakeBroadcastChannelProvider() :> IBroadcastChannelProvider
    let intRef = BroadcastChannel.getChannel<int> provider "ns" "k"
    let strRef = BroadcastChannel.getChannel<string> provider "ns" "k"
    test <@ intRef.GetType().GenericTypeArguments.[0] = typeof<int> @>
    test <@ strRef.GetType().GenericTypeArguments.[0] = typeof<string> @>

// --- Type signature tests ---

[<Fact>]
let ``BroadcastChannelRef is a record type`` () =
    test <@ Microsoft.FSharp.Reflection.FSharpType.IsRecord(typeof<BroadcastChannelRef<int>>) @>

[<Fact>]
let ``BroadcastChannelRef has Provider and ChannelId fields`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<BroadcastChannelRef<int>>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Provider" @>
    test <@ fields |> Array.contains "ChannelId" @>

[<Fact>]
let ``BroadcastChannelRef Provider field is IBroadcastChannelProvider`` () =
    let providerProp =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<BroadcastChannelRef<int>>)
        |> Array.find (fun p -> p.Name = "Provider")

    test <@ providerProp.PropertyType = typeof<IBroadcastChannelProvider> @>

[<Fact>]
let ``BroadcastChannelRef ChannelId field is ChannelId`` () =
    let channelIdProp =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<BroadcastChannelRef<int>>)
        |> Array.find (fun p -> p.Name = "ChannelId")

    test <@ channelIdProp.PropertyType = typeof<ChannelId> @>

[<Fact>]
let ``publish function exists in BroadcastChannel module`` () =
    let bcModule =
        typeof<BroadcastChannelRef<int>>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "BroadcastChannel" && t.IsAbstract && t.IsSealed)

    test <@ bcModule.IsSome @>

    let publishMethod =
        bcModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "publish")

    test <@ publishMethod.IsSome @>

[<Fact>]
let ``getChannel function exists in BroadcastChannel module`` () =
    let bcModule =
        typeof<BroadcastChannelRef<int>>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "BroadcastChannel" && t.IsAbstract && t.IsSealed)

    test <@ bcModule.IsSome @>

    let getChannelMethod =
        bcModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "getChannel")

    test <@ getChannelMethod.IsSome @>
