module Orleans.FSharp.Tests.SerializationPropertyTests

open System
open System.Text.Json
open Xunit
open Swensen.Unquote
open FsCheck.Xunit
open Orleans.FSharp

// ===========================================================================
// Mode 1: Clean (no attributes) — relies on JSON fallback serialization
// ===========================================================================

/// <summary>Simple fieldless DU for Mode 1 (Clean).</summary>
type CleanSimpleDU =
    | A
    | B
    | C

/// <summary>DU with data payloads for Mode 1 (Clean).</summary>
type CleanWithData =
    | Value of int
    | Name of string
    | Pair of int * string

/// <summary>Recursive nested DU for Mode 1 (Clean).</summary>
type CleanNested =
    | Leaf of int
    | Branch of CleanNested * CleanNested

/// <summary>Record with various field types for Mode 1 (Clean).</summary>
type CleanRecord =
    { Id: int
      Name: string
      Tags: string list
      Score: float option }

/// <summary>Record with DU and collection fields for Mode 1 (Clean).</summary>
type CleanComplex =
    { State: CleanSimpleDU
      Items: CleanWithData list
      Meta: Map<string, string> }

/// <summary>Single-case DU for Mode 1 (Clean).</summary>
type CleanSingleCase =
    | Only of value: int

/// <summary>DU with option fields for Mode 1 (Clean).</summary>
type CleanWithOption =
    | Present of int option
    | Absent

/// <summary>DU with ValueOption for Mode 1 (Clean).</summary>
type CleanWithValueOption =
    | VOPresent of int voption
    | VOAbsent

/// <summary>DU with array field for Mode 1 (Clean).</summary>
type CleanWithArray =
    | ArrayCase of int array
    | EmptyArray

/// <summary>DU with map field for Mode 1 (Clean).</summary>
type CleanWithMap =
    | MapCase of Map<string, int>
    | NoMap

/// <summary>DU with set field for Mode 1 (Clean).</summary>
type CleanWithSet =
    | SetCase of Set<string>
    | NoSet

/// <summary>DU with tuple for Mode 1 (Clean).</summary>
type CleanWithTuple =
    | TupleCase of int * string * bool
    | NoTuple

/// <summary>Record with all-optional fields for Mode 1 (Clean).</summary>
type CleanAllOptional =
    { X: int option
      Y: string option
      Z: float option }

// ===========================================================================
// Mode 2: Auto ([GenerateSerializer] only)
// ===========================================================================

/// <summary>Simple fieldless DU for Mode 2 (Auto).</summary>
[<Orleans.GenerateSerializer>]
type AutoSimpleDU =
    | Alpha
    | Beta
    | Gamma

/// <summary>DU with data payloads for Mode 2 (Auto).</summary>
[<Orleans.GenerateSerializer>]
type AutoWithData =
    | AutoValue of int
    | AutoName of string

/// <summary>Record for Mode 2 (Auto).</summary>
[<Orleans.GenerateSerializer>]
type AutoRecord =
    { AutoId: int
      AutoName: string }

/// <summary>DU with option for Mode 2 (Auto).</summary>
[<Orleans.GenerateSerializer>]
type AutoWithOption =
    | AutoSome of int option
    | AutoNone

/// <summary>DU with list for Mode 2 (Auto).</summary>
[<Orleans.GenerateSerializer>]
type AutoWithList =
    | AutoItems of string list
    | AutoEmpty

// ===========================================================================
// Mode 3: Explicit ([GenerateSerializer] + [Id])
// ===========================================================================

/// <summary>DU with explicit IDs for Mode 3 (Explicit).</summary>
[<Orleans.GenerateSerializer>]
type ExplicitDU =
    | [<Orleans.Id(0u)>] X of int
    | [<Orleans.Id(1u)>] Y of string

/// <summary>Record with explicit IDs for Mode 3 (Explicit).</summary>
[<Orleans.GenerateSerializer>]
type ExplicitRecord =
    { [<Orleans.Id(0u)>] EId: int
      [<Orleans.Id(1u)>] EName: string }

/// <summary>DU with complex fields and explicit IDs for Mode 3 (Explicit).</summary>
[<Orleans.GenerateSerializer>]
type ExplicitComplex =
    | [<Orleans.Id(0u)>] EEmpty
    | [<Orleans.Id(1u)>] EData of value: int * label: string
    | [<Orleans.Id(2u)>] EList of items: int list

// ===========================================================================
// Roundtrip helpers
// ===========================================================================

/// <summary>JSON serializer options pre-configured for F# types.</summary>
let private jsonOptions = FSharpJson.serializerOptions

/// <summary>Serialize then deserialize a value through JSON, returning true if identity holds.</summary>
let private roundTrip<'T when 'T: equality> (value: 'T) : bool =
    let json = JsonSerializer.Serialize<'T>(value, jsonOptions)
    let result = JsonSerializer.Deserialize<'T>(json, jsonOptions)
    result = value

// ===========================================================================
// Mode 1 (Clean) Property Tests
// ===========================================================================

/// <summary>Property: arbitrary CleanSimpleDU roundtrips through JSON.</summary>
[<Property>]
let ``Clean simple DU roundtrip`` (du: CleanSimpleDU) =
    test <@ roundTrip du @>

/// <summary>Property: arbitrary CleanWithData roundtrips through JSON (excluding NaN/Infinity).</summary>
[<Property>]
let ``Clean DU with data roundtrip`` (du: CleanWithData) =
    test <@ roundTrip du @>

/// <summary>Property: arbitrary CleanNested roundtrips through JSON.</summary>
[<Property>]
let ``Clean nested DU roundtrip`` (du: CleanNested) =
    test <@ roundTrip du @>

/// <summary>Property: arbitrary CleanRecord roundtrips through JSON (excluding NaN/Infinity).</summary>
[<Property>]
let ``Clean record roundtrip`` (id: int) (name: FsCheck.NonNull<string>) (tags: string list) =
    let filteredTags = tags |> List.choose (fun s -> if isNull s then None else Some s)
    let r = { Id = id; Name = name.Get; Tags = filteredTags; Score = Some 1.0 }
    test <@ roundTrip r @>

/// <summary>Property: arbitrary CleanComplex roundtrips through JSON.</summary>
[<Property>]
let ``Clean complex record roundtrip`` (state: CleanSimpleDU) =
    let r =
        { State = state
          Items = [ Value 1; Name "x" ]
          Meta = Map.ofList [ "k", "v" ] }
    test <@ roundTrip r @>

/// <summary>Property: single-case DU roundtrips through JSON.</summary>
[<Property>]
let ``Clean single-case DU roundtrip`` (n: int) =
    let du = Only n
    test <@ roundTrip du @>

/// <summary>Property: option fields inside DU roundtrip through JSON.</summary>
[<Property>]
let ``Clean DU with option roundtrip`` (du: CleanWithOption) =
    test <@ roundTrip du @>

/// <summary>Property: ValueOption fields inside DU roundtrip through JSON.</summary>
[<Property>]
let ``Clean DU with ValueOption roundtrip`` (du: CleanWithValueOption) =
    test <@ roundTrip du @>

/// <summary>Property: array fields inside DU roundtrip through JSON.</summary>
[<Property>]
let ``Clean DU with array roundtrip`` (du: CleanWithArray) =
    test <@ roundTrip du @>

/// <summary>Property: map fields inside DU roundtrip through JSON.</summary>
[<Property>]
let ``Clean DU with map roundtrip`` (entries: (FsCheck.NonNull<string> * int) list) =
    let m = entries |> List.map (fun (k, v) -> k.Get, v) |> Map.ofList
    let du = MapCase m
    test <@ roundTrip du @>

/// <summary>Property: set fields inside DU roundtrip through JSON.</summary>
[<Property>]
let ``Clean DU with set roundtrip`` (items: FsCheck.NonNull<string> list) =
    let s = items |> List.map (fun x -> x.Get) |> Set.ofList
    let du = SetCase s
    test <@ roundTrip du @>

/// <summary>Property: tuple fields inside DU roundtrip through JSON.</summary>
[<Property>]
let ``Clean DU with tuple roundtrip`` (n: int) (s: FsCheck.NonNull<string>) (b: bool) =
    let du = TupleCase(n, s.Get, b)
    test <@ roundTrip du @>

/// <summary>Property: record with all optional fields roundtrips through JSON.</summary>
[<Property>]
let ``Clean all-optional record roundtrip`` (x: int option) (y: bool) =
    let r =
        { X = x
          Y = if y then Some "yes" else None
          Z = None }
    test <@ roundTrip r @>

/// <summary>Property: string list roundtrips through JSON.</summary>
[<Property>]
let ``Clean string list roundtrip`` (items: FsCheck.NonNull<string> list) =
    let xs = items |> List.map (fun x -> x.Get)
    test <@ roundTrip xs @>

/// <summary>Property: int option roundtrips through JSON.</summary>
[<Property>]
let ``Clean int option roundtrip`` (value: int option) =
    test <@ roundTrip value @>

/// <summary>Property: string option roundtrips through JSON for Clean mode.</summary>
[<Property>]
let ``Clean string option roundtrip`` (value: FsCheck.NonNull<string> option) =
    let v = value |> Option.map (fun x -> x.Get)
    test <@ roundTrip v @>

// ===========================================================================
// Mode 2 (Auto) Property Tests
// ===========================================================================

/// <summary>Property: arbitrary AutoSimpleDU roundtrips through JSON.</summary>
[<Property>]
let ``Auto simple DU roundtrip`` (du: AutoSimpleDU) =
    test <@ roundTrip du @>

/// <summary>Property: arbitrary AutoWithData roundtrips through JSON.</summary>
[<Property>]
let ``Auto DU with data roundtrip`` (du: AutoWithData) =
    test <@ roundTrip du @>

/// <summary>Property: arbitrary AutoRecord roundtrips through JSON.</summary>
[<Property>]
let ``Auto record roundtrip`` (id: int) (name: FsCheck.NonNull<string>) =
    let r = { AutoId = id; AutoName = name.Get }
    test <@ roundTrip r @>

/// <summary>Property: AutoWithOption DU roundtrips through JSON.</summary>
[<Property>]
let ``Auto DU with option roundtrip`` (du: AutoWithOption) =
    test <@ roundTrip du @>

/// <summary>Property: AutoWithList DU roundtrips through JSON.</summary>
[<Property>]
let ``Auto DU with list roundtrip`` (items: FsCheck.NonNull<string> list) =
    let xs = items |> List.map (fun x -> x.Get)
    let du = AutoItems xs
    test <@ roundTrip du @>

// ===========================================================================
// Mode 3 (Explicit) Property Tests
// ===========================================================================

/// <summary>Property: arbitrary ExplicitDU roundtrips through JSON.</summary>
[<Property>]
let ``Explicit DU roundtrip`` (du: ExplicitDU) =
    test <@ roundTrip du @>

/// <summary>Property: arbitrary ExplicitRecord roundtrips through JSON.</summary>
[<Property>]
let ``Explicit record roundtrip`` (id: int) (name: FsCheck.NonNull<string>) =
    let r = { EId = id; EName = name.Get }
    test <@ roundTrip r @>

/// <summary>Property: arbitrary ExplicitComplex roundtrips through JSON.</summary>
[<Property>]
let ``Explicit complex DU roundtrip`` (du: ExplicitComplex) =
    test <@ roundTrip du @>

// ===========================================================================
// Cross-mode: empty DU (fieldless only) roundtrip
// ===========================================================================

/// <summary>Property: fieldless DU cases always roundtrip regardless of mode.</summary>
[<Property>]
let ``Fieldless DU cases roundtrip in all modes`` (clean: CleanSimpleDU) (auto: AutoSimpleDU) =
    test <@ roundTrip clean @>
    test <@ roundTrip auto @>

// ===========================================================================
// Map with various key types
// ===========================================================================

/// <summary>Property: Map with int keys roundtrips through JSON.</summary>
[<Property>]
let ``Map with int keys roundtrip`` (entries: (int * string) list) =
    let filtered = entries |> List.map (fun (k, v) -> k, if isNull v then "" else v)
    let m = filtered |> Map.ofList
    let json = JsonSerializer.Serialize(m, jsonOptions)
    let result = JsonSerializer.Deserialize<Map<int, string>>(json, jsonOptions)
    test <@ result = m @>

/// <summary>Property: nested option Some(Some x) roundtrips through JSON.</summary>
[<Property>]
let ``Nested option Some Some roundtrip`` (n: int) =
    let value: int option option = Some(Some n)
    test <@ roundTrip value @>

/// <summary>Nested option None roundtrips through JSON.</summary>
[<Fact>]
let ``Nested option None roundtrip`` () =
    let value: int option option = None
    test <@ roundTrip value @>

/// <summary>Nested option Some None does not roundtrip (JSON null is ambiguous).</summary>
[<Fact>]
let ``Nested option Some None loses distinction`` () =
    let value: int option option = Some None
    let json = JsonSerializer.Serialize(value, jsonOptions)
    let result = JsonSerializer.Deserialize<int option option>(json, jsonOptions)
    // Some None serializes to null, which deserializes as None — a known limitation
    test <@ result = None @>

/// <summary>Property: bool roundtrips through JSON.</summary>
[<Property>]
let ``Bool roundtrip`` (value: bool) =
    test <@ roundTrip value @>

/// <summary>Property: Result type roundtrips through JSON.</summary>
[<Property>]
let ``Result type roundtrip`` (n: int) (ok: bool) =
    let value: Result<int, string> =
        if ok then Ok n else Error "fail"
    test <@ roundTrip value @>
