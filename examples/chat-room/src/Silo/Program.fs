open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime
open ChatRoom.Grains

/// <summary>
/// Local observer implementation that prints received messages to the console.
/// </summary>
type ConsoleObserver(name: string) =
    interface IChatObserver with
        member _.ReceiveMessage(sender: string, message: string) : Task =
            if sender <> name then
                printfn "  [%s sees] %s: %s" name sender message
            Task.CompletedTask

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder
builder.Services.AddFSharpGrain<ChatState, ChatMessage>(ChatGrainDef.chat) |> ignore

let host = builder.Build()

let run () : Task =
    task {
        do! host.StartAsync()

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

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
