module Orleans.FSharp.Tests.FSharpBinaryCodecEdgeCases

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open Orleans.Serialization
open Orleans.Serialization.WireProtocol
open Orleans.FSharp
open Orleans.FSharp.CodecProbe
open Orleans.FSharp.Tests.Facades

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

/// A unary recursive shape makes the codec depth boundary exact and easy to construct on the wire.
type GraphChain =
    | End
    | Next of GraphChain

[<AllowNullLiteral>]
type GraphNode(value: int) =
    member val Value = value with get, set
    member val Next: GraphNode = null with get, set

type SharedGraph =
    { Left: GraphNode
      Right: GraphNode }

type PocoEnvelope =
    { Label: string
      Value: PropertyPoco }

[<Struct>]
type ValuePayload = { Number: int }

/// Record with multiple option fields
type RecordWithOptions =
    { IntOpt: int option
      StringOpt: string option
      ListOpt: int list option }

/// F# record containing a POCO field
type MixedRecord =
    { FSharpList: string list
      PocoValue: SimplePoco }

type BinaryStatus =
    | Waiting = -1
    | Done = 1

[<Flags>]
type BinaryPermissions =
    | None = 0
    | Read = 1
    | Write = 2

type EnumUnion =
    | StatusChanged of BinaryStatus
    | PermissionsChanged of BinaryPermissions

type EnumPayload =
    { Status: BinaryStatus
      Permissions: BinaryPermissions
      Unknown: BinaryStatus
      History: BinaryStatus list
      Current: BinaryStatus option
      Changes: EnumUnion list
      SignedBoundary: SignedByteEnum
      UnsignedBoundary: UnsignedLongEnum }

// ── Helper functions ─────────────────────────────────────────────────────────

let roundTrip<'T> (value: 'T) : 'T =
    let bytes = FSharpBinaryFormat.serialize (box value) typeof<'T>
    let result = FSharpBinaryFormat.deserialize bytes typeof<'T>
    unbox<'T> result

let roundTripWithType<'T> (value: 'T) : 'T =
    let bytes = FSharpBinaryFormat.serializeWithType (box value) typeof<'T>
    let result = FSharpBinaryFormat.deserializeWithType bytes typeof<'T>
    unbox<'T> result

let private withOrleansSerializer action =
    let services = ServiceCollection()

    ServiceCollectionExtensions.AddSerializer(
        services,
        Action<ISerializerBuilder>(fun builder ->
            FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore)
    )
    |> ignore

    use provider = services.BuildServiceProvider()
    let serializer = provider.GetRequiredService<Serializer>()
    action serializer

let private roundTripWithOrleans<'T> (value: 'T) : 'T =
    withOrleansSerializer (fun serializer ->
        let bytes = serializer.SerializeToArray value
        serializer.Deserialize<'T> bytes)

let private roundTripWithFunctionalPayload<'T> (value: 'T) : 'T =
    withOrleansSerializer (fun serializer ->
        let codec = FunctionalPayloadCodec(serializer, serializer.SessionPool)
        let bytes = codec.Serialize value
        codec.Deserialize<'T> bytes)

let private binaryFixture name =
    Path.Combine(__SOURCE_DIRECTORY__, "Fixtures", name)
    |> File.ReadAllText
    |> Convert.FromBase64String

let private chain length =
    (End, [ 1..length ]) ||> List.fold (fun current _ -> Next current)

let private chainBody length =
    use stream = new MemoryStream()
    use writer = new BinaryWriter(stream, Encoding.UTF8, true)

    for _ in 1..length do
        writer.Write 1 // Next
        writer.Write 1 // one field

    writer.Write 0 // End
    writer.Write 0 // no fields
    writer.Flush()
    stream.ToArray()

/// DU case type (a concrete union case) for regression testing.
type LocalCmd = | Deposit of decimal | Withdraw of decimal

// ── Tests ────────────────────────────────────────────────────────────────────

type FSharpBinaryCodecEdgeCases() =

    [<Theory>]
    [<InlineData(WireType.VarInt)>]
    [<InlineData(WireType.TagDelimited)>]
    [<InlineData(WireType.Fixed32)>]
    [<InlineData(WireType.Fixed64)>]
    member _.``generalized reads reject unsupported outer wire tags`` (wireType: WireType) =
        withOrleansSerializer (fun serializer ->
            let value: SimplePoco = { Name = "wire-tag"; Value = 42 }
            let valid = serializer.SerializeToArray value
            let malformed = Array.copy valid
            // Orleans stores WireType in the high three bits of the first tag byte.
            Assert.Equal(byte WireType.LengthPrefixed, malformed.[0] &&& 0xE0uy)
            malformed.[0] <- (malformed.[0] &&& 0x1Fuy) ||| byte wireType

            let error =
                Assert.Throws<UnsupportedWireTypeException>(fun () ->
                    serializer.Deserialize<SimplePoco> malformed |> ignore)

            Assert.Contains("LengthPrefixed", error.Message)
            // A rejected field must not poison a subsequently rented serializer session.
            Assert.Equal<SimplePoco>(value, serializer.Deserialize<SimplePoco> valid))

    member private _.RunProbe(command: string, timeoutDuration: TimeSpan) =
        task {
            let testAssembly = typeof<FSharpBinaryCodecEdgeCases>.Assembly.Location
            let probeAssembly = typeof<Marker>.Assembly.Location
            let startInfo = ProcessStartInfo("dotnet")
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.ArgumentList.Add "exec"
            startInfo.ArgumentList.Add "--runtimeconfig"
            startInfo.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"))
            startInfo.ArgumentList.Add "--depsfile"
            startInfo.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"))
            startInfo.ArgumentList.Add probeAssembly
            startInfo.ArgumentList.Add command

            use childProcess = new Process()
            childProcess.StartInfo <- startInfo
            Assert.True(childProcess.Start(), $"The {command} codec probe process did not start.")

            let outputTask = childProcess.StandardOutput.ReadToEndAsync()
            let errorTask = childProcess.StandardError.ReadToEndAsync()
            use timeout = new CancellationTokenSource(timeoutDuration)

            try
                do! childProcess.WaitForExitAsync(timeout.Token)
            with :? OperationCanceledException ->
                if not childProcess.HasExited then
                    childProcess.Kill(true)

                do! childProcess.WaitForExitAsync()
                failwithf "The %s codec probe exceeded its %.0f second timeout." command timeoutDuration.TotalSeconds

            let! output = outputTask
            let! errorOutput = errorTask
            return childProcess.ExitCode, output, errorOutput
        }

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

    [<Fact>]
    member _.``recursive graph at the depth boundary round-trips`` () =
        let value = chain (FSharpBinaryFormat.MaxGraphDepth - 1)
        test <@ roundTrip value = value @>

    [<Fact>]
    member _.``recursive graph beyond the depth boundary fails while writing`` () =
        let value = chain FSharpBinaryFormat.MaxGraphDepth

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                FSharpBinaryFormat.serialize (box value) typeof<GraphChain> |> ignore)

        test <@ error.Message.Contains "exceeds the maximum supported depth" @>
        test <@ error.Message.Contains "while writing" @>

    [<Fact>]
    member _.``recursive payload beyond the depth boundary fails while reading`` () =
        let bytes = chainBody FSharpBinaryFormat.MaxGraphDepth

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                FSharpBinaryFormat.deserialize bytes typeof<GraphChain> |> ignore)

        test <@ error.Message.Contains "exceeds the maximum supported depth" @>
        test <@ error.Message.Contains "while reading" @>

    [<Fact>]
    member this.``cyclic graph rejection and recovery stay inside a bounded child process`` () =
        task {
            let! exitCode, output, errorOutput =
                this.RunProbe("cycle", TimeSpan.FromSeconds 10.0)

            test <@ exitCode = 0 @>
            test <@ output.Contains "reference cycle was detected" @>
            test <@ output.Contains "Orleans.FSharp.CodecProbe.CyclicNode" @>
            test <@ output.Contains "CYCLE_TYPE_CONFIRMED" @>
            test <@ output.Contains "POST_FAILURE_ROUNDTRIP_OK" @>
            test <@ String.IsNullOrWhiteSpace errorOutput @>
        }

    [<Fact>]
    member this.``concurrent wire type admission never exceeds its process cap`` () =
        task {
            let! exitCode, output, errorOutput =
                this.RunProbe("cache-cap", TimeSpan.FromSeconds 30.0)

            test <@ exitCode = 0 @>
            test <@ output.Contains "CACHE_CAP_OK count=512 resolved=32 rejected=32 verified=64" @>
            test <@ String.IsNullOrWhiteSpace errorOutput @>
        }

    [<Fact>]
    member this.``assembly resolver reentry cannot exceed the wire type cap`` () =
        task {
            let! exitCode, output, errorOutput =
                this.RunProbe("cache-reentry", TimeSpan.FromSeconds 30.0)

            Assert.True((exitCode = 0), output + errorOutput)
            Assert.Contains("CACHE_REENTRY_OK count=512", output)
            Assert.True(String.IsNullOrWhiteSpace errorOutput, errorOutput)
        }

    [<Fact>]
    member _.``shared acyclic references are accepted but decoded as independent values`` () =
        let shared = GraphNode(7)
        let restored = roundTrip { Left = shared; Right = shared }

        test <@ restored.Left.Value = 7 @>
        test <@ restored.Right.Value = 7 @>
        test <@ not (Object.ReferenceEquals(restored.Left, restored.Right)) @>

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


// ── Issue #33: generalized-codec correctness ────────────────────────────────

[<Fact>]
let ``Orleans serializer preserves repeated generalized values by reference`` () : unit =
    let shared = PropertyPoco(Count = 42, Name = "shared")
    let distinct = PropertyPoco(Count = 7, Name = "distinct")
    let input: obj[] = [| shared; distinct; shared; null |]

    let restored = roundTripWithOrleans input

    test <@ restored.Length = 4 @>
    test <@ obj.ReferenceEquals(restored.[0], restored.[2]) @>
    test <@ not (obj.ReferenceEquals(restored.[0], restored.[1])) @>
    test <@ isNull restored.[3] @>

[<Fact>]
let ``generated Orleans wrapper mixes native fields and repeated generalized values`` () : unit =
    let shared = PropertyPoco(Count = 42, Name = "shared")

    let input =
        NativeMixedEnvelope(
            Native = 17,
            First = shared,
            Second = shared,
            Missing = null)

    for restored in [ roundTripWithOrleans input; roundTripWithFunctionalPayload input ] do
        test <@ restored.Native = 17 @>
        test <@ restored.First.Count = 42 @>
        test <@ obj.ReferenceEquals(restored.First, restored.Second) @>
        test <@ isNull restored.Missing @>

[<Fact>]
let ``generalized struct fields keep repeated FSharp record references aligned`` () : unit =
    let shared = { IntOpt = Some 42; StringOpt = Some "shared"; ListOpt = None }
    let distinct = { IntOpt = Some 7; StringOpt = None; ListOpt = Some [] }
    let input: obj[] =
        [| box 17; box shared; box { Number = 23 }; box distinct
           null; box { Number = 29 }; box shared; box distinct |]

    for restored in [ roundTripWithOrleans input; roundTripWithFunctionalPayload input ] do
        test <@ restored.[0] :?> int = 17 @>
        test <@ restored.[1] :?> RecordWithOptions = shared @>
        test <@ restored.[2] :?> ValuePayload = { Number = 23 } @>
        test <@ restored.[3] :?> RecordWithOptions = distinct @>
        test <@ isNull restored.[4] @>
        test <@ restored.[5] :?> ValuePayload = { Number = 29 } @>
        test <@ obj.ReferenceEquals(restored.[1], restored.[6]) @>
        test <@ obj.ReferenceEquals(restored.[3], restored.[7]) @>
        test <@ not (obj.ReferenceEquals(restored.[1], restored.[3])) @>

[<Fact>]
let ``property POCO keeps the historical binary body and envelope`` () : unit =
    let value = PropertyPoco(Count = 42, Name = "baseline")
    let expectedBody = binaryFixture "fsharp-binary-property-poco-body-v5-preview.base64"
    let expectedEnvelope = binaryFixture "fsharp-binary-property-poco-envelope-v5-preview.base64"

    let actualBody = FSharpBinaryFormat.serialize (box value) typeof<PropertyPoco>
    let actualEnvelope = FSharpBinaryFormat.serializeWithType (box value) typeof<PropertyPoco>
    let restoredBody = FSharpBinaryFormat.deserialize expectedBody typeof<PropertyPoco> :?> PropertyPoco
    let restoredEnvelope =
        FSharpBinaryFormat.deserializeWithType expectedEnvelope typeof<PropertyPoco> :?> PropertyPoco

    test <@ actualBody = expectedBody @>
    test <@ actualEnvelope = expectedEnvelope @>
    test <@ restoredBody.Count = 42 && restoredBody.Name = "baseline" @>
    test <@ restoredEnvelope.Count = 42 && restoredEnvelope.Name = "baseline" @>

[<Fact>]
let ``field-only CLR POCO round-trips every field`` () : unit =
    let value = FieldsOnlyPoco(Count = 42, Name = "fields")
    let restored = roundTrip value

    test <@ restored.Count = 42 @>
    test <@ restored.Name = "fields" @>

[<Fact>]
let ``historical field-only POCO body remains readable as its old default value`` () : unit =
    use stream = new MemoryStream()
    use writer = new BinaryWriter(stream, Encoding.UTF8, true)
    writer.Write 1uy // present
    writer.Write 0 // the old writer emitted zero properties and therefore no field data
    writer.Flush()

    let restored =
        FSharpBinaryFormat.deserialize (stream.ToArray()) typeof<FieldsOnlyPoco>
        :?> FieldsOnlyPoco

    test <@ restored.Count = 0 @>
    test <@ isNull restored.Name @>

[<Fact>]
let ``CLR POCO nested in an FSharp record crosses the production Orleans serializer`` () : unit =
    let input =
        { Label = "nested"
          Value = PropertyPoco(Count = 23, Name = "poco") }

    let restoredDirect = roundTrip input
    let restoredThroughOrleans = roundTripWithOrleans input

    for restored in [ restoredDirect; restoredThroughOrleans ] do
        test <@ restored.Label = "nested" @>
        test <@ restored.Value.Count = 23 @>
        test <@ restored.Value.Name = "poco" @>

[<Fact>]
let ``POCO properties are matched to their own storage instead of field position`` () : unit =
    let value = ReorderedBackingFieldsPoco(First = 11, Second = 22)
    let restored = roundTrip value

    test <@ restored.First = 11 @>
    test <@ restored.Second = 22 @>

[<Fact>]
let ``inherited POCO properties round-trip`` () : unit =
    let value = InheritedPoco(BaseValue = 3, DerivedValue = 9)
    let restored = roundTrip value

    test <@ restored.BaseValue = 3 @>
    test <@ restored.DerivedValue = 9 @>

[<Fact>]
let ``inherited read-only auto properties use their own backing fields`` () : unit =
    let value = InheritedReadOnlyPoco()

    for restored in [ roundTrip value; roundTripWithOrleans value ] do
        test <@ restored.Count = 3 @>
        test <@ restored.Name = "readonly" @>

[<Fact>]
let ``a hidden computed property cannot borrow a base property backing field`` () : unit =
    let value = HiddenComputedPoco()
    test <@ value.Count = 6 @>

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FSharpBinaryFormat.serialize (box value) typeof<HiddenComputedPoco> |> ignore)

    test <@ error.Message.Contains "Count" @>
    test <@ error.Message.Contains "cannot be reconstructed" @>

[<Fact>]
let ``ambiguous computed POCO fails instead of losing state`` () : unit =
    let value = ComputedPropertyPoco(Value = 21)

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FSharpBinaryFormat.serialize (box value) typeof<ComputedPropertyPoco> |> ignore)

    test <@ error.Message.Contains "ambiguous shape is unsupported" @>
    test <@ error.Message.Contains "ComputedPropertyPoco" @>

type EnumWidthPayload =
    { Signed8: SignedByteEnum[]
      Unsigned8: UnsignedByteEnum[]
      Signed16: SignedShortEnum[]
      Unsigned16: UnsignedShortEnum[]
      Signed32: SignedIntEnum[]
      Unsigned32: UnsignedIntEnum[]
      Signed64: SignedLongEnum[]
      Unsigned64: UnsignedLongEnum[] }

let private unnamedEnum<'T> (underlyingValue: obj) : 'T =
    let value = Enum.ToObject(typeof<'T>, underlyingValue) |> unbox<'T>
    Assert.False(Enum.IsDefined(typeof<'T>, value), $"{typeof<'T>.FullName} fixture must stay unnamed.")
    value

[<Fact>]
let ``all enum underlying widths preserve minimum maximum and unknown values`` () : unit =
    let value =
        { Signed8 =
            [| SignedByteEnum.Minimum
               unnamedEnum<SignedByteEnum> (box 37y)
               SignedByteEnum.Maximum |]
          Unsigned8 =
            [| UnsignedByteEnum.Minimum
               unnamedEnum<UnsignedByteEnum> (box 37uy)
               UnsignedByteEnum.Maximum |]
          Signed16 =
            [| SignedShortEnum.Minimum
               unnamedEnum<SignedShortEnum> (box 12345s)
               SignedShortEnum.Maximum |]
          Unsigned16 =
            [| UnsignedShortEnum.Minimum
               unnamedEnum<UnsignedShortEnum> (box 54321us)
               UnsignedShortEnum.Maximum |]
          Signed32 =
            [| SignedIntEnum.Minimum
               unnamedEnum<SignedIntEnum> (box 123456789)
               SignedIntEnum.Maximum |]
          Unsigned32 =
            [| UnsignedIntEnum.Minimum
               unnamedEnum<UnsignedIntEnum> (box 3456789012u)
               UnsignedIntEnum.Maximum |]
          Signed64 =
            [| SignedLongEnum.Minimum
               unnamedEnum<SignedLongEnum> (box 1234567890123456789L)
               SignedLongEnum.Maximum |]
          Unsigned64 =
            [| UnsignedLongEnum.Minimum
               unnamedEnum<UnsignedLongEnum> (box 12345678901234567890UL)
               UnsignedLongEnum.Maximum |] }

    for restored in [ roundTrip value; roundTripWithOrleans value ] do
        test <@ restored = value @>

[<Fact>]
let ``nested enums preserve exact underlying values`` () : unit =
    let unknown = enum<BinaryStatus> 123
    let flags = enum<BinaryPermissions> 3

    let value =
        { Status = BinaryStatus.Waiting
          Permissions = flags
          Unknown = unknown
          History = [ BinaryStatus.Done; BinaryStatus.Waiting; unknown ]
          Current = Some BinaryStatus.Done
          Changes = [ StatusChanged unknown; PermissionsChanged flags ]
          SignedBoundary = SignedByteEnum.Minimum
          UnsignedBoundary = UnsignedLongEnum.Maximum }

    let restored = roundTrip value
    let restoredThroughOrleans = roundTripWithOrleans value

    test <@ restored = value @>
    test <@ restoredThroughOrleans = value @>
    let unknownValue: int = LanguagePrimitives.EnumToValue restored.Unknown
    let permissionValue: int = LanguagePrimitives.EnumToValue restored.Permissions

    test <@ unknownValue = 123 @>
    test <@ permissionValue = 3 @>

[<Fact>]
let ``closed generic union runtime cases round-trip through their closed parent`` () : unit =
    let cases: obj[] =
        [| box (Leaf 42: Tree<int>)
           box (Node(Leaf 1, Leaf 2): Tree<int>)
           box (Leaf "forty-two": Tree<string>)
           box (Leaf(Leaf 42): Tree<Tree<int>>) |]

    for value in cases do
        let runtimeType = value.GetType()
        let bytes = FSharpBinaryFormat.serialize value runtimeType
        let restored = FSharpBinaryFormat.deserialize bytes runtimeType

        test <@ runtimeType.DeclaringType.ContainsGenericParameters @>
        test <@ not runtimeType.BaseType.ContainsGenericParameters @>
        test <@ restored = value @>

[<Fact>]
let ``closed generic union runtime cases cross the real Orleans serializer`` () : unit =
    let input: obj[] =
        [| box (Leaf 42: Tree<int>)
           box (Node(Leaf "a", Leaf "b"): Tree<string>)
           box (Leaf(Leaf 42): Tree<Tree<int>>) |]

    let restored = roundTripWithOrleans input

    test <@ restored.[0] :?> Tree<int> = Leaf 42 @>
    test <@ restored.[1] :?> Tree<string> = Node(Leaf "a", Leaf "b") @>
    test <@ restored.[2] :?> Tree<Tree<int>> = Leaf(Leaf 42) @>


// ── Codec build cell: concurrency and failed builds ──────────────────────────
//
// The codec cache publishes a forwarding codec before the real codec exists, so that
// self-referential types have something to hold. These tests pin the two ways that
// forwarder must NOT behave: it must not be handed to a racing thread that then
// dereferences it back onto itself (that self-recursion overflows the stack, which is
// unrecoverable — a regression aborts the whole test host rather than failing one case),
// and it must not survive a failed build.

/// <summary>
/// Distinct closed instantiations of this record give a distinct — and cold — codec cell
/// each, which is what makes the concurrent first-use race observable.
/// </summary>
type ColdPayload<'T> =
    { id: 'T
      name: string
      blob: byte[]
      tags: string list
      lookup: Map<string, int>
      stamp: DateTimeOffset }

/// <summary>
/// A cold SELF-referential type: its build hands the forwarder to its own element codec, which
/// is the case the forwarder exists for — and the one a racing thread must not break.
/// </summary>
type ColdTree<'T> =
    | ColdLeaf of 'T
    | ColdNode of ColdTree<'T> * ColdTree<'T>

let private coldPayload (id: 'T) : ColdPayload<'T> =
    { id = id
      name = "cold"
      blob = [| 1uy; 2uy; 3uy |]
      tags = [ "a"; "b" ]
      lookup = Map.ofList [ "x", 1; "y", 2 ]
      stamp = DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero) }

/// <summary>
/// Round-trips <paramref name="value"/> from several threads that all start at the same
/// instant, so they reach the type's cold codec cell together.
/// </summary>
let private hammerFirstUse (t: Type) (value: obj) =
    let threadCount = 8
    use barrier = new Barrier(threadCount)
    let results = Array.zeroCreate<obj> threadCount
    let failures = ResizeArray<exn>()

    let threads =
        Array.init threadCount (fun index ->
            let thread =
                Thread(
                    ThreadStart(fun () ->
                        try
                            barrier.SignalAndWait()
                            let bytes = FSharpBinaryFormat.serialize value t
                            results.[index] <- FSharpBinaryFormat.deserialize bytes t
                        with e ->
                            lock failures (fun () -> failures.Add e)),
                    IsBackground = true
                )

            thread.Start()
            thread)

    for thread in threads do
        test <@ thread.Join(TimeSpan.FromSeconds 30.0) @>

    if failures.Count > 0 then
        raise (AggregateException("concurrent first use failed", failures))

    for result in results do
        test <@ result = value @>

/// <summary>
/// Concurrent first use of a type whose codec has never been built must produce one usable
/// codec for every thread — never a forwarder that resolves to itself.
/// </summary>
[<Fact>]
let ``concurrent first use of a cold type never self-recurses`` () : unit =
    hammerFirstUse typeof<ColdPayload<int>> (box (coldPayload 7))
    hammerFirstUse typeof<ColdPayload<string>> (box (coldPayload "seven"))
    hammerFirstUse typeof<ColdPayload<bool>> (box (coldPayload true))
    hammerFirstUse typeof<ColdPayload<Guid>> (box (coldPayload (Guid("00000000-0000-0000-0000-0000000000aa"))))

/// <summary>
/// The same race on a self-referential type, where the forwarder is also handed to the type's
/// own element codec during the build.
/// </summary>
[<Fact>]
let ``concurrent first use of a cold recursive type never self-recurses`` () : unit =
    let tree =
        ColdNode(ColdLeaf 1, ColdNode(ColdLeaf 2, ColdNode(ColdLeaf 3, ColdLeaf 4)))

    hammerFirstUse typeof<ColdTree<int>> (box tree)
    hammerFirstUse typeof<ColdTree<string>> (box (ColdNode(ColdLeaf "a", ColdLeaf "b")))

/// <summary>
/// A build that throws must leave the cache clean: the second attempt reports the same
/// diagnostic instead of picking up the forwarder the failed build installed.
/// </summary>
[<Fact>]
let ``an unsupported type reports the same diagnostic on every attempt`` () : unit =
    let attempt () =
        try
            FSharpBinaryFormat.serialize (box 1) typeof<IDisposable> |> ignore
            "no exception"
        with :? InvalidOperationException as e ->
            e.Message

    let first = attempt ()
    let second = attempt ()
    let third = attempt ()

    test <@ first.Contains "unsupported type" @>
    test <@ second = first @>
    test <@ third = first @>

/// <summary>
/// The same, one level down: the failure happens inside a nested member's build, so both
/// the inner and the outer cell have to be released.
/// </summary>
[<Fact>]
let ``a type whose nested member is unsupported stays diagnosable`` () : unit =
    let attempt () =
        try
            FSharpBinaryFormat.serialize (box (None: IDisposable option)) typeof<IDisposable option>
            |> ignore
            "no exception"
        with :? InvalidOperationException as e ->
            e.Message

    let first = attempt ()
    let second = attempt ()

    test <@ first.Contains "unsupported type" @>
    test <@ second = first @>

// ──────────────────────────────────────────────────────────────────────────────
// Hardening: every length, count, arity, and type name on the wire is untrusted
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Write a raw codec body by hand, exactly the way the format specifies it.</summary>
let private bodyOf (write: BinaryWriter -> unit) : byte[] =
    use ms = new MemoryStream()
    use bw = new BinaryWriter(ms, Encoding.UTF8, true)
    write bw
    bw.Flush()
    ms.ToArray()

/// <summary>Write a type-prefixed payload by hand, so the length prefix can be a lie.</summary>
let private prefixedOf (typeName: string) (declaredLength: int) (body: byte[]) : byte[] =
    bodyOf (fun bw ->
        bw.Write(typeName)
        bw.Write(declaredLength)
        bw.Write(body))

/// <summary>Write BinaryWriter's unsigned 7-bit string-length prefix without a string body.</summary>
let private declaredTypeNameOf (byteCount: int) : byte[] =
    bodyOf (fun bw ->
        let mutable remaining = uint32 byteCount

        while remaining >= 0x80u do
            bw.Write(byte (remaining ||| 0x80u))
            remaining <- remaining >>> 7

        bw.Write(byte remaining))

let private rejects (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

let private readingAs<'T> (body: byte[]) =
    rejects (fun () -> FSharpBinaryFormat.deserialize body typeof<'T> |> ignore)

// ── F1: declared lengths and counts are checked against what is left ──────────

[<Fact>]
let ``a top-level payload length beyond the remaining bytes is rejected`` () : unit =
    // 2 GiB declared, four bytes supplied. The unhardened reader hands Int32.MaxValue straight
    // to BinaryReader.ReadBytes, which allocates the whole array before reading anything.
    let payload = prefixedOf typeof<RecordWithOptions>.FullName Int32.MaxValue [| 1uy; 2uy; 3uy; 4uy |]

    let error = rejects (fun () -> FSharpBinaryFormat.deserializeWithType payload typeof<RecordWithOptions> |> ignore)

    test <@ error.Message.Contains "exceeds remaining payload" @>
    test <@ error.Message.Contains "2147483647" @>

[<Fact>]
let ``a byte-array length beyond the remaining bytes is rejected`` () : unit =
    let error = readingAs<byte[]> (bodyOf (fun bw -> bw.Write 1000000))

    test <@ error.Message.Contains "exceeds remaining payload" @>
    test <@ error.Message.Contains "byte array" @>

[<Theory>]
[<InlineData "list">]
[<InlineData "array">]
[<InlineData "set">]
[<InlineData "map">]
let ``a container element count beyond the remaining bytes is rejected`` (container: string) : unit =
    let huge = bodyOf (fun bw -> bw.Write 100000000)

    let error =
        match container with
        | "list" -> readingAs<int list> huge
        | "array" -> readingAs<int[]> huge
        | "set" -> readingAs<Set<int>> huge
        | _ -> readingAs<Map<string, int>> huge

    test <@ error.Message.Contains "declared element count 100000000 exceeds remaining payload" @>

[<Fact>]
let ``a negative element count is rejected`` () : unit =
    let error = readingAs<int list> (bodyOf (fun bw -> bw.Write -1))

    test <@ error.Message.Contains "is negative" @>

[<Fact>]
let ``a zero-width element count is capped absolutely`` () : unit =
    // A unit list carries no per-element bytes, so the remaining-payload bound cannot apply and
    // the absolute cap is the only thing standing between the count and the allocation.
    let error = readingAs<unit list> (bodyOf (fun bw -> bw.Write 2000000))

    test <@ error.Message.Contains "1048576-element cap" @>

[<Fact>]
let ``a legitimate zero-width container still round-trips`` () : unit =
    // The counterweight to the cap: the bound must not reject a real unit list, whose five
    // elements occupy zero payload bytes between them.
    test <@ roundTrip (List.replicate 5 ()) = List.replicate 5 () @>
    test <@ roundTrip ([ (); () ] |> List.map (fun () -> ((), ()))) |> List.length = 2 @>

// ── F2: wire field counts are checked against the type's real arity ───────────

[<Fact>]
let ``a record field count that is not the type's arity is rejected`` () : unit =
    let error = readingAs<RecordWithOptions> (bodyOf (fun bw -> bw.Write 9))

    test <@ error.Message.Contains "declared field count 9 does not match the 3 fields" @>
    test <@ error.Message.Contains "RecordWithOptions" @>

[<Fact>]
let ``a record field count below the type's arity is rejected`` () : unit =
    // The unhardened reader reached FSharpValue.MakeRecord with the wrong arity instead.
    let error = readingAs<RecordWithOptions> (bodyOf (fun bw -> bw.Write 1))

    test <@ error.Message.Contains "declared field count 1 does not match the 3 fields" @>

[<Fact>]
let ``a union case tag outside the type's cases is rejected`` () : unit =
    let error = readingAs<Tree<int>> (bodyOf (fun bw -> bw.Write 7))

    test <@ error.Message.Contains "case tag 7 is out of range for the 2 cases" @>

[<Fact>]
let ``a negative union case tag is rejected`` () : unit =
    let error = readingAs<Tree<int>> (bodyOf (fun bw -> bw.Write -3))

    test <@ error.Message.Contains "case tag -3 is out of range" @>

[<Fact>]
let ``a union field count that is not the case's arity is rejected`` () : unit =
    let error =
        readingAs<Tree<int>> (
            bodyOf (fun bw ->
                bw.Write 0 // Leaf, arity 1
                bw.Write 4))

    test <@ error.Message.Contains "declared field count 4 does not match the 1 fields" @>

[<Fact>]
let ``a CLIMutable field count that is not the type's arity is rejected`` () : unit =
    let error = readingAs<SimplePoco> (bodyOf (fun bw -> bw.Write 6))

    test <@ error.Message.Contains "declared field count 6 does not match the 2 fields" @>

// ── F3: the caller's expected type constrains the wire name ───────────────────

/// <summary>A declared-abstract payload shape, as the specification's polymorphism rule allows.</summary>
type IHardenedPayload =
    interface
    end

type HardenedPayload =
    { hardened: string }

    interface IHardenedPayload

type UnrelatedPayload = { unrelated: string }

[<Fact>]
let ``a wire type outside the expected hierarchy is rejected`` () : unit =
    FSharpBinaryFormat.declareType typeof<HardenedPayload>

    let bytes =
        FSharpBinaryFormat.serializeWithType (box { hardened = "x" }) typeof<HardenedPayload>

    for hint in [ null; typeof<HardenedPayload> ] do
        let error =
            rejects (fun () ->
                FSharpBinaryFormat.ExpectedPayloadType.Scoped(
                    typeof<UnrelatedPayload>,
                    fun () -> FSharpBinaryFormat.deserializeWithType bytes hint |> ignore))

        test <@ error.Message.Contains "is not assignable to the expected type" @>
        test <@ error.Message.Contains "HardenedPayload" @>
        test <@ error.Message.Contains "UnrelatedPayload" @>

[<Fact>]
let ``a wire type inside the expected hierarchy still resolves by name`` () : unit =
    FSharpBinaryFormat.declareType typeof<HardenedPayload>

    let bytes =
        FSharpBinaryFormat.serializeWithType (box { hardened = "y" }) typeof<HardenedPayload>

    // The declared type is the INTERFACE; only wire-name resolution can reach the concrete
    // record, which is exactly the polymorphism the specification asks the codec to keep.
    let restored =
        FSharpBinaryFormat.ExpectedPayloadType.Scoped(
            typeof<IHardenedPayload>,
            fun () -> FSharpBinaryFormat.deserializeWithType bytes null)

    test <@ unbox<HardenedPayload> restored = { hardened = "y" } @>

[<Fact>]
let ``the payload codec publishes the exact type it was asked for`` () : unit =
    // The production wiring, not just the guard: a FunctionalPayloadCodec asked for one type
    // must reject a payload naming another even though both are declared and resolvable.
    FSharpBinaryFormat.declareType typeof<HardenedPayload>
    FSharpBinaryFormat.declareType typeof<UnrelatedPayload>

    let services = ServiceCollection()

    ServiceCollectionExtensions.AddSerializer(
        services,
        Action<ISerializerBuilder>(fun builder ->
            FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore))
    |> ignore

    use provider = services.BuildServiceProvider()
    let serializer = provider.GetRequiredService<Serializer>()
    let codec = FunctionalPayloadCodec(serializer, serializer.SessionPool)

    let payload = codec.Serialize<UnrelatedPayload> { unrelated = "z" }

    test <@ codec.Deserialize<UnrelatedPayload> payload = { unrelated = "z" } @>

    let error = rejects (fun () -> codec.Deserialize<HardenedPayload> payload |> ignore)

    test <@ error.Message.Contains "is not assignable to the expected type" @>

[<Fact>]
let ``a generalized root cannot be replaced by its generic argument`` () : unit =
    withOrleansSerializer (fun serializer ->
        let codec = FunctionalPayloadCodec(serializer, serializer.SessionPool)
        let value = { hardened = "root" }
        let payload = codec.Serialize<HardenedPayload> value

        let optionError =
            rejects (fun () -> codec.Deserialize<HardenedPayload option> payload |> ignore)

        let listError =
            rejects (fun () -> codec.Deserialize<HardenedPayload list> payload |> ignore)

        for error in [ optionError; listError ] do
            test <@ error.Message.Contains "is not assignable to the expected type" @>
            test <@ error.Message.Contains "HardenedPayload" @>

        // Refusal must not leave an ambient type or a poisoned serializer session behind.
        test <@ codec.Deserialize<HardenedPayload> payload = value @>)

// ── F4: the allow-list runs before any assembly is loaded, and rejections do not cache ──

[<Fact>]
let ``an unlisted assembly is rejected before Type.GetType runs`` () : unit =
    // The discriminator: Type.GetType would fail to load this assembly and the codec would then
    // report "not found". Naming the assembly proves the check ran first.
    let name =
        "Hardening.Probe, Hostile.Payloads, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"

    let error = rejects (fun () -> FSharpBinaryFormat.deserializeWithType (prefixedOf name 0 [||]) null |> ignore)

    test <@ error.Message.Contains "names assembly 'Hostile.Payloads'" @>
    test <@ error.Message.Contains "it was not loaded" @>

[<Fact>]
let ``an assembly name that merely starts with an allowed prefix is rejected`` () : unit =
    // The allow-list matches whole dotted segments. A raw StartsWith would admit every one of
    // these on the strength of a legitimate prefix.
    for hostile in [ "Orleans.FSharpHostile"; "SystemHostile"; "TypeShapeHostile"; "FSharp.CoreHostile" ] do
        let name = $"Hardening.Probe, {hostile}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"

        let error =
            rejects (fun () -> FSharpBinaryFormat.deserializeWithType (prefixedOf name 0 [||]) null |> ignore)

        test <@ error.Message.Contains $"names assembly '{hostile}'" @>

[<Fact>]
let ``a genuine sub-namespace of an allowed prefix still resolves`` () : unit =
    // The counterweight: tightening the match must not lock out the real framework assemblies.
    let name = typeof<Version>.FullName + ", System.Private.CoreLib"

    // System.Version is deliberately not reconstructable as a POCO. Reaching that shape error
    // proves resolution itself got past the allow-list.
    let body = [| 1uy; 0uy; 0uy; 0uy; 0uy |]

    let error =
        rejects (fun () -> FSharpBinaryFormat.deserializeWithType (prefixedOf name body.Length body) null |> ignore)

    test <@ not (error.Message.Contains "allow-list") @>
    test <@ error.Message.Contains "System.Version" @>
    test <@ error.Message.Contains "cannot be reconstructed" @>

[<Fact>]
let ``an unlisted assembly named only by a generic argument is rejected`` () : unit =
    // The outer type is a framework name; only the type ARGUMENT names the hostile assembly,
    // and Type.GetType would load it to close the generic.
    let name =
        "System.Collections.Generic.List`1[[Hardening.Probe, Evil.Payloads, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], System.Private.CoreLib"

    let error = rejects (fun () -> FSharpBinaryFormat.deserializeWithType (prefixedOf name 0 [||]) null |> ignore)

    test <@ error.Message.Contains "names assembly 'Evil.Payloads'" @>

[<Fact>]
let ``an over-long wire type name is rejected`` () : unit =
    let name = String('A', 5000)

    let error = rejects (fun () -> FSharpBinaryFormat.deserializeWithType (prefixedOf name 0 [||]) null |> ignore)

    test <@ error.Message.Contains "exceeds the 4096-character limit" @>

[<Fact>]
let ``a huge declared type-name length is rejected before its body is read`` () : unit =
    // The payload contains only a 32 MiB DECLARATION and no string body. BinaryReader.ReadString
    // would try to materialize that declaration before the old post-read character check ran.
    let error =
        rejects (fun () ->
            FSharpBinaryFormat.deserializeWithType (declaredTypeNameOf (32 * 1024 * 1024)) null
            |> ignore)

    test <@ error.Message.Contains "pre-allocation safety limit" @>
    test <@ error.Message.Contains "33554432-byte type name" @>

[<Fact>]
let ``a negative declared type-name length is rejected`` () : unit =
    let error =
        rejects (fun () ->
            FSharpBinaryFormat.deserializeWithType (declaredTypeNameOf -1) null |> ignore)

    test <@ error.Message.Contains "negative type-name byte count" @>

[<Fact>]
let ``a stream of rejected type names grows no cache`` () : unit =
    let before = FSharpBinaryFormat.wireResolvedTypeCount ()

    for index in 1..200 do
        let name =
            $"Hardening.Probe{index}, Hostile.Payloads{index}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"

        rejects (fun () -> FSharpBinaryFormat.deserializeWithType (prefixedOf name 0 [||]) null |> ignore)
        |> ignore

    // Also 200 names that resolve to nothing at all: still nothing to remember.
    for index in 1..200 do
        rejects (fun () ->
            FSharpBinaryFormat.deserializeWithType (prefixedOf $"Hardening.Missing{index}" 0 [||]) null
            |> ignore)
        |> ignore

    test <@ FSharpBinaryFormat.wireResolvedTypeCount () = before @>

// ── A cleared persistent state names the field it broke on ──────────────────

/// <summary>A stored-state shape whose fields have no null value.</summary>
type ClearedState =
    { events: string list
      tags: Map<string, int>
      label: string
      note: string option }

/// <summary>A stored-state shape whose only composite field is a union.</summary>
type ClearedColour =
    | Red
    | Green

type ClearedUnionState = { colour: ClearedColour; label: string }

/// <summary>Exactly what Orleans hands back after ClearStateAsync.</summary>
let private uninitialized (t: Type) =
    System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject t

[<Fact>]
let ``a cleared state names the null field and the likely cause`` () : unit =
    // Regression for the crash that has to be readable rather than merely thrown: before this,
    // a cleared facet died as ArgumentNullException("source") from inside a collection loop,
    // with nothing naming the field, the record, or ClearStateAsync.
    let error =
        rejects (fun () ->
            FSharpBinaryFormat.serialize (uninitialized typeof<ClearedState>) typeof<ClearedState>
            |> ignore)

    test <@ error.Message.Contains "field 'events'" @>
    test <@ error.Message.Contains "ClearedState" @>
    test <@ error.Message.Contains "has no null value" @>
    test <@ error.Message.Contains "ClearStateAsync" @>

[<Fact>]
let ``a cleared state names a null union field too`` () : unit =
    let error =
        rejects (fun () ->
            FSharpBinaryFormat.serialize (uninitialized typeof<ClearedUnionState>) typeof<ClearedUnionState>
            |> ignore)

    test <@ error.Message.Contains "field 'colour'" @>
    test <@ error.Message.Contains "has no null value" @>

[<Fact>]
let ``a null that IS a legal value is still written`` () : unit =
    // The check must not reject the nulls F# uses on purpose. `None` is compiled to null through
    // UseNullAsTrueValue, a string may be null, and an ordinary class field may be null — all
    // three are values the codec has always round-tripped, and rejecting any of them would break
    // far more than the cleared-state case fixes.
    let value =
        { events = []
          tags = Map.empty
          label = null
          note = None }

    let restored = roundTrip<ClearedState> value

    test <@ restored = value @>
    test <@ restored.note = None @>
    test <@ isNull restored.label @>

[<Fact>]
let ``a healthy record with the same shape still round-trips`` () : unit =
    let value =
        { events = [ "created"; "updated" ]
          tags = Map [ "k", 1 ]
          label = "live"
          note = Some "n" }

    test <@ roundTrip<ClearedState> value = value @>
