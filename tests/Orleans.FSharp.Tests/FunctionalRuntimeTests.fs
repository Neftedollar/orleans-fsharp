/// <summary>
/// Silo-side unit tests for spec 003 Phase 3: the frozen definition registry, the manifest
/// providers, the functional activator and its target, and target dispatch validation — all
/// against a stub activation context, so the ordering and diagnostic rules are pinned without
/// a cluster.
/// </summary>
module Orleans.FSharp.Tests.FunctionalRuntimeTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans
open Orleans.Configuration
open Orleans.Metadata
open Orleans.Runtime
open Orleans.Serialization
open Orleans.FSharp
open Swensen.Unquote
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Fixture contracts
// ──────────────────────────────────────────────────────────────────────────────

type RuntimeActor = private RuntimeActor of unit
type OtherRuntimeActor = private OtherRuntimeActor of unit

[<NoEquality; NoComparison>]
type RuntimeApi =
    { write: string -> Task<unit>
      read: unit -> Task<string>
      peek: unit -> Task<string>
      notify: string -> Task<unit>
      boom: string -> Task<string> }

type RuntimeState = { last: string }

let private contractFor<'Actor> (name: string) =
    grainContract<'Actor, string, RuntimeApi> () {
        grainType name
        stringKey
        readOnly (_.read)
        readOnly (_.peek)
        alwaysInterleave (_.peek)
        oneWay (_.notify)
        alwaysInterleave (_.notify)
    }

let private definitionFor (contract: GrainContract<'Actor, string, RuntimeApi>) =
    grainFor contract {
        defaultState (fun () -> { last = "" })
        handle (_.write) (fun _ _ (value: string) -> task { return { last = value }, () })
        handle (_.read) (fun _ state () -> task { return state, state.last })
        handle (_.peek) (fun _ state () -> task { return { last = "discarded" }, state.last })
        handle (_.notify) (fun _ _ (value: string) -> task { return { last = value }, () })

        handle (_.boom) (fun _ state (message: string) ->
            task {
                raise (ApplicationException message)
                return state, message
            })
    }

let private runtimeContract = contractFor<RuntimeActor> "runtime.probe"
let private otherContract = contractFor<OtherRuntimeActor> "runtime.other"
let private clashingContract = contractFor<RuntimeActor> "runtime.clash"

let private runtimeDefinition = definitionFor runtimeContract
let private otherDefinition = definitionFor otherContract
let private clashingDefinition = definitionFor clashingContract

let private hosted (definition: FunctionalGrainDefinition<'Actor, string, RuntimeApi, RuntimeState>) =
    FunctionalHosted.create definition

// ──────────────────────────────────────────────────────────────────────────────
// Stub activation context
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A stub <see cref="T:Orleans.Runtime.IGrainRuntime"/>. The functional activator only hands it
/// to the protected <c>Grain</c> constructor, and the target's deactivation wrappers route
/// through it, so the two deactivation members record instead of failing.
/// </summary>
type private StubGrainRuntime(services: IServiceProvider) =

    member val Deactivations = 0 with get, set
    member val Delay = TimeSpan.Zero with get, set

    interface IGrainRuntime with
        member _.SiloIdentity = "stub"
        member _.SiloAddress = Unchecked.defaultof<SiloAddress>
        member _.GrainFactory = Unchecked.defaultof<IGrainFactory>
        member _.TimerRegistry = Unchecked.defaultof<Orleans.Timers.ITimerRegistry>
        member _.ServiceProvider = services
        member _.TimeProvider = TimeProvider.System
        member this.DeactivateOnIdle(_context) = this.Deactivations <- this.Deactivations + 1
        member this.DelayDeactivation(_context, timeSpan) = this.Delay <- timeSpan
        member _.GetStorage<'T>(_context) = Unchecked.defaultof<Orleans.Core.IStorage<'T>>

let private activationServices () =
    let services = ServiceCollection()

    ServiceCollectionExtensions.AddSerializer(
        services,
        Action<ISerializerBuilder>(fun builder ->
            FunctionalTransportSerialization.AddFunctionalTransport builder |> ignore
            FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore)
    )
    |> ignore

    services.AddLogging() |> ignore

    services.AddSingleton<FunctionalPayloadCodec>(fun (provider: IServiceProvider) ->
        let serializer = provider.GetRequiredService<Serializer>()
        FunctionalPayloadCodec(serializer, serializer.SessionPool))
    |> ignore

    services.AddSingleton<IGrainRuntime>(fun (provider: IServiceProvider) ->
        StubGrainRuntime provider :> IGrainRuntime)
    |> ignore

    services.AddSingleton<IGrainFactory>(FunctionalTransportHarness.UnconfiguredFactory() :> IGrainFactory)
    |> ignore
    services.BuildServiceProvider() :> IServiceProvider

/// <summary>
/// A stub <see cref="T:Orleans.Runtime.IGrainContext"/>: the functional activator only reads
/// the grain identity and the activation services, so everything else fails loudly if used.
/// </summary>
type private StubGrainContext(grainId: GrainId, services: IServiceProvider) =

    let components = Dictionary<Type, obj>()

    member val Instance: obj = null with get, set

    interface IGrainContext with
        member _.GrainId = grainId
        member this.GrainInstance = this.Instance
        member _.GrainReference = Unchecked.defaultof<GrainReference>
        member _.ActivationId = ActivationId.NewId()
        member _.Address = Unchecked.defaultof<GrainAddress>
        member _.ActivationServices = services
        member _.ObservableLifecycle = Unchecked.defaultof<IGrainLifecycle>
        member _.Scheduler = Unchecked.defaultof<IWorkItemScheduler>
        member _.Deactivated = Task.CompletedTask
        member _.SetComponent<'T when 'T: not struct>(value: 'T) = components.[typeof<'T>] <- box value
        member _.ReceiveMessage(_message: obj) = ()
        member _.Activate(_requestContext, _cancellationToken) = ()
        member _.Deactivate(_reason, _cancellationToken) = ()
        member _.Rehydrate(_context) = ()
        member _.Migrate(_requestContext, _cancellationToken) = ()

    interface IEquatable<IGrainContext> with
        member this.Equals(other: IGrainContext) = obj.ReferenceEquals(this, other)

    interface Orleans.Serialization.Invocation.ITargetHolder with
        member this.GetTarget() = this.Instance

        member _.GetComponent(componentType: Type) =
            match components.TryGetValue componentType with
            | true, value -> value
            | _ -> null

let private grainIdOf (grainType: string) (key: string) =
    GrainId.Create(GrainType.Create grainType, key)

let private createTarget (definition: FunctionalHostedDefinition) (key: string) =
    let services = activationServices ()
    let context = StubGrainContext(grainIdOf definition.GrainTypeName key, services)

    let activator =
        FunctionalConfigureGrainTypeComponents.CreateActivator definition

    let instance = activator.CreateInstance context
    context.Instance <- instance
    activator, context, instance

let private payloadCodec (context: StubGrainContext) =
    (context :> IGrainContext).ActivationServices.GetRequiredService<FunctionalPayloadCodec>()

// ──────────────────────────────────────────────────────────────────────────────
// Registry
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the registry accepts a definition once and repeats idempotently`` () =
    let registry = FunctionalGrainRegistry()
    let definition = hosted runtimeDefinition
    registry.Add definition
    registry.Add definition

    test <@ registry.Snapshot.Length = 1 @>
    test <@ registry.Snapshot.[0].GrainTypeName = "runtime.probe" @>
    test <@ registry.Snapshot.[0].MarkerType = typedefof<FunctionalGrainMarker<_>>.MakeGenericType typeof<RuntimeActor> @>

[<Fact>]
let ``the registry rejects a second definition with the same actor brand`` () =
    let registry = FunctionalGrainRegistry()
    registry.Add(hosted runtimeDefinition)

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> registry.Add(hosted clashingDefinition))

    test <@ error.Message.Contains "runtime.clash" @>
    test <@ error.Message.Contains "runtime.probe" @>

[<Fact>]
let ``the registry rejects a different definition with the same grain type`` () =
    let registry = FunctionalGrainRegistry()
    registry.Add(hosted runtimeDefinition)

    // A second sealed definition value over the same contract is a different definition.
    let duplicate = hosted (definitionFor runtimeContract)

    let error = Assert.Throws<InvalidOperationException>(fun () -> registry.Add duplicate)
    test <@ error.Message.Contains "runtime.probe" @>

[<Fact>]
let ``the registry rejects registration after the freeze`` () =
    let registry = FunctionalGrainRegistry()
    registry.Add(hosted runtimeDefinition)
    registry.Freeze() |> ignore

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> registry.Add(hosted otherDefinition))

    test <@ registry.IsFrozen @>
    test <@ error.Message.Contains "already frozen" @>

// ──────────────────────────────────────────────────────────────────────────────
// Manifest providers
// ──────────────────────────────────────────────────────────────────────────────

let private frozenRegistry () =
    let registry = FunctionalGrainRegistry()
    registry.Add(hosted runtimeDefinition)
    registry.Add(hosted otherDefinition)
    registry

[<Fact>]
let ``the post-configure removes open functional entries and adds only closed ones`` () =
    let registry = frozenRegistry ()
    let options = GrainTypeOptions()
    options.Classes.Add typedefof<FunctionalGrainMarker<_>> |> ignore
    options.Classes.Add typeof<RuntimeState> |> ignore
    options.Interfaces.Add typedefof<IFunctionalGrainTarget<_>> |> ignore
    options.Interfaces.Add typeof<IDisposable> |> ignore

    (FunctionalGrainTypeOptionsPostConfigure registry :> IPostConfigureOptions<GrainTypeOptions>)
        .PostConfigure(Options.DefaultName, options)

    test <@ not (options.Classes.Contains typedefof<FunctionalGrainMarker<_>>) @>
    test <@ not (options.Interfaces.Contains typedefof<IFunctionalGrainTarget<_>>) @>

    test
        <@
            options.Classes.Contains(typedefof<FunctionalGrainMarker<_>>.MakeGenericType typeof<RuntimeActor>)
        @>

    test
        <@
            options.Interfaces.Contains(typedefof<IFunctionalGrainTarget<_>>.MakeGenericType typeof<RuntimeActor>)
        @>

    // Unrelated entries survive untouched.
    test <@ options.Classes.Contains typeof<RuntimeState> @>
    test <@ options.Interfaces.Contains typeof<IDisposable> @>
    test <@ registry.IsFrozen @>

[<Fact>]
let ``the post-configure ignores named options`` () =
    let registry = frozenRegistry ()
    let options = GrainTypeOptions()

    (FunctionalGrainTypeOptionsPostConfigure registry :> IPostConfigureOptions<GrainTypeOptions>)
        .PostConfigure("other", options)

    test <@ options.Classes.Count = 0 @>
    test <@ options.Interfaces.Count = 0 @>

[<Fact>]
let ``the properties provider replaces exactly the normalized functional entry`` () =
    let registry = frozenRegistry ()
    let provider = FunctionalGrainPropertiesProvider registry :> IGrainPropertiesProvider
    let markerType = typedefof<FunctionalGrainMarker<_>>.MakeGenericType typeof<RuntimeActor>
    let grainType = GrainType.Create "runtime.probe"

    let properties = Dictionary<string, string>()
    properties.["interface.0"] <- typedefof<IFunctionalGrainTarget<_>>.FullName
    properties.["interface.1"] <- "Orleans.IRemindable"
    // A decoy whose value merely CONTAINS the transport interface name: a substring match
    // would claim it and fail the "exactly one" rule.
    properties.["interface.2"] <- "Contoso.IFunctionalGrainTargetAudit"
    properties.["type.full"] <- markerType.FullName

    provider.Populate(markerType, grainType, properties)

    test <@ properties.["interface.0"] = "orleans.fsharp.functional/runtime.probe" @>
    test <@ properties.["interface.1"] = "Orleans.IRemindable" @>
    test <@ properties.["interface.2"] = "Contoso.IFunctionalGrainTargetAudit" @>
    test <@ properties.["type.full"] = markerType.FullName @>

[<Fact>]
let ``the properties provider fails startup on zero or multiple normalized entries`` () =
    let registry = frozenRegistry ()
    let provider = FunctionalGrainPropertiesProvider registry :> IGrainPropertiesProvider
    let markerType = typedefof<FunctionalGrainMarker<_>>.MakeGenericType typeof<RuntimeActor>
    let grainType = GrainType.Create "runtime.probe"

    let none = Dictionary<string, string>()
    none.["interface.0"] <- "Orleans.IRemindable"
    Assert.Throws<InvalidOperationException>(fun () -> provider.Populate(markerType, grainType, none))
    |> ignore

    let two = Dictionary<string, string>()
    two.["interface.0"] <- typedefof<IFunctionalGrainTarget<_>>.FullName
    two.["interface.1"] <- "orleans.fsharp.functional/runtime.probe"
    Assert.Throws<InvalidOperationException>(fun () -> provider.Populate(markerType, grainType, two))
    |> ignore

[<Fact>]
let ``the properties provider leaves unregistered grain classes alone`` () =
    let registry = frozenRegistry ()
    let provider = FunctionalGrainPropertiesProvider registry :> IGrainPropertiesProvider
    let properties = Dictionary<string, string>()
    properties.["interface.0"] <- "Orleans.IRemindable"

    provider.Populate(typeof<RuntimeState>, GrainType.Create "unrelated", properties)

    test <@ properties.["interface.0"] = "Orleans.IRemindable" @>

[<Fact>]
let ``the type providers map only registered closed types`` () =
    let registry = frozenRegistry ()
    let typeProvider = FunctionalGrainTypeProvider registry :> IGrainTypeProvider

    let interfaceProvider =
        FunctionalGrainInterfaceTypeProvider registry :> IGrainInterfaceTypeProvider

    let mutable grainType = Unchecked.defaultof<GrainType>
    let mutable interfaceType = Unchecked.defaultof<GrainInterfaceType>

    Assert.True(
        typeProvider.TryGetGrainType(
            typedefof<FunctionalGrainMarker<_>>.MakeGenericType typeof<RuntimeActor>,
            &grainType
        )
    )

    Assert.Equal<string>("runtime.probe", grainType.ToString())
    Assert.False(typeProvider.TryGetGrainType(typedefof<FunctionalGrainMarker<_>>, &grainType))
    Assert.False(typeProvider.TryGetGrainType(typeof<RuntimeState>, &grainType))

    Assert.True(
        interfaceProvider.TryGetGrainInterfaceType(
            typedefof<IFunctionalGrainTarget<_>>.MakeGenericType typeof<RuntimeActor>,
            &interfaceType
        )
    )

    Assert.Equal<string>("orleans.fsharp.functional/runtime.probe", interfaceType.ToString())
    Assert.False(interfaceProvider.TryGetGrainInterfaceType(typedefof<IFunctionalGrainTarget<_>>, &interfaceType))

[<Fact>]
let ``the interface properties provider publishes version 1 and the default grain type`` () =
    let registry = frozenRegistry ()

    let provider =
        FunctionalGrainInterfacePropertiesProvider registry :> IGrainInterfacePropertiesProvider

    let properties = Dictionary<string, string>()

    provider.Populate(
        typedefof<IFunctionalGrainTarget<_>>.MakeGenericType typeof<RuntimeActor>,
        GrainInterfaceType.Create "orleans.fsharp.functional/runtime.probe",
        properties
    )

    test <@ properties.[WellKnownGrainInterfaceProperties.Version] = "1" @>
    test <@ properties.[WellKnownGrainInterfaceProperties.DefaultGrainType] = "runtime.probe" @>

// ──────────────────────────────────────────────────────────────────────────────
// Activator and target
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the activator returns a target which is neither the marker nor shares its context`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "target-1"

    test <@ not (instance :? FunctionalGrainMarker<RuntimeActor>) @>
    test <@ instance :? IFunctionalGrainTarget<RuntimeActor> @>
    test <@ instance :? IFunctionalDispatchTarget @>
    test <@ instance :? IRemindable @>
    test <@ obj.ReferenceEquals((instance :?> IGrainBase).GrainContext, context) @>

[<Fact>]
let ``DisposeInstance disposes the target exactly once`` () =
    let activator, context, instance = createTarget (hosted runtimeDefinition) "target-dispose"
    let target = instance :?> FunctionalGrainTargetBase

    activator.DisposeInstance(context, instance) |> ignore
    activator.DisposeInstance(context, instance) |> ignore
    (instance :?> IDisposable).Dispose()

    test <@ target.DisposalCount = 1 @>

[<Fact>]
let ``an undeclared reminder fails the target callback explicitly`` () =
    let _, _, instance = createTarget (hosted runtimeDefinition) "target-reminder"

    let error =
        Assert.ThrowsAsync<InvalidOperationException>(fun () ->
            (instance :?> IRemindable).ReceiveReminder("nightly", Unchecked.defaultof<TickStatus>))

    test <@ error.Result.Message.Contains "nightly" @>
    test <@ error.Result.Message.Contains "runtime.probe" @>

[<Fact>]
let ``the component configurator installs the functional activator only for registered types`` () =
    let registry = frozenRegistry ()

    let configure =
        FunctionalConfigureGrainTypeComponents registry :> IConfigureGrainTypeComponents

    test <@ not (isNull (box configure)) @>

    let activator =
        FunctionalConfigureGrainTypeComponents.CreateActivator(hosted runtimeDefinition)

    test
        <@
            activator.GetType() = typedefof<FunctionalGrainActivator<_>>.MakeGenericType typeof<RuntimeActor>
        @>

// ──────────────────────────────────────────────────────────────────────────────
// Target dispatch validation order
// ──────────────────────────────────────────────────────────────────────────────

let private dispatch (instance: obj) (envelope: FunctionalRequestEnvelope) =
    (instance :?> IFunctionalDispatchTarget)
        .DispatchAsync(envelope, CancellationToken.None)

let private envelopeFor (operationId: string) (flags: byte) (payload: byte[]) =
    FunctionalRequestEnvelope(
        "runtime.probe",
        1,
        operationId,
        ProtocolToken.request "runtime.probe" 1 operationId,
        flags,
        payload
    )

let private failing (instance: obj) (envelope: FunctionalRequestEnvelope) =
    Assert.Throws<InvalidOperationException>(fun () -> dispatch instance envelope |> ignore)

[<Fact>]
let ``dispatch rejects a foreign grain type before anything else`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-type"
    let payload = (payloadCodec context).Serialize<string> "x"

    let foreign =
        FunctionalRequestEnvelope("runtime.other", 1, "write", ProtocolToken.request "runtime.other" 1 "write", 0uy, payload)

    let error = failing instance foreign
    test <@ error.Message.Contains "hosts grain type 'runtime.probe'" @>

[<Fact>]
let ``dispatch rejects a foreign contract version with expected and received values`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-version"
    let payload = (payloadCodec context).Serialize<string> "x"

    let wrong =
        FunctionalRequestEnvelope("runtime.probe", 3, "write", ProtocolToken.request "runtime.probe" 3 "write", 0uy, payload)

    let error = failing instance wrong
    test <@ error.Message.Contains "hosts contract version 1 but received version 3" @>

/// <remarks>
/// The envelope itself refuses to hold a wrong-length token, on construction and on
/// deserialization alike, so the dispatch-order token-length step is defence in depth that a
/// well-formed envelope can never reach. The reachable enforcement point is asserted here.
/// </remarks>
[<Fact>]
let ``a wrong-length protocol token cannot reach dispatch at all`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalRequestEnvelope("runtime.probe", 1, "write", Array.zeroCreate 31, 0uy, [||])
            |> ignore)

    test <@ error.Message.Contains "must be exactly 32 bytes" @>

    let missing =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalRequestEnvelope("runtime.probe", 1, "write", null, 0uy, [||]) |> ignore)

    test <@ missing.Message.Contains "must not be null" @>

[<Fact>]
let ``dispatch rejects an unknown operation`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-unknown"
    let payload = (payloadCodec context).Serialize<string> "x"
    let error = failing instance (envelopeFor "nope" 0uy payload)

    test <@ error.Message.Contains "hosts no operation 'nope'" @>

[<Fact>]
let ``dispatch rejects a mismatched request token`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-token"
    let payload = (payloadCodec context).Serialize<string> "x"

    let mismatched =
        FunctionalRequestEnvelope(
            "runtime.probe",
            1,
            "write",
            ProtocolToken.request "runtime.probe" 1 "read",
            0uy,
            payload
        )

    let error = failing instance mismatched
    test <@ error.Message.Contains "carries protocol token" @>

[<Fact>]
let ``dispatch rejects mismatched admission flags`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-flags"
    let payload = (payloadCodec context).Serialize<string> "x"
    let error = failing instance (envelopeFor "write" 0x01uy payload)

    test <@ error.Message.Contains "admission flags" @>

[<Fact>]
let ``dispatch runs the typed handler and publishes the returned state`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-state"
    let codec = payloadCodec context

    task {
        let! _ = dispatch instance (envelopeFor "write" 0uy (codec.Serialize<string> "published"))

        let! reply =
            dispatch instance (envelopeFor "read" AdmissionFlags.ReadOnly (codec.Serialize<unit> ()))

        test <@ codec.Deserialize<string> reply.Payload = "published" @>
        test <@ ProtocolToken.equal reply.ProtocolToken (ProtocolToken.reply "runtime.probe" 1 "read") @>
    }

[<Fact>]
let ``a read-only handler's replacement state is discarded`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-readonly"
    let codec = payloadCodec context

    task {
        let! _ = dispatch instance (envelopeFor "write" 0uy (codec.Serialize<string> "kept"))

        let! _ =
            dispatch
                instance
                (envelopeFor "peek" (AdmissionFlags.ReadOnly ||| AdmissionFlags.AlwaysInterleave) (codec.Serialize<unit> ()))

        let! reply =
            dispatch instance (envelopeFor "read" AdmissionFlags.ReadOnly (codec.Serialize<unit> ()))

        test <@ codec.Deserialize<string> reply.Payload = "kept" @>
    }

[<Fact>]
let ``an application handler exception is not dressed up as a protocol diagnostic`` () =
    let _, context, instance = createTarget (hosted runtimeDefinition) "dispatch-boom"
    let codec = payloadCodec context

    task {
        let! error =
            Assert.ThrowsAsync<ApplicationException>(fun () ->
                (dispatch instance (envelopeFor "boom" 0uy (codec.Serialize<string> "kaboom")))
                    .AsTask()
                :> Task)

        test <@ error.Message = "kaboom" @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Hosted definition and preclosed server adapters
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the hosted view preserves declaration order, tokens, and flags`` () =
    let definition = hosted runtimeDefinition

    test <@ definition.GrainTypeName = "runtime.probe" @>
    test <@ definition.Version = 1 @>
    test <@ definition.ActorType = typeof<RuntimeActor> @>
    test <@ definition.StateType = typeof<RuntimeState> @>
    test <@ definition.InterfaceId = "orleans.fsharp.functional/runtime.probe" @>

    test
        <@
            definition.Operations |> Array.map (fun operation -> operation.OperationId) = [| "write"
                                                                                             "read"
                                                                                             "peek"
                                                                                             "notify"
                                                                                             "boom" |]
        @>

    let notify = (definition.TryFindOperation "notify").Value
    test <@ notify.IsOneWay && notify.IsAlwaysInterleave && not notify.IsReadOnly @>

    test
        <@ ProtocolToken.equal notify.RequestToken (ProtocolToken.request "runtime.probe" 1 "notify") @>

    test <@ ProtocolToken.equal notify.ReplyToken (ProtocolToken.reply "runtime.probe" 1 "notify") @>
    test <@ (definition.TryFindOperation "missing").IsNone @>

[<Fact>]
let ``the hosted view decodes the key and creates the ephemeral state`` () =
    let definition = hosted runtimeDefinition
    let key = definition.DecodeKey(grainIdOf "runtime.probe" "abc")

    test <@ unbox<string> key = "abc" @>
    test <@ unbox<RuntimeState> (definition.CreateState key) = { last = "" } @>
