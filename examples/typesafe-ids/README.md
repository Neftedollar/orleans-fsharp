# Typesafe IDs

Three grains cover three everyday Orleans problems: a `User` grain whose key must never be
constructed from the wrong logical ID, an `Order` grain whose lifecycle is a closed set of
statuses driven by typed commands, and a `Router` grain that classifies an incoming message into a
delivery queue inside its own handler. Each is built so the compiler -- not a runtime check -- is
what stops the corresponding mistake: a grain key of the wrong flavor, a command left unhandled
after the domain grows, or a routing rule silently falling through.

All three run live through the functional grain runtime (`UserGrainFunctional.fs`,
`OrderGrainFunctional.fs`, `RouterGrainFunctional.fs`): the user and order grains keep their typed
`int64<UserId>` / `int64<OrderId>` contract keys, and the router grain reuses
`Routing.routeMessage` and its active patterns verbatim from the classic model. `UserGrain.fs` /
`OrderGrain.fs` / `RouterGrain.fs` keep the original `grain {}` versions as deprecated reference --
see `Program.fs` for why they cannot run standalone and
[docs/functional-grains.md](../../docs/functional-grains.md) for the full migration guide.

## How to run

```bash
dotnet run --project src/Silo
```

## How to test

```bash
dotnet test
```

## Expected output

```
--- Feature 1: Type-Safe IDs (Functional Grain Runtime key codec) ---

Created User 1, User 2
Created Order 100, Order 200
Set profile for User 1
Created Order 100 for User 1

--- Feature 2: Active Pattern Message Routing (Functional Grain Runtime) ---

  Sender 1 -> vip:question-queue
  Sender 2 -> dropped:spam
  Sender 3 -> standard:command-processor
  Sender 4 -> standard:greeting-bot
  Sender 5 -> standard:question-queue
  Sender 6 -> batch:low-priority
Router stats: processed = 5, dropped = 1

--- Feature 3: Exhaustive State Transitions (Functional Grain Runtime) ---

Order 100 confirmed: true
Order 100 shipped: true
Order 100 delivered: true
Order 100 cancel after delivery: false (no-op, status unchanged)
Order 100 final state: { OwnerId = 1L
  Total = 99.99M
  Status = Delivered }

Done. Shutting down...
```

## Feature 1: Grain keys that cannot be cross-wired

Both the user and order grains key themselves by a distinct `[<Measure>]`-tagged `int64`, declared
once in `src/Domain/Ids.fs`:

```fsharp
/// <summary>Unit of measure tagging an int64 as a User identifier.</summary>
[<Measure>] type UserId

/// <summary>Unit of measure tagging an int64 as an Order identifier.</summary>
[<Measure>] type OrderId
```

The tag exists purely at compile time -- it is erased before the value is ever boxed, sent over
the wire, or written to storage, so the guarantee below costs nothing at runtime.

The functional contract makes the tag part of the grain's actual Orleans identity, not just a
convention. `UserApi.contract` (`src/Domain/UserGrainFunctional.fs`) maps the key with
`int64KeyMapped rawId userId`, which makes the grain's key type -- the second type parameter of
`grainContract<UserActor, int64<UserId>, UserApi>` -- literally `int64<UserId>`:

```fsharp
    let contract =
        grainContract<UserActor, int64<UserId>, UserApi> () {
            grainType "typesafe-ids.user.functional"
            version 1
            int64KeyMapped rawId userId
        }
```

`OrderApi.contract` does the identical thing with `int64KeyMapped rawId orderId`. Because the key
type is baked into each contract, `UserApi.ref` only accepts an `int64<UserId>` and `OrderApi.ref`
only accepts an `int64<OrderId>` -- passing an order's key to the user grain's `ref` call, or the
reverse, is rejected by the ordinary F# type checker, the same way any other type mismatch would
be. `Program.fs` demonstrates both directions, commented out because they do not compile:

```fsharp
        // This compiles — the contract's key IS int64<UserId>:
        let userFn = UserApi.ref factory user1
        let! _ = userFn.setProfile ("Alice", "alice@example.com")
        printfn "Set profile for User %d" (rawId user1)

        // This would NOT compile — wrong type:
        // let wrong = UserApi.ref factory order1
        // Error: Expected int64<UserId>, got int64<OrderId>

        let orderFn = OrderApi.ref factory order1
        let! _ = orderFn.create (user1, 99.99m)
        printfn "Created Order %d for User %d" (rawId order1) (rawId user1)

        // This would NOT compile either — wrong type in the other direction:
        // let wrong = OrderApi.ref factory user1
        // Error: Expected int64<OrderId>, got int64<UserId>
```

In a system with many grain types sharing the same underlying `int64` representation, this is
exactly the bug class it closes: a grain reference built from the wrong logical ID compiles and
runs today, then silently reads or corrupts the wrong actor's state the day two IDs collide. The
classic grain model carries the same guarantee through its accessor functions instead of a
contract key -- `UserGrainDef.getUser (factory: IGrainFactory) (id: int64<UserId>)` and
`OrderGrainDef.getOrder (factory: IGrainFactory) (id: int64<OrderId>)` in `UserGrain.fs` /
`OrderGrain.fs` -- the same rejection, one layer earlier. See
[docs/functional-grains.md, "Key-codec identity rules"](../../docs/functional-grains.md#key-codec-identity-rules)
for the codec's full contract (determinism, injectivity, round-tripping).

## Feature 2: Message classification inside a grain handler

The router grain's job is to look at an incoming message and decide which queue it belongs to,
without a chain of `if`/`elif` checks. `src/Domain/Routing.fs` declares two active patterns and
composes them in `routeMessage`:

```fsharp
let routeMessage (msg: IncomingMessage) : string =
    match msg with
    | Spam -> "dropped:spam"
    | HighPriority ->
        match msg.Content with
        | Question -> "vip:question-queue"
        | Command -> "vip:command-processor"
        | _ -> "vip:general"
    | Normal ->
        match msg.Content with
        | Question -> "standard:question-queue"
        | Command -> "standard:command-processor"
        | Greeting -> "standard:greeting-bot"
        | Unknown -> "standard:general"
    | LowPriority -> "batch:low-priority"
```

`Spam|HighPriority|Normal|LowPriority` and `Question|Command|Greeting|Unknown` are each their own
active pattern (also in `Routing.fs`); nesting them like this is what lets one message get
classified on two independent axes -- priority, then intent -- in a single readable match instead
of a priority flag and an intent flag threaded through by hand.

The router grain's handler calls straight into `routeMessage` and matches the same `Spam` pattern
again to update its own counters, so the classification and the grain's own bookkeeping are driven
by one function, not duplicated logic. From `RouterFunctionalDef.router`
(`src/Domain/RouterGrainFunctional.fs`):

```fsharp
            handle
                (_.route)
                (fun _context state msg ->
                    task {
                        let route = routeMessage msg

                        match msg with
                        | Spam -> return { state with Dropped = state.Dropped + 1 }, route
                        | _ -> return { state with Processed = state.Processed + 1 }, route
                    })
```

The classic `RouterGrainDef.router` (`RouterGrain.fs`) calls the identical `routeMessage` and
matches the identical `Spam` pattern inside its own handler -- the routing logic does not know or
care which grain-authoring model is calling it.

## Feature 3: Order commands and status as closed DUs

Two different discriminated unions carry the "closed set of cases" guarantee for this example's
order domain, and they are enforced differently.

`UserCommand` (three cases: `SetProfile` / `IncrementOrders` / `GetProfile`) is dispatched by a
single hand-written match with no wildcard case, inside the classic grain's handler. From
`UserGrainDef.user` (`src/Domain/UserGrain.fs`):

```fsharp
            handle (fun state cmd ->
                task {
                    match cmd with
                    | SetProfile(name, email) ->
                        let next = { state with Name = name; Email = email }
                        return next, box true
                    | IncrementOrders ->
                        let next = { state with OrderCount = state.OrderCount + 1 }
                        return next, box next.OrderCount
                    | GetProfile ->
                        return state, box state
                })
```

Add a fourth `UserCommand` case and this is the one place in the example that stops compiling --
this handler is the only match on `UserCommand` anywhere in the codebase, and a wildcard would have
made the new case invisible to the compiler instead. That is not a style choice this example opted
into: this project's own `Directory.Build.props` sets `TreatWarningsAsErrors`, so the ordinary F#
incomplete-match warning (FS0025) is a build failure here, at the exact line missing the case. The
functional model sidesteps the question rather than inheriting it: there is no hand-written
dispatch match to keep exhaustive at all. `UserFunctionalDef.user` gives `setProfile` and
`getProfile` their own `handle` call apiece instead of one `match cmd with`, and `grainFor` itself
refuses to seal a definition that leaves an API field unhandled
(`src/Orleans.FSharp/FunctionalDefinition.fs`:
`"grain type '{grainTypeName}' has no handler for API field(s) {missingNames}."`) -- a
definition-build-time check rather than a compiler error, but the same class of mistake caught
before it ships either way.

`OrderStatus` (`Pending | Confirmed | Shipped | Delivered | Cancelled`, in `OrderGrain.fs`) is the
order lifecycle's state machine, and it is matched exhaustively too -- but deliberately *with* a
wildcard, because an invalid transition is meant to be a silent no-op rather than a crash:

```fsharp
    let tryTransition (current: OrderStatus) (target: OrderStatus) : OrderStatus =
        match current, target with
        | Pending, Confirmed -> Confirmed
        | Confirmed, Shipped -> Shipped
        | Shipped, Delivered -> Delivered
        | Pending, Cancelled -> Cancelled
        | Confirmed, Cancelled -> Cancelled
        | _ -> current
```

`tryTransition` is not reimplemented for the functional model -- it is called verbatim.
`OrderFunctionalDef.order`'s `confirm` handler (`src/Domain/OrderGrainFunctional.fs`), and `ship` /
`deliver` / `cancel` alongside it, each call straight into it:

```fsharp
            handle
                (_.confirm)
                (fun _context state () ->
                    task {
                        let next = { state with Status = OrderGrainDef.tryTransition state.Status Confirmed }
                        return next, next.Status = Confirmed
                    })
```

One state machine, defined once in the file that also hosts the deprecated `grain {}` model,
driving both authoring styles with zero risk of the two drifting apart.

## Key concepts

- **`grainContract` / `grainFor`** the functional grain runtime's contract + definition pair (this
  example's live path for all three grains)
- **`int64KeyMapped rawId userId` / `int64KeyMapped rawId orderId`** the contract's key codec --
  makes the grain's actual Orleans key type `int64<UserId>` / `int64<OrderId>`, so `UserApi.ref` /
  `OrderApi.ref` reject the wrong measure the same way any other type mismatch would be rejected
- **`siloConfig {}`** computation expression for silo configuration
- **`grain {}`** (deprecated) the original computation expression, kept in `UserGrain.fs` /
  `OrderGrain.fs` / `RouterGrain.fs` as reference -- needs a C#-generated proxy per grain interface
  and cannot resolve standalone in an F#-only project
- **Units of Measure** (`Ids.fs`) compile-time-only tags on the `int64` grain keys, erased before
  any value reaches storage or the wire
- **Active Patterns** (`Routing.fs`) `Spam|HighPriority|Normal|LowPriority` and
  `Question|Command|Greeting|Unknown` compose inside `routeMessage`, called identically from the
  classic and functional router handlers
- **Exhaustive matching** `UserGrainDef.user`'s command dispatch has no wildcard case, so this
  project's `TreatWarningsAsErrors` turns a left-out `UserCommand` case into a build failure;
  `OrderGrainDef.tryTransition`'s status-transition match is reused verbatim by both grain models

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
