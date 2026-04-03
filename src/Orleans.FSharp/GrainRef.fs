namespace Orleans.FSharp

open System
open System.Threading.Tasks
open Orleans

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
