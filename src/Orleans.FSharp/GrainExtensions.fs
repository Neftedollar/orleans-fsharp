namespace Orleans.FSharp

open Orleans
open Orleans.Runtime

/// <summary>
/// Functions for working with grain extensions.
/// Grain extensions allow adding behavior to grains without modifying them.
/// </summary>
[<RequireQualifiedAccess>]
module GrainExtension =

    /// <summary>
    /// Get a grain extension reference by casting the grain to the specified extension interface.
    /// </summary>
    /// <typeparam name="T">The grain extension interface type, which must implement <see cref="IGrainExtension"/>.</typeparam>
    /// <param name="grain">The grain to get the extension from.</param>
    /// <returns>The grain extension reference.</returns>
    let getExtension<'T when 'T :> IGrainExtension> (grain: IAddressable) : 'T =
        grain.AsReference<'T>()
