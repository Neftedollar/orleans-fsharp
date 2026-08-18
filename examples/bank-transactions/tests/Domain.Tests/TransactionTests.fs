module BankTransactions.Tests.TransactionTests

open Xunit
open FsCheck
open FsCheck.Xunit
open BankTransactions.Domain

/// <summary>
/// Property-based tests for the bank account transaction domain logic.
/// These tests verify the pure F# business logic independently of Orleans transactions.
/// </summary>
module Properties =

    /// <summary>
    /// Deposit always increases balance by the deposited amount.
    /// </summary>
    [<Property>]
    let ``deposit increases balance by exact amount`` (PositiveInt amount) =
        let initial = AccountBalance()
        initial.Balance <- 100m
        let result = AccountGrainDef.deposit initial (decimal amount)
        result.Balance = initial.Balance + decimal amount

    /// <summary>
    /// Withdraw decreases balance by the withdrawn amount.
    /// </summary>
    [<Property>]
    let ``withdraw decreases balance by exact amount`` (PositiveInt amount) =
        let initial = AccountBalance()
        initial.Balance <- decimal amount + 100m
        let result = AccountGrainDef.withdraw initial (decimal amount)
        result.Balance = initial.Balance - decimal amount

    /// <summary>
    /// Withdraw with insufficient funds throws InvalidOperationException.
    /// </summary>
    [<Fact>]
    let ``withdraw with insufficient funds throws`` () =
        let balance = AccountBalance()
        balance.Balance <- 50m
        Assert.Throws<System.InvalidOperationException>(fun () ->
            AccountGrainDef.withdraw balance 100m |> ignore)
        |> ignore

    /// <summary>
    /// A simulated transfer preserves total balance across two accounts.
    /// This is the key property: deposit + withdraw is a zero-sum operation.
    /// </summary>
    [<Property>]
    let ``transfer preserves total balance`` (PositiveInt initialA) (PositiveInt initialB) (PositiveInt transferAmt) =
        let balanceA = AccountBalance()
        balanceA.Balance <- decimal initialA + decimal transferAmt

        let balanceB = AccountBalance()
        balanceB.Balance <- decimal initialB

        let totalBefore = balanceA.Balance + balanceB.Balance

        let newA = AccountGrainDef.withdraw balanceA (decimal transferAmt)
        let newB = AccountGrainDef.deposit balanceB (decimal transferAmt)

        let totalAfter = newA.Balance + newB.Balance
        totalBefore = totalAfter

    /// <summary>
    /// Deposit of zero or negative amounts does not reduce balance.
    /// </summary>
    [<Fact>]
    let ``deposit of zero keeps balance unchanged`` () =
        let balance = AccountBalance()
        balance.Balance <- 500m
        let result = AccountGrainDef.deposit balance 0m
        Assert.Equal(500m, result.Balance)

    /// <summary>
    /// Balance is never negative after a valid withdraw operation.
    /// </summary>
    [<Property>]
    let ``balance is never negative after valid withdraw`` (PositiveInt balance) (PositiveInt withdrawAmt) =
        let state = AccountBalance()
        state.Balance <- decimal balance

        if decimal withdrawAmt <= state.Balance then
            let result = AccountGrainDef.withdraw state (decimal withdrawAmt)
            result.Balance >= 0m
        else
            // Should throw, which means balance stays non-negative
            try
                AccountGrainDef.withdraw state (decimal withdrawAmt) |> ignore
                false // should not reach here
            with :? System.InvalidOperationException ->
                true

/// <summary>
/// Parity pins for the functional-runtime twin (<c>AccountGrainFunctional.fs</c>). The twin hands
/// <c>AccountGrainDef.deposit</c> / <c>AccountGrainDef.withdraw</c> to Orleans as update functions
/// rather than restating them, so the properties above already cover both paths. What the twin
/// declares on its own is the transactional IDENTITY -- the state name, the storage name, and the
/// per-operation transaction options -- and every one of those is durable or protocol-visible.
/// </summary>
module FunctionalTwin =

    /// <summary>
    /// The state name is part of the <c>ParticipantId</c> Orleans uses during the commit protocol
    /// and of the storage key, so renaming it silently orphans every stored balance. It has to
    /// stay the <c>("state", "TransactionStore")</c> pair the classic
    /// <c>[&lt;TransactionalState&gt;]</c> attribute names.
    /// </summary>
    [<Fact>]
    let ``the twin keeps the classic transactional-state identity`` () =
        let ledger = string AccountApi.ledger

        Assert.Contains("stateName = 'state'", ledger)
        Assert.Contains("storageName = 'TransactionStore'", ledger)
        Assert.Contains($"storedType = '{typeof<AccountBalance>.FullName}'", ledger)

    /// <summary>
    /// Both contracts and both definitions are built at module initialisation, and every stage
    /// validates: the API records' shapes, every selector, and -- for a definition that attaches a
    /// transactional facet -- that each transaction-scoped operation really is declared
    /// <c>transactional</c>. Touching them here turns a construction error into a failing test
    /// rather than into a silo that refuses to start.
    /// </summary>
    [<Fact>]
    let ``the functional contracts and definitions are well-formed`` () =
        let account = string AccountApi.contract
        let atm = string AtmApi.contract

        Assert.Contains("grainType = 'bank-transactions.account.functional'", account)
        // deposit, withdraw, balance.
        Assert.Contains("operations = 3", account)
        Assert.Contains("grainType = 'bank-transactions.atm.functional'", atm)
        // transfer, totals, transferThenFail.
        Assert.Contains("operations = 3", atm)

        Assert.NotNull(box AccountFunctionalDef.account)
        Assert.NotNull(box AtmFunctionalDef.atm)
