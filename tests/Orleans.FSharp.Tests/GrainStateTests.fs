module Orleans.FSharp.Tests.GrainStateTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Runtime
open Orleans.FSharp

// ---------------------------------------------------------------------------
// Test DU types for GrainState property tests
// ---------------------------------------------------------------------------

/// Simple enum-like DU with no data.
type TrafficLight =
    | Red
    | Yellow
    | Green

/// DU with data in each case.
type Shape =
    | Circle of radius: float
    | Rectangle of width: float * height: float
    | Point

/// Nested record inside a DU.
type Address =
    { Street: string
      City: string
      Zip: string }

type Person =
    { Name: string
      Age: int
      Address: Address option }

type CustomerState =
    | Anonymous
    | Registered of Person
    | Suspended of reason: string

// ---------------------------------------------------------------------------
// Mock IPersistentState<T> for unit testing
// ---------------------------------------------------------------------------

/// <summary>
/// A simple mock of IPersistentState for testing GrainState functions
/// without requiring an actual Orleans silo.
/// </summary>
type MockPersistentState<'T>(initial: 'T) =
    let mutable state = initial
    let mutable recordExists = false
    let mutable cleared = false
    let mutable writeCount = 0
    let mutable readCount = 0

    member _.WriteCount = writeCount
    member _.ReadCount = readCount
    member _.WasCleared = cleared

    interface IPersistentState<'T> with
        member _.State
            with get () = state
            and set v = state <- v

        member _.RecordExists = recordExists

        member _.ReadStateAsync() =
            readCount <- readCount + 1
            recordExists <- true
            Task.CompletedTask

        member _.WriteStateAsync() =
            writeCount <- writeCount + 1
            recordExists <- true
            Task.CompletedTask

        member _.ClearStateAsync() =
            cleared <- true
            recordExists <- false
            state <- Unchecked.defaultof<'T>
            Task.CompletedTask

        member _.Etag = "mock-etag"

// ---------------------------------------------------------------------------
// Unit tests for GrainState module functions
// ---------------------------------------------------------------------------

[<Fact>]
let ``GrainState.current returns in-memory state`` () =
    let mock = MockPersistentState<TrafficLight>(Red)
    let ps = mock :> IPersistentState<TrafficLight>
    let result = GrainState.current ps
    test <@ result = Red @>

[<Fact>]
let ``GrainState.current returns state after mutation`` () =
    let mock = MockPersistentState<int>(0)
    let ps = mock :> IPersistentState<int>
    ps.State <- 42
    let result = GrainState.current ps
    test <@ result = 42 @>

[<Fact>]
let ``GrainState.write persists state`` () =
    task {
        let mock = MockPersistentState<TrafficLight>(Red)
        let ps = mock :> IPersistentState<TrafficLight>
        do! GrainState.write ps Green
        test <@ ps.State = Green @>
        test <@ mock.WriteCount = 1 @>
    }

[<Fact>]
let ``GrainState.write then current returns written value`` () =
    task {
        let mock = MockPersistentState<Shape>(Point)
        let ps = mock :> IPersistentState<Shape>
        do! GrainState.write ps (Circle 3.14)
        let result = GrainState.current ps
        test <@ result = Circle 3.14 @>
    }

[<Fact>]
let ``GrainState.read calls ReadStateAsync`` () =
    task {
        let mock = MockPersistentState<int>(99)
        let ps = mock :> IPersistentState<int>
        let! result = GrainState.read ps
        test <@ result = 99 @>
        test <@ mock.ReadCount = 1 @>
    }

[<Fact>]
let ``GrainState.clear calls ClearStateAsync`` () =
    task {
        let mock = MockPersistentState<TrafficLight>(Green)
        let ps = mock :> IPersistentState<TrafficLight>
        do! GrainState.clear ps
        test <@ mock.WasCleared @>
    }

[<Fact>]
let ``GrainState write then read roundtrip`` () =
    task {
        let mock = MockPersistentState<CustomerState>(Anonymous)
        let ps = mock :> IPersistentState<CustomerState>

        let person =
            Registered
                { Name = "Alice"
                  Age = 30
                  Address =
                    Some
                        { Street = "123 Main St"
                          City = "Springfield"
                          Zip = "62701" } }

        do! GrainState.write ps person
        let! result = GrainState.read ps
        test <@ result = person @>
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``write then current returns same value for TrafficLight`` (light: TrafficLight) =
    let mock = MockPersistentState<TrafficLight>(Red)
    let ps = mock :> IPersistentState<TrafficLight>
    (GrainState.write ps light).GetAwaiter().GetResult()
    let result = GrainState.current ps
    result = light

[<Property>]
let ``write then read roundtrip for int`` (value: int) =
    let mock = MockPersistentState<int>(0)
    let ps = mock :> IPersistentState<int>
    (GrainState.write ps value).GetAwaiter().GetResult()
    let result = (GrainState.read ps).GetAwaiter().GetResult()
    result = value

[<Property>]
let ``write then read roundtrip for string`` (value: NonNull<string>) =
    let s = value.Get
    let mock = MockPersistentState<string>("")
    let ps = mock :> IPersistentState<string>
    (GrainState.write ps s).GetAwaiter().GetResult()
    let result = (GrainState.read ps).GetAwaiter().GetResult()
    result = s

[<Property>]
let ``clear resets state`` (initial: TrafficLight) =
    let mock = MockPersistentState<TrafficLight>(initial)
    let ps = mock :> IPersistentState<TrafficLight>
    (GrainState.clear ps).GetAwaiter().GetResult()
    mock.WasCleared
