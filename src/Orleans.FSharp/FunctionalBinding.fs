namespace Orleans.FSharp

open System.Threading
open System.Threading.Tasks
open Orleans

/// <summary>
/// A bound functional reference: the domain key, the cached API record instance, and
/// selector-based calls for advanced scenarios.
/// </summary>
[<Sealed>]
type FunctionalGrainRef<'Actor, 'Key, 'Api>
    internal (key: 'Key, api: 'Api, contract: GrainContract<'Actor, 'Key, 'Api>) =

    /// <summary>The domain key this reference addresses.</summary>
    member _.key = key

    /// <summary>The bound API record instance; the same instance on every access.</summary>
    member _.api = api

    /// <summary>The contract this reference was bound from.</summary>
    member internal _.Contract = contract

    /// <summary>Call one operation identified by an explicit selector.</summary>
    member _.call (selector: OperationSelector<'Api, 'Argument, 'Reply>) (argument: 'Argument) : Task<'Reply> =
        ignore selector
        ignore argument
        FunctionalDiagnostics.notAvailable "Phase 2" "FunctionalGrainRef.call"

    /// <summary>Call one operation with cooperative remote cancellation.</summary>
    member _.callCancellable
        (selector: OperationSelector<'Api, 'Argument, 'Reply>)
        (argument: 'Argument)
        (cancellationToken: CancellationToken)
        : Task<'Reply> =
        ignore selector
        ignore argument
        ignore cancellationToken
        FunctionalDiagnostics.notAvailable "Phase 2" "FunctionalGrainRef.callCancellable"

/// <summary>Binding of a contract to an Orleans grain reference.</summary>
/// <remarks>
/// <para>
/// Call sites are ordinary curried applications —
/// <c>FunctionalGrain.ref contract factory key</c> — and the point-free binding
/// <c>let ref = FunctionalGrain.ref contract</c> infers the complete concrete type
/// <c>IGrainFactory -&gt; 'Key -&gt; 'Api</c> with no annotation and no later use site.
/// </para>
/// <para>
/// The binding takes <c>contract</c> as its single declared parameter and returns the
/// remaining curried function on purpose. F# inserts flexibility for non-sealed parameter
/// types at every use of a function or member, so declaring <c>factory: IGrainFactory</c> as
/// a second curried parameter would make every partial application generic in a flexible
/// <c>'_a :&gt; IGrainFactory</c> and hit the value restriction (FS0030). Flexibility is
/// inserted only for declared parameters of a member, so with the factory in the result type
/// the partial application stays concrete, while argument subsumption still lets any
/// <c>IGrainFactory</c> implementation (for example <c>IClusterClient</c>) be applied
/// directly.
/// </para>
/// </remarks>
[<AbstractClass; Sealed>]
type FunctionalGrain =

    /// <summary>
    /// Bind the contract to the grain addressed by the domain key and return the bound API
    /// record. The returned function takes the grain factory of the calling client or
    /// activation and then the domain key of the target grain.
    /// </summary>
    /// <param name="contract">The sealed contract.</param>
    static member ref(contract: GrainContract<'Actor, 'Key, 'Api>) : IGrainFactory -> 'Key -> 'Api =
        fun factory key ->
            ignore contract
            ignore factory
            ignore key
            FunctionalDiagnostics.notAvailable "Phase 2" "FunctionalGrain.ref"

    /// <summary>
    /// Bind the contract to the grain addressed by the domain key and return the typed wrapper
    /// exposing the key, the cached API record, and selector-based calls. The returned function
    /// takes the grain factory of the calling client or activation and then the domain key of
    /// the target grain.
    /// </summary>
    /// <param name="contract">The sealed contract.</param>
    static member rawRef
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        : IGrainFactory -> 'Key -> FunctionalGrainRef<'Actor, 'Key, 'Api> =
        fun factory key ->
            ignore contract
            ignore factory
            ignore key
            FunctionalDiagnostics.notAvailable "Phase 2" "FunctionalGrain.rawRef"
