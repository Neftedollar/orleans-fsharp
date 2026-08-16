namespace Orleans.FSharp

open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// A bound functional reference: the domain key, the cached API record instance, and
/// selector-based calls for advanced scenarios.
/// </summary>
[<Sealed>]
type FunctionalGrainRef<'Actor, 'Key, 'Api>
    internal
    (
        key: 'Key,
        api: 'Api,
        contract: GrainContract<'Actor, 'Key, 'Api>,
        grainId: GrainId,
        bound: BoundCall[]
    ) =

    /// <summary>Resolve one explicit selector against the cached shape for a raw call.</summary>
    member private _.Resolve<'Argument, 'Reply>
        (entry: string, selector: OperationSelector<'Api, 'Argument, 'Reply>)
        : BoundCall =
        let operation = contract.Resolve(entry, selector)

        // Defensive: the selector's inferred types always match the descriptor it resolved to,
        // so a mismatch here means the API record was reflected against a different shape.
        if operation.ArgumentType <> typeof<'Argument> || operation.ReplyType <> typeof<'Reply> then
            fail
                BindingStage
                $"the '{entry}' selector of grain type '{contract.GrainTypeName}' resolved to operation '{operation.OperationId}', whose argument and reply types are '{operation.ArgumentType.FullName}' and '{operation.ReplyType.FullName}', but the call site supplied '{typeof<'Argument>.FullName}' and '{typeof<'Reply>.FullName}'."

        bound.[operation.Index]

    /// <summary>The domain key this reference addresses.</summary>
    member _.key = key

    /// <summary>The bound API record instance; the same instance on every access.</summary>
    member _.api = api

    /// <summary>The contract this reference was bound from.</summary>
    member internal _.Contract = contract

    /// <summary>The exact Orleans identity this reference addresses.</summary>
    member internal _.GrainId = grainId

    /// <summary>Call one operation identified by an explicit selector.</summary>
    member this.call (selector: OperationSelector<'Api, 'Argument, 'Reply>) (argument: 'Argument) : Task<'Reply> =
        let call = this.Resolve("call", selector)
        (unbox<'Argument -> Task<'Reply>> call.Field) argument

    /// <summary>Call one operation with cooperative remote cancellation.</summary>
    member this.callCancellable
        (selector: OperationSelector<'Api, 'Argument, 'Reply>)
        (argument: 'Argument)
        (cancellationToken: CancellationToken)
        : Task<'Reply> =
        let call = this.Resolve("callCancellable", selector)
        (unbox<'Argument -> CancellationToken -> Task<'Reply>> call.Cancellable) argument cancellationToken

/// <summary>
/// Reference binding: encode the key, resolve the transport, validate serializers, and create
/// one preclosed typed closure per API-record field.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalBinding =

    /// <summary>
    /// Bind one contract to one domain key. Every reflective step (API shape, selectors,
    /// generic closing) has already happened while the contract was sealed; binding only
    /// encodes the key, resolves services, validates serializers, and instantiates the
    /// preclosed closures.
    /// </summary>
    let bind
        (contract: GrainContract<'Actor, 'Key, 'Api>)
        (factory: IGrainFactory)
        (key: 'Key)
        : FunctionalGrainRef<'Actor, 'Key, 'Api> =
        let grainTypeName = contract.GrainTypeName

        // 1. Encode the domain key and construct the exact grain identity.
        let grainId = contract.GrainIdOf key

        // 2-4. Resolve the functional transport of this process. Phase 3 replaces the fallback
        //      of this seam with GetGrain over the stable actor-specific GrainInterfaceType and
        //      the FunctionalGrainReference type check.
        let source = FunctionalTransportSource.resolve factory grainTypeName
        let services = source.Services

        // 5. Validate that every exact argument and reply type has a registered codec.
        let provider = SerializerPreflight.providerOf services grainTypeName
        SerializerPreflight.ensure provider grainTypeName contract.ApiType contract.DeclaredTypes

        let codec = FunctionalTransportConfiguration.payloadCodec services grainTypeName
        let maxPayloadBytes = FunctionalTransportConfiguration.maxPayloadBytes services
        let sender = source.CreateSender(grainId, contract.TargetMetadata)

        // 6. One call site and one preclosed closure pair per descriptor.
        let bound =
            contract.Operations
            |> Array.map (fun operation ->
                let site =
                    FunctionalCallSite(
                        sender,
                        codec,
                        grainTypeName,
                        contract.Version,
                        operation.OperationId,
                        operation.RequestToken,
                        operation.ReplyToken,
                        operation.AdmissionFlags,
                        maxPayloadBytes
                    )

                operation.ClosureFactory.Invoke site)

        // 7. Build the API record with the cached record constructor and retain that instance.
        let api =
            unbox<'Api> (contract.Shape.Constructor(bound |> Array.map (fun call -> call.Field)))

        FunctionalGrainRef<'Actor, 'Key, 'Api>(key, api, contract, grainId, bound)

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
/// <para>
/// One consequence is worth knowing at call sites: because the factory is applied to the
/// returned function rather than to a declared parameter, F# does not insert subtype
/// flexibility for it. Annotate a caller's factory parameter as <c>IGrainFactory</c> — any
/// implementation, <c>IClusterClient</c> included, is accepted by ordinary subsumption, so a
/// flexible <c>#IGrainFactory</c> annotation buys nothing here and is reported as
/// <c>FS0064</c> ("less generic than indicated by its type annotations"), which is an error
/// under <c>TreatWarningsAsErrors</c>. Code that must stay generic in the factory type — a
/// <c>'F when 'F :&gt; IGrainFactory</c> type parameter on a class, which would otherwise fail
/// with <c>FS0660</c>/<c>FS0663</c> — has two diagnostic-free forms: call through the
/// application-owned binding (<c>let ref = FunctionalGrain.ref contract</c>, then
/// <c>ref factory key</c>), because flexibility is inserted at every use of a named binding
/// even when the compiler has to look through its function type, or upcast once at the call
/// (<c>FunctionalGrain.ref contract (factory :&gt; IGrainFactory) key</c>).
/// The same two forms are also the ones that stay silent for projects that opt the
/// implicit-conversion informationals in with <c>--warnon:3388</c>: applying a derived interface
/// value such as <c>IClusterClient</c> straight to the returned function is an implicit upcast
/// and is reported under that flag, which is off by default.
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
        fun factory key -> (FunctionalBinding.bind contract factory key).api

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
        fun factory key -> FunctionalBinding.bind contract factory key
