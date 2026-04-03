namespace BankAccount.Domain

open Orleans.FSharp.EventSourcing

/// <summary>
/// Module containing the bank account event-sourced grain definition.
/// </summary>
module AccountGrainDef =

    /// <summary>
    /// Pure function that applies a single event to the account state.
    /// Deterministic and free of side effects.
    /// </summary>
    /// <param name="state">The current account state.</param>
    /// <param name="event">The event to apply.</param>
    /// <returns>The updated account state.</returns>
    let applyEvent (state: AccountState) (event: AccountEvent) : AccountState =
        match event with
        | Deposited amount ->
            let s = AccountState()
            s.Balance <- state.Balance + amount
            s
        | Withdrawn amount ->
            let s = AccountState()
            s.Balance <- state.Balance - amount
            s

    /// <summary>
    /// Command handler that produces events from commands.
    /// Returns an empty list when the command is a query or is rejected (e.g., overdraft).
    /// </summary>
    /// <param name="state">The current account state.</param>
    /// <param name="command">The command to handle.</param>
    /// <returns>A list of events produced by the command.</returns>
    let handleCommand (state: AccountState) (command: AccountCommand) : AccountEvent list =
        match command with
        | Deposit amount when amount > 0m -> [ Deposited amount ]
        | Withdraw amount when amount > 0m && state.Balance >= amount -> [ Withdrawn amount ]
        | Withdraw _ -> [] // insufficient funds or invalid amount
        | Deposit _ -> [] // invalid amount
        | GetBalance -> [] // query, no events

    /// <summary>
    /// The bank account grain definition using the eventSourcedGrain computation expression.
    /// </summary>
    let account =
        eventSourcedGrain {
            defaultState (AccountState())
            apply applyEvent
            handle handleCommand
            logConsistencyProvider "LogStorage"
        }
