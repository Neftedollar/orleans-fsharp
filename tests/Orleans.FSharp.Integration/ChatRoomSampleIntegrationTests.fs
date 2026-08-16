/// <summary>
/// Task 7 (spec 003 Phase 6): runs the runnable end-to-end sample's ACTUAL source
/// (<c>Chat.Contracts</c>/<c>Chat.Server</c> from <c>src/Orleans.FSharp.Sample/ChatRoomFunctional.fs</c>,
/// referenced here via the existing Sample project reference) against a real TestingHost silo +
/// client, exercising join/say/history/typing exactly as the sample's <c>Program.fs</c> does, and
/// printing the same transcript.
/// </summary>
/// <remarks>
/// This is the second half of the sample's proof: <c>Program.fs</c> demonstrates the identical
/// call sequence as a literal standalone console process (<c>dotnet run --project
/// src/Orleans.FSharp.Sample</c>), which this task's report documents separately together with a
/// pre-existing, environment-level Orleans hosting issue (reproduced independently in
/// examples/chat-room too) that currently prevents ANY standalone `Host.CreateApplicationBuilder`
/// + `UseOrleans` process on this machine from resolving grain interfaces at runtime -- unrelated
/// to spec 003 and not a regression this task introduces. TestingHost, which backs the entire
/// 400+-test Integration suite, has no such issue, so this test is the reliable, CI-safe proof
/// that the sample's own contract/definition/registration/call code genuinely works end to end.
/// </remarks>
module Orleans.FSharp.Integration.ChatRoomSampleIntegrationTests

open System
open Chat.Contracts
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

type private ChatRoomSampleSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage "Default" |> ignore
            siloBuilder.AddFunctionalGrain Chat.Server.Definition.roomDefinition |> ignore

type private ChatRoomSampleClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type ChatRoomSampleClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<ChatRoomSampleSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<ChatRoomSampleClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Client = cluster.Client

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("ChatRoomSampleCluster")>]
type ChatRoomSampleClusterCollection() =
    interface ICollectionFixture<ChatRoomSampleClusterFixture>

[<Collection("ChatRoomSampleCluster")>]
type ChatRoomSampleTests(fixture: ChatRoomSampleClusterFixture) =

    /// <remarks>
    /// Exactly the sample's own call sequence: join, say (twice), typing, a rejected say from a
    /// non-member, then history -- driven through <c>RoomApi.ref</c>, the spec's point-free
    /// <c>let ref = FunctionalGrain.ref contract</c> binding, unmodified from the sample source.
    /// </remarks>
    [<Fact>]
    member _.``the sample's chat room runs end to end: join, say, typing, and history``() =
        task {
            let roomId = RoomId.create $"integration-{Guid.NewGuid():N}"
            let lobby = RoomApi.ref fixture.Client roomId

            do! lobby.join (UserId.create "alice")
            do! lobby.join (UserId.create "bob")
            printfn "alice and bob joined #%s" (RoomId.value roomId)

            let! aliceResult = lobby.say { author = UserId.create "alice"; text = "Hey everyone!" }
            printfn "alice says \"Hey everyone!\" -> %A" aliceResult
            Assert.Equal(Ok 1L, aliceResult)

            let! bobResult =
                lobby.say
                    { author = UserId.create "bob"
                      text = "Hi Alice, how's it going?" }

            printfn "bob says \"Hi Alice, how's it going?\" -> %A" bobResult
            Assert.Equal(Ok 2L, bobResult)

            do! lobby.typing { user = UserId.create "alice"; isTyping = true }
            printfn "alice is typing..."

            let! rejected = lobby.say { author = UserId.create "carol"; text = "Can I join?" }
            printfn "carol (not a member) says \"Can I join?\" -> %A" rejected
            Assert.Equal(Error NotAMember, rejected)

            let! history = lobby.history { take = 20 }
            printfn "history (most recent last):"

            for message in history do
                printfn "  %s: %s" (UserId.value message.author) message.text

            // Two accepted messages, oldest first (say prepends, history reverses).
            Assert.Equal<string list>(
                [ "Hey everyone!"; "Hi Alice, how's it going?" ],
                history |> List.map (fun message -> message.text)
            )

            Assert.Equal<string list>([ "alice"; "bob" ], history |> List.map (fun message -> UserId.value message.author))
        }
