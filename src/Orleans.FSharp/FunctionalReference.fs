namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Orleans
open Orleans.CodeGeneration
open Orleans.GrainReferences
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Serialization.Cloning
open Orleans.Serialization.Serializers
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// Creates <c>FunctionalGrainReference</c> instances for one already-validated
/// <c>(grainType, interfaceType)</c> pair. The shared reference state is built once by the
/// provider; only the key varies per reference.
/// </summary>
[<Sealed>]
type internal FunctionalGrainReferenceActivator
    (shared: GrainReferenceShared, payloadCodec: IFunctionalPayloadCodec, services: IServiceProvider) =

    interface IGrainReferenceActivator with
        member _.CreateReference(grainId: GrainId) =
            FunctionalGrainReference(shared, grainId.Key, payloadCodec, services) :> GrainReference

/// <summary>
/// The functional reference activator provider. It accepts only the reserved functional
/// interface ID <c>orleans.fsharp.functional/&lt;grainType&gt;</c> whose non-empty, NUL-free
/// suffix exactly equals the supplied <c>GrainId.Type</c>, and declines every other ID so the
/// stock Orleans providers keep serving generated references.
/// </summary>
[<Sealed>]
type internal FunctionalGrainReferenceActivatorProvider(services: IServiceProvider) =

    let runtime = lazy services.GetRequiredService<IGrainReferenceRuntime>()
    let codecProvider = lazy services.GetRequiredService<CodecProvider>()
    let copyContextPool = lazy services.GetRequiredService<CopyContextPool>()

    let payloadCodec =
        lazy (services.GetRequiredService<FunctionalPayloadCodec>() :> IFunctionalPayloadCodec)

    interface IGrainReferenceActivatorProvider with
        member _.TryGet
            (grainType: GrainType, interfaceType: GrainInterfaceType, activator: byref<IGrainReferenceActivator>)
            =
            let id = interfaceType.ToString()

            if isNull id || not (id.StartsWith(FunctionalIds.Prefix, StringComparison.Ordinal)) then
                false
            else
                let suffix = id.Substring FunctionalIds.Prefix.Length

                if String.IsNullOrEmpty suffix || suffix.IndexOf '\000' >= 0 then
                    false
                elif not (String.Equals(suffix, grainType.ToString(), StringComparison.Ordinal)) then
                    false
                else
                    let shared =
                        GrainReferenceShared(
                            grainType,
                            interfaceType,
                            FunctionalIds.InterfaceVersion,
                            runtime.Value,
                            InvokeMethodOptions.None,
                            codecProvider.Value,
                            copyContextPool.Value,
                            services
                        )

                    activator <- FunctionalGrainReferenceActivator(shared, payloadCodec.Value, services)
                    true

/// <summary>
/// The production request sender: it turns the fixed envelope of one bound operation into an
/// Orleans send through the custom reference, carrying the contract's CLOSED target-interface
/// metadata so call filters see stable, actor-specific method metadata.
/// </summary>
[<Sealed>]
type internal FunctionalReferenceSender(reference: FunctionalGrainReference, metadata: FunctionalTargetMetadata) =

    interface IFunctionalRequestSender with
        member _.SendAsync(envelope: FunctionalRequestEnvelope, cancellationToken: CancellationToken) =
            reference.SendAsync(envelope, metadata.InterfaceType, metadata.DispatchMethod, cancellationToken)

        member _.SendTransactionalAsync(envelope: FunctionalRequestEnvelope, cancellationToken: CancellationToken) =
            reference.SendTransactionalAsync(
                envelope,
                metadata.InterfaceType,
                metadata.DispatchMethod,
                cancellationToken
            )

        member _.SendOneWay(envelope: FunctionalRequestEnvelope) =
            reference.SendOneWay(envelope, metadata.InterfaceType, metadata.DispatchMethod)

/// <summary>Presence marker making the client-service registration idempotent.</summary>
[<Sealed>]
type internal FunctionalClientServicesMarker() =
    class
    end

/// <summary>
/// The fixed transport types every functional process must be able to serialize: the request,
/// its envelope, and the reply.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalTransportTypes =

    /// <summary>The three fixed transport types, in wire-nesting order.</summary>
    let all: Type[] =
        [| typeof<FunctionalRequest>
           typeof<FunctionalRequestEnvelope>
           typeof<FunctionalReply> |]

    /// <summary>
    /// Resolve an Orleans codec for each fixed transport type. A functional process which
    /// cannot serialize them can never send or receive a functional call, so this runs at
    /// startup rather than at the first call.
    /// </summary>
    let preflight (services: IServiceProvider) =
        match services.GetService typeof<ICodecProvider> with
        | :? ICodecProvider as provider ->
            let codecs = provider :> IFieldCodecProvider

            for transportType in all do
                try
                    codecs.GetCodec transportType |> ignore
                with cause ->
                    failCause
                        BindingStage
                        $"the fixed functional transport type '{transportType.FullName}' has no registered Orleans serializer in this process. AddFunctionalGrainClient/AddFunctionalGrain must run on every process which binds or hosts a functional contract."
                        cause
        | _ ->
            fail
                BindingStage
                "the functional transport requires the Orleans serializer, but no ICodecProvider is registered in this process."

/// <summary>
/// Client startup validation: the fixed transport types must have serializers before the client
/// admits any functional call. Spec: "An external client validates fixed transport types at
/// startup."
/// </summary>
[<Sealed>]
type internal FunctionalClientStartupValidator(services: IServiceProvider) =

    interface ILifecycleParticipant<IClusterClientLifecycle> with
        member _.Participate(lifecycle: IClusterClientLifecycle) =
            lifecycle.Subscribe(
                "Orleans.FSharp.FunctionalGrainClient",
                ServiceLifecycleStage.RuntimeInitialize,
                Func<CancellationToken, Task>(fun _ ->
                    FunctionalTransportTypes.preflight services
                    Task.CompletedTask)
            )
            |> ignore

/// <summary>
/// The single idempotent client-side registration routine. <c>AddFunctionalGrainClient</c> and
/// <c>AddFunctionalGrain</c> both run it before adding anything of their own.
/// </summary>
[<RequireQualifiedAccess>]
module internal FunctionalClientServices =

    /// <summary>
    /// Insert the functional reference activator provider immediately before the first existing
    /// one. Orleans installs its default providers before a builder extension runs, so their
    /// absence means the extension was applied to something which is not an Orleans builder.
    /// </summary>
    let private insertReferenceActivatorProvider (services: IServiceCollection) =
        let index =
            services
            |> Seq.tryFindIndex (fun descriptor -> descriptor.ServiceType = typeof<IGrainReferenceActivatorProvider>)

        match index with
        | None ->
            fail
                BindingStage
                "no existing IGrainReferenceActivatorProvider registration was found, so the functional reference provider cannot be ordered before the stock providers. Apply AddFunctionalGrainClient/AddFunctionalGrain to an Orleans client or silo builder."
        | Some position ->
            let descriptor =
                ServiceDescriptor.Singleton<IGrainReferenceActivatorProvider>(fun (provider: IServiceProvider) ->
                    FunctionalGrainReferenceActivatorProvider provider :> IGrainReferenceActivatorProvider)

            services.Insert(position, descriptor)

    /// <summary>True once this service collection carries the functional client services.</summary>
    let isRegistered (services: IServiceCollection) =
        services
        |> Seq.exists (fun descriptor -> descriptor.ServiceType = typeof<FunctionalClientServicesMarker>)

    /// <summary>Register the fixed functional transport on a service collection. Idempotent.</summary>
    let addTo (services: IServiceCollection) : IServiceCollection =
        if isNull (box services) then
            fail BindingStage "the functional client transport requires a service collection."

        if isRegistered services then
            services
        else
            services.AddSingleton<FunctionalClientServicesMarker>() |> ignore

            // 1. The custom reference activator provider, ahead of every stock provider.
            insertReferenceActivatorProvider services

            // 2. Fixed request/reply serializers, copiers, and activators plus their type filter.
            FunctionalTransportSerialization.AddFunctionalTransport services |> ignore

            // 3. Exact-type payload codec services. The interface registration is what the
            //    observer handle codec resolves: it lives in the abstractions assembly and
            //    cannot name the concrete codec, and a deserialized handle has to be paired
            //    with the receiving process's serializer.
            services.TryAddSingleton<FunctionalPayloadCodec>(fun (provider: IServiceProvider) ->
                let serializer = provider.GetRequiredService<Serializer>()

                FunctionalPayloadCodec(
                    serializer,
                    serializer.SessionPool,
                    FunctionalTransportConfiguration.maxPayloadBytes provider
                ))

            services.TryAddSingleton<IFunctionalPayloadCodec>(fun (provider: IServiceProvider) ->
                provider.GetRequiredService<FunctionalPayloadCodec>() :> IFunctionalPayloadCodec)

            // 3b. The observer transport: the notification envelope and the typed handle. Both
            //     directions of the functional runtime are registered by one entry point, so a
            //     process that can call a functional grain can also be pushed to by one.
            FunctionalObserverSerialization.AddFunctionalObserverTransport services |> ignore

            // 4. Transport options with startup validation.
            services
                .AddOptions<FunctionalGrainTransportOptions>()
                .Validate(
                    (fun options -> options.MaxPayloadBytes > 0),
                    "FunctionalGrainTransportOptions.MaxPayloadBytes must be positive."
                )
                .ValidateOnStart()
            |> ignore

            // 5. The F# generalized codec and its type filter (no generalized copier: functional
            //    payloads cross an explicit byte boundary, which already isolates the graph).
            ServiceCollectionExtensions.AddSerializer(
                services,
                Action<ISerializerBuilder>(fun builder ->
                    FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore)
            )
            |> ignore

            // 6. Startup preflight of the fixed transport types on an external client. A silo
            //    has no IClusterClientLifecycle, so this participant is simply never invoked
            //    there; the silo runs its own, wider startup validation instead.
            services.AddSingleton<ILifecycleParticipant<IClusterClientLifecycle>, FunctionalClientStartupValidator>()
            |> ignore

            services
