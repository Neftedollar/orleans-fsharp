---
title: "Legacy: Advanced Patterns"
description: "Advanced patterns for the original CodeGen authoring path."
---

# Legacy Advanced Patterns

> **Archived and unsupported.** This material is retained only to help migrate existing
> applications. There is no new Legacy release line, feature or compatibility work, or security
> fixes. New development must use the current functional API.

**Advanced examples which depend on the original CodeGen authoring path.**

> Current functional transactions are documented in [Functional Grain Runtime](/orleans-fsharp/functional-grains/#distributed-acid-transactions).

## Higher-Level Transactional Grains

`Orleans.FSharp.Runtime` provides `FSharpTransactionalGrain<'State>` and `FSharpAtmGrain<'TAccountGrain>` — generic base classes that wire pure F# functions to Orleans ACID transactions. Compared to using `ITransactionalState<'T>` directly, they remove all boilerplate: you supply just the business logic as a record of functions.

### Why use these classes?

| Raw `ITransactionalState<'T>` | `FSharpTransactionalGrain<'State>` |
|-------------------------------|-------------------------------------|
| Write `PerformRead` / `PerformUpdate` in every grain method | Provide functions once as a `TransactionalGrainDefinition` |
| Repeat `CopyState` logic in every `PerformUpdate` | `CopyState` is centralised in the definition |
| Boilerplate C# class per grain | One C# stub, one F# definition |

### Define the state type

The state must be a **sealed class** with a parameterless constructor (Orleans requirement for `ITransactionalState<'T>`):

```fsharp
[<GenerateSerializer; Sealed>]
type AccountState() =
    [<Id(0u)>] member val Balance: decimal = 0m with get, set
```

### Define the grain behaviour (F#)

```fsharp
open Orleans.FSharp.Runtime

let accountDef: TransactionalGrainDefinition<AccountState> =
    {
        Deposit  = fun state amount ->
            let s = AccountState()
            s.Balance <- state.Balance + amount
            s

        Withdraw = fun state amount ->
            if state.Balance < amount then
                raise (System.InvalidOperationException("Insufficient funds"))
            let s = AccountState()
            s.Balance <- state.Balance - amount
            s

        GetBalance = fun state -> state.Balance

        CopyState  = fun source target ->
            target.Balance <- source.Balance
    }
```

`Deposit` and `Withdraw` must return a **new state object** — do not mutate the input. `CopyState` copies field values from source into target **in place** (Orleans requires this for transactional semantics).

### Define the grain interface (F#)

Use `TransactionOption.CreateOrJoin` on methods that should work both standalone and inside an ATM transaction:

`[<Transaction(...)>]` takes **Orleans' own `Orleans.TransactionOption` enum**. Do not open
`Orleans.FSharp.Transactions` in the same file: its `TransactionOption` union shares the simple
name, shadows the enum, and the attribute then fails to compile.

```fsharp
open Orleans
open Orleans.Transactions

type IAccountGrain =
    inherit IGrainWithStringKey

    [<Transaction(TransactionOption.CreateOrJoin)>]
    abstract Deposit:    amount: decimal -> Task

    [<Transaction(TransactionOption.CreateOrJoin)>]
    abstract Withdraw:   amount: decimal -> Task

    [<Transaction(TransactionOption.CreateOrJoin)>]
    abstract GetBalance: unit   -> Task<decimal>
```

### Create the C# stub

Orleans source generators only work with C#. Add one stub per concrete grain:

```csharp
using Orleans.Transactions.Abstractions;
using Orleans.FSharp.Runtime;
using Orleans.FSharp.Sample;

public sealed class AccountGrainImpl
    : FSharpTransactionalGrain<AccountState>,
      IAccountGrain
{
    public AccountGrainImpl(
        [TransactionalState("state", "TransactionStore")]
        ITransactionalState<AccountState> state,
        TransactionalGrainDefinition<AccountState> definition,
        ILogger<FSharpTransactionalGrain<AccountState>> logger)
        : base(state, definition, logger) { }
}
```

The `"TransactionStore"` string must match the storage provider name registered on the silo.

### Register the definition and silo providers

```fsharp
open Orleans.FSharp.Runtime

// In your silo configuration function:
siloBuilder
    .UseTransactions()
    .AddMemoryGrainStorage("TransactionStore")  // or real storage in production
|> ignore

// In DI:
services.AddFSharpTransactionalGrain<AccountState>(accountDef) |> ignore
```

### Call the grain

```fsharp
let account = grainFactory.GetGrain<IAccountGrain>("alice")

do! account.Deposit(500m)
do! account.Withdraw(200m)
let! balance = account.GetBalance()
// balance = 300m
```

---

## ATM Grain (cross-account transfers)

`FSharpAtmGrain<'TAccountGrain>` orchestrates atomic transfers **across multiple account grains** in a single Orleans transaction. It wraps two grain calls (Withdraw + Deposit) in a `[Transaction(Create)]` method.

### Define the ATM behaviour

```fsharp
open Orleans.FSharp.Runtime

let atmDef: AtmGrainDefinition<IAccountGrain> =
    {
        Transfer = fun from to' amount ->
            task {
                do! from.Withdraw(amount)
                do! to'.Deposit(amount)
            }
    }
```

### Define the ATM interface

```fsharp
type IAtmGrain =
    inherit IGrainWithStringKey

    [<Transaction(TransactionOption.Create)>]
    abstract Transfer: fromKey: string * toKey: string * amount: decimal -> Task
```

### Create the C# ATM stub

```csharp
public sealed class AtmGrainImpl
    : FSharpAtmGrain<IAccountGrain>,
      IAtmGrain
{
    public AtmGrainImpl(
        AtmGrainDefinition<IAccountGrain> definition,
        ILogger<FSharpAtmGrain<IAccountGrain>> logger)
        : base(definition, logger) { }
}
```

### Register the ATM definition

```fsharp
services.AddFSharpAtmGrain<IAccountGrain>(atmDef) |> ignore
```

### Execute an atomic transfer

```fsharp
let atm = grainFactory.GetGrain<IAtmGrain>("atm-1")
do! atm.Transfer("alice", "bob", 100m)
// Both the withdraw from alice and deposit to bob commit atomically,
// or both roll back if either throws.
```

### Testing transactional grains

Because `TransactionalGrainDefinition` is a plain F# record of functions, you can unit-test the business logic **without a silo**:

```fsharp
open FsCheck.Xunit

[<Property>]
let ``Deposit then Withdraw round-trips the balance`` (initial: decimal) (amount: decimal) =
    let safeInitial = abs initial % 1_000_000m
    let safeAmount  = abs amount  % 1_000_000m + 0.01m
    let state = AccountState()
    state.Balance <- safeInitial
    let afterDeposit  = accountDef.Deposit  state  safeAmount
    let afterWithdraw = accountDef.Withdraw afterDeposit safeAmount
    afterWithdraw.Balance = safeInitial

[<Fact>]
let ``Withdraw throws when balance is insufficient`` () =
    let state = AccountState()
    state.Balance <- 10m
    Assert.Throws<exn>(fun () -> accountDef.Withdraw state 50m |> ignore)
    |> ignore
```

---

## Other legacy APIs

The complete original grain, timer, reminder, scripting-handle, and registration surfaces are indexed in [Legacy API Reference](/orleans-fsharp/legacy/api-reference/) and [Legacy Grain Definition](/orleans-fsharp/legacy/grain-definition/).
