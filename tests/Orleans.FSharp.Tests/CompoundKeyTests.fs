module Orleans.FSharp.Tests.CompoundKeyTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans
open Orleans.FSharp

/// <summary>Test grain interface with GUID compound key.</summary>
type IGuidCompoundGrain =
    inherit IGrainWithGuidCompoundKey
    abstract GetInfo: unit -> Task<string>

/// <summary>Test grain interface with integer compound key.</summary>
type IIntCompoundGrain =
    inherit IGrainWithIntegerCompoundKey
    abstract GetInfo: unit -> Task<string>

/// <summary>Fake GUID compound grain for testing.</summary>
type FakeGuidCompoundGrain(guid: Guid, ext: string) =
    interface IGuidCompoundGrain with
        member _.GetInfo() = Task.FromResult($"{guid}:{ext}")

/// <summary>Fake integer compound grain for testing.</summary>
type FakeIntCompoundGrain(key: int64, ext: string) =
    interface IIntCompoundGrain with
        member _.GetInfo() = Task.FromResult($"{key}:{ext}")

// --- CompoundGuidKey tests ---

[<Fact>]
let ``GrainRef stores CompoundGuidKey correctly`` () =
    let guid = Guid.Parse("12345678-1234-1234-1234-123456789abc")
    let ext = "tenant-a"

    let ref: GrainRef<IGuidCompoundGrain, CompoundGuidKey> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = { Guid = guid; Extension = ext }
            Grain = FakeGuidCompoundGrain(guid, ext)
        }

    let key = GrainRef.key ref
    test <@ key.Guid = guid @>
    test <@ key.Extension = ext @>

[<Fact>]
let ``GrainRef.invoke calls grain method via GUID compound ref`` () =
    task {
        let guid = Guid.Parse("12345678-1234-1234-1234-123456789abc")
        let ext = "region-1"

        let ref: GrainRef<IGuidCompoundGrain, CompoundGuidKey> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = { Guid = guid; Extension = ext }
                Grain = FakeGuidCompoundGrain(guid, ext)
            }

        let! result = GrainRef.invoke ref (fun g -> g.GetInfo())
        test <@ result = $"{guid}:region-1" @>
    }

[<Fact>]
let ``CompoundGuidKey record equality works`` () =
    let guid = Guid.NewGuid()
    let k1 = { Guid = guid; Extension = "a" }
    let k2 = { Guid = guid; Extension = "a" }
    let k3 = { Guid = guid; Extension = "b" }

    test <@ k1 = k2 @>
    test <@ k1 <> k3 @>

// --- CompoundIntKey tests ---

[<Fact>]
let ``GrainRef stores CompoundIntKey correctly`` () =
    let key = 42L
    let ext = "partition-x"

    let ref: GrainRef<IIntCompoundGrain, CompoundIntKey> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = { Int = key; Extension = ext }
            Grain = FakeIntCompoundGrain(key, ext)
        }

    let compoundKey = GrainRef.key ref
    test <@ compoundKey.Int = 42L @>
    test <@ compoundKey.Extension = ext @>

[<Fact>]
let ``GrainRef.invoke calls grain method via int compound ref`` () =
    task {
        let key = 99L
        let ext = "shard-2"

        let ref: GrainRef<IIntCompoundGrain, CompoundIntKey> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = { Int = key; Extension = ext }
                Grain = FakeIntCompoundGrain(key, ext)
            }

        let! result = GrainRef.invoke ref (fun g -> g.GetInfo())
        test <@ result = "99:shard-2" @>
    }

[<Fact>]
let ``CompoundIntKey record equality works`` () =
    let k1 = { Int = 1L; Extension = "a" }
    let k2 = { Int = 1L; Extension = "a" }
    let k3 = { Int = 1L; Extension = "b" }
    let k4 = { Int = 2L; Extension = "a" }

    test <@ k1 = k2 @>
    test <@ k1 <> k3 @>
    test <@ k1 <> k4 @>

[<Fact>]
let ``GrainRef.unwrap works with compound GUID key`` () =
    task {
        let guid = Guid.NewGuid()
        let ext = "test"

        let ref: GrainRef<IGuidCompoundGrain, CompoundGuidKey> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = { Guid = guid; Extension = ext }
                Grain = FakeGuidCompoundGrain(guid, ext)
            }

        let grain = GrainRef.unwrap ref
        let! result = grain.GetInfo()
        test <@ result = $"{guid}:test" @>
    }

[<Fact>]
let ``GrainRef.unwrap works with compound int key`` () =
    task {
        let ref: GrainRef<IIntCompoundGrain, CompoundIntKey> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = { Int = 7L; Extension = "zone" }
                Grain = FakeIntCompoundGrain(7L, "zone")
            }

        let grain = GrainRef.unwrap ref
        let! result = grain.GetInfo()
        test <@ result = "7:zone" @>
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``CompoundGuidKey roundtrips Guid for any GUID and extension`` (ext: NonNull<string>) =
    let guid = Guid.NewGuid()
    let key = { Guid = guid; Extension = ext.Get }
    key.Guid = guid && key.Extension = ext.Get

[<Property>]
let ``CompoundIntKey roundtrips Int and Extension for any values`` (n: int64) (ext: NonNull<string>) =
    let key = { Int = n; Extension = ext.Get }
    key.Int = n && key.Extension = ext.Get

[<Property>]
let ``CompoundGuidKey equality: same components are equal`` (ext: NonNull<string>) =
    let guid = Guid.NewGuid()
    let k1 = { Guid = guid; Extension = ext.Get }
    let k2 = { Guid = guid; Extension = ext.Get }
    k1 = k2

[<Property>]
let ``CompoundIntKey equality: different Int values produce different keys`` (n1: int64) (n2: int64) (ext: NonNull<string>) =
    n1 = n2 || { Int = n1; Extension = ext.Get } <> { Int = n2; Extension = ext.Get }

[<Property>]
let ``GrainRef.key roundtrips CompoundIntKey for any key and extension`` (n: int64) (ext: NonNull<string>) =
    let ref: GrainRef<IIntCompoundGrain, CompoundIntKey> =
        {
            Factory = Unchecked.defaultof<IGrainFactory>
            Key = { Int = n; Extension = ext.Get }
            Grain = FakeIntCompoundGrain(n, ext.Get)
        }
    GrainRef.key ref = { Int = n; Extension = ext.Get }
