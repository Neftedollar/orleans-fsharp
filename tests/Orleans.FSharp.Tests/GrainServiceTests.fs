module Orleans.FSharp.Tests.GrainServiceTests

open System
open Xunit
open Swensen.Unquote
open FsCheck.Xunit
open Orleans.FSharp.Runtime
open Orleans.Services

/// <summary>Tests for the siloConfig CE addGrainService custom operation.</summary>

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
let ``addGrainService stores unique types for two distinct types`` () =
    let config =
        siloConfig {
            addGrainService typeof<IGrainService>
            addGrainService typeof<IDisposable>
        }
    config.GrainServiceTypes.Length = 2
    && config.GrainServiceTypes |> List.distinct |> List.length = 2
