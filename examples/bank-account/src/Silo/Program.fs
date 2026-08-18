open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.EventSourcing
open BankAccount.Domain

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

// Add log-consistency storage for event sourcing and register both grain definitions.
// These are Orleans' own log-consistency providers, called directly. The SAME provider name
// serves the classic definition's `logConsistencyProvider "LogStorage"` and the functional
// twin's `logProvider "LogStorage"` -- one journal store, two authoring models.
builder.UseOrleans(fun siloBuilder ->
    siloBuilder.AddLogStorageBasedLogConsistencyProvider(AccountApi.LogProvider) |> ignore
    siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault() |> ignore

    // Functional-runtime equivalent of the event-sourced grain below -- see
    // AccountGrainFunctional.fs. `AddFunctionalJournaledGrain`, not `AddFunctionalGrain`: a
    // journaled definition is its own kind.
    siloBuilder.AddFunctionalJournaledGrain AccountFunctionalDef.account |> ignore)
|> ignore

// The deprecated-but-still-registered classic path. The definition compiles, is registered, and
// is still the one both this example's property tests and the functional twin exercise -- the
// twin delegates to `AccountGrainDef.handleCommand` and `AccountGrainDef.applyEvent` rather than
// restating either.
builder.Services.AddFSharpEventSourcedGrain<AccountState, AccountEvent, AccountCommand>(AccountGrainDef.account)
|> ignore

let host = builder.Build()

(*
    Classic eventSourcedGrain { } model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so a bare `factory.GetGrain<IBankAccountGrain>(...)` fails with
    "Could not find an implementation for interface IBankAccountGrain" the moment it runs -- this
    example never had a C# CodeGen bridge project to fill that gap. Verified by running it, not
    inferred: the silo starts fine and the very first GrainRef.ofString throws. See
    docs/functional-grains.md, "Running a silo from a standalone F# process" for the exact
    mechanism, and docs/event-sourcing.md for the model the block below is rewritten into.

    let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

    // Create two bank accounts
    let alice = GrainRef.ofString<IBankAccountGrain> factory "alice"
    let bob = GrainRef.ofString<IBankAccountGrain> factory "bob"

    printfn "--- Bank Account: Event Sourcing Demo ---"
    printfn ""

    // Deposit into Alice's account
    let! balance = GrainRef.invoke alice (fun g -> g.HandleCommand(Deposit 1000m))
    printfn "Alice deposits $1000 -> balance = $%A" balance

    let! balance = GrainRef.invoke alice (fun g -> g.HandleCommand(Deposit 500m))
    printfn "Alice deposits $500  -> balance = $%A" balance

    // Deposit into Bob's account
    let! balance = GrainRef.invoke bob (fun g -> g.HandleCommand(Deposit 200m))
    printfn "Bob deposits $200    -> balance = $%A" balance

    printfn ""

    // Transfer from Alice to Bob (withdraw from Alice, deposit to Bob)
    let transferAmount = 300m
    printfn "Transfer $%M from Alice to Bob..." transferAmount

    let! aliceBalance = GrainRef.invoke alice (fun g -> g.HandleCommand(Withdraw transferAmount))
    printfn "  Alice after withdrawal: $%A" aliceBalance

    let! bobBalance = GrainRef.invoke bob (fun g -> g.HandleCommand(Deposit transferAmount))
    printfn "  Bob after deposit:      $%A" bobBalance

    printfn ""

    // Try overdraft (should be rejected)
    let! aliceBalance = GrainRef.invoke alice (fun g -> g.HandleCommand(Withdraw 5000m))
    printfn "Alice tries to withdraw $5000 (overdraft): balance unchanged = $%A" aliceBalance

    // Final balances
    printfn ""
    let! aliceFinal = GrainRef.invoke alice (fun g -> g.HandleCommand(GetBalance))
    let! bobFinal = GrainRef.invoke bob (fun g -> g.HandleCommand(GetBalance))
    printfn "Final balances:"
    printfn "  Alice: $%A" aliceFinal
    printfn "  Bob:   $%A" bobFinal
*)

let run () : Task =
    task {
        do! host.StartAsync()

        let factory = host.Services.GetRequiredService<Orleans.IGrainFactory>()

        // Two bank accounts. No interface, no code generation: the API record IS the surface.
        let alice = AccountApi.ref factory "alice"
        let bob = AccountApi.ref factory "bob"

        printfn "--- Bank Account (Functional Grain Runtime): journaledGrainFor + AccountEvent ---"
        printfn ""

        // Deposit into Alice's account
        let! balance = alice.deposit 1000m
        printfn "Alice deposits $1000 -> %A" balance

        let! balance = alice.deposit 500m
        printfn "Alice deposits $500  -> %A" balance

        // Deposit into Bob's account
        let! balance = bob.deposit 200m
        printfn "Bob deposits $200    -> %A" balance

        printfn ""

        // Transfer from Alice to Bob (withdraw from Alice, deposit to Bob). Two independent
        // journals, so this is NOT atomic -- see the bank-transactions example for the atomic
        // version. Event sourcing and distributed transactions are separate concerns.
        let transferAmount = 300m
        printfn "Transfer $%M from Alice to Bob (two journals, not one transaction)..." transferAmount

        let! aliceBalance = alice.withdraw transferAmount
        printfn "  Alice after withdrawal: %A" aliceBalance

        let! bobBalance = bob.deposit transferAmount
        printfn "  Bob after deposit:      %A" bobBalance

        printfn ""

        // Refusals: the classic handler answered both of these with the same empty event list,
        // and the caller could only see an unchanged balance. Here each says what it was.
        let! overdraft = alice.withdraw 5000m
        printfn "Alice tries to withdraw $5000 (overdraft): %A" overdraft

        let! nonPositive = alice.deposit 0m
        printfn "Alice tries to deposit $0:                 %A" nonPositive

        // A refused command raises no event, so it performs no storage write and does not move
        // the journal version. Alice: 2 deposits + 1 withdrawal = 3. Bob: 2 deposits = 2.
        let! aliceVersion = alice.journalVersion ()
        let! bobVersion = bob.journalVersion ()
        printfn ""
        printfn "Journal versions (confirmed events): Alice = %d, Bob = %d" aliceVersion bobVersion
        printfn "  -- neither refusal above is in a journal."

        // The claim an event-sourced example exists to make: the balance is not kept in memory.
        // End the activation, wait for it to go, then read again -- what comes back is the fold
        // of the journal, replayed through the very same `AccountGrainDef.applyEvent`.
        printfn ""
        printfn "Ending Alice's activation (context.deactivateOnIdle), then reading again..."
        do! alice.recycle ()
        do! Task.Delay 2000

        let! replayed = alice.balance ()
        let! replayedVersion = alice.journalVersion ()
        printfn "  Alice balance after replay: $%M (from journal version %d, nothing was written)" replayed replayedVersion

        // Final balances
        printfn ""
        let! aliceFinal = alice.balance ()
        let! bobFinal = bob.balance ()
        printfn "Final balances:"
        printfn "  Alice: $%M" aliceFinal
        printfn "  Bob:   $%M" bobFinal

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
