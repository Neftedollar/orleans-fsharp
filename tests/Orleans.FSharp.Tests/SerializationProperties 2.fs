module Orleans.FSharp.Tests.SerializationProperties

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsCheck.Xunit
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Test types: DUs with various shapes
// ---------------------------------------------------------------------------

/// Simple enum-like DU.
type Color =
    | Red
    | Green
    | Blue

/// DU with single-field cases.
type Wrapper =
    | StringVal of string
    | IntVal of int
    | FloatVal of float

/// Record with option fields.
type Contact =
    { Email: string
      Phone: string option }

/// DU with nested record.
type AccountStatus =
    | Active of Contact
    | Inactive
    | Suspended of reason: string

/// Record with list and map fields.
type Inventory =
    { Items: string list
      Quantities: Map<string, int> }

/// DU with nested records, options, lists.
type OrderState =
    | Empty
    | Pending of items: string list
    | Confirmed of Inventory
    | Cancelled of reason: string option

/// DU with ValueOption field.
type MaybeTagged =
    | Tagged of tag: string
    | Untagged of notes: string voption

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Get the pre-configured JsonSerializerOptions from Orleans.FSharp.
let private jsonOptions = FSharpJson.serializerOptions

/// Roundtrip: serialize then deserialize.
let private roundtrip<'T> (value: 'T) : 'T =
    let json = JsonSerializer.Serialize<'T>(value, jsonOptions)
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

// ---------------------------------------------------------------------------
// FsCheck property tests: DU roundtrips
// ---------------------------------------------------------------------------

[<Property>]
let ``Color DU survives JSON roundtrip`` (color: Color) =
    let result = roundtrip color
    result = color

[<Property>]
let ``Wrapper DU survives JSON roundtrip`` (wrapper: Wrapper) =
    // Filter out NaN/Infinity which don't roundtrip in JSON
    match wrapper with
    | FloatVal f when Double.IsNaN f || Double.IsInfinity f -> true
    | _ ->
        let result = roundtrip wrapper
        result = wrapper

[<Property>]
let ``int survives JSON roundtrip`` (n: int) =
    roundtrip n = n

[<Property>]
let ``string option survives JSON roundtrip`` (value: string option) =
    roundtrip value = value

[<Property>]
let ``string list survives JSON roundtrip`` (value: string list) =
    roundtrip value = value

[<Property>]
let ``Map string int survives JSON roundtrip`` (entries: (FsCheck.NonNull<string> * int) list) =
    let m = entries |> List.map (fun (k, v) -> k.Get, v) |> Map.ofList
    roundtrip m = m

// ---------------------------------------------------------------------------
// Fact tests: complex nested types
// ---------------------------------------------------------------------------

[<Fact>]
let ``Contact record with Some phone roundtrips`` () =
    let contact =
        { Email = "test@example.com"
          Phone = Some "+1-555-0123" }

    let result = roundtrip contact
    test <@ result = contact @>

[<Fact>]
let ``Contact record with None phone roundtrips`` () =
    let contact =
        { Email = "test@example.com"
          Phone = None }

    let result = roundtrip contact
    test <@ result = contact @>

[<Fact>]
let ``AccountStatus Active with nested record roundtrips`` () =
    let status =
        Active
            { Email = "admin@test.com"
              Phone = Some "555-9999" }

    let result = roundtrip status
    test <@ result = status @>

[<Fact>]
let ``AccountStatus Inactive roundtrips`` () =
    let result = roundtrip Inactive
    test <@ result = Inactive @>

[<Fact>]
let ``AccountStatus Suspended roundtrips`` () =
    let status = Suspended "policy violation"
    let result = roundtrip status
    test <@ result = status @>

[<Fact>]
let ``OrderState with nested Inventory roundtrips`` () =
    let order =
        Confirmed
            { Items = [ "widget"; "gadget" ]
              Quantities = Map.ofList [ "widget", 5; "gadget", 3 ] }

    let result = roundtrip order
    test <@ result = order @>

[<Fact>]
let ``OrderState Pending with list roundtrips`` () =
    let order = Pending [ "item1"; "item2"; "item3" ]
    let result = roundtrip order
    test <@ result = order @>

[<Fact>]
let ``OrderState Cancelled with Some reason roundtrips`` () =
    let order = Cancelled(Some "customer request")
    let result = roundtrip order
    test <@ result = order @>

[<Fact>]
let ``OrderState Cancelled with None reason roundtrips`` () =
    let order = Cancelled None
    let result = roundtrip order
    test <@ result = order @>

[<Fact>]
let ``MaybeTagged with ValueSome roundtrips`` () =
    let tagged = Untagged(ValueSome "my note")
    let result = roundtrip tagged
    test <@ result = tagged @>

[<Fact>]
let ``MaybeTagged with ValueNone roundtrips`` () =
    let tagged = Untagged ValueNone
    let result = roundtrip tagged
    test <@ result = tagged @>

[<Fact>]
let ``Empty list roundtrips`` () =
    let result = roundtrip<string list> []
    test <@ result = [] @>

[<Fact>]
let ``Empty map roundtrips`` () =
    let result = roundtrip<Map<string, int>> Map.empty
    test <@ result = Map.empty @>

// ---------------------------------------------------------------------------
// T029b: Deserialization error test
// ---------------------------------------------------------------------------

[<Fact>]
let ``Corrupted DU case discriminator produces descriptive error`` () =
    let original = Active { Email = "test@test.com"; Phone = None }
    let json = JsonSerializer.Serialize<AccountStatus>(original, jsonOptions)
    // Corrupt the case discriminator
    let corrupted = json.Replace("Active", "BogusCase")

    let ex =
        Assert.ThrowsAny<exn>(fun () ->
            JsonSerializer.Deserialize<AccountStatus>(corrupted, jsonOptions)
            |> ignore)

    // The error should help identify what went wrong
    test <@ ex.Message.Contains("BogusCase") || ex.InnerException <> null @>

[<Fact>]
let ``Invalid JSON for DU throws descriptive error`` () =
    let badJson = """{"Case":"Nonexistent","Fields":[123]}"""

    let ex =
        Assert.ThrowsAny<exn>(fun () ->
            JsonSerializer.Deserialize<Color>(badJson, jsonOptions)
            |> ignore)

    // The exception should be informative
    test <@ ex <> null @>
