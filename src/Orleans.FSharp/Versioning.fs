namespace Orleans.FSharp.Versioning

open Orleans.Configuration
open Orleans.Versions.Compatibility
open Orleans.Versions.Selector

/// <summary>
/// Versioning compatibility strategies that determine which grain interface versions
/// can communicate with each other during rolling upgrades.
/// </summary>
type CompatibilityStrategy =
    /// <summary>Older versions can call newer versions, but not vice versa. This is the Orleans default.</summary>
    | BackwardCompatible
    /// <summary>Only the exact same version can communicate. No cross-version calls allowed.</summary>
    | StrictVersion
    /// <summary>All versions are compatible and can freely communicate with each other.</summary>
    | AllVersions

/// <summary>
/// Version selector strategies that determine which version of a grain is activated
/// when multiple compatible versions exist in the cluster.
/// </summary>
type VersionSelectorStrategy =
    /// <summary>Randomly select from all compatible versions (weighted by silo count). This is the Orleans default.</summary>
    | AllCompatibleVersions
    /// <summary>Always activate the latest (highest) compatible version.</summary>
    | LatestVersion
    /// <summary>Always activate the minimum (lowest) compatible version.</summary>
    | MinimumVersion

/// <summary>
/// Functions for configuring grain interface versioning behavior.
/// </summary>
[<RequireQualifiedAccess>]
module Versioning =

    /// <summary>
    /// Converts a <see cref="CompatibilityStrategy"/> to the Orleans strategy name string
    /// used by <see cref="GrainVersioningOptions.DefaultCompatibilityStrategy"/>.
    /// </summary>
    /// <param name="strategy">The compatibility strategy.</param>
    /// <returns>The Orleans strategy name string.</returns>
    let compatibilityStrategyName (strategy: CompatibilityStrategy) : string =
        match strategy with
        | BackwardCompatible -> nameof BackwardCompatible
        | StrictVersion -> nameof StrictVersionCompatible
        | AllVersions -> nameof AllVersionsCompatible

    /// <summary>
    /// Converts a <see cref="VersionSelectorStrategy"/> to the Orleans strategy name string
    /// used by <see cref="GrainVersioningOptions.DefaultVersionSelectorStrategy"/>.
    /// </summary>
    /// <param name="selector">The version selector strategy.</param>
    /// <returns>The Orleans strategy name string.</returns>
    let versionSelectorStrategyName (selector: VersionSelectorStrategy) : string =
        match selector with
        | AllCompatibleVersions -> nameof Orleans.Versions.Selector.AllCompatibleVersions
        | LatestVersion -> nameof Orleans.Versions.Selector.LatestVersion
        | MinimumVersion -> nameof Orleans.Versions.Selector.MinimumVersion
