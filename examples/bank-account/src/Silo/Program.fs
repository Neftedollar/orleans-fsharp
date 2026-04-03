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

// Add log-consistency storage for event sourcing and register the grain definition
builder.UseOrleans(fun siloBuilder ->
    MartenConfig.addLogStorage "LogStorage" siloBuilder |> ignore
    MartenConfig.addLogStorageDefault siloBuilder |> ignore)
|> ignore

builder.Services.AddFSharpEventSourcedGrain<AccountState, AccountEvent, AccountCommand>(AccountGrainDef.account)
|> ignore

let host = builder.Build()

let run () : Task =
    task {
        do! host.StartAsync()

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

        printfn ""
        printfn "Done. Shutting down..."
        do! host.StopAsync()
    }

run().GetAwaiter().GetResult()
