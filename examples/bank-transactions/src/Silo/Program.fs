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
        addMemoryStorage "TransactionStore"
        useJsonFallbackSerialization
    }

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

// Enable Orleans transactions on the silo
builder.UseOrleans(fun siloBuilder ->
    siloBuilder.UseTransactions() |> ignore)
|> ignore

let host = builder.Build()

let run () : Task =
    task {
        do! host.StartAsync()

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

        printfn ""

        // Verify total is preserved
        let total = aliceBalance + bobBalance
        printfn "Total across both accounts: $%M (should be $2000)" total

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
