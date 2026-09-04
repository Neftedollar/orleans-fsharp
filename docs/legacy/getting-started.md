# Legacy Getting Started

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

**Migration archive for the original Orleans.FSharp grain authoring model.**

> This page is intentionally isolated from the current functional API documentation. New
> applications should start with [Getting Started](../getting-started.md).

## Define and run a classic grain

Everything below this heading records the original `grain { }` CE and universal
`FSharpGrain.ref`/`send`/`ask` pattern from 4.1-and-earlier releases. It is frozen as migration
reference and is not verified against the 5.0 package set. New code should use the functional
runtime -- see [functional-grains.md](../functional-grains.md).

### Define state and commands

Define your state and commands as plain F# types. **No `[<GenerateSerializer>]` or `[<Id>]` attributes needed** — the built-in `FSharpBinaryCodec` handles serialization automatically.

```fsharp
open Orleans.FSharp
open Orleans.FSharp.Runtime

// Plain record — no attributes
type CounterState = { Count: int }

// Plain DU — no attributes
type CounterCommand =
    | Increment
    | Decrement
    | GetValue
```

### Define the grain

Use the `grain { }` computation expression. `handleTyped` is the most convenient handler variant — it auto-boxes the result so you never write `box` by hand:

```fsharp
let counter =
    grain {
        defaultState { Count = 0 }

        handleTyped (fun state cmd ->
            task {
                match cmd with
                | Increment -> return { Count = state.Count + 1 }, state.Count + 1
                | Decrement -> return { Count = state.Count - 1 }, state.Count - 1
                | GetValue  -> return state, state.Count
            })

        persist "Default"  // name of the storage provider
    }
```

The handler returns `(newState, result)` — the types are inferred, no `box` needed.
Use `handle` (manual `box`) when the return type varies per command case; use `handleState`
when you only care about state and don't need to return a separate result.
The `persist` keyword names the storage provider for durable state.

### Configure the silo

```fsharp
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}
```

`addMemoryStorage "Default"` wires in-memory state storage for the `persist "Default"` keyword above (data is cleared on restart; swap for Redis or Azure in production).

### Register the grain and start the host

```fsharp
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection

[<EntryPoint>]
let main _ =
    let builder = HostApplicationBuilder()

    // Register the grain definition with the universal dispatcher.
    // FSharpBinaryCodec is registered automatically — nothing else needed.
    builder.Services.AddFSharpGrain<CounterState, CounterCommand>(counter) |> ignore

    SiloConfig.applyToHost config builder

    let host = builder.Build()
    host.Start()

    let factory = host.Services.GetRequiredService<IGrainFactory>()

    // Get a typed handle — no generated interface required
    let handle = FSharpGrain.ref<CounterState, CounterCommand> factory "my-counter"

    // Send a command, get back the state
    let state = handle |> FSharpGrain.send Increment |> _.GetAwaiter().GetResult()
    printfn "Count after increment = %d" state.Count

    // ask returns a typed result (int here), not the state
    let count = handle |> FSharpGrain.ask<CounterState, CounterCommand, int> GetValue |> _.GetAwaiter().GetResult()
    printfn "Current count = %d" count

    printfn "Silo running. Press Enter to stop."
    System.Console.ReadLine() |> ignore
    host.StopAsync().GetAwaiter().GetResult()
    0
```

`FSharpGrain.ref` returns a zero-allocation struct handle (`FSharpGrainHandle<CounterState, CounterCommand>`). Piping commands through `FSharpGrain.send` (returns state) or `FSharpGrain.post` (fire-and-forget) keeps call sites clean.

### Key types at a glance

| Name | Purpose |
|---|---|
| `grain { }` | Computation expression to define grain behavior |
| `siloConfig { }` | Computation expression to configure the silo |
| `FSharpGrain.ref` | Create a string-keyed typed grain handle |
| `FSharpGrain.refGuid` | Create a GUID-keyed typed grain handle |
| `FSharpGrain.refInt` | Create an integer-keyed typed grain handle |
| `FSharpGrain.send` | Send command, return typed state (`Task<'State>`) |
| `FSharpGrain.ask` | Send command, return a different typed result (`Task<'R>`) |
| `FSharpGrain.post` | Fire-and-forget command |
| `AddFSharpGrain<S,M>` | Register a grain definition in DI |

### GUID and integer keys

```fsharp
open System

// GUID-keyed grain
let guidHandle = FSharpGrain.refGuid<CounterState, CounterCommand> factory (Guid.NewGuid())
let! state = guidHandle |> FSharpGrain.sendGuid Increment

// Integer-keyed grain
let intHandle = FSharpGrain.refInt<CounterState, CounterCommand> factory 42L
do! intHandle |> FSharpGrain.postInt Increment
```

### Model a state machine

A classic F# pattern is a DU state machine where the compiler enforces valid transitions:

```fsharp
type OrderState =
    | Created
    | Confirmed of confirmedAt: System.DateTime
    | Shipped   of trackingNumber: string
    | Delivered

type OrderCommand =
    | Confirm
    | Ship of trackingNumber: string
    | MarkDelivered
    | GetStatus

let order =
    grain {
        defaultState Created

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Created,    Confirm            -> return Confirmed System.DateTime.UtcNow, box "confirmed"
                | Confirmed _, Ship tracking     -> return Shipped tracking, box tracking
                | Shipped _,  MarkDelivered      -> return Delivered, box "delivered"
                | _, GetStatus ->
                    let status =
                        match state with
                        | Created       -> "created"
                        | Confirmed _   -> "confirmed"
                        | Shipped t     -> $"shipped ({t})"
                        | Delivered     -> "delivered"
                    return state, box status
                | _ -> return state, box "invalid transition"
            })
    }
```

The F# compiler enforces exhaustive matching — illegal state/command pairs are compile errors.

### Write a property test with FsCheck

```bash
dotnet add package Orleans.FSharp.Testing
dotnet add package FsCheck.Xunit
dotnet add package xunit
```

```fsharp
open FsCheck.Xunit
open Orleans.FSharp

// Drive the grain handler directly — no silo, instant feedback.
let applyViaHandler (state: CounterState) cmd =
    let h = GrainDefinition.getHandler counter
    fst ((h state cmd).GetAwaiter().GetResult())

[<Property>]
let ``Count equals net of Increments minus Decrements`` (commands: CounterCommand list) =
    let final = List.fold applyViaHandler { Count = 0 } commands
    let net =
        commands |> List.sumBy (function
            | Increment ->  1
            | Decrement -> -1
            | GetValue  ->  0)
    final.Count = net

[<Property>]
let ``GetValue never changes state`` (state: CounterState) =
    let h = GrainDefinition.getHandler counter
    let (ns, _) = (h state GetValue).GetAwaiter().GetResult()
    ns = state
```

### Run it

```bash
dotnet build
dotnet run --project MyCounter.Silo
dotnet test
```

## Related legacy documentation

- [Grain Definition](grain-definition.md)
- [Redis Example](redis-example.md)
