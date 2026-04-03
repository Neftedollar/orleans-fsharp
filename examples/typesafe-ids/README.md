# Type-Safe IDs & Active Patterns -- F# Features Impossible in C#

This example showcases three F# features that have **no equivalent in C#** and that make distributed systems fundamentally safer:

1. **Units of Measure** for grain IDs -- compile-time prevention of ID mixups at zero runtime cost
2. **Active Patterns** for message routing -- decompose messages into categories via pattern matching
3. **Discriminated Union exhaustiveness** -- the compiler catches unhandled state transitions

## How to run

```bash
dotnet run --project src/Silo
```

## How to test

```bash
dotnet test
```

## Feature 1: Units of Measure for Grain IDs

### What they are

F# Units of Measure let you tag numeric types (like `int64`) with a phantom type that exists only at compile time. The tag is erased completely during compilation -- there is zero runtime cost, no wrapper objects, no boxing.

```fsharp
[<Measure>] type UserId
[<Measure>] type OrderId

let user1 : int64<UserId> = 42L * 1L<UserId>
let order1 : int64<OrderId> = 100L * 1L<OrderId>

// This is a COMPILE ERROR:
// let wrong : int64<UserId> = order1
```

### Why C# cannot have them

Units of Measure require compiler support for phantom types on value types that are erased at compile time. C# has no mechanism for this. The closest C# alternatives all have significant downsides:

- **`readonly record struct UserId(long Value)`** -- adds runtime overhead (wrapping/unwrapping), complicates arithmetic, and requires explicit conversions everywhere
- **Strong-typing libraries** -- require source generators and still produce wrapper types at runtime
- **Type aliases** (`using UserId = long`) -- provide zero type safety; `UserId` and `OrderId` are still interchangeable

### How they prevent bugs

In a distributed system with thousands of grain calls, passing the wrong ID type is a common bug that is invisible until runtime:

```csharp
// C# -- compiles fine, fails at runtime (or worse, silently corrupts data)
long userId = 42;
long orderId = 100;
var grain = grainFactory.GetGrain<IUserGrain>(orderId); // WRONG! No compiler error.
```

```fsharp
// F# -- compile error, caught before code ever runs
let user1 = userId 42L
let order1 = orderId 100L
let grain = UserGrainDef.getUser factory order1 // COMPILE ERROR: Expected int64<UserId>, got int64<OrderId>
```

## Feature 2: Active Patterns for Message Routing

### What they are

Active Patterns are user-defined pattern-matching decompositions. They let you create custom "cases" that can be matched against in `match` expressions, without modifying the type being matched.

```fsharp
let (|HighPriority|Normal|LowPriority|Spam|) (msg: IncomingMessage) =
    if msg.SpamScore > 0.8 then Spam
    elif msg.IsVip then HighPriority
    elif msg.Content.Length > 500 then LowPriority
    else Normal

// Usage reads like English:
match msg with
| Spam -> "dropped:spam"
| HighPriority -> "vip:queue"
| Normal -> "standard:queue"
| LowPriority -> "batch:queue"
```

### Why C# cannot have them

Active Patterns require:
- First-class pattern matching with custom decomposition (C# `switch` expressions only match on types and constants)
- Compiler-verified exhaustiveness over user-defined categories (C# cannot verify that a set of `if` branches is exhaustive)
- Composition of multiple independent decompositions (nesting two Active Patterns works naturally in F#)

The C# equivalent would be either:
- A chain of `if/else if` statements with no exhaustiveness checking
- A Visitor pattern or Strategy pattern with a class hierarchy -- far more code, and still no compiler guarantee that all cases are handled

### How they compose

Active Patterns nest naturally. The router in this example composes a priority pattern with an intent pattern:

```fsharp
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

## Feature 3: Exhaustive Matching on Discriminated Unions

### What it is

When you match on a discriminated union, the F# compiler ensures every case is handled. If you add a new case, every `match` expression in the codebase that does not handle it becomes a compile error (with `TreatWarningsAsErrors`).

```fsharp
type OrderStatus =
    | Pending | Confirmed | Shipped | Delivered | Cancelled

// If you add "| Returned" here, every match on OrderStatus
// across the entire codebase will fail to compile until updated.
```

### Why this matters more than C# enums

C# enums are just integers. The compiler does not check exhaustiveness in `switch` expressions:

```csharp
// C# -- adding a new enum value silently falls through to default
enum OrderStatus { Pending, Confirmed, Shipped, Delivered, Cancelled }
// Adding "Returned" here? Every switch keeps compiling. Bugs hide.
```

F# discriminated unions are closed, tagged types. The compiler tracks every case:

```fsharp
// F# -- adding Returned forces you to update every match expression
// With TreatWarningsAsErrors, this is a hard compile failure
```

## Why This Matters

These three features eliminate entire **classes** of bugs that are common in production distributed systems:

| Bug class | F# prevention | C# situation |
|-----------|---------------|--------------|
| **Wrong ID passed to grain** | Units of Measure -- compile error | Silent runtime misbehavior |
| **Unhandled message category** | Active Pattern exhaustiveness | Missed `else if` branch |
| **New state not handled everywhere** | DU exhaustiveness -- compile error | Silent fallthrough in switch |
| **Routing logic scattered across classes** | Active Patterns compose in one place | Strategy/Visitor class explosion |

In a distributed system with hundreds of grain types and thousands of message flows, these compile-time guarantees prevent bugs that would otherwise only surface in production under specific conditions -- the hardest kind to diagnose and fix.

## Key concepts

- **`grain {}`** computation expression for declarative grain behavior
- **`siloConfig {}`** computation expression for silo configuration
- **`GrainRef`** type-safe grain references with `invoke`
- **Units of Measure** zero-cost compile-time type tags on numeric IDs
- **Active Patterns** user-defined pattern-matching decompositions
- **Exhaustive matching** compiler-enforced handling of all discriminated union cases

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
