namespace Orleans.FSharp.Testing

open System
open System.Collections.Generic
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Orleans.FSharp

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
    /// <param name="grainType">The grain interface type to register under.</param>
    /// <param name="key">The serialized grain key to register under.</param>
    /// <param name="impl">The grain implementation to return for this type and key.</param>
    member internal _.Register(grainType: Type, key: string, impl: obj) =
        grains.[(grainType, key)] <- impl

    /// <summary>
    /// Tries to find a registered grain for the given type and serialized key.
    /// </summary>
    /// <param name="key">The serialized grain key to look up.</param>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">
    /// Thrown when no grain of type <c>'T</c> was registered under <paramref name="key"/>.
    /// </exception>
    member internal _.TryGet<'T>(key: string) : 'T =
        match grains.TryGetValue((typeof<'T>, key)) with
        | true, v -> v :?> 'T
        | false, _ -> raise (KeyNotFoundException $"No mock grain registered for type '{typeof<'T>.Name}' with key '{key}'. Use GrainMock.withGrain to register.")

    interface IGrainFactory with
        /// <summary>Returns the registered Guid-keyed mock grain.</summary>
        /// <param name="primaryKey">The grain's Guid key, looked up by its string form.</param>
        /// <param name="grainClassNamePrefix">Ignored; grain class resolution is not simulated.</param>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when no matching grain was registered.</exception>
        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithGuidKey>
            (primaryKey: Guid, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        /// <summary>Returns the registered integer-keyed mock grain.</summary>
        /// <param name="primaryKey">The grain's int64 key, looked up by its string form.</param>
        /// <param name="grainClassNamePrefix">Ignored; grain class resolution is not simulated.</param>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when no matching grain was registered.</exception>
        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithIntegerKey>
            (primaryKey: int64, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        /// <summary>Returns the registered string-keyed mock grain.</summary>
        /// <param name="primaryKey">The grain's string key.</param>
        /// <param name="grainClassNamePrefix">Ignored; grain class resolution is not simulated.</param>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when no matching grain was registered.</exception>
        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithStringKey>
            (primaryKey: string, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(primaryKey)

        /// <summary>Returns the registered Guid-compound-keyed mock grain. The key extension is not part of the lookup.</summary>
        /// <param name="primaryKey">The grain's Guid key, looked up by its string form.</param>
        /// <param name="keyExtension">Ignored; the mock does not distinguish key extensions.</param>
        /// <param name="grainClassNamePrefix">Ignored; grain class resolution is not simulated.</param>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when no matching grain was registered.</exception>
        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithGuidCompoundKey>
            (primaryKey: Guid, keyExtension: string, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        /// <summary>Returns the registered integer-compound-keyed mock grain. The key extension is not part of the lookup.</summary>
        /// <param name="primaryKey">The grain's int64 key, looked up by its string form.</param>
        /// <param name="keyExtension">Ignored; the mock does not distinguish key extensions.</param>
        /// <param name="grainClassNamePrefix">Ignored; grain class resolution is not simulated.</param>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Thrown when no matching grain was registered.</exception>
        member this.GetGrain<'TGrainInterface when 'TGrainInterface :> IGrainWithIntegerCompoundKey>
            (primaryKey: int64, keyExtension: string, grainClassNamePrefix: string)
            : 'TGrainInterface =
            this.TryGet<'TGrainInterface>(string primaryKey)

        /// <summary>Not supported: this mock has no observer object-reference table.</summary>
        /// <param name="_obj">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.CreateObjectReference<'TGrainObserverInterface when 'TGrainObserverInterface :> IGrainObserver>
            (_obj: IGrainObserver)
            : 'TGrainObserverInterface =
            raise (NotSupportedException "MockGrainFactory does not support CreateObjectReference")

        /// <summary>Not supported: this mock has no observer object-reference table.</summary>
        /// <param name="_obj">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.DeleteObjectReference<'TGrainObserverInterface when 'TGrainObserverInterface :> IGrainObserver>
            (_obj: IGrainObserver)
            : unit =
            raise (NotSupportedException "MockGrainFactory does not support DeleteObjectReference")

        /// <summary>Not supported: use the generic <c>GetGrain&lt;T&gt;</c> overload instead.</summary>
        /// <param name="_grainInterfaceType">Unused.</param>
        /// <param name="_grainPrimaryKey">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: Guid) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        /// <summary>Not supported: use the generic <c>GetGrain&lt;T&gt;</c> overload instead.</summary>
        /// <param name="_grainInterfaceType">Unused.</param>
        /// <param name="_grainPrimaryKey">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: int64) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        /// <summary>Not supported: use the generic <c>GetGrain&lt;T&gt;</c> overload instead.</summary>
        /// <param name="_grainInterfaceType">Unused.</param>
        /// <param name="_grainPrimaryKey">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: string) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        /// <summary>Not supported: use the generic <c>GetGrain&lt;T&gt;</c> overload instead.</summary>
        /// <param name="_grainInterfaceType">Unused.</param>
        /// <param name="_grainPrimaryKey">Unused.</param>
        /// <param name="_keyExtension">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: Guid, _keyExtension: string) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        /// <summary>Not supported: use the generic <c>GetGrain&lt;T&gt;</c> overload instead.</summary>
        /// <param name="_grainInterfaceType">Unused.</param>
        /// <param name="_grainPrimaryKey">Unused.</param>
        /// <param name="_keyExtension">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_grainInterfaceType: Type, _grainPrimaryKey: int64, _keyExtension: string) : IGrain =
            raise (NotSupportedException "MockGrainFactory.GetGrain by Type is not supported. Use the generic GetGrain<T> overload.")

        /// <summary>Not supported: use the typed <c>GetGrain&lt;T&gt;(key)</c> overload instead.</summary>
        /// <param name="_grainId">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain<'TGrainInterface when 'TGrainInterface :> IAddressable>
            (_grainId: GrainId)
            : 'TGrainInterface =
            raise (NotSupportedException "MockGrainFactory.GetGrain by GrainId is not supported. Use the typed GetGrain<T>(key) overload.")

        /// <summary>Not supported: GrainId-based lookup is not simulated.</summary>
        /// <param name="_grainId">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_grainId: GrainId) : IAddressable =
            raise (NotSupportedException "MockGrainFactory.GetGrain by GrainId is not supported.")

        /// <summary>Not supported: GrainId-based lookup is not simulated.</summary>
        /// <param name="_grainId">Unused.</param>
        /// <param name="_interfaceType">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_grainId: GrainId, _interfaceType: GrainInterfaceType) : IAddressable =
            raise (NotSupportedException "MockGrainFactory.GetGrain by GrainId is not supported.")

        /// <summary>Not supported: IdSpan-based lookup is not simulated.</summary>
        /// <param name="_interfaceType">Unused.</param>
        /// <param name="_grainKey">Unused.</param>
        /// <param name="_grainClassNamePrefix">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
        member _this.GetGrain(_interfaceType: Type, _grainKey: IdSpan, _grainClassNamePrefix: string) : IAddressable =
            raise (NotSupportedException "MockGrainFactory.GetGrain by IdSpan is not supported.")

        /// <summary>Not supported: IdSpan-based lookup is not simulated.</summary>
        /// <param name="_interfaceType">Unused.</param>
        /// <param name="_grainKey">Unused.</param>
        /// <exception cref="System.NotSupportedException">Always thrown.</exception>
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

    /// <summary>
    /// Registers a mock F# grain (universal pattern) for unit-testing code that uses
    /// <c>FSharpGrain.ref</c> / <c>FSharpGrain.send</c> / <c>FSharpGrain.ask</c>.
    /// </summary>
    /// <remarks>
    /// The mock simulates the grain in memory: it starts with <paramref name="def"/>'s
    /// <c>DefaultState</c>, applies the handler on each message, and maintains local state.
    /// No Orleans silo is required — all dispatching happens in-process.
    /// </remarks>
    /// <param name="key">The string key used to look up the grain handle.</param>
    /// <param name="def">The grain definition produced by the <c>grain { }</c> CE.</param>
    /// <param name="factory">The mock grain factory to register with.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>The same mock grain factory with the registration added.</returns>
    /// <exception cref="System.Exception">Thrown when <paramref name="def"/> has no default state.</exception>
    // deprecated API self-reference (spec-003 deprecation pass)
#nowarn "44"
    [<Obsolete("GrainMock.withFSharpGrain mocks the superseded grain{} message-passing cluster: functional grains defined with grainContract<...> / grainFor are tested against a real TestingHost silo (AddFunctionalGrain + FunctionalGrain.ref) or by calling the handler functions directly, since they are plain functions. See docs/functional-grains.md for the migration.", false)>]
    let withFSharpGrain<'State, 'Command>
        (key: string)
        (def: GrainDefinition<'State, 'Command>)
        (factory: MockGrainFactory)
        : MockGrainFactory =
        let mutable state =
            match def.DefaultState with
            | Some s -> s
            | None   -> failwith $"GrainMock.withFSharpGrain: grain definition for '{typeof<'State>.Name}' has no default state. Call 'defaultState' in the grain {{ }} CE."

        let mock =
            { new IFSharpGrain with
                member _.HandleMessage(msg: obj) : Task<obj> =
                    task {
                        match def.Handler with
                        | None ->
                            return failwith $"GrainMock: no handler registered for grain with state '{typeof<'State>.Name}'."
                        | Some handler ->
                            let! (newState, result) = handler state (msg :?> 'Command)
                            state <- newState
                            return result
                    }

                member _.HandleMessageOneWay(msg: obj) : Task =
                    task {
                        match def.Handler with
                        | None ->
                            failwith $"GrainMock: no handler registered for grain with state '{typeof<'State>.Name}'."
                        | Some handler ->
                            let! (newState, _result) = handler state (msg :?> 'Command)
                            state <- newState
                    } }

        factory.Register(typeof<IFSharpGrain>, key, box mock)
        factory

    /// <summary>
    /// Registers a mock GUID-keyed F# grain for unit-testing code that uses
    /// <c>FSharpGrain.refGuid</c> / <c>FSharpGrain.sendGuid</c> / <c>FSharpGrain.askGuid</c>.
    /// </summary>
    /// <param name="key">The GUID key used to look up the grain handle.</param>
    /// <param name="def">The grain definition produced by the <c>grain { }</c> CE.</param>
    /// <param name="factory">The mock grain factory to register with.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>The same mock grain factory with the registration added.</returns>
    /// <exception cref="System.Exception">Thrown when <paramref name="def"/> has no default state.</exception>
    [<Obsolete("GrainMock.withFSharpGrainGuid mocks the superseded grain{} message-passing cluster: functional grains defined with grainContract<...> / grainFor are tested against a real TestingHost silo (AddFunctionalGrain + FunctionalGrain.ref) or by calling the handler functions directly, since they are plain functions. See docs/functional-grains.md for the migration.", false)>]
    let withFSharpGrainGuid<'State, 'Command>
        (key: Guid)
        (def: GrainDefinition<'State, 'Command>)
        (factory: MockGrainFactory)
        : MockGrainFactory =
        let mutable state =
            match def.DefaultState with
            | Some s -> s
            | None   -> failwith $"GrainMock.withFSharpGrainGuid: grain definition for '{typeof<'State>.Name}' has no default state."

        let mock =
            { new IFSharpGrainWithGuidKey with
                member _.HandleMessage(msg: obj) : Task<obj> =
                    task {
                        match def.Handler with
                        | None ->
                            return failwith $"GrainMock: no handler registered for grain with state '{typeof<'State>.Name}'."
                        | Some handler ->
                            let! (newState, result) = handler state (msg :?> 'Command)
                            state <- newState
                            return result
                    }

                member _.HandleMessageOneWay(msg: obj) : Task =
                    task {
                        match def.Handler with
                        | None ->
                            failwith $"GrainMock: no handler registered for grain with state '{typeof<'State>.Name}'."
                        | Some handler ->
                            let! (newState, _result) = handler state (msg :?> 'Command)
                            state <- newState
                    } }

        factory.Register(typeof<IFSharpGrainWithGuidKey>, string key, box mock)
        factory

    /// <summary>
    /// Registers a mock integer-keyed F# grain for unit-testing code that uses
    /// <c>FSharpGrain.refInt</c> / <c>FSharpGrain.sendInt</c> / <c>FSharpGrain.askInt</c>.
    /// </summary>
    /// <param name="key">The int64 key used to look up the grain handle.</param>
    /// <param name="def">The grain definition produced by the <c>grain { }</c> CE.</param>
    /// <param name="factory">The mock grain factory to register with.</param>
    /// <typeparam name="'State">The grain's state type.</typeparam>
    /// <typeparam name="'Command">The grain's command/message type.</typeparam>
    /// <returns>The same mock grain factory with the registration added.</returns>
    /// <exception cref="System.Exception">Thrown when <paramref name="def"/> has no default state.</exception>
    [<Obsolete("GrainMock.withFSharpGrainInt mocks the superseded grain{} message-passing cluster: functional grains defined with grainContract<...> / grainFor are tested against a real TestingHost silo (AddFunctionalGrain + FunctionalGrain.ref) or by calling the handler functions directly, since they are plain functions. See docs/functional-grains.md for the migration.", false)>]
    let withFSharpGrainInt<'State, 'Command>
        (key: int64)
        (def: GrainDefinition<'State, 'Command>)
        (factory: MockGrainFactory)
        : MockGrainFactory =
        let mutable state =
            match def.DefaultState with
            | Some s -> s
            | None   -> failwith $"GrainMock.withFSharpGrainInt: grain definition for '{typeof<'State>.Name}' has no default state."

        let mock =
            { new IFSharpGrainWithIntKey with
                member _.HandleMessage(msg: obj) : Task<obj> =
                    task {
                        match def.Handler with
                        | None ->
                            return failwith $"GrainMock: no handler registered for grain with state '{typeof<'State>.Name}'."
                        | Some handler ->
                            let! (newState, result) = handler state (msg :?> 'Command)
                            state <- newState
                            return result
                    }

                member _.HandleMessageOneWay(msg: obj) : Task =
                    task {
                        match def.Handler with
                        | None ->
                            failwith $"GrainMock: no handler registered for grain with state '{typeof<'State>.Name}'."
                        | Some handler ->
                            let! (newState, _result) = handler state (msg :?> 'Command)
                            state <- newState
                    } }

        factory.Register(typeof<IFSharpGrainWithIntKey>, string key, box mock)
        factory
#warnon "44"
