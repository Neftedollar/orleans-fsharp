open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime
open ChatRoom.Grains

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.Services.AddFSharpGrain<ChatState, ChatMessage>(ChatGrainDef.chat) |> ignore

// Functional-runtime equivalent of the grain above -- see ChatGrainFunctional.fs.
builder.UseOrleans(fun siloBuilder -> siloBuilder.AddFunctionalGrain(RoomFunctionalDef.room) |> ignore)
|> ignore

let host = builder.Build()

(*
    Classic grain { } model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so a bare `factory.GetGrain<IChatGrain>(...)` fails with
    "Could not find an implementation for interface IChatGrain" the moment it runs -- this example
    never had a C# CodeGen bridge project to fill that gap. See docs/functional-grains.md,
    "Running a silo from a standalone F# process" for the exact mechanism, and "Migrating from
    the grain { } CE" for the rewrite this file demonstrates.

    The observer/pub-sub slice below (Subscribe/Unsubscribe/IChatObserver) is blocked by a second,
    orthogonal Orleans constraint that applies regardless of grain{} vs. the functional runtime:
    an observer *interface* needs a source-generated proxy too, and that generator only runs on a
    C# project. ChatRoom.sln has none, and IChatObserver is declared in F# (ChatTypes.fs), so the
    observer slice cannot be reproduced standalone under either model here. This is not a
    functional-runtime capability gap -- FSharpObserverManager and Observer.createRef work
    unchanged inside grainFor handlers when the observer interface *is* C#-declared, proven by
    tests/Orleans.FSharp.Integration/FunctionalObserverIntegrationTests.fs and documented under
    "Observers, streams, and the other orthogonal surfaces" in docs/functional-grains.md.

    /// Local observer implementation that prints received messages to the console.
    type ConsoleObserver(name: string) =
        interface IChatObserver with
            member _.ReceiveMessage(sender: string, message: string) : Task =
                if sender <> name then
                    printfn "  [%s sees] %s: %s" name sender message
                Task.CompletedTask

    let factory = host.Services.GetRequiredService<IGrainFactory>()
    let chatRef = GrainRef.ofString<IChatGrain> factory "general"

    // Create two simulated clients
    let alice = ConsoleObserver("Alice")
    let bob = ConsoleObserver("Bob")

    let aliceRef = Observer.createRef<IChatObserver> factory alice
    let bobRef = Observer.createRef<IChatObserver> factory bob

    let! _ = GrainRef.invoke chatRef (fun g -> g.HandleMessage(Subscribe aliceRef))
    let! _ = GrainRef.invoke chatRef (fun g -> g.HandleMessage(Subscribe bobRef))

    let! countResult = GrainRef.invoke chatRef (fun g -> g.HandleMessage(GetSubscriberCount))
    let count = unbox<int> countResult
    printfn "--- Chat Room: %d subscribers connected ---" count
    printfn ""

    // Simulate a conversation
    let messages =
        [ "Alice", "Hey everyone!"
          "Bob", "Hi Alice, how's it going?"
          "Alice", "Great! Just trying out Orleans.FSharp observers."
          "Bob", "That's awesome, the DX is really clean." ]

    for (sender, msg) in messages do
        printfn "%s: %s" sender msg
        let! _ = GrainRef.invoke chatRef (fun g -> g.HandleMessage(SendMessage(sender, msg)))
        do! Task.Delay(300)

    printfn ""

    // Unsubscribe Bob
    let! _ = GrainRef.invoke chatRef (fun g -> g.HandleMessage(Unsubscribe bobRef))
    Observer.deleteRef<IChatObserver> factory bobRef

    printfn "Bob left the chat."
    printfn "Alice: Anyone still here?"
    let! _ = GrainRef.invoke chatRef (fun g -> g.HandleMessage(SendMessage("Alice", "Anyone still here?")))
    do! Task.Delay(300)

    // Cleanup
    let! _ = GrainRef.invoke chatRef (fun g -> g.HandleMessage(Unsubscribe aliceRef))
    Observer.deleteRef<IChatObserver> factory aliceRef
*)

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<IGrainFactory>()

        printfn "--- Chat Room (Functional Grain Runtime) ---"
        printfn "Note: push notification (observers) hit a C#-codegen wall in this example --"
        printfn "see ChatGrainFunctional.fs's header. history() below is the honest fallback:"
        printfn "clients poll for new messages instead of receiving a push."
        printfn ""
        let room = RoomApi.ref factory "general"

        // Multiple members join.
        do! room.join "Alice"
        do! room.join "Bob"
        let! count = room.memberCount ()
        printfn "--- Chat Room: %d members joined ---" count
        printfn ""

        // Both members can post.
        let! aliceResult = room.say ("Alice", "Hey everyone!")
        printfn "Alice: Hey everyone! -> %A" aliceResult
        let! bobResult = room.say ("Bob", "Hi Alice, how's it going?")
        printfn "Bob: Hi Alice, how's it going? -> %A" bobResult

        // A non-member is rejected.
        let! rejected = room.say ("Charlie", "Can I join in?")
        printfn "Charlie (not a member): Can I join in? -> %A" rejected

        // Empty text is rejected too.
        let! emptyRejected = room.say ("Alice", "   ")
        printfn "Alice (empty message) -> %A" emptyRejected

        // Typing indicator: fire-and-forget, always interleaves. The field is spelled curried,
        // so the call takes its two arguments one after the other and builds no tuple.
        do! room.typing "Bob" true

        printfn ""

        // Bob leaves; a message from Bob after leaving is rejected again.
        do! room.leave "Bob"
        let! afterLeave = room.say ("Bob", "Anyone still here?")
        printfn "Bob left. Bob: Anyone still here? -> %A" afterLeave

        let! finalCount = room.memberCount ()
        printfn "Members remaining: %d" finalCount

        // The polling fallback: read the message history back.
        printfn ""
        printfn "--- History (poll-based fallback for push notification) ---"
        let! history = room.history 10
        for (sender, message, timestamp) in history do
            printfn "  [%s] %s: %s" (timestamp.ToString("HH:mm:ss")) sender message

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
