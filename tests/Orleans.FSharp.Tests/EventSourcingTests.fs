module Orleans.FSharp.Tests.EventSourcingTests

open Xunit
open Swensen.Unquote
open FsCheck.Xunit
open Orleans.FSharp.EventSourcing

// ---------------------------------------------------------------------------
// Test domain: BankAccount
// ---------------------------------------------------------------------------

/// Events that can happen to a bank account.
type BankAccountEvent =
    | Deposited of decimal
    | Withdrawn of decimal

/// Commands that can be sent to a bank account.
type BankAccountCommand =
    | Credit of decimal
    | Debit of decimal
    | GetBalance

/// Bank account state.
type BankAccountState = { Balance: decimal }

/// Pure apply function: folds a single event into state.
let applyEvent (state: BankAccountState) (event: BankAccountEvent) : BankAccountState =
    match event with
    | Deposited amount -> { state with Balance = state.Balance + amount }
    | Withdrawn amount -> { state with Balance = state.Balance - amount }

/// Command handler: produces events from a command given current state.
let handleCommand (state: BankAccountState) (command: BankAccountCommand) : BankAccountEvent list =
    match command with
    | Credit amount when amount > 0m -> [ Deposited amount ]
    | Debit amount when amount > 0m && state.Balance >= amount -> [ Withdrawn amount ]
    | Debit _ -> [] // insufficient funds or invalid amount: no events
    | Credit _ -> [] // invalid amount: no events
    | GetBalance -> [] // query: no events

// ---------------------------------------------------------------------------
// V031: CE produces valid EventSourcedGrainDefinition
// ---------------------------------------------------------------------------

[<Fact>]
let ``eventSourcedGrain CE sets defaultState`` () =
    let def =
        eventSourcedGrain {
            defaultState { Balance = 0m }
            apply applyEvent
            handle handleCommand
        }

    test <@ def.DefaultState = Some { Balance = 0m } @>

[<Fact>]
let ``eventSourcedGrain CE sets apply function`` () =
    let def =
        eventSourcedGrain {
            defaultState { Balance = 0m }
            apply applyEvent
            handle handleCommand
        }

    test <@ def.Apply { Balance = 10m } (Deposited 5m) = { Balance = 15m } @>

[<Fact>]
let ``eventSourcedGrain CE sets handle function`` () =
    let def =
        eventSourcedGrain {
            defaultState { Balance = 100m }
            apply applyEvent
            handle handleCommand
        }

    test <@ def.Handle { Balance = 100m } (Credit 50m) = [ Deposited 50m ] @>

[<Fact>]
let ``eventSourcedGrain CE sets logConsistencyProvider`` () =
    let def =
        eventSourcedGrain {
            defaultState { Balance = 0m }
            apply applyEvent
            handle handleCommand
            logConsistencyProvider "LogStorage"
        }

    test <@ def.ConsistencyProvider = Some "LogStorage" @>

[<Fact>]
let ``eventSourcedGrain CE without logConsistencyProvider has None`` () =
    let def =
        eventSourcedGrain {
            defaultState { Balance = 0m }
            apply applyEvent
            handle handleCommand
        }

    test <@ def.ConsistencyProvider = None @>

[<Fact>]
let ``eventSourcedGrain CE produces complete definition`` () =
    let def =
        eventSourcedGrain {
            defaultState { Balance = 0m }
            apply applyEvent
            handle handleCommand
            logConsistencyProvider "LogStorage"
        }

    test <@ def.DefaultState = Some { Balance = 0m } @>
    test <@ def.ConsistencyProvider = Some "LogStorage" @>
    // Verify apply works
    test <@ def.Apply { Balance = 0m } (Deposited 100m) = { Balance = 100m } @>
    // Verify handle works
    test <@ def.Handle { Balance = 100m } (Debit 50m) = [ Withdrawn 50m ] @>

// ---------------------------------------------------------------------------
// V031: apply function folds events correctly (pure, no Orleans)
// ---------------------------------------------------------------------------

[<Fact>]
let ``apply folds single deposit`` () =
    let state = { Balance = 0m }
    let result = applyEvent state (Deposited 100m)
    test <@ result = { Balance = 100m } @>

[<Fact>]
let ``apply folds single withdrawal`` () =
    let state = { Balance = 100m }
    let result = applyEvent state (Withdrawn 30m)
    test <@ result = { Balance = 70m } @>

[<Fact>]
let ``apply folds multiple events sequentially`` () =
    let events = [ Deposited 100m; Withdrawn 30m; Deposited 50m; Withdrawn 20m ]
    let finalState = events |> List.fold applyEvent { Balance = 0m }
    test <@ finalState = { Balance = 100m } @>

// ---------------------------------------------------------------------------
// V031: handle command handler produces expected events
// ---------------------------------------------------------------------------

[<Fact>]
let ``handle Credit produces Deposited event`` () =
    let events = handleCommand { Balance = 0m } (Credit 100m)
    test <@ events = [ Deposited 100m ] @>

[<Fact>]
let ``handle Debit with sufficient funds produces Withdrawn event`` () =
    let events = handleCommand { Balance = 100m } (Debit 50m)
    test <@ events = [ Withdrawn 50m ] @>

[<Fact>]
let ``handle Debit with insufficient funds produces no events`` () =
    let events = handleCommand { Balance = 10m } (Debit 50m)
    test <@ events = [] @>

[<Fact>]
let ``handle GetBalance produces no events`` () =
    let events = handleCommand { Balance = 100m } GetBalance
    test <@ events = [] @>

[<Fact>]
let ``handle Credit with zero amount produces no events`` () =
    let events = handleCommand { Balance = 100m } (Credit 0m)
    test <@ events = [] @>

[<Fact>]
let ``handle Credit with negative amount produces no events`` () =
    let events = handleCommand { Balance = 100m } (Credit -10m)
    test <@ events = [] @>

// ---------------------------------------------------------------------------
// V031: FsCheck property: fold(events) = sequential apply for BankAccount
// ---------------------------------------------------------------------------

[<Property>]
let ``fold of events equals sequential apply`` (amounts: decimal list) =
    let events =
        amounts
        |> List.filter (fun a -> a > 0m && a < 1_000_000m)
        |> List.map Deposited

    let foldResult = events |> List.fold applyEvent { Balance = 0m }

    let sequentialResult =
        events
        |> List.fold (fun state event -> applyEvent state event) { Balance = 0m }

    foldResult = sequentialResult

[<Property>]
let ``balance is always sum of deposits minus withdrawals`` (deposits: decimal list) =
    let validDeposits =
        deposits
        |> List.filter (fun a -> a > 0m && a < 1_000_000m)

    let events = validDeposits |> List.map Deposited
    let finalState = events |> List.fold applyEvent { Balance = 0m }
    let expectedBalance = validDeposits |> List.sum
    finalState.Balance = expectedBalance

[<Property>]
let ``command handler never produces events for GetBalance`` (balance: decimal) =
    let state = { Balance = abs balance }
    let events = handleCommand state GetBalance
    events = []

[<Property>]
let ``Credit then fold produces correct balance`` (initial: decimal) (amount: decimal) =
    let amount = abs amount % 1_000_000m + 0.01m // ensure positive
    let state = { Balance = abs initial }
    let events = handleCommand state (Credit amount)
    let finalState = events |> List.fold applyEvent state
    finalState.Balance = state.Balance + amount

[<Property>]
let ``arbitrary command sequences produce non-negative balance when starting at zero`` (commands: BankAccountCommand list) =
    // Filter to valid commands only
    let validCommands =
        commands
        |> List.map (fun cmd ->
            match cmd with
            | Credit amount -> Credit(abs amount % 1_000_000m)
            | Debit amount -> Debit(abs amount % 1_000_000m)
            | GetBalance -> GetBalance)

    let applyCommand (state: BankAccountState) (cmd: BankAccountCommand) =
        let events = handleCommand state cmd
        events |> List.fold applyEvent state

    let finalState = validCommands |> List.fold applyCommand { Balance = 0m }
    finalState.Balance >= 0m
