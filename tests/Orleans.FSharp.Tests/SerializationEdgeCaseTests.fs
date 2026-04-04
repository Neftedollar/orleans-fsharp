module Orleans.FSharp.Tests.SerializationEdgeCaseTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// ===========================================================================
// Test types for edge cases
// ===========================================================================

/// <summary>DU with string data for edge case testing.</summary>
type StringDU =
    | Label of string
    | Empty

/// <summary>DU with numeric data for boundary testing.</summary>
type NumericDU =
    | IntCase of int
    | FloatCase of float
    | DecimalCase of decimal

/// <summary>Deeply nestable tree DU for depth testing.</summary>
type TreeNode =
    | TreeLeaf of int
    | TreeBranch of TreeNode * TreeNode

/// <summary>DU containing DateTime, Guid, and TimeSpan for serialization testing.</summary>
type TemporalDU =
    | WithDateTime of DateTime
    | WithDateTimeOffset of DateTimeOffset
    | WithGuid of Guid
    | WithTimeSpan of TimeSpan

/// <summary>DU containing byte array for binary data testing.</summary>
type BinaryDU =
    | Bytes of byte array
    | NoBytes

/// <summary>Record with all optional fields for None/Some edge case testing.</summary>
type AllOptionalRecord =
    { A: int option
      B: string option
      C: float option
      D: bool option }

/// <summary>DU with large collection payloads for stress testing.</summary>
type CollectionDU =
    | BigList of int list
    | BigMap of Map<string, int>
    | BigSet of Set<int>

/// <summary>DU with single case and no fields.</summary>
type UnitDU = | Singleton

/// <summary>DU with single case carrying data.</summary>
type SingleDataDU = | Wrapped of value: int

// ===========================================================================
// Roundtrip helpers
// ===========================================================================

/// <summary>JSON serializer options pre-configured for F# types.</summary>
let private jsonOptions = FSharpJson.serializerOptions

/// <summary>Serialize then deserialize a value through JSON.</summary>
let private roundTrip<'T> (value: 'T) : 'T =
    let json = JsonSerializer.Serialize<'T>(value, jsonOptions)
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

// ===========================================================================
// String edge cases
// ===========================================================================

/// <summary>DU case with empty string roundtrips correctly.</summary>
[<Fact>]
let ``Empty string in DU case roundtrips`` () =
    let value = Label ""
    let result = roundTrip value
    test <@ result = value @>

/// <summary>DU case with very long string (10000 chars) roundtrips correctly.</summary>
[<Fact>]
let ``Very long string in DU case roundtrips`` () =
    let longStr = String.replicate 10000 "a"
    let value = Label longStr
    let result = roundTrip value
    test <@ result = value @>

/// <summary>String with special JSON characters roundtrips correctly.</summary>
[<Fact>]
let ``String with special JSON characters roundtrips`` () =
    let value = Label "he said \"hello\" and \\ then\nnewline\ttab"
    let result = roundTrip value
    test <@ result = value @>

/// <summary>String with unicode characters roundtrips correctly.</summary>
[<Fact>]
let ``String with unicode characters roundtrips`` () =
    let value = Label "\U0001F600 Emoji and \u4e16\u754c Chinese"
    let result = roundTrip value
    test <@ result = value @>

/// <summary>String with null characters roundtrips correctly.</summary>
[<Fact>]
let ``String with null character roundtrips`` () =
    let value = Label "before\u0000after"
    let result = roundTrip value
    test <@ result = value @>

// ===========================================================================
// Numeric edge cases
// ===========================================================================

/// <summary>Int32.MinValue roundtrips correctly in a DU.</summary>
[<Fact>]
let ``Int32 MinValue roundtrips in DU`` () =
    let value = IntCase Int32.MinValue
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Int32.MaxValue roundtrips correctly in a DU.</summary>
[<Fact>]
let ``Int32 MaxValue roundtrips in DU`` () =
    let value = IntCase Int32.MaxValue
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Zero roundtrips correctly in a DU.</summary>
[<Fact>]
let ``Zero int roundtrips in DU`` () =
    let value = IntCase 0
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Negative int roundtrips correctly in a DU.</summary>
[<Fact>]
let ``Negative int roundtrips in DU`` () =
    let value = IntCase -42
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Float infinity does not roundtrip through JSON (expected behavior).</summary>
[<Fact>]
let ``Float infinity throws on JSON serialize`` () =
    let value = FloatCase Double.PositiveInfinity
    Assert.ThrowsAny<exn>(fun () ->
        JsonSerializer.Serialize(value, jsonOptions) |> ignore)
    |> ignore

/// <summary>Float NaN does not roundtrip through JSON (expected behavior).</summary>
[<Fact>]
let ``Float NaN throws on JSON serialize`` () =
    let value = FloatCase Double.NaN
    Assert.ThrowsAny<exn>(fun () ->
        JsonSerializer.Serialize(value, jsonOptions) |> ignore)
    |> ignore

/// <summary>Float negative infinity does not roundtrip through JSON.</summary>
[<Fact>]
let ``Float negative infinity throws on JSON serialize`` () =
    let value = FloatCase Double.NegativeInfinity
    Assert.ThrowsAny<exn>(fun () ->
        JsonSerializer.Serialize(value, jsonOptions) |> ignore)
    |> ignore

/// <summary>Very small positive float roundtrips correctly.</summary>
[<Fact>]
let ``Float Epsilon roundtrips in DU`` () =
    let value = FloatCase Double.Epsilon
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Decimal.MinValue roundtrips correctly in a DU.</summary>
[<Fact>]
let ``Decimal MinValue roundtrips in DU`` () =
    let value = DecimalCase Decimal.MinValue
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Decimal.MaxValue roundtrips correctly in a DU.</summary>
[<Fact>]
let ``Decimal MaxValue roundtrips in DU`` () =
    let value = DecimalCase Decimal.MaxValue
    let result = roundTrip value
    test <@ result = value @>

// ===========================================================================
// Collection edge cases
// ===========================================================================

/// <summary>Empty list in DU case roundtrips correctly.</summary>
[<Fact>]
let ``Empty list in DU roundtrips`` () =
    let value = BigList []
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Empty map in DU case roundtrips correctly.</summary>
[<Fact>]
let ``Empty map in DU roundtrips`` () =
    let value = BigMap Map.empty
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Empty set in DU case roundtrips correctly.</summary>
[<Fact>]
let ``Empty set in DU roundtrips`` () =
    let value = BigSet Set.empty
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Large list (1000 items) in DU case roundtrips correctly.</summary>
[<Fact>]
let ``Large list in DU roundtrips`` () =
    let value = BigList [ 1..1000 ]
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Large map (1000 entries) in DU case roundtrips correctly.</summary>
[<Fact>]
let ``Large map in DU roundtrips`` () =
    let entries = [ for i in 1..1000 -> $"key{i}", i ]
    let value = BigMap(Map.ofList entries)
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Large set (1000 items) in DU case roundtrips correctly.</summary>
[<Fact>]
let ``Large set in DU roundtrips`` () =
    let value = BigSet(Set.ofList [ 1..1000 ])
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Map with special characters in keys roundtrips correctly.</summary>
[<Fact>]
let ``Map with special char keys roundtrips`` () =
    let m = Map.ofList [ "key with spaces", 1; "key\"quotes", 2; "key\nnewline", 3 ]
    let value = BigMap m
    let result = roundTrip value
    test <@ result = value @>

// ===========================================================================
// Deeply nested DU edge cases
// ===========================================================================

/// <summary>Build a tree of specified depth for testing deep nesting.</summary>
let private buildDeepTree depth =
    let rec build d =
        if d <= 0 then TreeLeaf 0
        else TreeBranch(build (d - 1), TreeLeaf d)
    build depth

/// <summary>Deeply nested DU (10 levels) roundtrips correctly.</summary>
[<Fact>]
let ``Deeply nested DU 10 levels roundtrips`` () =
    let tree = buildDeepTree 10
    let result = roundTrip tree
    test <@ result = tree @>

/// <summary>Deeply nested DU (20 levels) roundtrips correctly.</summary>
[<Fact>]
let ``Deeply nested DU 20 levels roundtrips`` () =
    let tree = buildDeepTree 20
    let result = roundTrip tree
    test <@ result = tree @>

// ===========================================================================
// Single-case DU edge cases
// ===========================================================================

/// <summary>Singleton (fieldless single-case) DU roundtrips correctly.</summary>
[<Fact>]
let ``Singleton DU roundtrips`` () =
    let value = Singleton
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Single-case DU with data roundtrips correctly.</summary>
[<Fact>]
let ``Single data case DU roundtrips`` () =
    let value = Wrapped 999
    let result = roundTrip value
    test <@ result = value @>

// ===========================================================================
// All-optional record edge cases
// ===========================================================================

/// <summary>Record with all fields set to None roundtrips correctly.</summary>
[<Fact>]
let ``All optional fields None roundtrips`` () =
    let value = { A = None; B = None; C = None; D = None }
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Record with all fields set to Some roundtrips correctly.</summary>
[<Fact>]
let ``All optional fields Some roundtrips`` () =
    let value = { A = Some 42; B = Some "hello"; C = Some 3.14; D = Some true }
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Record with mixed None/Some roundtrips correctly.</summary>
[<Fact>]
let ``Mixed optional fields roundtrips`` () =
    let value = { A = Some 1; B = None; C = Some 2.0; D = None }
    let result = roundTrip value
    test <@ result = value @>

// ===========================================================================
// Temporal and Guid edge cases
// ===========================================================================

/// <summary>DateTime roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``DateTime in DU roundtrips`` () =
    let dt = DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc)
    let value = WithDateTime dt
    let result = roundTrip value
    test <@ result = value @>

/// <summary>DateTimeOffset roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``DateTimeOffset in DU roundtrips`` () =
    let dto = DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.FromHours(5.0))
    let value = WithDateTimeOffset dto
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Guid roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``Guid in DU roundtrips`` () =
    let g = Guid.NewGuid()
    let value = WithGuid g
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Guid.Empty roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``Guid Empty in DU roundtrips`` () =
    let value = WithGuid Guid.Empty
    let result = roundTrip value
    test <@ result = value @>

/// <summary>TimeSpan roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``TimeSpan in DU roundtrips`` () =
    let ts = TimeSpan.FromMinutes(90.5)
    let value = WithTimeSpan ts
    let result = roundTrip value
    test <@ result = value @>

/// <summary>TimeSpan.Zero roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``TimeSpan Zero in DU roundtrips`` () =
    let value = WithTimeSpan TimeSpan.Zero
    let result = roundTrip value
    test <@ result = value @>

// ===========================================================================
// Byte array edge cases
// ===========================================================================

/// <summary>Byte array roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``Byte array in DU roundtrips`` () =
    let value = Bytes [| 0uy; 1uy; 255uy; 128uy |]
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Empty byte array roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``Empty byte array in DU roundtrips`` () =
    let value = Bytes [||]
    let result = roundTrip value
    test <@ result = value @>

/// <summary>Large byte array (1000 bytes) roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``Large byte array in DU roundtrips`` () =
    let bytes = Array.init 1000 (fun i -> byte (i % 256))
    let value = Bytes bytes
    let result = roundTrip value
    test <@ result = value @>

// ===========================================================================
// DateTime boundary edge cases
// ===========================================================================

/// <summary>DateTime.MinValue roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``DateTime MinValue roundtrips in DU`` () =
    let value = WithDateTime DateTime.MinValue
    let result = roundTrip value
    test <@ result = value @>

/// <summary>DateTime.MaxValue roundtrips correctly in a DU case.</summary>
[<Fact>]
let ``DateTime MaxValue roundtrips in DU`` () =
    let value = WithDateTime DateTime.MaxValue
    let result = roundTrip value
    test <@ result = value @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``IntCase roundtrips for any int via FSharpJson`` (n: int) =
    let value = IntCase n
    roundTrip value = value

[<Property>]
let ``Label roundtrips for any non-null string via FSharpJson`` (s: NonNull<string>) =
    let value = Label s.Get
    roundTrip value = value

[<Property>]
let ``Wrapped roundtrips for any int via FSharpJson`` (n: int) =
    let value = Wrapped n
    roundTrip<SingleDataDU> value = value

[<Property>]
let ``AllOptionalRecord roundtrips for any int option in field A`` (a: int option) =
    let value = { A = a; B = None; C = None; D = None }
    roundTrip value = value
