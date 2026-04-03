module Orleans.FSharp.Tests.GrainMockTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Testing

/// <summary>Tests for GrainMock.fs — MockGrainFactory and GrainMock module.</summary>

// --- Test grain interfaces ---

type ITestStringGrain =
    inherit IGrainWithStringKey
    abstract member GetValue: unit -> Task<string>

type ITestGuidGrain =
    inherit IGrainWithGuidKey
    abstract member GetValue: unit -> Task<int>

type ITestIntGrain =
    inherit IGrainWithIntegerKey
    abstract member GetValue: unit -> Task<float>

// --- Test grain implementations ---

type FakeStringGrain(value: string) =
    interface ITestStringGrain with
        member _.GetValue() = Task.FromResult(value)
    interface IGrain

type FakeGuidGrain(value: int) =
    interface ITestGuidGrain with
        member _.GetValue() = Task.FromResult(value)
    interface IGrain

type FakeIntGrain(value: float) =
    interface ITestIntGrain with
        member _.GetValue() = Task.FromResult(value)
    interface IGrain

// --- MockGrainFactory creation tests ---

[<Fact>]
let ``GrainMock.create returns a MockGrainFactory`` () =
    let factory = GrainMock.create ()
    test <@ not (isNull (box factory)) @>
    test <@ factory.GetType() = typeof<MockGrainFactory> @>

[<Fact>]
let ``MockGrainFactory implements IGrainFactory`` () =
    let factory = GrainMock.create ()
    let asInterface = factory :> IGrainFactory
    test <@ not (isNull (box asInterface)) @>

// --- withGrain registration tests ---

[<Fact>]
let ``withGrain returns the same factory instance`` () =
    let factory = GrainMock.create ()
    let fakeGrain = FakeStringGrain("hello") :> ITestStringGrain
    let result = factory |> GrainMock.withGrain<ITestStringGrain> "key1" fakeGrain
    test <@ obj.ReferenceEquals(factory, result) @>

[<Fact>]
let ``withGrain allows chaining`` () =
    let fakeStr = FakeStringGrain("hello") :> ITestStringGrain
    let fakeGuid = FakeGuidGrain(42) :> ITestGuidGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestStringGrain> "key1" fakeStr
        |> GrainMock.withGrain<ITestGuidGrain> (Guid.Empty) fakeGuid

    test <@ not (isNull (box factory)) @>

// --- GetGrain retrieval tests ---

[<Fact>]
let ``GetGrain by string returns registered grain`` () =
    let fakeGrain = FakeStringGrain("hello") :> ITestStringGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestStringGrain> "key1" fakeGrain
        :> IGrainFactory

    let grain = factory.GetGrain<ITestStringGrain>("key1", null)
    test <@ not (isNull (box grain)) @>

[<Fact>]
let ``GetGrain by string returns correct implementation`` () =
    let fakeGrain = FakeStringGrain("hello") :> ITestStringGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestStringGrain> "key1" fakeGrain
        :> IGrainFactory

    let grain = factory.GetGrain<ITestStringGrain>("key1", null)
    let result = grain.GetValue().Result
    test <@ result = "hello" @>

[<Fact>]
let ``GetGrain by Guid returns registered grain`` () =
    let guid = Guid.NewGuid()
    let fakeGrain = FakeGuidGrain(42) :> ITestGuidGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestGuidGrain> guid fakeGrain
        :> IGrainFactory

    let grain = factory.GetGrain<ITestGuidGrain>(guid, null)
    let result = grain.GetValue().Result
    test <@ result = 42 @>

[<Fact>]
let ``GetGrain by int64 returns registered grain`` () =
    let fakeGrain = FakeIntGrain(3.14) :> ITestIntGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestIntGrain> 99L fakeGrain
        :> IGrainFactory

    let grain = factory.GetGrain<ITestIntGrain>(99L, null)
    let result = grain.GetValue().Result
    test <@ result = 3.14 @>

// --- Missing grain tests ---

[<Fact>]
let ``GetGrain throws when grain not registered`` () =
    let factory = GrainMock.create () :> IGrainFactory
    Assert.Throws<System.Collections.Generic.KeyNotFoundException>(fun () ->
        factory.GetGrain<ITestStringGrain>("nonexistent", null) |> ignore)
    |> ignore

[<Fact>]
let ``GetGrain throws with wrong key`` () =
    let fakeGrain = FakeStringGrain("hello") :> ITestStringGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestStringGrain> "key1" fakeGrain
        :> IGrainFactory

    Assert.Throws<System.Collections.Generic.KeyNotFoundException>(fun () ->
        factory.GetGrain<ITestStringGrain>("wrong-key", null) |> ignore)
    |> ignore

// --- Multiple grain registration tests ---

[<Fact>]
let ``factory can hold multiple grains of same type with different keys`` () =
    let fake1 = FakeStringGrain("first") :> ITestStringGrain
    let fake2 = FakeStringGrain("second") :> ITestStringGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestStringGrain> "key1" fake1
        |> GrainMock.withGrain<ITestStringGrain> "key2" fake2
        :> IGrainFactory

    let grain1 = factory.GetGrain<ITestStringGrain>("key1", null)
    let grain2 = factory.GetGrain<ITestStringGrain>("key2", null)
    test <@ grain1.GetValue().Result = "first" @>
    test <@ grain2.GetValue().Result = "second" @>

[<Fact>]
let ``factory can hold grains of different types`` () =
    let fakeStr = FakeStringGrain("hello") :> ITestStringGrain
    let fakeGuid = FakeGuidGrain(42) :> ITestGuidGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestStringGrain> "key1" fakeStr
        |> GrainMock.withGrain<ITestGuidGrain> (Guid.Empty) fakeGuid
        :> IGrainFactory

    let strGrain = factory.GetGrain<ITestStringGrain>("key1", null)
    let guidGrain = factory.GetGrain<ITestGuidGrain>(Guid.Empty, null)
    test <@ strGrain.GetValue().Result = "hello" @>
    test <@ guidGrain.GetValue().Result = 42 @>

// --- GrainMock module existence tests ---

[<Fact>]
let ``GrainMock module exists in the testing assembly`` () =
    let moduleType =
        typeof<MockGrainFactory>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "GrainMock" && t.IsAbstract && t.IsSealed)

    test <@ moduleType.IsSome @>

[<Fact>]
let ``GrainMock.create method exists`` () =
    let moduleType =
        typeof<MockGrainFactory>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainMock" && t.IsAbstract && t.IsSealed)

    let method =
        moduleType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "create")

    test <@ method.IsSome @>

[<Fact>]
let ``GrainMock.withGrain method exists`` () =
    let moduleType =
        typeof<MockGrainFactory>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainMock" && t.IsAbstract && t.IsSealed)

    let method =
        moduleType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "withGrain")

    test <@ method.IsSome @>

// --- Override test ---

[<Fact>]
let ``withGrain overrides previous registration for same type and key`` () =
    let fake1 = FakeStringGrain("first") :> ITestStringGrain
    let fake2 = FakeStringGrain("second") :> ITestStringGrain

    let factory =
        GrainMock.create ()
        |> GrainMock.withGrain<ITestStringGrain> "key1" fake1
        |> GrainMock.withGrain<ITestStringGrain> "key1" fake2
        :> IGrainFactory

    let grain = factory.GetGrain<ITestStringGrain>("key1", null)
    test <@ grain.GetValue().Result = "second" @>

// ── withFSharpGrain tests ─────────────────────────────────────────────────

/// Ping grain types for mock tests
type MockPingState = { Count: int }
type MockPingCmd = | MockPing | MockGetCount

[<Fact>]
let ``withFSharpGrain creates a working string-keyed mock`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }
                handle (fun state (cmd: MockPingCmd) ->
                    task {
                        match cmd with
                        | MockPing     -> let ns = { Count = state.Count + 1 } in return ns, box ns
                        | MockGetCount -> return state, box state
                    })
            }

        let factory =
            GrainMock.create ()
            |> GrainMock.withFSharpGrain "mock-ping-1" def

        let handle = FSharpGrain.ref<MockPingState, MockPingCmd> factory "mock-ping-1"
        let! state = handle |> FSharpGrain.send MockPing
        test <@ state.Count = 1 @>
    }

[<Fact>]
let ``withFSharpGrain maintains state across multiple calls`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }
                handle (fun state (cmd: MockPingCmd) ->
                    task {
                        match cmd with
                        | MockPing     -> let ns = { Count = state.Count + 1 } in return ns, box ns
                        | MockGetCount -> return state, box state
                    })
            }

        let factory =
            GrainMock.create ()
            |> GrainMock.withFSharpGrain "mock-ping-state" def

        let handle = FSharpGrain.ref<MockPingState, MockPingCmd> factory "mock-ping-state"
        let! _ = handle |> FSharpGrain.send MockPing
        let! _ = handle |> FSharpGrain.send MockPing
        let! state = handle |> FSharpGrain.send MockGetCount
        test <@ state.Count = 2 @>
    }

[<Fact>]
let ``withFSharpGrain works with ask for typed result`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }
                handleTyped (fun state (cmd: MockPingCmd) ->
                    task {
                        match cmd with
                        | MockPing     -> return { Count = state.Count + 1 }, state.Count + 1
                        | MockGetCount -> return state, state.Count
                    })
            }

        let factory =
            GrainMock.create ()
            |> GrainMock.withFSharpGrain "mock-ask-1" def

        let handle = FSharpGrain.ref<MockPingState, MockPingCmd> factory "mock-ask-1"
        let! result = handle |> FSharpGrain.ask<MockPingState, MockPingCmd, int> MockPing
        test <@ result = 1 @>
    }

[<Fact>]
let ``withFSharpGrain works with post (fire-and-forget)`` () =
    task {
        let def =
            grain {
                defaultState { Count = 0 }
                handle (fun state (cmd: MockPingCmd) ->
                    task {
                        match cmd with
                        | MockPing     -> let ns = { Count = state.Count + 1 } in return ns, box ns
                        | MockGetCount -> return state, box state
                    })
            }

        let factory =
            GrainMock.create ()
            |> GrainMock.withFSharpGrain "mock-post-1" def

        let handle = FSharpGrain.ref<MockPingState, MockPingCmd> factory "mock-post-1"
        do! handle |> FSharpGrain.post MockPing
        let! state = handle |> FSharpGrain.send MockGetCount
        test <@ state.Count = 1 @>
    }

type MockGuidState = { Name: string }
type MockGuidCmd = | SetName of string | GetName

[<Fact>]
let ``withFSharpGrainGuid creates a working GUID-keyed mock`` () =
    task {
        let def =
            grain {
                defaultState { Name = "" }
                handle (fun state (cmd: MockGuidCmd) ->
                    task {
                        match cmd with
                        | SetName n -> let ns = { Name = n } in return ns, box ns
                        | GetName   -> return state, box state
                    })
            }

        let key = Guid.NewGuid()
        let factory =
            GrainMock.create ()
            |> GrainMock.withFSharpGrainGuid key def

        let handle = FSharpGrain.refGuid<MockGuidState, MockGuidCmd> factory key
        let! state = handle |> FSharpGrain.sendGuid (SetName "Orleans")
        test <@ state.Name = "Orleans" @>
    }

type MockIntState = { Total: int64 }
type MockIntCmd = | AddAmount of int64 | GetTotal

[<Fact>]
let ``withFSharpGrainInt creates a working int-keyed mock`` () =
    task {
        let def =
            grain {
                defaultState { Total = 0L }
                handle (fun state (cmd: MockIntCmd) ->
                    task {
                        match cmd with
                        | AddAmount n -> let ns = { Total = state.Total + n } in return ns, box ns
                        | GetTotal    -> return state, box state
                    })
            }

        let factory =
            GrainMock.create ()
            |> GrainMock.withFSharpGrainInt 42L def

        let handle = FSharpGrain.refInt<MockIntState, MockIntCmd> factory 42L
        let! s1 = handle |> FSharpGrain.sendInt (AddAmount 10L)
        let! s2 = handle |> FSharpGrain.sendInt (AddAmount 5L)
        let! _ = s1.Total |> ignore |> Task.FromResult
        test <@ s2.Total = 15L @>
    }

[<Fact>]
let ``withFSharpGrain methods exist on GrainMock module`` () =
    let moduleType =
        typeof<MockGrainFactory>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainMock" && t.IsAbstract && t.IsSealed)

    let methods = moduleType.GetMethods() |> Array.map (fun m -> m.Name)
    test <@ methods |> Array.contains "withFSharpGrain" @>
    test <@ methods |> Array.contains "withFSharpGrainGuid" @>
    test <@ methods |> Array.contains "withFSharpGrainInt" @>
