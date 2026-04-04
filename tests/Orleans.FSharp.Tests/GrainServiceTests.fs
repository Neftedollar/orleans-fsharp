module Orleans.FSharp.Tests.GrainServiceTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.Hosting
open Orleans.Services

/// <summary>Tests for GrainServices module and silo config CE integration.</summary>

// --- Type signature tests ---

[<Fact>]
let ``GrainServices module exists`` () =
    let gsModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "GrainServices" && t.IsAbstract && t.IsSealed)

    test <@ gsModule.IsSome @>

[<Fact>]
let ``GrainServices.addGrainService function exists`` () =
    let gsModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainServices" && t.IsAbstract && t.IsSealed)

    let method =
        gsModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "addGrainService")

    test <@ method.IsSome @>

[<Fact>]
let ``GrainServices.addGrainService is generic`` () =
    let gsModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainServices" && t.IsAbstract && t.IsSealed)

    let method =
        gsModule.GetMethods()
        |> Array.find (fun m -> m.Name = "addGrainService")

    test <@ method.IsGenericMethod @>

[<Fact>]
let ``GrainServices.addGrainService takes ISiloBuilder and returns ISiloBuilder`` () =
    let gsModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainServices" && t.IsAbstract && t.IsSealed)

    let method =
        gsModule.GetMethods()
        |> Array.find (fun m -> m.Name = "addGrainService")

    let parameters = method.GetParameters()
    test <@ parameters.Length = 1 @>
    test <@ parameters.[0].ParameterType = typeof<ISiloBuilder> @>
    test <@ typeof<ISiloBuilder>.IsAssignableFrom(method.ReturnType) @>

// --- SiloConfig CE tests ---

[<Fact>]
let ``SiloConfig default has empty GrainServiceTypes`` () =
    let config = SiloConfig.Default
    test <@ config.GrainServiceTypes = [] @>

[<Fact>]
let ``siloConfig CE default has empty GrainServiceTypes`` () =
    let config = siloConfig { () }
    test <@ config.GrainServiceTypes = [] @>

[<Fact>]
let ``siloConfig CE addGrainService stores the type`` () =
    // Use IGrainService as a stand-in type since we cannot instantiate a real GrainService in unit tests
    let serviceType = typeof<IGrainService>

    let config = siloConfig { addGrainService serviceType }
    test <@ config.GrainServiceTypes.Length = 1 @>
    test <@ config.GrainServiceTypes.[0] = typeof<IGrainService> @>

[<Fact>]
let ``siloConfig CE multiple addGrainService accumulate`` () =
    let type1 = typeof<IGrainService>
    let type2 = typeof<IDisposable>

    let config =
        siloConfig {
            addGrainService type1
            addGrainService type2
        }

    test <@ config.GrainServiceTypes.Length = 2 @>
    test <@ config.GrainServiceTypes.[0] = type1 @>
    test <@ config.GrainServiceTypes.[1] = type2 @>

[<Fact>]
let ``siloConfig CE addGrainService composes with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addGrainService typeof<IGrainService>
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.GrainServiceTypes.Length = 1 @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``GrainServices module methods all have non-empty names`` () =
    let gsModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainServices" && t.IsAbstract && t.IsSealed)
    gsModule.GetMethods()
    |> Array.forall (fun m -> m.Name.Length > 0)

[<Property>]
let ``addGrainService stores unique types for two distinct types`` () =
    let config =
        siloConfig {
            addGrainService typeof<IGrainService>
            addGrainService typeof<IDisposable>
        }
    config.GrainServiceTypes.Length = 2
    && config.GrainServiceTypes |> List.distinct |> List.length = 2
