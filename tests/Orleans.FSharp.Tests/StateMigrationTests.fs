module Orleans.FSharp.Tests.StateMigrationTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
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
// tryApplyMigrations tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``tryApplyMigrations returns Ok for valid chain`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "default" })
    ]
    let result = StateMigration.tryApplyMigrations<StateV2> migrations 1 (box { Name = "Alice" })
    match result with
    | Ok v2 ->
        test <@ v2.Name = "Alice" @>
        test <@ v2.Email = "default" @>
    | Error _ -> failwith "Expected Ok"

[<Fact>]
let ``tryApplyMigrations returns Error for duplicate FromVersion`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "" })
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "x" })
    ]
    let result = StateMigration.tryApplyMigrations<StateV2> migrations 1 (box { Name = "Bob" })
    test <@ result |> Result.isError @>

[<Fact>]
let ``tryApplyMigrations returns Error for gap in chain`` () =
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "" })
        StateMigration.migration<StateV2, StateV3> 3 4 (fun v2 -> { Name = v2.Name; Email = v2.Email; Active = false })
    ]
    let result = StateMigration.tryApplyMigrations<StateV3> migrations 1 (box { Name = "Carol" })
    test <@ result |> Result.isError @>

[<Fact>]
let ``tryApplyMigrations returns Ok for empty migration list`` () =
    let input = { Name = "Dave" }
    let result = StateMigration.tryApplyMigrations<StateV1> [] 1 (box input)
    test <@ result = Ok input @>

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

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

/// Identity migration from version N to N+1 that just passes the string through.
let identityMigration (fromVer: int) =
    StateMigration.migration<string, string> fromVer (fromVer + 1) id

[<Property>]
let ``applyMigrations with empty list returns state unchanged for any version`` (name: string) (ver: PositiveInt) =
    let input = box name
    let result = StateMigration.applyMigrations<string> [] ver.Get input
    result = name

[<Property>]
let ``applyMigrations is idempotent when version is already current`` (name: string) (currentVer: PositiveInt) =
    // Single migration from 1 to 2 — starting at version 2 means no migration applies
    let migrations = [
        StateMigration.migration<StateV1, StateV2> 1 2 (fun v1 -> { Name = v1.Name; Email = "migrated" })
    ]
    // Input is at version 2 (already past the only migration), so state is returned unchanged
    let input = { Name = name; Email = "original" }
    let result = StateMigration.applyMigrations<StateV2> migrations 2 (box input)
    result = input

[<Property>]
let ``validate returns empty for contiguous chain of any positive length`` (startVer: NonNegativeInt) (chainLen: PositiveInt) =
    // Build a chain of identity migrations: start → start+1 → ... → start+chainLen
    let start = startVer.Get + 1  // ensure starting version is positive
    let migrations =
        [ for i in 0 .. chainLen.Get - 1 ->
            identityMigration (start + i) ]

    let errors = StateMigration.validate migrations
    errors = []

[<Property>]
let ``validate is deterministic — same input always gives same output`` (startVer: NonNegativeInt) =
    let start = startVer.Get + 1
    let migrations = [
        identityMigration start
        identityMigration (start + 1)
    ]
    let errors1 = StateMigration.validate migrations
    let errors2 = StateMigration.validate migrations
    errors1 = errors2

[<Property>]
let ``validate detects gap for any non-adjacent pair`` (a: NonNegativeInt) (gap: PositiveInt) =
    // gap >= 2 creates a gap between version a+1 and a+1+gap+1 (skip at least one version)
    let v1 = a.Get + 1
    let v2 = v1 + gap.Get + 1  // ensure there is a gap of at least 1
    let migrations = [
        identityMigration v1   // v1 → v1+1
        StateMigration.migration<string, string> v2 (v2 + 1) id  // v2 → v2+1
    ]
    let errors = StateMigration.validate migrations
    // Should detect the gap between v1+1 and v2
    errors.Length > 0

[<Property>]
let ``applying identity migrations preserves string content`` (name: NonEmptyString) (start: NonNegativeInt) =
    let v = start.Get + 1
    // Two-step chain: v → v+1 → v+2, all identity
    let migrations = [
        identityMigration v
        identityMigration (v + 1)
    ]
    let result = StateMigration.applyMigrations<string> migrations v (box name.Get)
    result = name.Get

[<Property>]
let ``tryApplyMigrations returns Ok for contiguous chain of any length`` (startVer: NonNegativeInt) (chainLen: PositiveInt) =
    let start = startVer.Get + 1
    let migrations = [ for i in 0 .. chainLen.Get - 1 -> identityMigration (start + i) ]
    let result = StateMigration.tryApplyMigrations<string> migrations start (box "test")
    result |> Result.isOk

[<Property>]
let ``tryApplyMigrations with valid chain returns same value as applyMigrations`` (name: NonEmptyString) (startVer: NonNegativeInt) =
    let start = startVer.Get + 1
    let migrations = [ identityMigration start ]
    let direct = StateMigration.applyMigrations<string> migrations start (box name.Get)
    let via = StateMigration.tryApplyMigrations<string> migrations start (box name.Get)
    via = Ok direct

[<Property>]
let ``tryApplyMigrations with empty list always returns Ok`` (name: NonNull<string>) (ver: PositiveInt) =
    let result = StateMigration.tryApplyMigrations<string> [] ver.Get (box name.Get)
    result = Ok name.Get
