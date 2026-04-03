namespace Orleans.FSharp

open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open Orleans

/// <summary>
/// Marker type used to identify the Orleans.FSharp assembly for reflection and code generation.
/// </summary>
type AssemblyMarker =
    class
    end

/// <summary>
/// Reexports of core Orleans grain interface types for convenient access from F# code.
/// </summary>
[<AutoOpen>]
module OrleansTypes =
    /// <summary>Alias for Orleans IGrain interface.</summary>
    type IGrain = Orleans.IGrain

    /// <summary>Alias for Orleans IGrainWithStringKey interface.</summary>
    type IGrainWithStringKey = Orleans.IGrainWithStringKey

    /// <summary>Alias for Orleans IGrainWithGuidKey interface.</summary>
    type IGrainWithGuidKey = Orleans.IGrainWithGuidKey

    /// <summary>Alias for Orleans IGrainWithIntegerKey interface.</summary>
    type IGrainWithIntegerKey = Orleans.IGrainWithIntegerKey

    /// <summary>Alias for Orleans IGrainWithGuidCompoundKey interface.</summary>
    type IGrainWithGuidCompoundKey = Orleans.IGrainWithGuidCompoundKey

    /// <summary>Alias for Orleans IGrainWithIntegerCompoundKey interface.</summary>
    type IGrainWithIntegerCompoundKey = Orleans.IGrainWithIntegerCompoundKey

    /// <summary>Alias for Orleans IGrainFactory interface.</summary>
    type IGrainFactory = Orleans.IGrainFactory

/// <summary>
/// Utility functions for composing Task-based operations with Result values.
/// All functions use Task (not Async) to align with Orleans runtime conventions.
/// </summary>
[<RequireQualifiedAccess>]
module TaskHelpers =

    /// <summary>
    /// Wraps a value in a Task&lt;Result&lt;'T, 'E&gt;&gt; as an Ok result.
    /// </summary>
    /// <param name="value">The value to wrap.</param>
    /// <returns>A Task containing Ok of the value.</returns>
    let taskResult (value: 'T) : Task<Result<'T, 'E>> =
        Task.FromResult(Ok value)

    /// <summary>
    /// Wraps an error value in a Task&lt;Result&lt;'T, 'E&gt;&gt; as an Error result.
    /// </summary>
    /// <param name="error">The error value to wrap.</param>
    /// <returns>A Task containing Error of the value.</returns>
    let taskError (error: 'E) : Task<Result<'T, 'E>> =
        Task.FromResult(Error error)

    /// <summary>
    /// Maps a function over the Ok value of a Task&lt;Result&lt;'T, 'E&gt;&gt;.
    /// If the result is Error, the error is propagated unchanged.
    /// </summary>
    /// <param name="f">The mapping function to apply to the Ok value.</param>
    /// <param name="taskRes">The Task containing the Result to map over.</param>
    /// <returns>A Task containing the mapped Result.</returns>
    let taskMap (f: 'T -> 'U) (taskRes: Task<Result<'T, 'E>>) : Task<Result<'U, 'E>> =
        task {
            let! result = taskRes

            return
                match result with
                | Ok v -> Ok(f v)
                | Error e -> Error e
        }

    /// <summary>
    /// Binds a function over the Ok value of a Task&lt;Result&lt;'T, 'E&gt;&gt;.
    /// The function itself returns a Task&lt;Result&lt;'U, 'E&gt;&gt;, enabling chaining of Task-based Result operations.
    /// If the result is Error, the error is propagated unchanged.
    /// </summary>
    /// <param name="f">The binding function to apply to the Ok value.</param>
    /// <param name="taskRes">The Task containing the Result to bind over.</param>
    /// <returns>A Task containing the bound Result.</returns>
    let taskBind (f: 'T -> Task<Result<'U, 'E>>) (taskRes: Task<Result<'T, 'E>>) : Task<Result<'U, 'E>> =
        task {
            let! result = taskRes

            return!
                match result with
                | Ok v -> f v
                | Error e -> Task.FromResult(Error e)
        }

/// <summary>
/// Provides F#-idiomatic access to Orleans Immutable&lt;T&gt; for zero-copy grain argument passing.
/// Opening this module (via AutoOpen) exposes the Immutable type alias and helper functions.
/// </summary>
[<AutoOpen>]
module ImmutableTypes =
    /// <summary>Alias for Orleans.Concurrency.Immutable&lt;'T&gt;. Wraps a value to indicate it will not be modified,
    /// allowing Orleans to skip serialization copies for improved performance.</summary>
    type Immutable<'T> = Orleans.Concurrency.Immutable<'T>

    /// <summary>
    /// Wrap a value as Immutable for zero-copy grain argument passing.
    /// </summary>
    /// <param name="value">The value to wrap.</param>
    /// <typeparam name="'T">The type of the value.</typeparam>
    /// <returns>An Immutable wrapper around the value.</returns>
    let immutable (value: 'T) = Immutable<'T>(value)

    /// <summary>
    /// Unwrap an Immutable value.
    /// </summary>
    /// <param name="imm">The Immutable wrapper.</param>
    /// <typeparam name="'T">The type of the wrapped value.</typeparam>
    /// <returns>The unwrapped value.</returns>
    let unwrapImmutable (imm: Immutable<'T>) = imm.Value

/// <summary>
/// Re-exports FsToolkit.ErrorHandling for convenient access from Orleans.FSharp consumers.
/// Opening this module provides taskResult { }, result { }, option { }, and validation { } CEs
/// for composable error handling in grain handlers.
/// </summary>
/// <example>
/// <code>
/// open Orleans.FSharp
/// open FsToolkit.ErrorHandling
///
/// let handle state cmd = taskResult {
///     let! validated = validateCommand cmd
///     let! newState = applyTransition state validated
///     return newState
/// }
/// </code>
/// </example>
module FsToolkitReexport =
    // FsToolkit.ErrorHandling is a transitive dependency of Orleans.FSharp.
    // Consumers just need: open FsToolkit.ErrorHandling
    // This module exists to document the dependency and ensure it's not trimmed.
    let private _ensureFsToolkitLoaded =
        typeof<FsToolkit.ErrorHandling.TaskResultBuilder>.Assembly

/// <summary>
/// Pre-configured System.Text.Json serialization options for F# types.
/// Uses FSharp.SystemTextJson to support discriminated unions, records,
/// options, value options, lists, maps, and other F# types.
/// </summary>
[<RequireQualifiedAccess>]
module FSharpJson =

    /// <summary>
    /// The FSharp.SystemTextJson options configured for Orleans F# type serialization.
    /// Supports DU, Record, Option, ValueOption, list, set, map, and tuple types.
    /// Uses adjacent tag encoding for unions with named fields.
    /// </summary>
    let options =
        JsonFSharpOptions
            .Default()
            .WithUnionAdjacentTag()
            .WithUnionNamedFields()
            .WithUnionUnwrapFieldlessTags()

    /// <summary>
    /// A pre-configured JsonSerializerOptions instance with the FSharp.SystemTextJson converter registered.
    /// This is the recommended options instance for serializing F# types with System.Text.Json.
    /// </summary>
    let serializerOptions: JsonSerializerOptions =
        options.ToJsonSerializerOptions()
