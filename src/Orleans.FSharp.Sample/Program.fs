open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.Sample
open Chat.Contracts

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

// Register all [<FSharpGrain>]-annotated definitions in this assembly automatically
builder.Services.AddFSharpGrainsFromAssembly(typeof<CounterState>.Assembly) |> ignore
// Grains without the attribute still register manually:
builder.Services.AddFSharpGrain<OrderStatus, OrderCommand>(OrderGrainDef.order) |> ignore

// Spec 003's functional-runtime chat room: `AddFunctionalGrain` on the silo builder is enough
// for a colocated process, since the same `IGrainFactory` that hosts the definition also binds
// its own functional references (see FunctionalTransportSource.Guidance) -- a genuinely separate
// client process would call `clientBuilder.AddFunctionalGrainClient()` instead.
builder.UseOrleans(fun siloBuilder -> siloBuilder.AddFunctionalGrain(Chat.Server.Definition.roomDefinition) |> ignore)
|> ignore
builder.Services.AddFSharpGrain<string, EchoCommand>(EchoGrainDef.echo) |> ignore

let host = builder.Build()

/// Run the sample silo, make a few grain calls, then exit cleanly.
let runSample () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

        // Functional grain runtime demo (spec 003) — the exact chat room from the
        // specification's "Public authoring model" section, driven end to end: `RoomApi.ref` is
        // the point-free `let ref = FunctionalGrain.ref contract` binding. Runs first so it
        // completes independently of the older CodeGen-backed demos below.
        printfn "--- Functional Grain Runtime Demo (chat.room) ---"

        let lobby = RoomApi.ref factory (RoomId.create "general")

        do! lobby.join (UserId.create "alice")
        do! lobby.join (UserId.create "bob")
        printfn "alice and bob joined #general"

        let! aliceResult = lobby.say { author = UserId.create "alice"; text = "Hey everyone!" }
        printfn "alice says \"Hey everyone!\" -> %A" aliceResult

        let! bobResult =
            lobby.say
                { author = UserId.create "bob"
                  text = "Hi Alice, how's it going?" }

        printfn "bob says \"Hi Alice, how's it going?\" -> %A" bobResult

        // typing is oneWay + alwaysInterleave: the call acknowledges locally and never blocks
        // on the target's own scheduling.
        do! lobby.typing { user = UserId.create "alice"; isTyping = true }
        printfn "alice is typing..."

        // say against a non-member fails without touching the message list — the readOnly
        // history call below still reports only the two accepted messages.
        let! rejected = lobby.say { author = UserId.create "carol"; text = "Can I join?" }
        printfn "carol (not a member) says \"Can I join?\" -> %A" rejected

        let! history = lobby.history { take = 20 }
        printfn "history (most recent last):"

        for message in history do
            printfn "  [%s] %s: %s" (message.sentAt.ToString "HH:mm:ss") (UserId.value message.author) message.text

        // Counter grain demo
        printfn ""
        let counterRef = GrainRef.ofInt64<ICounterGrain> factory 1L
        printfn "--- Counter Grain Demo ---"

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
        printfn "After Increment: %A" result

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Increment))
        printfn "After Increment: %A" result

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(GetValue))
        printfn "Current value: %A" result

        let! result = GrainRef.invoke counterRef (fun g -> g.HandleMessage(Decrement))
        printfn "After Decrement: %A" result

        // Echo grain demo
        let echoRef = GrainRef.ofString<IEchoGrain> factory "world"
        printfn ""
        printfn "--- Echo Grain Demo ---"

        let! result = GrainRef.invoke echoRef (fun g -> g.HandleMessage(Echo "hello"))
        printfn "Echo: %A" result

        let! result = GrainRef.invoke echoRef (fun g -> g.HandleMessage(Greet))
        printfn "Greet: %A" result

        // Order grain demo
        let orderRef = GrainRef.ofString<IOrderGrain> factory "order-001"
        printfn ""
        printfn "--- Order Grain Demo ---"

        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Place "Widget x10"))
        printfn "Place: %A" result

        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(Confirm))
        printfn "Confirm: %A" result

        let! result = GrainRef.invoke orderRef (fun g -> g.HandleMessage(GetStatus))
        printfn "Status: %A" result

        // Universal grain pattern demo (FSharpGrain.ref — no CodeGen interface)
        printfn ""
        printfn "--- Universal Grain Pattern Demo (no C# stubs) ---"

        // String-keyed: FSharpGrainImpl handles messages via IUniversalGrainHandler dispatch.
        // FSharpBinaryCodec was registered automatically by AddFSharpGrain above.
        let uHandle = FSharpGrain.ref<CounterState, CounterCommand> factory "universal-counter"

        let! s1 = uHandle |> FSharpGrain.send Increment
        printfn "Universal counter after Increment: %A" s1

        let! s2 = uHandle |> FSharpGrain.send Increment
        printfn "Universal counter after Increment: %A" s2

        do! uHandle |> FSharpGrain.post Decrement   // fire-and-forget (no return value needed)
        let! s3 = uHandle |> FSharpGrain.send GetValue
        printfn "Universal counter after Decrement: %A" s3

        // ask demo — returns typed result ('R), not the full state ('S)
        // The counter handler returns box<int> for all commands, so ask<_, _, int> extracts the int directly.
        printfn ""
        printfn "--- ask Demo (typed result, not full state) ---"

        let askHandle = FSharpGrain.ref<CounterState, CounterCommand> factory "ask-demo-counter"

        // ask<'State, 'Command, 'Result> — result type is int here (not CounterState)
        let! count1 = askHandle |> FSharpGrain.ask<CounterState, CounterCommand, int> Increment
        printfn "After Increment (ask → int): %d" count1

        let! count2 = askHandle |> FSharpGrain.ask<CounterState, CounterCommand, int> Increment
        printfn "After Increment (ask → int): %d" count2

        let! value = askHandle |> FSharpGrain.ask<CounterState, CounterCommand, int> GetValue
        printfn "GetValue via ask: %d" value

        // Compare: send returns the full CounterState record
        let! fullState = askHandle |> FSharpGrain.send GetValue
        printfn "GetValue via send (full state): %A" fullState

        printfn ""
        printfn "Sample complete. Shutting down..."
        do! host.StopAsync()
    }

runSample().GetAwaiter().GetResult()
