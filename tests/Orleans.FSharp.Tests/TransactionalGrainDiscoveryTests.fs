module Orleans.FSharp.Tests.TransactionalGrainDiscoveryTests

open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Orleans.FSharp.Runtime

// ---------------------------------------------------------------------------
// Test domain types
// ---------------------------------------------------------------------------

[<Sealed>]
type AccountState() =
    member val Balance: decimal = 0m with get, set

[<Sealed>]
type InventoryState() =
    member val Quantity: int = 0 with get, set

let accountDefinition: TransactionalGrainDefinition<AccountState> =
    {
        Deposit = fun state amount -> let s = AccountState() in s.Balance <- state.Balance + amount; s
        Withdraw = fun state amount ->
            if state.Balance < amount then
                failwith "Overdraft"
            let s = AccountState()
            s.Balance <- state.Balance - amount
            s
        GetBalance = fun state -> state.Balance
        CopyState = fun source target -> target.Balance <- source.Balance
    }

let inventoryDefinition: TransactionalGrainDefinition<InventoryState> =
    {
        Deposit = fun state amount -> let s = InventoryState() in s.Quantity <- state.Quantity + int amount; s
        Withdraw = fun state amount ->
            if state.Quantity < int amount then
                failwith "Insufficient inventory"
            let s = InventoryState()
            s.Quantity <- state.Quantity - int amount
            s
        GetBalance = fun state -> decimal state.Quantity
        CopyState = fun source target -> target.Quantity <- source.Quantity
    }

// ---------------------------------------------------------------------------
// AddFSharpTransactionalGrain registration tests
// ---------------------------------------------------------------------------

/// <summary>Verifies that AddFSharpTransactionalGrain registers the definition as a singleton.</summary>
[<Fact>]
let ``AddFSharpTransactionalGrain registers definition as singleton`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpTransactionalGrain<AccountState>(accountDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetService<TransactionalGrainDefinition<AccountState>>()
    test <@ resolved |> box |> isNull |> not @>

/// <summary>Verifies that the registered definition is resolvable from DI.</summary>
[<Fact>]
let ``definition is resolvable from DI`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpTransactionalGrain<AccountState>(accountDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<TransactionalGrainDefinition<AccountState>>()
    test <@ resolved.GetBalance (AccountState()) = 0m @>

/// <summary>Verifies that TransactionalGrainDefinition has the expected members.</summary>
[<Fact>]
let ``TransactionalGrainDefinition has expected members`` () =
    let t = typeof<TransactionalGrainDefinition<AccountState>>
    let fields = Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(t)
    let fieldNames = fields |> Array.map (fun f -> f.Name) |> Array.sort
    test <@ fieldNames = [| "CopyState"; "Deposit"; "GetBalance"; "Withdraw" |] @>

/// <summary>Verifies registration with different state types works independently.</summary>
[<Fact>]
let ``registration with different state types`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpTransactionalGrain<AccountState>(accountDefinition) |> ignore
    services.AddFSharpTransactionalGrain<InventoryState>(inventoryDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let acctDef = sp.GetRequiredService<TransactionalGrainDefinition<AccountState>>()
    let invDef = sp.GetRequiredService<TransactionalGrainDefinition<InventoryState>>()
    test <@ acctDef.GetBalance (AccountState()) = 0m @>
    test <@ invDef.GetBalance (InventoryState()) = 0m @>

/// <summary>Verifies that AddFSharpTransactionalGrain returns the service collection for chaining.</summary>
[<Fact>]
let ``AddFSharpTransactionalGrain returns service collection for chaining`` () =
    let services = ServiceCollection() :> IServiceCollection
    let result = services.AddFSharpTransactionalGrain<AccountState>(accountDefinition)
    test <@ obj.ReferenceEquals(services, result) @>

/// <summary>Verifies that the registered Deposit function works correctly.</summary>
[<Fact>]
let ``registered definition preserves Deposit function`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpTransactionalGrain<AccountState>(accountDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<TransactionalGrainDefinition<AccountState>>()
    let state = AccountState()
    state.Balance <- 100m
    let newState = resolved.Deposit state 50m
    test <@ newState.Balance = 150m @>

/// <summary>Verifies that the registered Withdraw function works correctly.</summary>
[<Fact>]
let ``registered definition preserves Withdraw function`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpTransactionalGrain<AccountState>(accountDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<TransactionalGrainDefinition<AccountState>>()
    let state = AccountState()
    state.Balance <- 100m
    let newState = resolved.Withdraw state 30m
    test <@ newState.Balance = 70m @>

/// <summary>Verifies that Withdraw throws on overdraft.</summary>
[<Fact>]
let ``Withdraw throws on overdraft`` () =
    let state = AccountState()
    state.Balance <- 10m

    let ex =
        Assert.Throws<System.Exception>(fun () ->
            accountDefinition.Withdraw state 50m |> ignore)

    test <@ ex.Message.Contains("Overdraft") @>

/// <summary>Verifies that the registered CopyState function copies correctly.</summary>
[<Fact>]
let ``registered definition preserves CopyState function`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpTransactionalGrain<AccountState>(accountDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<TransactionalGrainDefinition<AccountState>>()
    let source = AccountState()
    source.Balance <- 250m
    let target = AccountState()
    resolved.CopyState source target
    test <@ target.Balance = 250m @>

/// <summary>Verifies that the registered GetBalance function returns the correct value.</summary>
[<Fact>]
let ``registered definition preserves GetBalance function`` () =
    let services = ServiceCollection() :> IServiceCollection
    services.AddFSharpTransactionalGrain<AccountState>(accountDefinition) |> ignore
    let sp = services.BuildServiceProvider()
    let resolved = sp.GetRequiredService<TransactionalGrainDefinition<AccountState>>()
    let state = AccountState()
    state.Balance <- 42.5m
    test <@ resolved.GetBalance state = 42.5m @>
