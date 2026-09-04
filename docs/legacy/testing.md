# Legacy Testing

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

**Testing patterns for the original Orleans.FSharp grain and event-sourcing APIs.**

> This page records the 4.1-and-earlier helper surface and is not verified against the 5.0 package
> set. Current testing guidance lives in [Testing](../testing.md).

## Testing Handler Logic Directly (No Silo Required)

The fastest way to test a grain is to call its handler function directly — no TestCluster, no DI setup.

```fsharp
open Orleans.FSharp
open Xunit

type CounterState = { Count: int }
type CounterCommand = Increment | Decrement | GetValue

let counter =
    grain {
        defaultState { Count = 0 }
        handle (fun state cmd ->
            task {
                match cmd with
                | Increment -> return { Count = state.Count + 1 }, box (state.Count + 1)
                | Decrement -> return { Count = state.Count - 1 }, box (state.Count - 1)
                | GetValue  -> return state, box state.Count
            })
    }

[<Fact>]
let ``increment increases count`` () =
    task {
        let handler = GrainDefinition.getHandler counter
        let! newState, result = handler { Count = 0 } Increment
        Assert.Equal({ Count = 1 }, newState)
        Assert.Equal(box 1, result)
    }
```

`GrainDefinition.getHandler` extracts the pure `state -> command -> Task<state * obj>` function — no grain activation, no silo, instant feedback.

**Functional equivalent.** A `FunctionalGrainDefinition` exposes no comparable extraction function --
but you never lose the handler in the first place, since `handle (_.op) handler` takes *your own*
plain function value rather than hiding it inside the CE. A handler that ignores its `context`
argument is directly callable:

```fsharp
open Orleans.FSharp

let increment (_context: FunctionalGrainContext<CounterActor, string>) (state: int) () =
    task { let next = state + 1 in return next, next }

[<Fact>]
let ``increment increases count (functional)`` () =
    task {
        let! newState, result = increment Unchecked.defaultof<_> 0 ()
        Assert.Equal(1, newState)
        Assert.Equal(1, result)
    }
```

A handler that *does* read `context` (services, persistent state, grain factory, request context)
cannot be unit-tested this way: `FunctionalGrainContext`'s constructor is `internal`, so nothing
outside the library can fabricate one. That handler needs a real activation -- see
[Testing a Functional Grain](../testing.md#testing-a-functional-grain).

---

## Testing the Universal Grain Pattern

Use `UniversalGrainHandlerRegistry` directly to verify dispatch behavior without a cluster:

```fsharp
open Orleans.FSharp.Runtime

[<Fact>]
let ``registry dispatches Increment`` () =
    task {
        let registry = UniversalGrainHandlerRegistry()
        registry.Register<CounterState, CounterCommand>(counter)

        let handler = registry :> IUniversalGrainHandler
        let! result = handler.Handle(null, box Increment)
        let state = result.NewState :?> CounterState
        Assert.Equal(1, state.Count)
    }
```

For integration tests with a real silo, wire up `AddFSharpGrain` in the cluster configurator:

```fsharp
type MySiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            // FSharpBinaryCodec is registered automatically — nothing else needed
            siloBuilder.Services.AddFSharpGrain<CounterState, CounterCommand>(counter) |> ignore

[<Fact>]
let ``FSharpGrain.ref round-trip`` () =
    task {
        // Assumes a TestCluster started with MySiloConfigurator
        let handle = FSharpGrain.ref<CounterState, CounterCommand> grainFactory "test-key"
        let! state = handle |> FSharpGrain.send Increment
        Assert.Equal(1, state.Count)
    }
```

---

## GrainMock

`GrainMock` provides a mock `IGrainFactory` for unit testing grain interactions without starting a real silo.

### Create a mock factory

```fsharp
open Orleans.FSharp.Testing

let factory =
    GrainMock.create()
    |> GrainMock.withGrain<ICounterGrain> "counter-1" mockCounterImpl
    |> GrainMock.withGrain<IOrderGrain> "order-42" mockOrderImpl
```

### Use in a GrainContext

```fsharp
let ctx : GrainContext =
    {
        GrainFactory = factory :> IGrainFactory
        ServiceProvider = serviceProvider
        States = Map.empty
        DeactivateOnIdle = None
        DelayDeactivation = None
        GrainId = None
        PrimaryKey = None
    }

// Now test a handleWithContext handler
let handler = GrainDefinition.getContextHandler myGrainDefinition
let! newState, result = handler ctx initialState myCommand
```

### Register mocks for different key types

```fsharp
let factory =
    GrainMock.create()
    |> GrainMock.withGrain<ISessionGrain> (Guid.Parse("...")) mockSession
    |> GrainMock.withGrain<IOrderGrain> 42L mockOrder
    |> GrainMock.withGrain<IChatGrain> "room-1" mockChat
```

The key is converted to a string internally for lookup matching.

### Mock universal-pattern grains (no silo needed)

`withFSharpGrain` / `withFSharpGrainGuid` / `withFSharpGrainInt` register an in-memory
`IFSharpGrain` implementation built directly from your grain definition. You can unit-test
code that calls `FSharpGrain.ref/send/ask/post` without starting a `TestCluster`:

```fsharp
open Orleans.FSharp.Testing

// Define the grain with the grain { } CE as usual
let counterDef =
    grain {
        defaultState { Count = 0 }
        handle (fun state (cmd: CounterCommand) ->
            task {
                match cmd with
                | Increment -> let ns = { Count = state.Count + 1 } in return ns, box ns
                | GetValue  -> return state, box state
            })
    }

// Create a mock factory with the grain registered for key "test-counter"
let factory =
    GrainMock.create()
    |> GrainMock.withFSharpGrain "test-counter" counterDef

// Call it exactly as you would in production code
let handle = FSharpGrain.ref<CounterState, CounterCommand> factory "test-counter"
let! state = handle |> FSharpGrain.send Increment   // returns CounterState

// ask also works
let typedDef =
    grain {
        defaultState { Count = 0 }
        handleTyped (fun state (cmd: CounterCommand) ->
            task {
                match cmd with
                | Increment -> return { Count = state.Count + 1 }, state.Count + 1
                | GetValue  -> return state, state.Count
            })
    }
let typedFactory = GrainMock.create() |> GrainMock.withFSharpGrain "test" typedDef
let handle2 = FSharpGrain.ref<CounterState, CounterCommand> typedFactory "test"
let! count = handle2 |> FSharpGrain.ask<CounterState, CounterCommand, int> Increment

// GUID and int64 keys
let guidFactory = GrainMock.create() |> GrainMock.withFSharpGrainGuid myGuid counterDef
let intFactory  = GrainMock.create() |> GrainMock.withFSharpGrainInt 42L counterDef
```

The mock maintains grain state across calls exactly like a real grain — each `send` or `ask`
updates the internal state.

**Functional equivalent: none today.** `GrainMock` has no `withFunctionalGrain*` counterpart --
verified against its public surface, which stops at `create` / `withGrain` / `withFSharpGrain*`. A
functional grain's handlers can only run inside a real activation, so the supported unit-testing
path is either calling a context-free handler function directly, or a `TestingHost` cluster; see
[Testing a Functional Grain](../testing.md#testing-a-functional-grain).

---

## Testing Event-Sourced Grains

Event-sourced grains are especially testable because `apply` and `handle` are pure functions:

```fsharp
open Orleans.FSharp.EventSourcing

[<Fact>]
let ``deposit produces Deposited event`` () =
    let state = { Balance = 100m; TransactionCount = 0 }
    let newState, events =
        EventSourcedGrainDefinition.handleCommand bankAccount state (Deposit 50m)
    Assert.Equal([ Deposited 50m ], events)
    Assert.Equal(150m, newState.Balance)

[<Fact>]
let ``apply Deposited increases balance`` () =
    let state = { Balance = 100m; TransactionCount = 0 }
    let newState = EventSourcedGrainDefinition.applyEvent bankAccount state (Deposited 50m)
    Assert.Equal(150m, newState.Balance)

[<Property>]
let ``replaying events produces the same state as fold`` () =
    let arb = GrainArbitrary.forCommands<BankAccountCommand>()
    Prop.forAll arb (fun commands ->
        let mutable state = { Balance = 0m; TransactionCount = 0 }
        let mutable allEvents = []
        for cmd in commands do
            let newState, events =
                EventSourcedGrainDefinition.handleCommand bankAccount state cmd
            allEvents <- allEvents @ events
            state <- newState

        let replayed =
            EventSourcedGrainDefinition.foldEvents bankAccount
                { Balance = 0m; TransactionCount = 0 }
                allEvents

        state = replayed)
```

---

## Testing Handlers Directly

> **Functional equivalent.** Everything in this section is `GrainDefinition`'s handler-extraction
> surface, which the functional runtime does not have (and does not need -- see
> [Testing Handler Logic Directly](#testing-handler-logic-directly-no-silo-required) above for why).
> The nearest functional-runtime analogue for a context-free handler is calling your own function
> directly; for a context-touching one, see [Testing a Functional Grain](../testing.md#testing-a-functional-grain).

All 12 CE handler variants can be tested without a silo by extracting the handler function from a
`GrainDefinition` and calling it directly. Three dispatch helpers cover all variants:

| Helper | Variants covered |
|---|---|
| `GrainDefinition.getHandler` | `handle`, `handleState`, `handleTyped` |
| `GrainDefinition.getContextHandler` | `handleWithContext`, `handleStateWithContext`, `handleTypedWithContext`, and their `WithServices` aliases |
| `GrainDefinition.getCancellableContextHandler` | all six `*Cancellable` variants (falls back through the chain) |

### `handle` / `handleState` / `handleTyped`

```fsharp
open Orleans.FSharp
open Xunit

let counter =
    grain {
        defaultState { Count = 0 }
        handleTyped (fun state (cmd: CounterCommand) ->
            task {
                match cmd with
                | Increment -> return { Count = state.Count + 1 }, state.Count + 1
                | GetValue  -> return state, state.Count
            })
    }

[<Fact>]
let ``increment returns next count`` () =
    task {
        let handler = GrainDefinition.getHandler counter
        let! newState, boxedResult = handler { Count = 0 } Increment
        Assert.Equal({ Count = 1 }, newState)
        Assert.Equal(box 1, boxedResult)
    }
```

### `handleWithContext` / `handleStateWithContext` / `handleTypedWithContext`

Pass `Unchecked.defaultof<GrainContext>` when the handler does not actually use the context (common in
unit tests). Use `GrainMock` to build a real context when the handler calls `ctx.GrainFactory`:

```fsharp
open System.Threading
open Orleans.FSharp

let sumGrain =
    grain {
        defaultState 0
        handleStateWithContext (fun _ctx state (delta: int) ->
            task { return state + delta })
    }

[<Fact>]
let ``handleStateWithContext accumulates correctly`` () =
    task {
        let handler = GrainDefinition.getContextHandler sumGrain
        let ctx = Unchecked.defaultof<GrainContext>
        let! s1, _ = handler ctx 0 10
        let! s2, _ = handler ctx s1 5
        Assert.Equal(15, s2)
    }
```

### All `*Cancellable` variants

`getCancellableContextHandler` is the universal entry point for every cancellable variant. It follows
the fallback chain `CancellableContextHandler → CancellableHandler → ContextHandler → Handler`, so
you always get the most-specific handler registered. Use `CancellationToken.None` in tests unless
you are specifically testing cancellation behaviour:

```fsharp
open System.Threading
open Orleans.FSharp

// handleStateWithContextCancellable
let accumulator =
    grain {
        defaultState 0
        handleStateWithContextCancellable (fun _ctx state (delta: int) _ct ->
            task { return state + delta })
    }

[<Fact>]
let ``handleStateWithContextCancellable accumulates`` () =
    task {
        let handler = GrainDefinition.getCancellableContextHandler accumulator
        let ctx = Unchecked.defaultof<GrainContext>
        let! s1, _ = handler ctx 0  10 CancellationToken.None
        let! s2, _ = handler ctx s1  5 CancellationToken.None
        Assert.Equal(15, s2)
    }

// handleTypedWithContextCancellable — result type differs from state type
let calculator =
    grain {
        defaultState 0
        handleTypedWithContextCancellable (fun _ctx state (n: int) _ct ->
            task { return state + n, string (state + n) })
    }

[<Fact>]
let ``handleTypedWithContextCancellable returns typed result`` () =
    task {
        let handler = GrainDefinition.getCancellableContextHandler calculator
        let ctx = Unchecked.defaultof<GrainContext>
        let! newState, boxed = handler ctx 5 3 CancellationToken.None
        Assert.Equal(8, newState)
        Assert.Equal("8", unbox<string> boxed)
    }
```

### Quick reference: which helper to use

```fsharp
// plain handle / handleState / handleTyped
let h = GrainDefinition.getHandler myDef
let! (ns, r) = h state msg

// handleWithContext / handleStateWithContext / handleTypedWithContext
let h = GrainDefinition.getContextHandler myDef
let! (ns, r) = h ctx state msg

// any *Cancellable variant (also works as fallback for non-cancellable)
let h = GrainDefinition.getCancellableContextHandler myDef
let! (ns, r) = h ctx state msg CancellationToken.None
```

---

## Complete Test Suite Example

The example below shows the full testing spectrum for a score-tracking grain — unit tests,
property-based state-machine tests, and cross-variant equivalence checks.

```fsharp
open System.Threading
open Xunit
open Swensen.Unquote
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.FSharp.Testing

// ── domain ───────────────────────────────────────────────────────────────────

type ScoreState = { Wins: int; Losses: int; Draws: int }
    with member s.NetScore = s.Wins - s.Losses

type ScoreCommand = Win | Lose | Draw | Reset | GetScore

// ── grain definition ─────────────────────────────────────────────────────────

let scoreGrain =
    grain {
        defaultState { Wins = 0; Losses = 0; Draws = 0 }
        handleTyped (fun state (cmd: ScoreCommand) ->
            task {
                match cmd with
                | Win   -> return { state with Wins    = state.Wins    + 1 }, state.Wins + 1
                | Lose  -> return { state with Losses  = state.Losses  + 1 }, state.Losses + 1
                | Draw  -> return { state with Draws   = state.Draws   + 1 }, state.Draws + 1
                | Reset -> return { Wins = 0; Losses = 0; Draws = 0 },        0
                | GetScore -> return state, state.NetScore
            })
    }

// ── unit tests ───────────────────────────────────────────────────────────────

module ScoreUnitTests =

    [<Fact>]
    let ``Win increments wins`` () =
        task {
            let h = GrainDefinition.getHandler scoreGrain
            let! ns, _ = h { Wins = 0; Losses = 0; Draws = 0 } Win
            test <@ ns.Wins = 1 @>
        }

    [<Fact>]
    let ``Reset zeroes all counters`` () =
        task {
            let h = GrainDefinition.getHandler scoreGrain
            let! ns, _ = h { Wins = 5; Losses = 3; Draws = 2 } Reset
            test <@ ns = { Wins = 0; Losses = 0; Draws = 0 } @>
        }

    [<Fact>]
    let ``GetScore returns net score`` () =
        task {
            let h = GrainDefinition.getHandler scoreGrain
            let! _, boxed = h { Wins = 7; Losses = 3; Draws = 1 } GetScore
            test <@ unbox<int> boxed = 4 @>
        }

// ── property tests ───────────────────────────────────────────────────────────

module ScoreProperties =

    let applyHandler (state: ScoreState) (cmd: ScoreCommand) =
        let h = GrainDefinition.getHandler scoreGrain
        let (ns, _) = (h state cmd).Result
        ns

    // Core invariant: NetScore = Wins - Losses must always hold
    [<Property>]
    let ``net score invariant holds for any command sequence`` (commands: ScoreCommand list) =
        let mutable s = { Wins = 0; Losses = 0; Draws = 0 }
        for cmd in commands do
            s <- applyHandler s cmd
        s.NetScore = s.Wins - s.Losses

    // Monotonicity: total count never decreases except after Reset
    [<Property>]
    let ``total count only resets on Reset`` (commands: ScoreCommand list) =
        let mutable s = { Wins = 0; Losses = 0; Draws = 0 }
        let mutable ok = true
        for cmd in commands do
            let prev = s.Wins + s.Losses + s.Draws
            s <- applyHandler s cmd
            let curr = s.Wins + s.Losses + s.Draws
            if cmd <> Reset then
                ok <- ok && (curr >= prev)
        ok

    // Cross-variant equivalence: handleTyped and getCancellableContextHandler
    // must produce identical state for every command sequence
    [<Property>]
    let ``getCancellableContextHandler is equivalent to getHandler`` (commands: ScoreCommand list) =
        let h1 = GrainDefinition.getHandler scoreGrain
        let h2 = GrainDefinition.getCancellableContextHandler scoreGrain
        let ctx = Unchecked.defaultof<GrainContext>
        let mutable s1 = { Wins = 0; Losses = 0; Draws = 0 }
        let mutable s2 = { Wins = 0; Losses = 0; Draws = 0 }
        for cmd in commands do
            s1 <- fst (h1 s1 cmd).Result
            s2 <- fst (h2 ctx s2 cmd CancellationToken.None).Result
        s1 = s2
```

This suite's cross-variant equivalence check is specific to the classic CE's dispatch-helper family
(`getHandler` vs. `getCancellableContextHandler`), which the functional runtime does not have; its
unit test, property test, and TestingHost integration test are covered individually above under
[Testing a Functional Grain](../testing.md#testing-a-functional-grain).

## Current testing guide

For `grainContract`, `grainFor`, and `journaledGrainFor`, see [Testing](../testing.md).
