open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open BankTransactions.Domain

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        addMemoryStorage AccountApi.Storage
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

// Enable Orleans transactions on the silo, and register the functional twin's two definitions.
// One UseTransactions call serves both authoring models: the classic FSharpTransactionalGrain
// below and the functional definitions here are ordinary Orleans transaction participants.
builder.UseOrleans(fun siloBuilder ->
    siloBuilder.UseTransactions() |> ignore

    // Functional-runtime equivalents of the two grains below -- see AccountGrainFunctional.fs.
    siloBuilder.AddFunctionalGrain AccountFunctionalDef.account |> ignore
    siloBuilder.AddFunctionalGrain AtmFunctionalDef.atm |> ignore)
|> ignore

// The deprecated-but-still-registered classic path. Both definitions compile, are registered, and
// are still the ones this example's property tests and the functional twin exercise -- the twin
// hands `AccountGrainDef.deposit` / `AccountGrainDef.withdraw` straight to Orleans as update
// functions rather than restating either.
builder.Services.AddFSharpTransactionalGrain<AccountBalance>(AccountGrainDef.transactionalAccount)
|> ignore

builder.Services.AddFSharpAtmGrain<ITransactionalAccountGrain>(AccountGrainDef.atm) |> ignore

let host = builder.Build()

(*
    Classic FSharpTransactionalGrain / FSharpAtmGrain model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so a bare `factory.GetGrain<ITransactionalAccountGrain>(...)`
    fails with "Could not find an implementation for interface ITransactionalAccountGrain" the
    moment it runs -- this example never had a C# CodeGen bridge project to fill that gap.
    Verified by running it, not inferred: the silo starts fine and the very first GetGrain throws.
    See docs/functional-grains.md, "Running a silo from a standalone F# process" for the exact
    mechanism, and its "Distributed ACID transactions" section for the model the block below is
    rewritten into.

    let factory = host.Services.GetRequiredService<IGrainFactory>()

    // Get account grain references
    let alice = factory.GetGrain<ITransactionalAccountGrain>("alice")
    let bob = factory.GetGrain<ITransactionalAccountGrain>("bob")
    let atm = factory.GetGrain<IAtmGrain>("atm")

    printfn "--- Bank Transactions: ACID Transaction Demo ---"
    printfn ""

    // Deposit into both accounts
    do! alice.Deposit(1000m)
    let! aliceBalance = alice.GetBalance()
    printfn "Alice deposits $1000 -> balance = $%M" aliceBalance

    do! bob.Deposit(1000m)
    let! bobBalance = bob.GetBalance()
    printfn "Bob deposits $1000   -> balance = $%M" bobBalance

    printfn ""

    // Atomic transfer: $500 from Alice to Bob via ATM grain
    printfn "Atomic transfer: $500 from Alice to Bob..."
    do! atm.Transfer("alice", "bob", 500m)

    let! aliceBalance = alice.GetBalance()
    let! bobBalance = bob.GetBalance()
    printfn "  Alice balance: $%M" aliceBalance
    printfn "  Bob balance:   $%M" bobBalance

    printfn ""

    // Try overdraft transfer (should fail and roll back)
    printfn "Attempting transfer of $2000 from Alice to Bob (should fail)..."

    try
        do! atm.Transfer("alice", "bob", 2000m)
        printfn "  ERROR: Transfer should have failed!"
    with ex ->
        printfn "  Transaction rolled back: %s" (ex.GetBaseException().Message)

    // Verify balances unchanged after failed transaction
    let! aliceBalance = alice.GetBalance()
    let! bobBalance = bob.GetBalance()
    printfn "  Alice balance (unchanged): $%M" aliceBalance
    printfn "  Bob balance (unchanged):   $%M" bobBalance
*)

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<IGrainFactory>()

        // Account and ATM references. No interface, no code generation, and no [Transaction] /
        // [TransactionalState] attributes anywhere: the contract carries the policy.
        let alice = AccountApi.ref factory "alice"
        let bob = AccountApi.ref factory "bob"
        let atm = AtmApi.ref factory "atm"

        printfn "--- Bank Transactions (Functional Grain Runtime): ACID transfers on grainFor ---"
        printfn ""

        // Deposit into both accounts. Each call is its own transaction: `CreateOrJoin` with no
        // ambient transaction creates one.
        do! alice.deposit 1000m
        let! aliceBalance = alice.balance ()
        printfn "Alice deposits $1000 -> balance = $%M" aliceBalance

        do! bob.deposit 1000m
        let! bobBalance = bob.balance ()
        printfn "Bob deposits $1000   -> balance = $%M" bobBalance

        printfn ""

        // Atomic transfer: $500 from Alice to Bob, in ONE transaction the ATM creates.
        printfn "Atomic transfer: $500 from Alice to Bob..."
        do! atm.transfer ("alice", "bob", 500m)

        let! aliceBalance = alice.balance ()
        let! bobBalance = bob.balance ()
        printfn "  Alice balance: $%M" aliceBalance
        printfn "  Bob balance:   $%M" bobBalance

        printfn ""

        // Abort #1 -- the participant refuses. AccountGrainDef.withdraw throws on an overdraft,
        // exactly as it does for the classic grain, and the whole transaction aborts.
        printfn "Attempting transfer of $2000 from Alice to Bob (overdraft, should fail)..."

        try
            do! atm.transfer ("alice", "bob", 2000m)
            printfn "  ERROR: Transfer should have failed!"
        with ex ->
            printfn "  Transaction rolled back: %s" (ex.GetBaseException().Message)

        let! aliceBalance = alice.balance ()
        let! bobBalance = bob.balance ()
        printfn "  Alice balance (unchanged): $%M" aliceBalance
        printfn "  Bob balance (unchanged):   $%M" bobBalance

        printfn ""

        // Abort #2 -- the real atomicity proof. The overdraft above aborted BEFORE Bob's account
        // was touched, so it only shows short-circuiting. Here both accounts complete their
        // writes and the orchestrator then fails, so Orleans has two writes on two grains to roll
        // back. Neither balance may move.
        printfn "Transferring $200 and then failing AFTER both accounts were written..."

        try
            do! atm.transferThenFail ("alice", "bob", 200m)
            printfn "  ERROR: Transfer should have failed!"
        with ex ->
            printfn "  Transaction rolled back: %s" (ex.GetBaseException().Message)

        let! aliceBalance = alice.balance ()
        let! bobBalance = bob.balance ()
        printfn "  Alice balance (unchanged): $%M -- her withdrawal was undone" aliceBalance
        printfn "  Bob balance (unchanged):   $%M -- his deposit was undone" bobBalance

        printfn ""

        // Both balances read inside ONE transaction: a consistent snapshot, not two reads that
        // could straddle a commit.
        let! (aliceSnapshot, bobSnapshot) = atm.totals ("alice", "bob")
        printfn "Both balances in one transaction: Alice $%M, Bob $%M" aliceSnapshot bobSnapshot

        // Verify total is preserved
        let total = aliceSnapshot + bobSnapshot
        printfn "Total across both accounts: $%M (should be $2000)" total

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
