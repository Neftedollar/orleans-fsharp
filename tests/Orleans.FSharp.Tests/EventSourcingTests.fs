module Orleans.FSharp.Tests.EventSourcingTests

open Xunit
open Swensen.Unquote
open FsCheck
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

// ---------------------------------------------------------------------------
// EventStore module — pure function property tests
// ---------------------------------------------------------------------------

/// <summary>
/// Shared bank-account grain definition used by EventStore property tests.
/// Defined here so all tests in this section share the same definition instance.
/// </summary>
let private bankAccountDef =
    eventSourcedGrain {
        defaultState { Balance = 0m }
        apply applyEvent
        handle handleCommand
    }

// ── EventStore.replayEvents ──────────────────────────────────────────────────

[<Fact>]
let ``replayEvents with empty list is identity`` () =
    let state = { Balance = 250m }
    let result = EventStore.replayEvents bankAccountDef state []
    test <@ result = state @>

[<Fact>]
let ``replayEvents with single deposit event changes balance`` () =
    let state = { Balance = 100m }
    let result = EventStore.replayEvents bankAccountDef state [ Deposited 50m ]
    test <@ result = { Balance = 150m } @>

[<Property>]
let ``replayEvents is equivalent to List.fold definition.Apply`` (amounts: decimal list) =
    let validAmounts =
        amounts |> List.filter (fun a -> a > 0m && a < 1_000_000m)

    let events = validAmounts |> List.map Deposited
    let initial = { Balance = 0m }

    let viaReplay = EventStore.replayEvents bankAccountDef initial events
    let viaFold = events |> List.fold (fun s e -> EventStore.applyEvent bankAccountDef s e) initial

    viaReplay = viaFold

[<Property>]
let ``replayEvents satisfies fold-concatenation: replay of appended lists equals staged replay``
    (amounts1: decimal list)
    (amounts2: decimal list)
    =
    let toEvents amounts =
        amounts
        |> List.filter (fun a -> a > 0m && a < 100_000m)
        |> List.map Deposited

    let e1 = toEvents amounts1
    let e2 = toEvents amounts2
    let initial = { Balance = 0m }

    let combined = EventStore.replayEvents bankAccountDef initial (e1 @ e2)
    let staged = EventStore.replayEvents bankAccountDef (EventStore.replayEvents bankAccountDef initial e1) e2

    combined = staged

[<Property>]
let ``replayEvents with only GetBalance commands (no events) does not change state`` (balances: decimal list) =
    // GetBalance produces no events, so replayEvents on an empty list is identity
    let initial = { Balance = 0m }
    // apply no events
    let result = EventStore.replayEvents bankAccountDef initial []
    result = initial

// ── EventStore.applyEvent ────────────────────────────────────────────────────

[<Property>]
let ``applyEvent Deposited increases balance by amount`` (initial: decimal) (amount: decimal) =
    let amount = abs amount % 1_000_000m + 0.01m
    let state = { Balance = abs initial % 1_000_000m }
    let result = EventStore.applyEvent bankAccountDef state (Deposited amount)
    result.Balance = state.Balance + amount

[<Property>]
let ``applyEvent Withdrawn decreases balance by amount`` (initial: decimal) (amount: decimal) =
    let amount = abs amount % 1_000_000m + 0.01m
    let state = { Balance = abs initial % 1_000_000m + amount }
    let result = EventStore.applyEvent bankAccountDef state (Withdrawn amount)
    result.Balance = state.Balance - amount

// ── EventStore.processCommand ─────────────────────────────────────────────────

[<Property>]
let ``processCommand is deterministic: same state + command always produces same events``
    (balance: decimal)
    =
    let state = { Balance = abs balance % 1_000_000m }
    let cmd = Credit 100m
    let events1 = EventStore.processCommand bankAccountDef state cmd
    let events2 = EventStore.processCommand bankAccountDef state cmd
    events1 = events2

[<Property>]
let ``processCommand GetBalance always returns empty event list`` (balance: decimal) =
    let state = { Balance = abs balance % 1_000_000m }
    let events = EventStore.processCommand bankAccountDef state GetBalance
    events = []

[<Property>]
let ``processCommand Credit with positive amount produces exactly one event`` (amount: decimal) =
    let amount = abs amount % 1_000_000m + 0.01m
    let state = { Balance = 0m }
    let events = EventStore.processCommand bankAccountDef state (Credit amount)
    events.Length = 1

[<Property>]
let ``processCommand Debit with sufficient funds produces exactly one event`` (amount: decimal) =
    let amount = abs amount % 1_000_000m + 0.01m
    let state = { Balance = amount + 1m }
    let events = EventStore.processCommand bankAccountDef state (Debit amount)
    events.Length = 1

[<Property>]
let ``processCommand then replayEvents produces state consistent with manual fold``
    (initial: decimal)
    (amount: decimal)
    =
    let amount = abs amount % 1_000_000m + 0.01m
    let state = { Balance = abs initial % 1_000_000m }
    let events = EventStore.processCommand bankAccountDef state (Credit amount)
    let resultViaReplay = EventStore.replayEvents bankAccountDef state events
    let resultViaFold = events |> List.fold (fun s e -> EventStore.applyEvent bankAccountDef s e) state
    resultViaReplay = resultViaFold
