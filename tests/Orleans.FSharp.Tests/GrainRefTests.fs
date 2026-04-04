module Orleans.FSharp.Tests.GrainRefTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans
open Orleans.FSharp

/// <summary>Test grain interface with string key.</summary>
type IStringGrain =
    inherit IGrainWithStringKey
    abstract Echo: string -> Task<string>

/// <summary>Test grain interface with GUID key.</summary>
type IGuidGrain =
    inherit IGrainWithGuidKey
    abstract GetId: unit -> Task<Guid>

/// <summary>Test grain interface with integer key.</summary>
type IInt64Grain =
    inherit IGrainWithIntegerKey
    abstract Add: int -> Task<int>

/// <summary>Fake string grain for testing invoke and unwrap.</summary>
type FakeStringGrain(key: string) =
    interface IStringGrain with
        member _.Echo(msg) = Task.FromResult($"{key}:{msg}")

/// <summary>Fake GUID grain for testing invoke and unwrap.</summary>
type FakeGuidGrain(id: Guid) =
    interface IGuidGrain with
        member _.GetId() = Task.FromResult(id)

/// <summary>Fake int64 grain for testing invoke and unwrap.</summary>
type FakeInt64Grain(key: int64) =
    interface IInt64Grain with
        member _.Add(n) = Task.FromResult(int key + n)

// Since GrainRef has a `private` (internal) constructor and the test project
// has InternalsVisibleTo, we can construct GrainRef values directly for unit tests.
// This avoids needing a full IGrainFactory mock.

[<Fact>]
let ``GrainRef stores string key correctly`` () =
    let ref: GrainRef<IStringGrain, string> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = "my-key"
            Grain = FakeStringGrain("my-key")
        }

    test <@ GrainRef.key ref = "my-key" @>

[<Fact>]
let ``GrainRef stores Guid key correctly`` () =
    let guid = Guid.Parse("12345678-1234-1234-1234-123456789abc")

    let ref: GrainRef<IGuidGrain, Guid> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = guid
            Grain = FakeGuidGrain(guid)
        }

    test <@ GrainRef.key ref = guid @>

[<Fact>]
let ``GrainRef stores int64 key correctly`` () =
    let ref: GrainRef<IInt64Grain, int64> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = 42L
            Grain = FakeInt64Grain(42L)
        }

    test <@ GrainRef.key ref = 42L @>

[<Fact>]
let ``GrainRef.invoke calls grain method via string ref`` () =
    task {
        let ref: GrainRef<IStringGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "test-key"
                Grain = FakeStringGrain("test-key")
            }

        let! result = GrainRef.invoke ref (fun g -> g.Echo("hello"))
        test <@ result = "test-key:hello" @>
    }

[<Fact>]
let ``GrainRef.invoke calls grain method via Guid ref`` () =
    task {
        let guid = Guid.Parse("12345678-1234-1234-1234-123456789abc")

        let ref: GrainRef<IGuidGrain, Guid> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = guid
                Grain = FakeGuidGrain(guid)
            }

        let! result = GrainRef.invoke ref (fun g -> g.GetId())
        test <@ result = guid @>
    }

[<Fact>]
let ``GrainRef.invoke calls grain method via int64 ref`` () =
    task {
        let ref: GrainRef<IInt64Grain, int64> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = 10L
                Grain = FakeInt64Grain(10L)
            }

        let! result = GrainRef.invoke ref (fun g -> g.Add(5))
        test <@ result = 15 @>
    }

[<Fact>]
let ``GrainRef.unwrap returns the underlying grain proxy`` () =
    task {
        let ref: GrainRef<IStringGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "unwrap-key"
                Grain = FakeStringGrain("unwrap-key")
            }

        let grain = GrainRef.unwrap ref
        let! result = grain.Echo("direct")
        test <@ result = "unwrap-key:direct" @>
    }

[<Fact>]
let ``GrainRef.key returns correct key for all ref types`` () =
    let strRef: GrainRef<IStringGrain, string> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = "abc"
            Grain = FakeStringGrain("abc")
        }

    let guidVal = Guid.NewGuid()

    let guidRef: GrainRef<IGuidGrain, Guid> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = guidVal
            Grain = FakeGuidGrain(guidVal)
        }

    let intRef: GrainRef<IInt64Grain, int64> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = 99L
            Grain = FakeInt64Grain(99L)
        }

    test <@ GrainRef.key strRef = "abc" @>
    test <@ GrainRef.key guidRef = guidVal @>
    test <@ GrainRef.key intRef = 99L @>

[<Fact>]
let ``Multiple GrainRefs to same key return independent references`` () =
    task {
        let ref1: GrainRef<IStringGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "same-key"
                Grain = FakeStringGrain("same-key")
            }

        let ref2: GrainRef<IStringGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "same-key"
                Grain = FakeStringGrain("same-key")
            }

        let! r1 = GrainRef.invoke ref1 (fun g -> g.Echo("a"))
        let! r2 = GrainRef.invoke ref2 (fun g -> g.Echo("b"))
        test <@ r1 = "same-key:a" @>
        test <@ r2 = "same-key:b" @>
    }

[<Fact>]
let ``GrainRef is a struct type`` () =
    test <@ typeof<GrainRef<IStringGrain, string>>.IsValueType @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``GrainRef.key round-trips any string key`` (key: NonNull<string>) =
    let ref: GrainRef<IStringGrain, string> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = key.Get
            Grain = FakeStringGrain(key.Get)
        }
    GrainRef.key ref = key.Get

[<Property>]
let ``GrainRef.key round-trips any int64 key`` (key: int64) =
    let ref: GrainRef<IInt64Grain, int64> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = key
            Grain = FakeInt64Grain(key)
        }
    GrainRef.key ref = key

[<Property>]
let ``GrainRef.invoke passes message through for any string key and payload`` (key: NonNull<string>) (msg: NonNull<string>) =
    let ref: GrainRef<IStringGrain, string> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = key.Get
            Grain = FakeStringGrain(key.Get)
        }
    let result = (GrainRef.invoke ref (fun g -> g.Echo(msg.Get))).GetAwaiter().GetResult()
    result = $"{key.Get}:{msg.Get}"

[<Property>]
let ``GrainRef.invoke add is correct for any key and delta`` (key: int64) (delta: int) =
    let ref: GrainRef<IInt64Grain, int64> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = key
            Grain = FakeInt64Grain(key)
        }
    let result = (GrainRef.invoke ref (fun g -> g.Add(delta))).GetAwaiter().GetResult()
    result = int key + delta

[<Property>]
let ``GrainRef.unwrap returns grain that responds to calls for any string key`` (key: NonNull<string>) =
    let ref: GrainRef<IStringGrain, string> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = key.Get
            Grain = FakeStringGrain(key.Get)
        }
    let grain = GrainRef.unwrap ref
    let result = grain.Echo("probe").GetAwaiter().GetResult()
    result = $"{key.Get}:probe"
