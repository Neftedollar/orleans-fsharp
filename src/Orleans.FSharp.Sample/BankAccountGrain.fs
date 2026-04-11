namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp.EventSourcing

// ---------------------------------------------------------------------------
// BankAccount Event Sourced Domain
// ---------------------------------------------------------------------------

/// <summary>
/// Events that can occur on a bank account.
/// These are the facts that are stored and replayed to rebuild state.
/// </summary>
type BankAccountEvent =
    /// <summary>A deposit was made to the account.</summary>
    | Deposited of amount: decimal
    /// <summary>A withdrawal was made from the account.</summary>
    | Withdrawn of amount: decimal

/// <summary>
/// Commands that can be sent to the bank account grain.
/// </summary>
type BankAccountCommand =
    /// <summary>Deposit funds into the account.</summary>
    | Deposit of amount: decimal
    /// <summary>Withdraw funds from the account.</summary>
    | Withdraw of amount: decimal
    /// <summary>Query the current balance without modifying state.</summary>
    | GetBalance

/// <summary>
/// The state of a bank account, derived by folding events.
/// </summary>
[<GenerateSerializer>]
[<Sealed>]
type BankAccountState() =
    /// <summary>The current account balance.</summary>
    [<Id(0u)>]
    member val Balance: decimal = 0m with get, set

/// <summary>
/// Grain interface for the event-sourced bank account grain.
/// Inherits <see cref="IFSharpEventSourcedGrain"/> so Orleans can back it with the
/// universal <c>FSharpEventSourcedGrainImpl</c> — no F# type parameters in the C# stub.
/// </summary>
type IBankAccountGrain =
    inherit Orleans.FSharp.IFSharpEventSourcedGrain

/// <summary>
/// Module containing the bank account event-sourced grain definition.
/// </summary>
module BankAccountGrainDef =

    /// <summary>
    /// Pure function that applies a single event to the bank account state.
    /// This function must be deterministic and free of side effects.
    /// </summary>
    /// <param name="state">The current bank account state.</param>
    /// <param name="event">The event to apply.</param>
    /// <returns>The updated bank account state.</returns>
    let applyEvent (state: BankAccountState) (event: BankAccountEvent) : BankAccountState =
        match event with
        | Deposited amount ->
            let s = BankAccountState()
            s.Balance <- state.Balance + amount
            s
        | Withdrawn amount ->
            let s = BankAccountState()
            s.Balance <- state.Balance - amount
            s

    /// <summary>
    /// Command handler that produces events from commands.
    /// Returns an empty list when the command is a query or is rejected.
    /// </summary>
    /// <param name="state">The current bank account state.</param>
    /// <param name="command">The command to handle.</param>
    /// <returns>A list of events produced by the command.</returns>
    let handleCommand (state: BankAccountState) (command: BankAccountCommand) : BankAccountEvent list =
        match command with
        | Deposit amount when amount > 0m -> [ Deposited amount ]
        | Withdraw amount when amount > 0m && state.Balance >= amount -> [ Withdrawn amount ]
        | Withdraw _ -> [] // insufficient funds or invalid amount
        | Deposit _ -> [] // invalid amount
        | GetBalance -> [] // query, no events

    /// <summary>
    /// The bank account grain definition using the eventSourcedGrain CE.
    /// The [&lt;FSharpEventSourcedGrain&gt;] attribute instructs Orleans.FSharp.Generator
    /// to emit a C# stub that wires this definition to IBankAccountGrain.
    /// </summary>
    [<FSharpEventSourcedGrain(typeof<IBankAccountGrain>)>]
    let bankAccount =
        eventSourcedGrain {
            defaultState (BankAccountState())
            apply applyEvent
            handle handleCommand
            logConsistencyProvider "LogStorage"
        }
