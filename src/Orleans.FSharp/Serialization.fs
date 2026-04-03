namespace Orleans.FSharp

open System.Text.Json
open System.Text.Json.Serialization

/// <summary>
/// Helpers for configuring F# type serialization within Orleans.
/// Provides pre-configured JSON serialization options using FSharp.SystemTextJson
/// and functions to register custom converters with Orleans serializer builders.
/// </summary>
[<RequireQualifiedAccess>]
module Serialization =

    /// <summary>
    /// Pre-configured JsonSerializerOptions instance for Orleans F# type serialization.
    /// Uses FSharp.SystemTextJson with adjacent tag encoding for discriminated unions,
    /// named fields, and unwrapped fieldless tags. This is the same as
    /// <see cref="FSharpJson.serializerOptions"/> re-exported for discoverability
    /// in serialization-focused code.
    /// </summary>
    let fsharpJsonOptions: JsonSerializerOptions =
        FSharpJson.serializerOptions

    /// <summary>
    /// Registers the FSharp.SystemTextJson converter on the given JsonSerializerOptions.
    /// Idempotent: if the converter is already registered, this is a no-op.
    /// </summary>
    /// <param name="options">The JsonSerializerOptions to configure.</param>
    /// <returns>The same options instance with the F# converter registered.</returns>
    let addFSharpConverters (options: JsonSerializerOptions) : JsonSerializerOptions =
        let alreadyRegistered =
            options.Converters
            |> Seq.exists (fun c -> c :? JsonFSharpConverter)

        if not alreadyRegistered then
            let fsharpOptions =
                JsonFSharpOptions
                    .Default()
                    .WithUnionAdjacentTag()
                    .WithUnionNamedFields()
                    .WithUnionUnwrapFieldlessTags()

            let converter = JsonFSharpConverter(fsharpOptions)
            options.Converters.Add(converter)

        options

    /// <summary>
    /// Creates a new JsonSerializerOptions instance configured for F# types with
    /// the specified additional converters. Starts from the pre-configured
    /// <see cref="fsharpJsonOptions"/> and adds each converter.
    /// </summary>
    /// <param name="converters">Additional JsonConverter instances to register.</param>
    /// <returns>A new JsonSerializerOptions with F# support and the additional converters.</returns>
    let withConverters (converters: JsonConverter list) : JsonSerializerOptions =
        let options = JsonSerializerOptions(fsharpJsonOptions)

        for converter in converters do
            options.Converters.Add(converter)

        options
