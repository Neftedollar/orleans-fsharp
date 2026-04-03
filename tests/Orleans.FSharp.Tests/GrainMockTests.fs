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
