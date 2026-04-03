namespace Orleans.FSharp

open System
open Orleans.Hosting
open Orleans.Services

/// <summary>
/// Functions for registering GrainService types with an Orleans silo.
/// GrainServices are system grains that run on every silo and are useful for background processing.
/// </summary>
[<RequireQualifiedAccess>]
module GrainServices =

    /// <summary>
    /// Register a GrainService type with the silo.
    /// GrainServices run on every silo and are useful for background processing.
    /// </summary>
    /// <typeparam name="T">The GrainService implementation type, which must implement <see cref="IGrainService"/>.</typeparam>
    /// <returns>A function that configures the ISiloBuilder to include the grain service.</returns>
    let addGrainService<'T when 'T :> Orleans.Runtime.GrainService> (siloBuilder: ISiloBuilder) : ISiloBuilder =
        siloBuilder.AddGrainService<'T>()
