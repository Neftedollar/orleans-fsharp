module Orleans.FSharp.Tests.EventSourcedGrainDiscoveryTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
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

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``apply Credited increases balance by any positive amount`` (amount: PositiveInt) =
    let state = TestState()
    state.Balance <- 0m
    let event = Credited (decimal amount.Get)
    let updated = testDefinition.Apply state event
    updated.Balance = decimal amount.Get

[<Property>]
let ``handle Debit with overdraft protection rejects for any amount exceeding balance`` (balance: PositiveInt) (excess: PositiveInt) =
    let state = TestState()
    state.Balance <- decimal balance.Get
    let debit = decimal (balance.Get + excess.Get)
    let events = testDefinition.Handle state (Debit debit)
    events = []

// ---------------------------------------------------------------------------
// CustomStorage — registration and callback round-trip
// ---------------------------------------------------------------------------

let private definitionWithCustomStorage =
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
        logConsistencyProvider "CustomStorage"
        customStorage
            (fun () -> task { return 5, (let s = TestState() in s.Balance <- 77m; s) })
            (fun _events _version -> task { return true })
    }

[<Fact>]
let ``definition with customStorage has CustomStorage set to Some`` () =
    test <@ definitionWithCustomStorage.CustomStorage.IsSome @>

[<Fact>]
let ``definition with customStorage has correct ConsistencyProvider`` () =
    test <@ definitionWithCustomStorage.ConsistencyProvider = Some "CustomStorage" @>

[<Fact>]
let ``AddFSharpEventSourcedGrain preserves CustomStorage in registered definition`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpEventSourcedGrain<TestState, TestEvent, TestCommand>(definitionWithCustomStorage)
    |> ignore

    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<EventSourcedGrainDefinition<TestState, TestEvent, TestCommand>>()
    test <@ resolved.CustomStorage.IsSome @>

[<Fact>]
let ``customStorage ReadBoxed returns correct version from registered definition`` () =
    let services = ServiceCollection() :> IServiceCollection

    services.AddFSharpEventSourcedGrain<TestState, TestEvent, TestCommand>(definitionWithCustomStorage)
    |> ignore

    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<EventSourcedGrainDefinition<TestState, TestEvent, TestCommand>>()
    let struct (version, _) = resolved.CustomStorage.Value.ReadBoxed.Invoke().GetAwaiter().GetResult()
    test <@ version = 5 @>

[<Fact>]
let ``customStorage WriteBoxed forwards events and version to the write callback`` () =
    let mutable lastVersion = -1

    let def =
        eventSourcedGrain {
            defaultState (TestState())
            apply (fun state event ->
                let s = TestState()
                match event with
                | Credited amount -> s.Balance <- state.Balance + amount
                | Debited amount -> s.Balance <- state.Balance - amount
                s)
            handle (fun _state _cmd -> [])
            customStorage
                (fun () -> task { return 0, TestState() })
                (fun _events version ->
                    task {
                        lastVersion <- version
                        return true
                    })
        }

    let boxedEvents = [ box (Credited 10m) ]
    def.CustomStorage.Value.WriteBoxed.Invoke(boxedEvents, 42).GetAwaiter().GetResult() |> ignore
    test <@ lastVersion = 42 @>
