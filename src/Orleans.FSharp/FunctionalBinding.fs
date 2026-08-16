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
/// F# inserts flexibility for the non-sealed <c>IGrainFactory</c> parameter at every use of
/// these functions, so a point-free module binding such as
/// <c>let rawRef = FunctionalGrain.rawRef contract</c> is generalized and hits the value
/// restriction (FS0030) unless the bound value is applied later in the same file. The
/// eta-expanded form <c>let rawRef factory key = FunctionalGrain.rawRef contract factory key</c>
/// always infers its complete concrete type.
/// </remarks>
[<RequireQualifiedAccess>]
module FunctionalGrain =

    /// <summary>
    /// Bind the contract to the grain addressed by <paramref name="key"/> and return the
    /// bound API record.
    /// </summary>
    /// <param name="contract">The sealed contract.</param>
    /// <param name="factory">The grain factory of the calling client or activation.</param>
    /// <param name="key">The domain key of the target grain.</param>
    let ref (contract: GrainContract<'Actor, 'Key, 'Api>) (factory: IGrainFactory) (key: 'Key) : 'Api =
        ignore contract
        ignore factory
        ignore key
        FunctionalDiagnostics.notAvailable "Phase 2" "FunctionalGrain.ref"

    /// <summary>
    /// Bind the contract to the grain addressed by <paramref name="key"/> and return the typed
    /// wrapper exposing the key, the cached API record, and selector-based calls.
    /// </summary>
    /// <param name="contract">The sealed contract.</param>
    /// <param name="factory">The grain factory of the calling client or activation.</param>
    /// <param name="key">The domain key of the target grain.</param>
    let rawRef
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        (factory: IGrainFactory)
        (key: 'Key)
        : FunctionalGrainRef<'Actor, 'Key, 'Api> =
        ignore contract
        ignore factory
        ignore key
        FunctionalDiagnostics.notAvailable "Phase 2" "FunctionalGrain.rawRef"
