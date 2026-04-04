module Orleans.FSharp.Tests.VersioningTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Versioning
open Orleans.FSharp.Runtime

// --- CompatibilityStrategy tests ---

[<Fact>]
let ``compatibilityStrategyName maps BackwardCompatible correctly`` () =
    let name = Versioning.compatibilityStrategyName BackwardCompatible
    test <@ name = "BackwardCompatible" @>

[<Fact>]
let ``compatibilityStrategyName maps StrictVersion correctly`` () =
    let name = Versioning.compatibilityStrategyName StrictVersion
    test <@ name = "StrictVersionCompatible" @>

[<Fact>]
let ``compatibilityStrategyName maps AllVersions correctly`` () =
    let name = Versioning.compatibilityStrategyName AllVersions
    test <@ name = "AllVersionsCompatible" @>

// --- VersionSelectorStrategy tests ---

[<Fact>]
let ``versionSelectorStrategyName maps AllCompatibleVersions correctly`` () =
    let name = Versioning.versionSelectorStrategyName AllCompatibleVersions
    test <@ name = "AllCompatibleVersions" @>

[<Fact>]
let ``versionSelectorStrategyName maps LatestVersion correctly`` () =
    let name = Versioning.versionSelectorStrategyName LatestVersion
    test <@ name = "LatestVersion" @>

[<Fact>]
let ``versionSelectorStrategyName maps MinimumVersion correctly`` () =
    let name = Versioning.versionSelectorStrategyName MinimumVersion
    test <@ name = "MinimumVersion" @>

// --- CompatibilityStrategy DU tests ---

[<Fact>]
let ``CompatibilityStrategy is a discriminated union with 3 cases`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<CompatibilityStrategy>)

    test <@ cases.Length = 3 @>
    let names = cases |> Array.map (fun c -> c.Name) |> Array.sort
    test <@ names = [| "AllVersions"; "BackwardCompatible"; "StrictVersion" |] @>

[<Fact>]
let ``VersionSelectorStrategy is a discriminated union with 3 cases`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<VersionSelectorStrategy>)

    test <@ cases.Length = 3 @>
    let names = cases |> Array.map (fun c -> c.Name) |> Array.sort
    test <@ names = [| "AllCompatibleVersions"; "LatestVersion"; "MinimumVersion" |] @>

// --- SiloConfig CE versioning tests ---

[<Fact>]
let ``siloConfig CE default has no versioning config`` () =
    let config = siloConfig { () }
    test <@ config.VersioningConfig.IsNone @>

[<Fact>]
let ``siloConfig CE useGrainVersioning stores config`` () =
    let config =
        siloConfig {
            useGrainVersioning BackwardCompatible AllCompatibleVersions
        }

    test <@ config.VersioningConfig.IsSome @>
    let (compat, selector) = config.VersioningConfig.Value
    test <@ compat = BackwardCompatible @>
    test <@ selector = AllCompatibleVersions @>

[<Fact>]
let ``siloConfig CE useGrainVersioning with StrictVersion and LatestVersion`` () =
    let config =
        siloConfig {
            useGrainVersioning StrictVersion LatestVersion
        }

    let (compat, selector) = config.VersioningConfig.Value
    test <@ compat = StrictVersion @>
    test <@ selector = LatestVersion @>

[<Fact>]
let ``siloConfig CE useGrainVersioning with AllVersions and MinimumVersion`` () =
    let config =
        siloConfig {
            useGrainVersioning AllVersions MinimumVersion
        }

    let (compat, selector) = config.VersioningConfig.Value
    test <@ compat = AllVersions @>
    test <@ selector = MinimumVersion @>

[<Fact>]
let ``siloConfig CE composes versioning with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            useGrainVersioning BackwardCompatible AllCompatibleVersions
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.VersioningConfig.IsSome @>

// --- SiloConfig CE broadcast channel tests ---

[<Fact>]
let ``siloConfig CE default has no broadcast channels`` () =
    let config = siloConfig { () }
    test <@ config.BroadcastChannels = [] @>

[<Fact>]
let ``siloConfig CE addBroadcastChannel stores channel name`` () =
    let config =
        siloConfig {
            addBroadcastChannel "notifications"
        }

    test <@ config.BroadcastChannels = [ "notifications" ] @>

[<Fact>]
let ``siloConfig CE multiple addBroadcastChannel calls accumulate`` () =
    let config =
        siloConfig {
            addBroadcastChannel "notifications"
            addBroadcastChannel "events"
        }

    test <@ config.BroadcastChannels = [ "notifications"; "events" ] @>

[<Fact>]
let ``siloConfig CE composes broadcast channels with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addBroadcastChannel "notifications"
            useGrainVersioning BackwardCompatible AllCompatibleVersions
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.BroadcastChannels = [ "notifications" ] @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``compatibilityStrategyName is deterministic for any case`` (strategy: CompatibilityStrategy) =
    Versioning.compatibilityStrategyName strategy = Versioning.compatibilityStrategyName strategy

[<Property>]
let ``compatibilityStrategyName always returns a non-empty string`` (strategy: CompatibilityStrategy) =
    let name = Versioning.compatibilityStrategyName strategy
    name.Length > 0

[<Property>]
let ``versionSelectorStrategyName is deterministic for any case`` (selector: VersionSelectorStrategy) =
    Versioning.versionSelectorStrategyName selector = Versioning.versionSelectorStrategyName selector

[<Property>]
let ``versionSelectorStrategyName always returns a non-empty string`` (selector: VersionSelectorStrategy) =
    let name = Versioning.versionSelectorStrategyName selector
    name.Length > 0

[<Property>]
let ``addBroadcastChannel stores the correct channel name for any non-whitespace input`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addBroadcastChannel name.Get }
        config.BroadcastChannels = [ name.Get ])

[<Property>]
let ``useGrainVersioning always stores both strategy fields`` (compat: CompatibilityStrategy) (selector: VersionSelectorStrategy) =
    let config = siloConfig { useGrainVersioning compat selector }
    config.VersioningConfig.IsSome &&
    config.VersioningConfig.Value = (compat, selector)
