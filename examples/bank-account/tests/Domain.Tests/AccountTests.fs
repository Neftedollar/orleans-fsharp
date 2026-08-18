module BankAccount.Tests.AccountTests

open Xunit
open FsCheck
open FsCheck.Xunit
open BankAccount.Domain
open Orleans.FSharp.EventSourcing

/// <summary>
/// Generates arbitrary account commands with reasonable amounts.
/// </summary>
type AccountCommandGen() =
    static member AccountCommand() : Arbitrary<AccountCommand> =
        let genDeposit = Gen.choose (1, 10000) |> Gen.map (fun n -> Deposit(decimal n))
        let genWithdraw = Gen.choose (1, 5000) |> Gen.map (fun n -> Withdraw(decimal n))
        let genGetBalance = Gen.constant GetBalance

        Gen.frequency
            [ 3, genDeposit
              2, genWithdraw
              1, genGetBalance ]
        |> Arb.fromGen

/// <summary>
/// Property-based tests for the bank account domain using FsCheck.
/// </summary>
module Properties =

    let private definition = AccountGrainDef.account

    let private applyCommands (commands: AccountCommand list) : AccountState =
        commands
        |> List.fold
            (fun state cmd ->
                let events = definition.Handle state cmd
                events |> List.fold definition.Apply state)
            (AccountState())

    /// <summary>
    /// Balance is never negative: arbitrary command sequences never produce overdraft.
    /// </summary>
    [<Property(Arbitrary = [| typeof<AccountCommandGen> |])>]
    let ``balance is never negative after any command sequence`` (commands: AccountCommand list) =
        let finalState = applyCommands commands
        finalState.Balance >= 0m

    /// <summary>
    /// Deposits always increase balance.
    /// </summary>
    [<Property>]
    let ``deposit increases balance`` (PositiveInt amount) =
        let state = AccountState()
        state.Balance <- 100m
        let events = definition.Handle state (Deposit(decimal amount))
        let newState = events |> List.fold definition.Apply state
        newState.Balance >= state.Balance

    /// <summary>
    /// Withdrawal of more than the balance produces no events.
    /// </summary>
    [<Property>]
    let ``overdraft withdrawal produces no events`` (PositiveInt balance) (PositiveInt extra) =
        let state = AccountState()
        state.Balance <- decimal balance
        let overdraftAmount = decimal balance + decimal extra + 1m
        let events = definition.Handle state (Withdraw overdraftAmount)
        events.IsEmpty

    /// <summary>
    /// GetBalance produces no events (read-only query).
    /// </summary>
    [<Fact>]
    let ``GetBalance produces no events`` () =
        let state = AccountState()
        state.Balance <- 500m
        let events = definition.Handle state GetBalance
        Assert.Empty(events)

    /// <summary>
    /// Event replay produces the same state as direct command processing.
    /// </summary>
    [<Property(Arbitrary = [| typeof<AccountCommandGen> |])>]
    let ``event replay produces same state as command processing`` (commands: AccountCommand list) =
        // Process commands, collecting all events
        let allEvents =
            commands
            |> List.fold
                (fun (state, events) cmd ->
                    let newEvents = definition.Handle state cmd
                    let newState = newEvents |> List.fold definition.Apply state
                    (newState, events @ newEvents))
                (AccountState(), [])
            |> snd

        // Replay all events from initial state
        let replayedState =
            EventSourcedGrainDefinition.foldEvents definition (AccountState()) allEvents

        // Process commands directly
        let directState = applyCommands commands

        replayedState.Balance = directState.Balance

/// <summary>
/// Parity pins for the functional-runtime twin (<c>AccountGrainFunctional.fs</c>). The twin
/// delegates its fold and its command decision to <c>AccountGrainDef</c>, so those need no
/// separate tests -- the properties above already cover both paths. The one thing the twin adds
/// on its own is <c>refusalFor</c>, which RE-DERIVES why the classic handler returned an empty
/// event list, and that is exactly where the two could drift apart.
/// </summary>
module FunctionalTwin =

    let private stateWith (balance: decimal) =
        let state = AccountState()
        state.Balance <- balance
        state

    /// <summary>
    /// The invariant, derived from the classic handler rather than restated: whenever
    /// <c>handleCommand</c> refuses a withdrawal, the named refusal the twin reports has to be
    /// the true reason. A refusal that named the wrong cause -- or a case the classic handler
    /// would have accepted -- fails here.
    /// </summary>
    [<Property>]
    let ``a refused withdrawal is named by the guard that actually refused it``
        (NonNegativeInt balance)
        (amount: decimal)
        =
        let state = stateWith (decimal balance)
        let events = AccountGrainDef.handleCommand state (Withdraw amount)

        if events.IsEmpty then
            match AccountFunctionalDef.refusalFor state amount with
            | NonPositiveAmount refused -> refused = amount && amount <= 0m
            | InsufficientFunds(seen, requested) ->
                seen = state.Balance && requested = amount && amount > 0m && state.Balance < amount
        else
            // Accepted by the classic handler, so the twin answers Ok and never consults refusalFor.
            amount > 0m && state.Balance >= amount

    /// <summary>The same invariant for deposits, whose only refusal is a non-positive amount.</summary>
    [<Property>]
    let ``a refused deposit is always a non-positive amount`` (NonNegativeInt balance) (amount: decimal) =
        let state = stateWith (decimal balance)
        let events = AccountGrainDef.handleCommand state (Deposit amount)

        if events.IsEmpty then
            AccountFunctionalDef.refusalFor state amount = NonPositiveAmount amount && amount <= 0m
        else
            amount > 0m

    /// <summary>
    /// The contract and the journaled definition are built at module initialisation, and both
    /// stages validate: the API record's shape, every selector, the required
    /// <c>initialEventState</c>/<c>apply</c> ordering, and the presence of <c>logProvider</c>.
    /// Touching them here turns a definition-stage error into a failing test rather than into a
    /// silo that refuses to start.
    /// </summary>
    [<Fact>]
    let ``the functional contract and journaled definition are well-formed`` () =
        let contract = string AccountApi.contract
        let definition = string AccountFunctionalDef.account

        Assert.Contains("grainType = 'bank-account.account.functional'", contract)
        Assert.Contains("version = 1", contract)
        // Five operations: deposit, withdraw, balance, journalVersion, recycle.
        Assert.Contains("operations = 5", contract)
        // The journal is keyed on the SAME event type the classic definition raises.
        Assert.Contains($"event = '{typeof<AccountEvent>.FullName}'", definition)
        Assert.Contains($"state = '{typeof<AccountState>.FullName}'", definition)
