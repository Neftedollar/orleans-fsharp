module Orleans.FSharp.Integration.BroadcastChannelIntegrationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Orleans.BroadcastChannel
open Orleans.FSharp.BroadcastChannel

/// <summary>
/// Integration tests for the Orleans.FSharp.BroadcastChannel module.
/// Tests channel ref construction and publish through an active broadcast channel.
/// </summary>
[<Collection("ClusterCollection")>]
type BroadcastChannelIntegrationTests(fixture: ClusterFixture) =

    /// Gets the broadcast channel provider from the silo's service provider.
    let getProvider () =
        let sp = fixture.Cluster.GetSiloServiceProvider(fixture.Cluster.Primary.SiloAddress)
        sp.GetRequiredKeyedService<IBroadcastChannelProvider>("BroadcastProvider")

    [<Fact>]
    member _.``BroadcastChannel ref can be constructed from silo provider`` () =
        task {
            let provider = getProvider ()
            let channelRef = BroadcastChannel.getChannel<string> provider "test-ns" "test-key"
            let expectedId = ChannelId.Create("test-ns", "test-key")
            test <@ channelRef.ChannelId = expectedId @>
            test <@ not (isNull (box channelRef.Provider)) @>
        }

    [<Fact>]
    member _.``Publish to broadcast channel completes without error`` () =
        task {
            let provider = getProvider ()
            let key = Guid.NewGuid().ToString()
            let channelRef = BroadcastChannel.getChannel<int> provider "publish-ns" key

            // Publishing should succeed without exceptions
            do! BroadcastChannel.publish channelRef 42
            do! BroadcastChannel.publish channelRef 99
        }

    [<Fact>]
    member _.``Multiple publishes to same channel complete without error`` () =
        task {
            let provider = getProvider ()
            let key = Guid.NewGuid().ToString()
            let channelRef = BroadcastChannel.getChannel<string> provider "multi-publish-ns" key

            for i in 1..10 do
                do! BroadcastChannel.publish channelRef $"message-{i}"
        }

    [<Fact>]
    member _.``Different channel keys produce independent channels`` () =
        task {
            let provider = getProvider ()
            let ref1 = BroadcastChannel.getChannel<int> provider "ns" "key1"
            let ref2 = BroadcastChannel.getChannel<int> provider "ns" "key2"

            test <@ ref1.ChannelId <> ref2.ChannelId @>

            // Both should publish independently without error
            do! BroadcastChannel.publish ref1 1
            do! BroadcastChannel.publish ref2 2
        }
