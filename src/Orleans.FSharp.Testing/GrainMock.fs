namespace Orleans.FSharp.Testing

open System
open System.Collections.Generic
open Orleans
open Orleans.Runtime

/// <summary>
/// A mock grain factory that responds with predefined grain implementations.
/// Useful for unit testing grain interactions without starting a real silo.
/// Register grain implementations using <see cref="GrainMock.withGrain"/>,
/// then pass this factory wherever <c>IGrainFactory</c> is required.
/// </summary>
type MockGrainFactory() =
    let grains = Dictionary<Type * string, obj>()

    /// <summary>
    /// Registers a grain implementation for a given grain type and serialized key.
    /// </summary>
    member internal _.Register(grainType: Type, key: string, impl: obj) =
        grains.[(grainType, key)] <- impl

    /// <summary>
    /// Tries to find a registered grain for the given type and serialized key.
    /// </summary>
    member internal _.TryGet<'T>(key: string) : 'T =
        match grains.TryGetValue((typeof<'T>, key)) with
        | true, v -> v :?> 'T
        | false, _ -> raise (KeyNotFoundException $"No mock grain registered for type '{typeof<'T>.Name}' with key '{key}'. Use GrainMock.withGrain to register.")

    interface IGrainFactory with
        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithGuidKey>
            (primaryKey: Guid, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithIntegerKey>
            (primaryKey: int64, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithStringKey>
            (primaryKey: string, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(primaryKey)

        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithGuidCompoundKey>
            (primaryKey: Guid, keyExtension: string, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithIntegerCompoundKey>
            (primaryKey: int64, keyExtension: string, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        member _this.CreateObjectReference<'TGrainObserverInterface when 'TGrainObserverInterface :> IGrainObserver>
            (_obj: IGrainObserver)
            : 'TGrainObserverInterface =
            raise (NotSupportedException "MockGrainFactory does not support CreateObjectReference")

        member _this.DeleteObjectReference<'TGrainObserverInterface when 'TGrainObserverInterface :> IGrainObserver>
            (_obj: IGrainObserver)
            : unit =
            raise (NotSupportedException "MockGrainFactory does not support DeleteObjectReference")

        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: Guid) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: int64) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: string) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: Guid, _keyExtension: string) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: int64, _keyExtension: string) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        member _this.GetGrain<'TGrainInterface when 'TGrainInterface :> IAddressable>
            (_grainId: GrainId)
            : 'TGrainInterface =
            raise (NotSupportedException "MockGrainFactory.GetGrain by GrainId is not supported. Use the typed GetGrain<T>(key) overload.")

        member _this.GetGrain(_grainId: GrainId) : IAddressable =
            raise (NotSupportedException "MockGrainFactory.GetGrain by GrainId is not supported.")

        member _this.GetGrain(_grainId: GrainId, _interfaceType: GrainInterfaceType) : IAddressable =
            raise (NotSupportedException "MockGrainFactory.GetGrain by GrainId is not supported.")

        member _this.GetGrain(_interfaceType: Type, _grainKey: IdSpan, _grainClassNamePrefix: string) : IAddressable =
            raise (NotSupportedException "MockGrainFactory.GetGrain by IdSpan is not supported.")

        member _this.GetGrain(_interfaceType: Type, _grainKey: IdSpan) : IAddressable =
            raise (NotSupportedException "MockGrainFactory.GetGrain by IdSpan is not supported.")

/// <summary>
/// Functions for creating and configuring mock grain factories for unit testing.
/// </summary>
[<RequireQualifiedAccess>]
module GrainMock =

    /// <summary>
    /// Creates a new empty <see cref="MockGrainFactory"/>.
    /// </summary>
    /// <returns>A new mock grain factory with no registered grains.</returns>
    let create () : MockGrainFactory =
        MockGrainFactory()

    /// <summary>
    /// Register a mock response for a grain type and key.
    /// The key is converted to a string for lookup matching.
    /// Returns the same factory for fluent chaining.
    /// </summary>
    /// <param name="key">The grain key (string, Guid, or int64 — converted via string).</param>
    /// <param name="impl">The grain implementation to return when this type + key is requested.</param>
    /// <param name="factory">The mock grain factory to register with.</param>
    /// <typeparam name="'T">The grain interface type.</typeparam>
    /// <returns>The same mock grain factory with the registration added.</returns>
    let withGrain<'T when 'T :> IGrain> (key: obj) (impl: 'T) (factory: MockGrainFactory) : MockGrainFactory =
        factory.Register(typeof<'T>, string key, box impl)
        factory
