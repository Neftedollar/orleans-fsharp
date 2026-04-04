module Orleans.FSharp.Tests.SchemaEvolutionTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// ===========================================================================
// Schema evolution test types
//
// These types simulate what happens when DU/record schemas change over time.
// We test by manually constructing JSON that represents "old" data shapes
// and deserializing into "new" types.
// ===========================================================================

/// <summary>JSON serializer options pre-configured for F# types.</summary>
let private jsonOptions = FSharpJson.serializerOptions

/// <summary>Serialize a value to JSON string.</summary>
let private toJson<'T> (value: 'T) : string =
    JsonSerializer.Serialize<'T>(value, jsonOptions)

/// <summary>Deserialize a JSON string to a value.</summary>
let private fromJson<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

// ---------------------------------------------------------------------------
// Scenario 1: DU with cases added
// ---------------------------------------------------------------------------

/// <summary>Original DU with two cases (simulates "old" schema).</summary>
type StatusV1 =
    | Active
    | Inactive

/// <summary>Extended DU with a third case added (simulates "new" schema).</summary>
type StatusV2 =
    | Active
    | Inactive
    | Suspended

/// <summary>Adding a new case to a DU: old serialized Active still deserializes.</summary>
[<Fact>]
let ``Added DU case - old Active data still deserializes`` () =
    let json = toJson<StatusV1> StatusV1.Active
    let result = fromJson<StatusV2> json
    test <@ result = StatusV2.Active @>

/// <summary>Adding a new case to a DU: old serialized Inactive still deserializes.</summary>
[<Fact>]
let ``Added DU case - old Inactive data still deserializes`` () =
    let json = toJson<StatusV1> StatusV1.Inactive
    let result = fromJson<StatusV2> json
    test <@ result = StatusV2.Inactive @>

/// <summary>New case Suspended serializes and deserializes correctly on V2.</summary>
[<Fact>]
let ``Added DU case - new Suspended case roundtrips on V2`` () =
    let json = toJson<StatusV2> StatusV2.Suspended
    let result = fromJson<StatusV2> json
    test <@ result = StatusV2.Suspended @>

// ---------------------------------------------------------------------------
// Scenario 2: DU with case carrying data added
// ---------------------------------------------------------------------------

/// <summary>Original DU with simple cases (simulates "old" schema).</summary>
type CommandV1 =
    | Start
    | Stop

/// <summary>Extended DU with data-carrying case added (simulates "new" schema).</summary>
type CommandV2 =
    | Start
    | Stop
    | Pause of duration: int

/// <summary>Old Start data deserializes into the extended CommandV2 type.</summary>
[<Fact>]
let ``Added data case - old Start deserializes to V2`` () =
    let json = toJson<CommandV1> CommandV1.Start
    let result = fromJson<CommandV2> json
    test <@ result = CommandV2.Start @>

/// <summary>New Pause case with data roundtrips on V2.</summary>
[<Fact>]
let ``Added data case - new Pause case roundtrips on V2`` () =
    let json = toJson<CommandV2> (CommandV2.Pause 30)
    let result = fromJson<CommandV2> json
    test <@ result = CommandV2.Pause 30 @>

// ---------------------------------------------------------------------------
// Scenario 3: Removed DU case
// ---------------------------------------------------------------------------

/// <summary>Original DU with three cases including one that will be removed.</summary>
type ModeV1 =
    | ReadOnly
    | ReadWrite
    | Admin

/// <summary>Reduced DU after removing Admin case (simulates "new" schema).</summary>
type ModeV2 =
    | ReadOnly
    | ReadWrite

/// <summary>Old ReadOnly data still deserializes after case removal.</summary>
[<Fact>]
let ``Removed DU case - old ReadOnly still deserializes`` () =
    let json = toJson<ModeV1> ModeV1.ReadOnly
    let result = fromJson<ModeV2> json
    test <@ result = ModeV2.ReadOnly @>

/// <summary>Old data with removed case (Admin) throws on deserialization.</summary>
[<Fact>]
let ``Removed DU case - old Admin data throws on V2 deserialization`` () =
    let json = toJson<ModeV1> ModeV1.Admin
    Assert.ThrowsAny<exn>(fun () ->
        fromJson<ModeV2> json |> ignore)
    |> ignore

// ---------------------------------------------------------------------------
// Scenario 4: Record field added with default
// ---------------------------------------------------------------------------

/// <summary>Original record with two fields (simulates "old" schema).</summary>
type PersonV1 =
    { Name: string
      Age: int }

/// <summary>Extended record with optional field added (simulates "new" schema).</summary>
type PersonV2 =
    { Name: string
      Age: int
      Email: string option }

/// <summary>
/// FSharp.SystemTextJson is strict: missing record fields throw even if the F# type is optional.
/// Old data missing the new Email field causes a deserialization error.
/// </summary>
[<Fact>]
let ``Added optional record field - old data throws due to strict missing field`` () =
    let json = toJson<PersonV1> { Name = "Alice"; Age = 30 }
    Assert.ThrowsAny<exn>(fun () ->
        fromJson<PersonV2> json |> ignore)
    |> ignore

/// <summary>New record with all fields populated roundtrips correctly.</summary>
[<Fact>]
let ``Added optional record field - new data roundtrips`` () =
    let value = { PersonV2.Name = "Bob"; Age = 25; Email = Some "bob@test.com" }
    let json = toJson value
    let result = fromJson<PersonV2> json
    test <@ result = value @>

// ---------------------------------------------------------------------------
// Scenario 5: Record field type change
// ---------------------------------------------------------------------------

/// <summary>Record with int field (simulates "old" schema).</summary>
type ConfigV1 =
    { Setting: int }

/// <summary>Record with string field (simulates "new" schema where type changed).</summary>
type ConfigV2 =
    { Setting: string }

/// <summary>Changing field type from int to string causes deserialization error.</summary>
[<Fact>]
let ``Changed field type - int to string throws on deserialization`` () =
    let json = toJson<ConfigV1> { ConfigV1.Setting = 42 }
    // JSON "42" (number) cannot be deserialized to string
    Assert.ThrowsAny<exn>(fun () ->
        fromJson<ConfigV2> json |> ignore)
    |> ignore

// ---------------------------------------------------------------------------
// Scenario 6: Renamed DU case
// ---------------------------------------------------------------------------

/// <summary>Original DU with old case name (simulates "old" schema).</summary>
type ColorV1 =
    | Red
    | Green

/// <summary>DU with renamed case (Blue instead of Green) (simulates "new" schema).</summary>
type ColorV2 =
    | Red
    | Blue

/// <summary>Shared case name (Red) still deserializes after rename.</summary>
[<Fact>]
let ``Renamed DU case - shared case Red still deserializes`` () =
    let json = toJson<ColorV1> ColorV1.Red
    let result = fromJson<ColorV2> json
    test <@ result = ColorV2.Red @>

/// <summary>Old case name (Green) is not present in new type, throws on deserialize.</summary>
[<Fact>]
let ``Renamed DU case - old Green not recognized as Blue`` () =
    let json = toJson<ColorV1> ColorV1.Green
    Assert.ThrowsAny<exn>(fun () ->
        fromJson<ColorV2> json |> ignore)
    |> ignore

// ---------------------------------------------------------------------------
// Scenario 7: DU case field added
// ---------------------------------------------------------------------------

/// <summary>DU with single-field case (simulates "old" schema).</summary>
type EventV1 =
    | Created of name: string
    | Deleted

/// <summary>DU with additional field in case (simulates "new" schema).</summary>
type EventV2 =
    | Created of name: string * timestamp: string
    | Deleted

/// <summary>Old Created with single field cannot deserialize to new two-field version.</summary>
[<Fact>]
let ``Added case field - old single-field Created fails on V2 deserialization`` () =
    let json = toJson<EventV1> (EventV1.Created "test")
    // The JSON structure has one field but V2 expects two named fields
    Assert.ThrowsAny<exn>(fun () ->
        fromJson<EventV2> json |> ignore)
    |> ignore

/// <summary>Shared fieldless case (Deleted) still deserializes after field changes.</summary>
[<Fact>]
let ``Added case field - fieldless Deleted still deserializes`` () =
    let json = toJson<EventV1> EventV1.Deleted
    let result = fromJson<EventV2> json
    test <@ result = EventV2.Deleted @>

// ---------------------------------------------------------------------------
// Scenario 8: Record with non-optional field added
// ---------------------------------------------------------------------------

/// <summary>Original record (simulates "old" schema).</summary>
type ItemV1 =
    { ItemName: string }

/// <summary>Extended record with required field added (simulates "new" schema).</summary>
type ItemV2 =
    { ItemName: string
      Quantity: int }

/// <summary>
/// FSharp.SystemTextJson is strict: missing record fields throw.
/// Old data missing the new Quantity field causes a deserialization error.
/// </summary>
[<Fact>]
let ``Added required record field - old data throws due to strict missing field`` () =
    let json = toJson<ItemV1> { ItemName = "widget" }
    Assert.ThrowsAny<exn>(fun () ->
        fromJson<ItemV2> json |> ignore)
    |> ignore

// ---------------------------------------------------------------------------
// Scenario 9: DU with nested record evolution
// ---------------------------------------------------------------------------

/// <summary>Nested record V1.</summary>
type AddressV1 =
    { Street: string
      City: string }

/// <summary>Nested record V2 with optional field added.</summary>
type AddressV2 =
    { Street: string
      City: string
      Zip: string option }

/// <summary>DU using nested record V1 (simulates "old" schema).</summary>
type CustomerV1 =
    | WithAddress of AddressV1
    | NoAddress

/// <summary>DU using nested record V2 (simulates "new" schema).</summary>
type CustomerV2 =
    | WithAddress of AddressV2
    | NoAddress

/// <summary>
/// FSharp.SystemTextJson is strict: missing fields in nested records also throw.
/// Old address data missing Zip causes deserialization error even though Zip is option.
/// </summary>
[<Fact>]
let ``Nested record evolution - old address throws due to strict missing Zip field`` () =
    let json = toJson<CustomerV1> (CustomerV1.WithAddress { Street = "123 Main"; City = "NYC" })
    Assert.ThrowsAny<exn>(fun () ->
        fromJson<CustomerV2> json |> ignore)
    |> ignore

/// <summary>Fieldless case shared between versions still deserializes.</summary>
[<Fact>]
let ``Nested record evolution - NoAddress shared case works`` () =
    let json = toJson<CustomerV1> CustomerV1.NoAddress
    let result = fromJson<CustomerV2> json
    test <@ result = CustomerV2.NoAddress @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``StatusV2 roundtrips through JSON for any generated case`` (status: StatusV2) =
    let json = toJson status
    let result = fromJson<StatusV2> json
    result = status

[<Property>]
let ``CommandV2 roundtrips through JSON for any generated case`` (cmd: CommandV2) =
    let json = toJson cmd
    let result = fromJson<CommandV2> json
    result = cmd

[<Property>]
let ``ModeV2 roundtrips through JSON for any generated case`` (mode: ModeV2) =
    let json = toJson mode
    let result = fromJson<ModeV2> json
    result = mode

[<Property>]
let ``ColorV2 roundtrips through JSON for any generated case`` (color: ColorV2) =
    let json = toJson color
    let result = fromJson<ColorV2> json
    result = color

[<Property>]
let ``PersonV2 with all fields roundtrips through JSON`` (name: NonNull<string>) (age: int) (emailOpt: Option<NonNull<string>>) =
    let person =
        { PersonV2.Name = name.Get
          Age = age
          Email = emailOpt |> Option.map (fun e -> e.Get) }
    let json = toJson person
    let result = fromJson<PersonV2> json
    result = person

[<Property>]
let ``JSON serialization is deterministic - same value always produces same JSON`` (status: StatusV2) =
    let json1 = toJson status
    let json2 = toJson status
    json1 = json2

[<Property>]
let ``StatusV1 values always deserialize successfully as StatusV2 - both cases are compatible`` (status: StatusV1) =
    // All StatusV1 cases (Active, Inactive) exist in StatusV2, so deserialization must not throw
    let json = toJson status
    let recovered =
        try Some (fromJson<StatusV2> json)
        with _ -> None
    recovered.IsSome

[<Property>]
let ``EventV2 Deleted case roundtrips for any generated EventV2 Deleted`` () =
    // Deleted is a fieldless case shared between EventV1 and EventV2 — must always roundtrip
    let json = toJson EventV2.Deleted
    let result = fromJson<EventV2> json
    result = EventV2.Deleted

[<Property>]
let ``EventV2 Created roundtrips with any name and timestamp`` (name: NonNull<string>) (ts: NonNull<string>) =
    let evt = EventV2.Created(name.Get, ts.Get)
    let json = toJson evt
    let result = fromJson<EventV2> json
    result = evt
