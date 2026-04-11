module Orleans.FSharp.Tests.TestHarnessTests

open System
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Testing

// --- TestHarness type tests ---

[<Fact>]
let ``TestHarness type has Cluster field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<TestHarness>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Cluster" @>

[<Fact>]
let ``TestHarness type has Client field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<TestHarness>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Client" @>

[<Fact>]
let ``TestHarness type has LogFactory field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<TestHarness>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "LogFactory" @>

// --- CapturedLogEntry type tests ---

[<Fact>]
let ``CapturedLogEntry is a record type`` () =
    test <@ Microsoft.FSharp.Reflection.FSharpType.IsRecord(typeof<CapturedLogEntry>) @>

[<Fact>]
let ``CapturedLogEntry has Level field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<CapturedLogEntry>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Level" @>

[<Fact>]
let ``CapturedLogEntry has Template field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<CapturedLogEntry>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Template" @>

[<Fact>]
let ``CapturedLogEntry has Properties field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<CapturedLogEntry>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Properties" @>

[<Fact>]
let ``CapturedLogEntry has Timestamp field`` () =
    let fields =
        Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<CapturedLogEntry>)
        |> Array.map (fun p -> p.Name)

    test <@ fields |> Array.contains "Timestamp" @>

// --- LogCapture tests ---

[<Fact>]
let ``LogCapture create returns factory`` () =
    let factory = LogCapture.create ()
    test <@ not (isNull (box factory)) @>

[<Fact>]
let ``LogCapture captureLogs returns empty list initially`` () =
    let factory = LogCapture.create ()
    let entries = LogCapture.captureLogs factory
    test <@ entries.Length = 0 @>

// --- FsCheckHelpers type existence tests ---

[<Fact>]
let ``FsCheckHelpers commandSequenceArb produces non-empty lists`` () =
    let arb = FsCheckHelpers.commandSequenceArb<int> ()
    let gen = FsCheck.FSharp.Arb.toGen arb
    let samples = FsCheck.FSharp.Gen.sampleWithSize 10 10 gen
    test <@ samples |> Array.forall (fun l -> l.Length > 0) @>

[<Fact>]
let ``FsCheckHelpers stateMachineProperty detects valid invariant`` () =
    let result =
        FsCheckHelpers.stateMachineProperty
            0
            (fun state cmd ->
                match cmd with
                | true -> state + 1
                | false -> max 0 (state - 1))
            (fun state -> state >= 0)
            [ true; true; false; true ]

    test <@ result = true @>

[<Fact>]
let ``FsCheckHelpers stateMachineProperty detects broken invariant`` () =
    let result =
        FsCheckHelpers.stateMachineProperty
            0
            (fun state (_cmd: int) -> state - 1)
            (fun state -> state >= 0)
            [ 1; 2; 3 ]

    test <@ result = false @>

// --- TestHarness module function existence tests ---
// The F# compiler may name the static class "TestHarnessModule" when a type and module have the same name.

let private findTestHarnessModule () =
    typeof<TestHarness>.Assembly.GetTypes()
    |> Array.tryFind (fun t ->
        t.FullName.Contains("TestHarness")
        && t.IsAbstract
        && t.IsSealed
        && t.GetMethods() |> Array.exists (fun m -> m.Name = "createTestCluster"))

[<Fact>]
let ``TestHarness module has createTestCluster function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let createMethod =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "createTestCluster")

    test <@ createMethod.IsSome @>

[<Fact>]
let ``TestHarness module has captureLogs function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let captureMethod =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "captureLogs")

    test <@ captureMethod.IsSome @>

[<Fact>]
let ``TestHarness module has reset function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let resetMethod =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "reset")

    test <@ resetMethod.IsSome @>

[<Fact>]
let ``TestHarness module has dispose function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let disposeMethod =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "dispose")

    test <@ disposeMethod.IsSome @>

// --- New FSharpGrain helper function existence tests ---

[<Fact>]
let ``TestHarness module has getFSharpGrain function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let method =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "getFSharpGrain")

    test <@ method.IsSome @>

[<Fact>]
let ``TestHarness module has getFSharpGrainGuid function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let method =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "getFSharpGrainGuid")

    test <@ method.IsSome @>

[<Fact>]
let ``TestHarness module has getFSharpGrainInt function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let method =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "getFSharpGrainInt")

    test <@ method.IsSome @>

[<Fact>]
let ``TestHarness module has createTestClusterWith function`` () =
    let testHarnessModule = findTestHarnessModule ()
    test <@ testHarnessModule.IsSome @>

    let method =
        testHarnessModule.Value.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "createTestClusterWith")

    test <@ method.IsSome @>
