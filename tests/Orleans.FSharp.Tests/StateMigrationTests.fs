module Orleans.FSharp.Tests.StateMigrationTests

open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Test state types for migration scenarios
// ---------------------------------------------------------------------------

type StateV1 = { Name: string }

type StateV2 = { Name: string; Email: string }

type StateV3 =
    { Name: string
      Email: string
      Active: bool }

// ---------------------------------------------------------------------------
// migration function tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``migration creates a Migration with correct versions`` () =
    let m = StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "" })
    test <@ m.FromVersion = 1 @>
    test <@ m.ToVersion = 2 @>

[<Fact>]
let ``migration applies typed transform through boxed interface`` () =
    let m = StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "default@test.com" })
    let input: obj = box { Name = "Alice" }
    let result = m.Migrate input :?> StateV2
    test <@ result.Name = "Alice" @>
    test <@ result.Email = "default@test.com" @>

// ---------------------------------------------------------------------------
// applyMigrations tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``applyMigrations with single migration upgrades state`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "none" })
    ]

    let result = StateMigration.applyMigrations<StateV2> migrations 1 (box { Name = "Bob" })
    test <@ result.Name = "Bob" @>
    test <@ result.Email = "none" @>

[<Fact>]
let ``applyMigrations chains multiple migrations`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 ->
            { Name = v1.Name; Email = "default" })
        StateMigration.migration<StateV2, StateV3> 2 3 (fun v2 ->
            { Name = v2.Name; Email = v2.Email; Active = true })
    ]

    let result = StateMigration.applyMigrations<StateV3> migrations 1 (box { Name = "Charlie" })
    test <@ result.Name = "Charlie" @>
    test <@ result.Email = "default" @>
    test <@ result.Active = true @>

[<Fact>]
let ``applyMigrations skips already-applied migrations`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 ->
            { Name = v1.Name; Email = "default" })
        StateMigration.migration<StateV2, StateV3> 2 3 (fun v2 ->
            { Name = v2.Name; Email = v2.Email; Active = true })
    ]

    // Start from version 2, so only migration 2->3 should apply
    let result =
        StateMigration.applyMigrations<StateV3> migrations 2 (box { Name = "Dana"; Email = "dana@test.com" })

    test <@ result.Name = "Dana" @>
    test <@ result.Email = "dana@test.com" @>
    test <@ result.Active = true @>

[<Fact>]
let ``applyMigrations with no applicable migrations returns state as-is`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 ->
            { Name = v1.Name; Email = "default" })
    ]

    // State is already at version 3, no migration from 3
    let input = { Name = "Eve"; Email = "eve@test.com"; Active = false }
    let result = StateMigration.applyMigrations<StateV3> migrations 3 (box input)
    test <@ result = input @>

[<Fact>]
let ``applyMigrations handles empty migration list`` () =
    let input = { Name = "Frank" }
    let result = StateMigration.applyMigrations<StateV1> [] 1 (box input)
    test <@ result = input @>

[<Fact>]
let ``applyMigrations applies migrations in version order regardless of list order`` () =
    let migrations = [
        // Intentionally reversed order in the list
        StateMigration.migration<StateV2, StateV3> 2 3 (fun v2 ->
            { Name = v2.Name; Email = v2.Email; Active = true })
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 ->
            { Name = v1.Name; Email = "sorted" })
    ]

    let result = StateMigration.applyMigrations<StateV3> migrations 1 (box { Name = "Grace" })
    test <@ result.Name = "Grace" @>
    test <@ result.Email = "sorted" @>
    test <@ result.Active = true @>

// ---------------------------------------------------------------------------
// validate tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``validate returns empty list for contiguous chain`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun _ -> { Name = ""; Email = "" })
        StateMigration.migration<StateV2, StateV3> 2 3 (fun _ -> { Name = ""; Email = ""; Active = false })
    ]

    let errors = StateMigration.validate migrations
    test <@ errors = [] @>

[<Fact>]
let ``validate detects duplicate FromVersion`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun _ -> { Name = ""; Email = "" })
        StateMigration.migration<StateV1, StateV2> 1 2 (fun _ -> { Name = "x"; Email = "x" })
    ]

    let errors = StateMigration.validate migrations
    test <@ errors.Length > 0 @>
    test <@ errors |> List.exists (fun e -> e.Contains("Duplicate") && e.Contains("1")) @>

[<Fact>]
let ``validate detects gap in chain`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun _ -> { Name = ""; Email = "" })
        // Gap: missing migration from 2 to 3
        StateMigration.migration<StateV2, StateV3> 3 4 (fun _ -> { Name = ""; Email = ""; Active = false })
    ]

    let errors = StateMigration.validate migrations
    test <@ errors.Length > 0 @>
    test <@ errors |> List.exists (fun e -> e.Contains("Gap")) @>

[<Fact>]
let ``validate returns empty list for single migration`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun _ -> { Name = ""; Email = "" })
    ]

    let errors = StateMigration.validate migrations
    test <@ errors = [] @>

[<Fact>]
let ``validate returns empty list for empty migration list`` () =
    let errors = StateMigration.validate []
    test <@ errors = [] @>

// ---------------------------------------------------------------------------
// Migration type tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``Migration type has expected fields`` () =
    let cases =
        typeof<Migration<obj, obj>>.GetProperties()
        |> Array.map (fun p -> p.Name)
        |> Array.sort

    test <@ cases |> Array.contains "FromVersion" @>
    test <@ cases |> Array.contains "ToVersion" @>
    test <@ cases |> Array.contains "Migrate" @>

[<Fact>]
let ``StateMigration module exists in the assembly`` () =
    let moduleType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "StateMigration" && t.IsAbstract && t.IsSealed)

    test <@ moduleType.IsSome @>
