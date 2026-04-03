module Orleans.FSharp.Tests.EventSourcedGrainDiscoveryTests

open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp.EventSourcing

// ---------------------------------------------------------------------------
// Test domain
// ---------------------------------------------------------------------------

[<Sealed>]
type TestState() =
    member val Balance: decimal = 0m with get, set

type TestEvent =
    | Credited of decimal
    | Debited of decimal

type TestCommand =
    | Credit of decimal
    | Debit of decimal
    | GetBalance

let testDefinition =
    eventSourcedGrain {
        defaultState (TestState())
        apply (fun state event ->
            let s = TestState()
            match event with
            | Credited amount -> s.Balance <- state.Balance + amount
            | Debited amount -> s.Balance <- state.Balance - amount
            s)
        handle (fun state cmd ->
            match cmd with
            | Credit amount when amount > 0m -> [ Credited amount ]
            | Debit amount when amount > 0m && state.Balance >= amount -> [ Debited amount ]
            | _ -> [])
        logConsistencyProvider "LogStorage"
    }

// ---------------------------------------------------------------------------
// AddFSharpEventSourcedGrain registration tests
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that AddFSharpEventSourcedGrain registers the definition as a singleton.
/// </summary>
[<Fact>]
let ``AddFSharpEventSourcedGrain registers definition as singleton`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpEventSourcedGrain<TestState, TestEvent, TestCommand>(testDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetService<EventSourcedGrainDefinition<TestState, TestEvent, TestCommand>>()
    test <@ resolved |> box |> isNull |> not @>

/// <summary>
/// Verifies that the registered definition preserves the apply function.
/// </summary>
[<Fact>]
let ``registered definition preserves apply function`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpEventSourcedGrain<TestState, TestEvent, TestCommand>(testDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<EventSourcedGrainDefinition<TestState, TestEvent, TestCommand>>()
    let state = TestState()
    state.Balance <- 100m
    let newState = resolved.Apply state (Credited 50m)
    test <@ newState.Balance = 150m @>

/// <summary>
/// Verifies that the registered definition preserves the handle function.
/// </summary>
[<Fact>]
let ``registered definition preserves handle function`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpEventSourcedGrain<TestState, TestEvent, TestCommand>(testDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<EventSourcedGrainDefinition<TestState, TestEvent, TestCommand>>()
    let state = TestState()
    state.Balance <- 100m
    let events = resolved.Handle state (Credit 50m)
    test <@ events = [ Credited 50m ] @>

/// <summary>
/// Verifies that the registered definition preserves the consistency provider.
/// </summary>
[<Fact>]
let ``registered definition preserves consistency provider`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpEventSourcedGrain<TestState, TestEvent, TestCommand>(testDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<EventSourcedGrainDefinition<TestState, TestEvent, TestCommand>>()
    test <@ resolved.ConsistencyProvider = Some "LogStorage" @>

/// <summary>
/// Verifies that AddFSharpEventSourcedGrain returns the service collection for chaining.
/// </summary>
[<Fact>]
let ``AddFSharpEventSourcedGrain returns service collection for chaining`` () =
    let services = ServiceCollection() :> IServiceCollection
    let result = services.AddFSharpEventSourcedGrain<TestState, TestEvent, TestCommand>(testDefinition)
    test <@ obj.ReferenceEquals(services, result) @>

/// <summary>
/// Verifies that definition handle rejects overdraft.
/// </summary>
[<Fact>]
let ``handle rejects overdraft by returning empty events`` () =
    let state = TestState()
    state.Balance <- 10m
    let events = testDefinition.Handle state (Debit 50m)
    test <@ events = [] @>

/// <summary>
/// Verifies that GetBalance produces no events.
/// </summary>
[<Fact>]
let ``handle GetBalance produces no events`` () =
    let state = TestState()
    state.Balance <- 100m
    let events = testDefinition.Handle state GetBalance
    test <@ events = [] @>
