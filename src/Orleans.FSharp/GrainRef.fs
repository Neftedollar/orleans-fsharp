namespace Orleans.FSharp

open System
open System.Threading.Tasks
open Orleans

/// <summary>
/// Represents a compound key consisting of a GUID and a string extension.
/// Used with grains implementing IGrainWithGuidCompoundKey.
/// </summary>
type CompoundGuidKey =
    {
        /// <summary>The GUID part of the compound key.</summary>
        Guid: Guid
        /// <summary>The string extension part of the compound key.</summary>
        Extension: string
    }

/// <summary>
/// Represents a compound key consisting of an int64 and a string extension.
/// Used with grains implementing IGrainWithIntegerCompoundKey.
/// </summary>
type CompoundIntKey =
    {
        /// <summary>The int64 part of the compound key.</summary>
        Int: int64
        /// <summary>The string extension part of the compound key.</summary>
        Extension: string
    }

/// <summary>
/// A type-safe reference to an Orleans grain, parameterized by the grain interface and key type.
/// Wraps an IGrainFactory, the grain key, and the resolved grain proxy.
/// </summary>
/// <typeparam name="'TInterface">The grain interface type (must inherit from an appropriate IGrainWithXKey).</typeparam>
/// <typeparam name="'TKey">The type of the grain's primary key (string, Guid, or int64).</typeparam>
[<Struct>]
type GrainRef<'TInterface, 'TKey> =
    internal
        {
            /// <summary>The grain factory used to create the grain reference.</summary>
            Factory: IGrainFactory
            /// <summary>The primary key of the grain.</summary>
            Key: 'TKey
            /// <summary>The resolved grain proxy for invoking methods.</summary>
            Grain: 'TInterface
        }

/// <summary>
/// Functions for creating and interacting with type-safe grain references.
/// Provides factory functions constrained to the appropriate Orleans key interface,
/// and an invoke function for calling grain methods through the reference.
/// </summary>
[<RequireQualifiedAccess>]
module GrainRef =

    /// <summary>
    /// Gets a reference to a grain by string key.
    /// The grain interface must inherit from IGrainWithStringKey.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The string primary key of the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithStringKey.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let ofString<'TInterface when 'TInterface :> IGrainWithStringKey>
        (factory: IGrainFactory)
        (key: string)
        : GrainRef<'TInterface, string> =
        let grain = factory.GetGrain<'TInterface>(key)

        {
            Factory = factory
            Key = key
            Grain = grain
        }

    /// <summary>
    /// Gets a reference to a grain by GUID key.
    /// The grain interface must inherit from IGrainWithGuidKey.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The GUID primary key of the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithGuidKey.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let ofGuid<'TInterface when 'TInterface :> IGrainWithGuidKey>
        (factory: IGrainFactory)
        (key: Guid)
        : GrainRef<'TInterface, Guid> =
        let grain = factory.GetGrain<'TInterface>(key)

        {
            Factory = factory
            Key = key
            Grain = grain
        }

    /// <summary>
    /// Gets a reference to a grain by int64 key.
    /// The grain interface must inherit from IGrainWithIntegerKey.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The int64 primary key of the grain.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithIntegerKey.</typeparam>
    /// <returns>A type-safe grain reference.</returns>
    let ofInt64<'TInterface when 'TInterface :> IGrainWithIntegerKey>
        (factory: IGrainFactory)
        (key: int64)
        : GrainRef<'TInterface, int64> =
        let grain = factory.GetGrain<'TInterface>(key)

        {
            Factory = factory
            Key = key
            Grain = grain
        }

    /// <summary>
    /// Gets a reference to a grain by compound GUID+string key.
    /// The grain interface must inherit from IGrainWithGuidCompoundKey.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="guid">The GUID part of the compound key.</param>
    /// <param name="ext">The string extension part of the compound key.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithGuidCompoundKey.</typeparam>
    /// <returns>A type-safe grain reference with a CompoundGuidKey.</returns>
    let ofGuidCompound<'TInterface when 'TInterface :> IGrainWithGuidCompoundKey>
        (factory: IGrainFactory)
        (guid: Guid)
        (ext: string)
        : GrainRef<'TInterface, CompoundGuidKey> =
        let grain = factory.GetGrain<'TInterface>(guid, ext)

        {
            Factory = factory
            Key = { Guid = guid; Extension = ext }
            Grain = grain
        }

    /// <summary>
    /// Gets a reference to a grain by compound int64+string key.
    /// The grain interface must inherit from IGrainWithIntegerCompoundKey.
    /// </summary>
    /// <param name="factory">The Orleans grain factory.</param>
    /// <param name="key">The int64 part of the compound key.</param>
    /// <param name="ext">The string extension part of the compound key.</param>
    /// <typeparam name="'TInterface">The grain interface type, constrained to IGrainWithIntegerCompoundKey.</typeparam>
    /// <returns>A type-safe grain reference with a CompoundIntKey.</returns>
    let ofIntCompound<'TInterface when 'TInterface :> IGrainWithIntegerCompoundKey>
        (factory: IGrainFactory)
        (key: int64)
        (ext: string)
        : GrainRef<'TInterface, CompoundIntKey> =
        let grain = factory.GetGrain<'TInterface>(key, ext)

        {
            Factory = factory
            Key = { Int = key; Extension = ext }
            Grain = grain
        }

    /// <summary>
    /// Invokes a method on the referenced grain.
    /// The call function receives the grain proxy and should return a Task of the result.
    /// </summary>
    /// <param name="ref">The grain reference to invoke the method on.</param>
    /// <param name="call">A function that takes the grain proxy and returns a Task of the result.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <typeparam name="'TKey">The grain key type.</typeparam>
    /// <typeparam name="'Result">The return type of the grain method call.</typeparam>
    /// <returns>A Task containing the result of the grain method call.</returns>
    let invoke<'TInterface, 'TKey, 'Result>
        (ref: GrainRef<'TInterface, 'TKey>)
        (call: 'TInterface -> Task<'Result>)
        : Task<'Result> =
        call ref.Grain

    /// <summary>
    /// Gets the underlying grain proxy for advanced scenarios where direct access is needed.
    /// Prefer using <see cref="invoke"/> for standard grain method calls.
    /// </summary>
    /// <param name="ref">The grain reference to unwrap.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <typeparam name="'TKey">The grain key type.</typeparam>
    /// <returns>The underlying grain proxy.</returns>
    let unwrap<'TInterface, 'TKey> (ref: GrainRef<'TInterface, 'TKey>) : 'TInterface = ref.Grain

    /// <summary>
    /// Gets the primary key of the grain reference.
    /// </summary>
    /// <param name="ref">The grain reference.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <typeparam name="'TKey">The grain key type.</typeparam>
    /// <returns>The primary key value.</returns>
    let key<'TInterface, 'TKey> (ref: GrainRef<'TInterface, 'TKey>) : 'TKey = ref.Key

    /// <summary>
    /// Invokes a one-way (fire-and-forget) call on a grain.
    /// The caller does not wait for the call to complete on the grain.
    /// For Orleans to treat the call as one-way, the interface method must be decorated
    /// with the [OneWay] attribute in the C# CodeGen interface.
    /// </summary>
    /// <param name="ref">The grain reference to invoke the method on.</param>
    /// <param name="call">A function that takes the grain proxy and returns a Task.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <typeparam name="'TKey">The grain key type.</typeparam>
    /// <returns>A Task that completes when the message is enqueued (not when the grain finishes processing).</returns>
    /// <summary>
    /// Invokes a method on the referenced grain with a timeout.
    /// Uses a CancellationTokenSource to enforce the timeout. If the call does not
    /// complete within the specified duration, the Task is cancelled.
    /// </summary>
    /// <param name="ref">The grain reference to invoke the method on.</param>
    /// <param name="timeout">The maximum duration to wait for the call to complete.</param>
    /// <param name="call">A function that takes the grain proxy and returns a Task of the result.</param>
    /// <typeparam name="'TInterface">The grain interface type.</typeparam>
    /// <typeparam name="'TKey">The grain key type.</typeparam>
    /// <typeparam name="'Result">The return type of the grain method call.</typeparam>
    /// <returns>A Task containing the result of the grain method call.</returns>
    /// <exception cref="System.Threading.Tasks.TaskCanceledException">Thrown when the call exceeds the timeout.</exception>
    let invokeWithTimeout<'TInterface, 'TKey, 'Result>
        (ref: GrainRef<'TInterface, 'TKey>)
        (timeout: TimeSpan)
        (call: 'TInterface -> Task<'Result>)
        : Task<'Result> =
        task {
            use cts = new System.Threading.CancellationTokenSource(timeout)
            let callTask = call ref.Grain
            let! completed = Task.WhenAny(callTask, Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cts.Token))
            if completed = (callTask :> Task) then
                return! callTask
            else
                cts.Cancel()
                return raise (System.TimeoutException($"Grain call timed out after {timeout}."))
        }

    let invokeOneWay<'TInterface, 'TKey>
        (ref: GrainRef<'TInterface, 'TKey>)
        (call: 'TInterface -> Task)
        : Task =
        call ref.Grain
