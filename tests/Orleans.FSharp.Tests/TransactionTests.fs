module Orleans.FSharp.Tests.TransactionTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Transactions
open Orleans.Transactions.Abstractions

/// <summary>Tests for Transactions.fs — TransactionOption DU, TransactionOption module, and TransactionalState module.</summary>

// --- TransactionOption DU tests ---

[<Fact>]
let ``TransactionOption is a discriminated union with 6 cases`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<TransactionOption>)

    test <@ cases.Length = 6 @>
    let names = cases |> Array.map (fun c -> c.Name) |> Array.sort

    test
        <@ names = [| "Create"; "CreateOrJoin"; "Join"; "NotAllowed"; "Supported"; "Suppress" |] @>

[<Fact>]
let ``TransactionOption.Create case can be constructed`` () =
    let opt = TransactionOption.Create
    test <@ opt = Create @>

[<Fact>]
let ``TransactionOption.Join case can be constructed`` () =
    let opt = TransactionOption.Join
    test <@ opt = Join @>

[<Fact>]
let ``TransactionOption.CreateOrJoin case can be constructed`` () =
    let opt = TransactionOption.CreateOrJoin
    test <@ opt = CreateOrJoin @>

[<Fact>]
let ``TransactionOption.Supported case can be constructed`` () =
    let opt = TransactionOption.Supported
    test <@ opt = Supported @>

[<Fact>]
let ``TransactionOption.NotAllowed case can be constructed`` () =
    let opt = TransactionOption.NotAllowed
    test <@ opt = NotAllowed @>

[<Fact>]
let ``TransactionOption.Suppress case can be constructed`` () =
    let opt = TransactionOption.Suppress
    test <@ opt = Suppress @>

// --- TransactionOption.toOrleans mapping tests ---

[<Fact>]
let ``toOrleans maps Create to Orleans TransactionOption.Create`` () =
    let result = TransactionOption.toOrleans Create
    test <@ result = Orleans.TransactionOption.Create @>

[<Fact>]
let ``toOrleans maps Join to Orleans TransactionOption.Join`` () =
    let result = TransactionOption.toOrleans Join
    test <@ result = Orleans.TransactionOption.Join @>

[<Fact>]
let ``toOrleans maps CreateOrJoin to Orleans TransactionOption.CreateOrJoin`` () =
    let result = TransactionOption.toOrleans CreateOrJoin
    test <@ result = Orleans.TransactionOption.CreateOrJoin @>

[<Fact>]
let ``toOrleans maps Supported to Orleans TransactionOption.Supported`` () =
    let result = TransactionOption.toOrleans Supported
    test <@ result = Orleans.TransactionOption.Supported @>

[<Fact>]
let ``toOrleans maps NotAllowed to Orleans TransactionOption.NotAllowed`` () =
    let result = TransactionOption.toOrleans NotAllowed
    test <@ result = Orleans.TransactionOption.NotAllowed @>

[<Fact>]
let ``toOrleans maps Suppress to Orleans TransactionOption.Suppress`` () =
    let result = TransactionOption.toOrleans Suppress
    test <@ result = Orleans.TransactionOption.Suppress @>

// --- TransactionalState module function signature tests ---

[<Fact>]
let ``TransactionalState module exists in the assembly`` () =
    let stateModule =
        typeof<TransactionOption>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "TransactionalState" && t.IsAbstract && t.IsSealed)

    test <@ stateModule.IsSome @>

[<Fact>]
let ``TransactionalState.read method exists`` () =
    let stateModule =
        typeof<TransactionOption>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "TransactionalState" && t.IsAbstract && t.IsSealed)

    let readMethod =
        stateModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "read")

    test <@ readMethod.IsSome @>

[<Fact>]
let ``TransactionalState.update method exists`` () =
    let stateModule =
        typeof<TransactionOption>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "TransactionalState" && t.IsAbstract && t.IsSealed)

    let updateMethod =
        stateModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "update")

    test <@ updateMethod.IsSome @>

[<Fact>]
let ``TransactionalState.performRead method exists`` () =
    let stateModule =
        typeof<TransactionOption>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "TransactionalState" && t.IsAbstract && t.IsSealed)

    let performReadMethod =
        stateModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "performRead")

    test <@ performReadMethod.IsSome @>

// --- TransactionalState.read return type test ---

[<Fact>]
let ``TransactionalState.read returns Task<'T>`` () =
    let stateModule =
        typeof<TransactionOption>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "TransactionalState" && t.IsAbstract && t.IsSealed)

    let readMethod =
        stateModule.GetMethods()
        |> Array.find (fun m -> m.Name = "read")

    test <@ readMethod.ReturnType.GetGenericTypeDefinition() = typedefof<Task<_>> @>

// --- TransactionOption equality tests ---

[<Fact>]
let ``TransactionOption cases support structural equality`` () =
    test <@ Create = Create @>
    test <@ Join = Join @>
    test <@ Create <> Join @>
    test <@ Supported <> NotAllowed @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``toOrleans mapping is total — all 6 cases are covered`` () =
    let allCases =
        [ Create; Join; CreateOrJoin; Supported; NotAllowed; Suppress ]

    allCases
    |> List.map TransactionOption.toOrleans
    |> List.length = 6

[<Property>]
let ``TransactionOption DU has exactly 6 cases`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<TransactionOption>)

    cases.Length = 6

[<Property>]
let ``TransactionalState module has at least 3 public methods`` () =
    let stateModule =
        typeof<TransactionOption>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "TransactionalState" && t.IsAbstract && t.IsSealed)

    stateModule.GetMethods().Length >= 3
