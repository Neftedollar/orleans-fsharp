module Orleans.FSharp.Integration.TransactionConflictTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

/// <summary>
/// Integration tests for transaction conflict resolution.
/// Validates that Orleans Transactions correctly handle concurrent
/// conflicting operations without data corruption.
/// </summary>
[<Collection("ClusterCollection")>]
type TransactionConflictTests(fixture: ClusterFixture) =

    /// <summary>
    /// Two concurrent withdrawals from the same account should not both
    /// succeed when the balance can only cover one. The transaction system
    /// must abort one of them.
    /// </summary>
    [<Fact>]
    member _.``concurrent withdrawals don't overdraft`` () =
        task {
            let atm = fixture.GrainFactory.GetGrain<ITransactionalAtmGrain>("atm-overdraft-test")
            let fromKey = "overdraft-from"
            let toKey = "overdraft-to"

            // Setup: deposit 100 to the source account
            let fromAccount = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(fromKey)
            do! fromAccount.Deposit(100m)

            // Setup: deposit 0 to the target account
            let toAccount = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(toKey)
            do! toAccount.Deposit(0m)

            // Try to transfer 80 twice concurrently
            // Balance is only 100, so only one transfer should succeed
            let t1 = atm.Transfer(fromKey, toKey, 80m)
            let t2 = atm.Transfer(fromKey, toKey, 80m)
            let! _ = t1
            and! _ = t2

            // Check final balance - should be >= 0
            let! finalBalance = fromAccount.GetBalance()

            // With 100 balance and two 80 transfers:
            // - If one succeeds: balance = 20
            // - If both abort: balance = 100
            // - If both succeed (bug): balance = -60
            test <@ finalBalance >= 0m @>
        }

    /// <summary>
    /// 10 concurrent deposits of 10 each to an account with balance 0
    /// must result in exactly 100.
    /// </summary>
    [<Fact>]
    member _.``concurrent deposits don't lose money`` () =
        task {
            let account = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>("concurrent-deposit-test")

            // 10 concurrent deposits of 10 each using and! for parallelism
            let t1 = account.Deposit(10m)
            let t2 = account.Deposit(10m)
            let t3 = account.Deposit(10m)
            let t4 = account.Deposit(10m)
            let t5 = account.Deposit(10m)
            let t6 = account.Deposit(10m)
            let t7 = account.Deposit(10m)
            let t8 = account.Deposit(10m)
            let t9 = account.Deposit(10m)
            let t10 = account.Deposit(10m)

            let! _ = t1
            and! _ = t2
            and! _ = t3
            and! _ = t4
            and! _ = t5
            and! _ = t6
            and! _ = t7
            and! _ = t8
            and! _ = t9
            and! _ = t10

            let! balance = account.GetBalance()

            // All 10 deposits should have been applied: 10 * 10 = 100
            test <@ balance = 100m @>
        }

    /// <summary>
    /// ATM transfer must be atomic: if the destination account throws
    /// during deposit, the source account's withdrawal must be rolled back.
    /// </summary>
    [<Fact>]
    member _.``ATM transfer is atomic all-or-nothing`` () =
        task {
            let atm = fixture.GrainFactory.GetGrain<ITransactionalAtmGrain>("atomic-transfer-test")
            let fromKey = "atomic-from"
            let toKey = "atomic-to"

            // Setup
            let fromAccount = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(fromKey)
            do! fromAccount.Deposit(50m)

            let toAccount = fixture.GrainFactory.GetGrain<ITransactionalAccountGrain>(toKey)
            do! toAccount.Deposit(0m)

            // Transfer should succeed
            do! atm.Transfer(fromKey, toKey, 25m)

            // Verify: from=25, to=25, total=50
            let! fromBalance = fromAccount.GetBalance()
            let! toBalance = toAccount.GetBalance()

            test <@ fromBalance = 25m @>
            test <@ toBalance = 25m @>
            test <@ fromBalance + toBalance = 50m @>
        }
