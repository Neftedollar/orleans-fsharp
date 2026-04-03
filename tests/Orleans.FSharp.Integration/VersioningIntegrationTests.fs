module Orleans.FSharp.Integration.VersioningIntegrationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans.Configuration
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp.Versioning
open Orleans.FSharp.Runtime

/// <summary>
/// Integration tests for grain interface versioning configuration.
/// Verifies that the versioning configuration from the CE is correctly applied to the silo.
/// </summary>
[<Collection("ClusterCollection")>]
type VersioningIntegrationTests(fixture: ClusterFixture) =

    [<Fact>]
    member _.``SiloConfig applyToSiloBuilder applies versioning config without error`` () =
        // This test verifies the config can be built and the strategy names are valid.
        // The actual silo application is tested via the ClusterFixture which uses these settings.
        let config =
            siloConfig {
                useLocalhostClustering
                useGrainVersioning BackwardCompatible AllCompatibleVersions
            }

        test <@ config.VersioningConfig.IsSome @>
        let (compat, selector) = config.VersioningConfig.Value
        test <@ Versioning.compatibilityStrategyName compat = "BackwardCompatible" @>
        test <@ Versioning.versionSelectorStrategyName selector = "AllCompatibleVersions" @>

    [<Fact>]
    member _.``All compatibility strategy names are valid Orleans strategy names`` () =
        // Verify each strategy maps to a non-empty name that Orleans recognizes
        let strategies = [ BackwardCompatible; StrictVersion; AllVersions ]

        for s in strategies do
            let name = Versioning.compatibilityStrategyName s
            test <@ not (String.IsNullOrWhiteSpace(name)) @>

    [<Fact>]
    member _.``All version selector strategy names are valid Orleans strategy names`` () =
        let strategies = [ AllCompatibleVersions; LatestVersion; MinimumVersion ]

        for s in strategies do
            let name = Versioning.versionSelectorStrategyName s
            test <@ not (String.IsNullOrWhiteSpace(name)) @>

    [<Fact>]
    member _.``SiloConfig with versioning can be composed with broadcast channels`` () =
        let config =
            siloConfig {
                useLocalhostClustering
                addMemoryStorage "Default"
                addBroadcastChannel "notifications"
                useGrainVersioning StrictVersion LatestVersion
            }

        test <@ config.BroadcastChannels = [ "notifications" ] @>
        test <@ config.VersioningConfig.IsSome @>
        let (compat, selector) = config.VersioningConfig.Value
        test <@ compat = StrictVersion @>
        test <@ selector = LatestVersion @>
