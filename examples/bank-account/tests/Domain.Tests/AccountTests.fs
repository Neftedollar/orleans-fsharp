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
