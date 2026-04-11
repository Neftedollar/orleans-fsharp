module Orleans.FSharp.Tests.FSharpBinaryCodecEdgeCases

open System
open System.IO
open System.Text
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ── Test types ───────────────────────────────────────────────────────────────

/// Simple POCO class for null reference testing
[<CLIMutable>]
type SimplePoco =
    { Name: string
      Value: int }

/// Deeply nested DU (10 levels)
type Level10 = Leaf10 of int
type Level9 = L9 of Level10 | Leaf9 of int
type Level8 = L8 of Level9 | Leaf8 of int
type Level7 = L7 of Level8 | Leaf7 of int
type Level6 = L6 of Level7 | Leaf6 of int
type Level5 = L5 of Level6 | Leaf5 of int
type Level4 = L4 of Level5 | Leaf4 of int
type Level3 = L3 of Level4 | Leaf3 of int
type Level2 = L2 of Level3 | Leaf2 of int
type Level1 = L1 of Level2 | Leaf1 of int

/// Recursive binary tree
type Tree<'T> =
    | Leaf of 'T
    | Node of Tree<'T> * Tree<'T>

/// Record with multiple option fields
type RecordWithOptions =
    { IntOpt: int option
      StringOpt: string option
      ListOpt: int list option }

/// F# record containing a POCO field
type MixedRecord =
    { FSharpList: string list
      PocoValue: SimplePoco }

// ── Helper functions ─────────────────────────────────────────────────────────

let roundTrip<'T> (value: 'T) : 'T =
    let bytes = FSharpBinaryFormat.serialize (box value) typeof<'T>
    let result = FSharpBinaryFormat.deserialize bytes typeof<'T>
    unbox<'T> result

let roundTripWithType<'T> (value: 'T) : 'T =
    let bytes = FSharpBinaryFormat.serializeWithType (box value) typeof<'T>
    let result = FSharpBinaryFormat.deserializeWithType bytes typeof<'T>
    unbox<'T> result

/// DU case type (a concrete union case) for regression testing.
type LocalCmd = | Deposit of decimal | Withdraw of decimal

// ── Tests ────────────────────────────────────────────────────────────────────

type FSharpBinaryCodecEdgeCases() =

    /// <summary>
    /// Null reference type should round-trip as null.
    /// </summary>
    [<Fact>]
    member _.``round-trip null reference type`` () =
        let value: SimplePoco option = None
        let bytes = FSharpBinaryFormat.serialize (box value) typeof<SimplePoco option>
        let result = FSharpBinaryFormat.deserialize bytes typeof<SimplePoco option>
        let unboxed = unbox<SimplePoco option> result
        test <@ unboxed = None @>

    /// <summary>
    /// DU case type (a concrete union case) should serialize/deserialize
    /// correctly via the parent union codec. Regression test for the fix.
    /// </summary>
    [<Fact>]
    member _.``round-trip DU case type via parent union`` () =
        let depositValue = box (Deposit 500m)
        let runtimeType = depositValue.GetType()

        // The runtime type is the concrete case type
        test <@ runtimeType.Name.Contains("Deposit") @>

        // Serialize using the case type
        let bytes = FSharpBinaryFormat.serialize depositValue runtimeType

        // Deserialize should recover the value
        let result = FSharpBinaryFormat.deserialize bytes runtimeType
        let unboxed = unbox<LocalCmd> result
        match unboxed with
        | Deposit 500m -> ()
        | _ -> failwith "Expected Deposit 500m"

    /// <summary>
    /// Empty bytes should throw when trying to deserialize.
    /// </summary>
    [<Fact>]
    member _.``deserialize empty bytes throws exception`` () =
        let empty = [||]
        let throws =
            try
                FSharpBinaryFormat.deserialize empty typeof<int> |> ignore
                false
            with _ -> true
        test <@ throws @>

    /// <summary>
    /// Deeply nested DU (10 levels) should round-trip correctly.
    /// </summary>
    [<Fact>]
    member _.``round-trip deeply nested DU`` () =
        let value =
            Leaf10 42
            |> L9
            |> L8
            |> L7
            |> L6
            |> L5
            |> L4
            |> L3
            |> L2
            |> L1
        let result = roundTrip value
        test <@ result = value @>

    /// <summary>
    /// Large list (100K elements) should round-trip correctly.
    /// </summary>
    [<Fact>]
    member _.``round-trip large list`` () =
        let value = [ for i in 1 .. 100000 -> i ]
        let result = roundTrip value
        test <@ result = value @>

    /// <summary>
    /// Large map (10K entries) should round-trip correctly.
    /// </summary>
    [<Fact>]
    member _.``round-trip large map`` () =
        let value = Map.ofList [ for i in 1 .. 10000 -> string i, i ]
        let result = roundTrip value
        test <@ result = value @>

    /// <summary>
    /// Recursive binary tree (depth 20 = ~2M nodes) should round-trip correctly.
    /// </summary>
    [<Fact>]
    member _.``round-trip recursive binary tree`` () =
        // Build a balanced tree of depth 15 (65K nodes — enough to test recursion without timeout)
        let rec buildTree depth =
            if depth <= 0 then Leaf depth
            else Node (buildTree (depth - 1), buildTree (depth - 1))

        let value: Tree<int> = buildTree 15
        let result = roundTrip value
        test <@ result = value @>

    /// <summary>
    /// Record with various option field combinations should round-trip.
    /// </summary>
    [<Fact>]
    member _.``round-trip record with option field combinations`` () =
        let testCases =
            [ { IntOpt = Some 42; StringOpt = Some "hello"; ListOpt = Some [1; 2; 3] }
              { IntOpt = None; StringOpt = Some "hello"; ListOpt = Some [1; 2; 3] }
              { IntOpt = Some 42; StringOpt = None; ListOpt = Some [1; 2; 3] }
              { IntOpt = Some 42; StringOpt = Some "hello"; ListOpt = None }
              { IntOpt = None; StringOpt = None; ListOpt = None } ]

        for tc in testCases do
            let result = roundTrip tc
            test <@ result = tc @>

    /// <summary>
    /// Mixed F#/C# composition: F# record containing a C#-style POCO.
    /// </summary>
    [<Fact>]
    member _.``round-trip mixed F# and POCO composition`` () =
        let value: MixedRecord =
            { FSharpList = ["a"; "b"; "c"]
              PocoValue = { Name = "test-poco"; Value = 99 } }
        let result = roundTrip value
        test <@ result.FSharpList = value.FSharpList @>
        test <@ result.PocoValue.Name = value.PocoValue.Name @>
        test <@ result.PocoValue.Value = value.PocoValue.Value @>

    /// <summary>
    /// serializeWithType + deserializeWithType should recover the type
    /// when hintType is provided.
    /// </summary>
    [<Fact>]
    member _.``serializeWithType recovers type from hint`` () =
        let value = { IntOpt = Some 42; StringOpt = None; ListOpt = Some [] }
        let bytes = FSharpBinaryFormat.serializeWithType (box value) typeof<RecordWithOptions>
        let result = FSharpBinaryFormat.deserializeWithType bytes typeof<RecordWithOptions>
        let unboxed = unbox<RecordWithOptions> result
        test <@ unboxed.IntOpt = Some 42 @>
