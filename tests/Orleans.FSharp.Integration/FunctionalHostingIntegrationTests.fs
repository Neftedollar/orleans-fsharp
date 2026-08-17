/// <summary>
/// Spec 003 Phase 3 hosting requirements which need their own silo: the existing-silo
/// regression (a silo which references the abstractions but hosts no functional definition
/// starts clean) and component-configurator order independence.
/// </summary>
module Orleans.FSharp.Integration.FunctionalHostingIntegrationTests

open System
open System.Linq
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Metadata
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalClusterFixture
open Xunit

let private siloServices (cluster: TestCluster) =
    (cluster.Silos.[0] :?> InProcessSiloHandle).SiloHost.Services

/// <summary>A silo which loads the abstractions assembly but hosts no functional definition.</summary>
type PlainSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            // Touch the abstractions assembly the way any application referencing the package
            // would, without registering any functional definition.
            siloBuilder.Services.AddSingleton<Type>(typeof<IFunctionalRequestMetadata>) |> ignore

/// <summary>A silo whose functional component configurator is deliberately ordered FIRST.</summary>
type ConfiguratorFirstSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

            let services = siloBuilder.Services

            let ours =
                services
                |> Seq.find (fun descriptor ->
                    descriptor.ServiceType = typeof<IConfigureGrainTypeComponents>
                    && descriptor.ImplementationType = typeof<FunctionalConfigureGrainTypeComponents>)

            services.Remove ours |> ignore
            services.Insert(0, ours)

/// <summary>A silo whose functional component configurator keeps its natural (last) position.</summary>
type ConfiguratorLastSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

/// <summary>
/// A silo whose functional <c>GrainTypeOptions</c> post-configure has been removed, so the
/// closed marker and interface never reach the manifest. Silo startup validation must catch it.
/// </summary>
type BrokenManifestSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

            let services = siloBuilder.Services

            let postConfigure =
                services
                |> Seq.find (fun descriptor ->
                    descriptor.ImplementationType = typeof<FunctionalGrainTypeOptionsPostConfigure>)

            services.Remove postConfigure |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// A definition nothing binds — the silo-side payload-type declaration
// ──────────────────────────────────────────────────────────────────────────────

type ValidatorActor = private ValidatorActor of unit

/// <summary>An argument type used by nothing else in the process.</summary>
type ValidatorArgument = { note: string }

/// <summary>A reply type used by nothing else in the process.</summary>
type ValidatorReply = { echoed: string }

[<NoEquality; NoComparison>]
type ValidatorApi = { keep: ValidatorArgument -> Task<ValidatorReply> }

type ValidatorState = { seen: int }

/// <summary>A durable stored type used by nothing else in the process.</summary>
type ValidatorStored = { kept: string }

/// <summary>The named storage provider the validator definition attaches.</summary>
[<Literal>]
let private ValidatorProvider = "ValidatorStore"

let private validatorStore =
    PersistentState.create<ValidatorStored> "validator" ValidatorProvider

let private validatorDefinition =
    let contract =
        grainContract<ValidatorActor, string, ValidatorApi> () {
            grainType "functional.validator"
            stringKey
        }

    grainFor contract {
        defaultState (fun () -> { seen = 0 })
        usePersistentState validatorStore (fun _ -> { kept = "" })

        handle (_.keep) (fun _ state (payload: ValidatorArgument) ->
            task { return { seen = state.seen + 1 }, { echoed = payload.note } })
    }

/// <summary>
/// A silo hosting a definition NO client in this process ever binds, so the client-side binding
/// preflight cannot mask the silo-side declaration.
/// </summary>
type UnboundDefinitionSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage ValidatorProvider |> ignore
            siloBuilder.AddFunctionalGrain validatorDefinition |> ignore

/// <summary>
/// The same definition on a silo where its named storage provider is NOT registered. Silo
/// startup validation has to reject it, since Orleans would otherwise fail on the first
/// activation instead.
/// </summary>
type MissingStorageSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain validatorDefinition |> ignore

/// <summary>A silo whose transport limit violates the ValidateOnStart rule.</summary>
type InvalidLimitSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

            siloBuilder.Services.Configure<FunctionalGrainTransportOptions>(fun
                                                                                (options:
                                                                                    FunctionalGrainTransportOptions) ->
                options.MaxPayloadBytes <- 0)
            |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// A definition whose declared reminder period violates ReminderOptions.MinimumReminderPeriod
// ──────────────────────────────────────────────────────────────────────────────

type TooFastReminderActor = private TooFastReminderActor of unit

[<NoEquality; NoComparison>]
type TooFastReminderApi = { touch: unit -> Task<unit> }

type TooFastReminderState = { ticks: int }

let private tooFastReminderDefinition =
    let contract =
        grainContract<TooFastReminderActor, string, TooFastReminderApi> () {
            grainType "functional.toofastreminder"
            stringKey
        }

    grainFor contract {
        defaultState (fun () -> { ticks = 0 })

        onReminder "too-fast" TimeSpan.Zero (TimeSpan.FromSeconds 1.0) (fun _ state _ ->
            task { return { state with ticks = state.ticks + 1 } })

        handle (_.touch) (fun _ state () -> task { return state, () })
    }

/// <summary>
/// A silo whose configured <c>ReminderOptions.MinimumReminderPeriod</c> (2 seconds) exceeds the
/// declared reminder's own period (1 second). Silo startup validation must catch this instead of
/// letting it surface only when the reminder is first registered at activation.
/// </summary>
type TooFastReminderSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.UseInMemoryReminderService() |> ignore

            siloBuilder.Services.Configure<Orleans.Hosting.ReminderOptions>(fun
                                                                                  (options:
                                                                                      Orleans.Hosting.ReminderOptions) ->
                options.MinimumReminderPeriod <- TimeSpan.FromSeconds 2.0)
            |> ignore

            siloBuilder.AddFunctionalGrain tooFastReminderDefinition |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// A definition whose implicit subscriptions name providers the silo may not have
// (spec 004 item 1)
// ──────────────────────────────────────────────────────────────────────────────

type SubscriberActor = private SubscriberActor of unit

[<NoEquality; NoComparison>]
type SubscriberApi = { touch: unit -> Task<unit> }

type SubscriberState = { seen: int }

[<Literal>]
let private SubscriberStreamProvider = "SubscriberStreams"

[<Literal>]
let private SubscriberChannelProvider = "SubscriberChannels"

let private streamSubscriberDefinition =
    let contract =
        grainContract<SubscriberActor, string, SubscriberApi> () {
            grainType "functional.streamsubscriber"
            stringKey
        }

    grainFor contract {
        defaultState (fun () -> { seen = 0 })

        onStream SubscriberStreamProvider "hosting.items" (fun _ state (_: string) ->
            task { return { seen = state.seen + 1 } })

        handle (_.touch) (fun _ state () -> task { return state, () })
    }

type ChannelSubscriberActor = private ChannelSubscriberActor of unit

let private channelSubscriberDefinition =
    let contract =
        grainContract<ChannelSubscriberActor, string, SubscriberApi> () {
            grainType "functional.channelsubscriber"
            stringKey
        }

    grainFor contract {
        defaultState (fun () -> { seen = 0 })

        onBroadcast SubscriberChannelProvider "hosting.control" (fun _ state (_: string) ->
            task { return { seen = state.seen + 1 } })

        handle (_.touch) (fun _ state () -> task { return state, () })
    }

/// <summary>The stream provider the declaration names is registered: the silo must start.</summary>
type StreamProviderPresentSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage "PubSubStore" |> ignore
            siloBuilder.AddMemoryStreams SubscriberStreamProvider |> ignore
            siloBuilder.AddBroadcastChannel SubscriberChannelProvider |> ignore
            siloBuilder.AddFunctionalGrain streamSubscriberDefinition |> ignore
            siloBuilder.AddFunctionalGrain channelSubscriberDefinition |> ignore

/// <summary>The named stream provider is absent: startup validation has to reject it.</summary>
type MissingStreamProviderSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain streamSubscriberDefinition |> ignore

/// <summary>The named broadcast-channel provider is absent.</summary>
type MissingChannelProviderSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain channelSubscriberDefinition |> ignore

let private deploy<'Configurator when 'Configurator :> ISiloConfigurator and 'Configurator: (new: unit -> 'Configurator)>
    ()
    =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<'Configurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster

[<Fact>]
let ``a silo which references the abstractions but hosts no functional definition starts clean`` () =
    let cluster = deploy<PlainSiloConfigurator> ()

    try
        let services = siloServices cluster
        let options = services.GetRequiredService<IOptions<GrainTypeOptions>>().Value

        // Nothing functional is registered…
        Assert.Null(services.GetService typeof<FunctionalGrainRegistry>)

        Assert.Empty(
            options.Classes
            |> Seq.filter (fun candidate ->
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() = typedefof<FunctionalGrainMarker<_>>)
        )

        // …and the silo's own manifest carries no functional grain type.
        let manifest = services.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

        Assert.False(manifest.Grains.ContainsKey(GrainType.Create FunctionalGrainTypes.Probe))

        // The silo is genuinely running: a stock system grain answers.
        Assert.NotNull(services.GetRequiredService<ILocalSiloDetails>().SiloAddress)
    finally
        cluster.StopAllSilos()
        cluster.Dispose()

let private activatorTypeOf (cluster: TestCluster) =
    (siloServices cluster)
        .GetRequiredService<GrainTypeSharedContextResolver>()
        .GetComponents(GrainType.Create FunctionalGrainTypes.Probe)
        .GetComponent<IGrainActivator>()
        .GetType()

[<Fact>]
let ``both component-configurator orders produce the same final activator`` () =
    let expected =
        typedefof<FunctionalGrainActivator<_>>.MakeGenericType typeof<ProbeActor>

    let first = deploy<ConfiguratorFirstSiloConfigurator> ()

    let firstActivator =
        try
            activatorTypeOf first
        finally
            first.StopAllSilos()
            first.Dispose()

    let last = deploy<ConfiguratorLastSiloConfigurator> ()

    let lastActivator =
        try
            activatorTypeOf last
        finally
            last.StopAllSilos()
            last.Dispose()

    Assert.Equal(expected, firstActivator)
    Assert.Equal(expected, lastActivator)

/// <summary>Every message of an exception chain, so a hosted-startup failure can be inspected.</summary>
let rec private messages (error: exn) =
    match error with
    | null -> []
    | :? AggregateException as aggregate ->
        error.Message :: (aggregate.InnerExceptions |> Seq.collect messages |> List.ofSeq)
    | _ -> error.Message :: messages error.InnerException

let private deployExpectingFailure<'Configurator
    when 'Configurator :> ISiloConfigurator and 'Configurator: (new: unit -> 'Configurator)>
    ()
    =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<'Configurator>() |> ignore
    let cluster = builder.Build()

    let error = Assert.ThrowsAny<exn>(fun () -> cluster.Deploy())

    try
        try
            cluster.StopAllSilos()
        with _ ->
            ()
    finally
        cluster.Dispose()

    messages error

/// <remarks>
/// Non-vacuity control for silo startup validation: with the functional post-configure removed
/// the manifest disagrees with the registry, and the silo must refuse to start instead of
/// serving a grain type Orleans cannot route.
/// </remarks>
[<Fact>]
let ``silo startup validation rejects a manifest which disagrees with the registry`` () =
    let reported = deployExpectingFailure<BrokenManifestSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains "post-configure did not run"))

/// <remarks>
/// The silo startup validator declares every hosted argument and reply type as a top-level
/// payload type (Phase-2 defect 2 on the target side). The cluster fixture cannot prove this:
/// its client binds the same definitions in the same process and therefore declares those types
/// into the same static table first. Here NOTHING binds the definition, so the only code that
/// can put these two type names into the table is the validator's declaration loop — deleting
/// it makes the two "after" assertions fail with a type-resolution error.
/// </remarks>
[<Fact>]
let ``starting a silo declares the payload types of a definition nothing binds`` () =
    let argument =
        FSharpBinaryFormat.serializeWithType (box { note = "silo-only" }) typeof<ValidatorArgument>

    let reply =
        FSharpBinaryFormat.serializeWithType (box { echoed = "silo-only" }) typeof<ValidatorReply>

    // The durable stored type of an attached facet is declared by the same loop.
    let stored =
        FSharpBinaryFormat.serializeWithType (box { kept = "silo-only" }) typeof<ValidatorStored>

    // Before the silo exists, the elided-type path resolves none of the names.
    for bytes in [ argument; reply; stored ] do
        let before =
            Assert.Throws<InvalidOperationException>(fun () ->
                FSharpBinaryFormat.deserializeWithType bytes null |> ignore)

        Assert.Contains("not found", before.Message)

    let cluster = deploy<UnboundDefinitionSiloConfigurator> ()

    try
        // The silo really hosts it — otherwise the declaration loop had nothing to declare.
        let manifest =
            (siloServices cluster)
                .GetRequiredService<IClusterManifestProvider>()
                .LocalGrainManifest

        Assert.True(manifest.Grains.ContainsKey(GrainType.Create "functional.validator"))

        Assert.Equal<ValidatorArgument>(
            { note = "silo-only" },
            unbox (FSharpBinaryFormat.deserializeWithType argument null)
        )

        Assert.Equal<ValidatorReply>(
            { echoed = "silo-only" },
            unbox (FSharpBinaryFormat.deserializeWithType reply null)
        )

        Assert.Equal<ValidatorStored>(
            { kept = "silo-only" },
            unbox (FSharpBinaryFormat.deserializeWithType stored null)
        )
    finally
        cluster.StopAllSilos()
        cluster.Dispose()

/// <remarks>
/// Spec "Persistence and activation lifecycle": "silo startup checks that every named provider
/// is available". The positive control is every other test in this file, all of which host the
/// same definition with the provider registered and start cleanly.
/// </remarks>
[<Fact>]
let ``a definition naming an unregistered storage provider fails silo startup`` () =
    let reported = deployExpectingFailure<MissingStorageSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains ValidatorProvider))
    Assert.Contains(reported, (fun message -> message.Contains "which is not registered on this silo"))
    Assert.Contains(reported, (fun message -> message.Contains "'validator'"))

[<Fact>]
let ``a non-positive payload limit fails silo startup`` () =
    let reported = deployExpectingFailure<InvalidLimitSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "MaxPayloadBytes must be positive"))

/// <remarks>
/// Spec 004 item 1: "a definition with stream hooks requires the silo to have the named
/// provider (startup validation, like storage providers)". Without this check an unregistered
/// stream provider is SILENT — the binding is still published, Orleans still activates the grain
/// on a publish, and the delivery is simply dropped with a warning nobody is watching for.
/// </remarks>
[<Fact>]
let ``a definition naming an unregistered stream provider fails silo startup`` () =
    let reported = deployExpectingFailure<MissingStreamProviderSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains "'onStream'"))
    Assert.Contains(reported, (fun message -> message.Contains SubscriberStreamProvider))
    Assert.Contains(reported, (fun message -> message.Contains "which is not registered on this silo"))
    Assert.Contains(reported, (fun message -> message.Contains "AddMemoryStreams"))

[<Fact>]
let ``a definition naming an unregistered broadcast-channel provider fails silo startup`` () =
    let reported = deployExpectingFailure<MissingChannelProviderSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "'onBroadcast'"))
    Assert.Contains(reported, (fun message -> message.Contains SubscriberChannelProvider))
    Assert.Contains(reported, (fun message -> message.Contains "AddBroadcastChannel"))

/// <remarks>
/// The non-vacuity control for the two rejections above: the same two definitions, on a silo
/// where both named providers ARE registered, start cleanly and publish their bindings.
/// </remarks>
[<Fact>]
let ``a definition whose named stream and channel providers are registered starts cleanly`` () =
    let cluster = deploy<StreamProviderPresentSiloConfigurator> ()

    try
        let manifest =
            (siloServices cluster)
                .GetRequiredService<IClusterManifestProvider>()
                .LocalGrainManifest

        let bindingCount (grainTypeName: string) =
            manifest.Grains.[GrainType.Create grainTypeName].Properties.Keys
            |> Seq.filter (fun key -> key.StartsWith("binding.", StringComparison.Ordinal))
            |> Seq.length

        // Three keys per binding group, one group per declaration.
        Assert.Equal(3, bindingCount "functional.streamsubscriber")
        Assert.Equal(3, bindingCount "functional.channelsubscriber")
    finally
        cluster.StopAllSilos()
        cluster.Dispose()

/// <remarks>
/// Spec "Lifecycle hooks, timers, and reminders": "Silo startup validates every declared period
/// against its configured ReminderOptions.MinimumReminderPeriod." The real reminder service also
/// enforces this floor, but only lazily at the first RegisterOrUpdateReminder call during
/// activation; this proves the silo fails fast at startup instead.
/// </remarks>
[<Fact>]
let ``a reminder period below the configured MinimumReminderPeriod fails silo startup`` () =
    let reported = deployExpectingFailure<TooFastReminderSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains "'too-fast'"))
    Assert.Contains(reported, (fun message -> message.Contains "functional.toofastreminder"))
    Assert.Contains(reported, (fun message -> message.Contains "00:00:01"))
    Assert.Contains(reported, (fun message -> message.Contains "00:00:02"))

// ──────────────────────────────────────────────────────────────────────────────
// Spec 004 item 2 — transactional startup validation
// ──────────────────────────────────────────────────────────────────────────────

type TxHostActor = private TxHostActor of unit

[<NoEquality; NoComparison>]
type TxHostApi = { bump: int -> Task<unit> }

type TxHostState = { total: int }

[<Literal>]
let private TxHostGrainType = "hosting.tx"

[<Literal>]
let private TxHostStorage = "HostingTransactionStore"

let private txHostState =
    TransactionalState.create<TxHostState> "ledger" TxHostStorage

let private txHostContract =
    grainContract<TxHostActor, string, TxHostApi> () {
        grainType TxHostGrainType
        stringKey
        transactional Orleans.TransactionOption.CreateOrJoin (_.bump)
    }

let private txHostDefinition =
    grainFor txHostContract {
        defaultState (fun () -> ())
        transactionalStateFrom txHostState (fun _ -> { total = 0 })

        handle (_.bump) (fun context state (by: int) ->
            task {
                do! (context.transactionalState txHostState).update (fun value -> { total = value.total + by })
                return state, ()
            })
    }

/// <summary>Transactions enabled and the named transactional storage present: the silo starts.</summary>
type TransactionsPresentSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.UseTransactions() |> ignore
            siloBuilder.AddMemoryGrainStorage TxHostStorage |> ignore
            siloBuilder.AddFunctionalGrain txHostDefinition |> ignore

/// <summary>UseTransactions is absent: startup validation has to reject it.</summary>
type MissingTransactionsSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage TxHostStorage |> ignore
            siloBuilder.AddFunctionalGrain txHostDefinition |> ignore

/// <summary>The named transactional storage is absent.</summary>
type MissingTransactionalStorageSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.UseTransactions() |> ignore
            siloBuilder.AddFunctionalGrain txHostDefinition |> ignore

[<Fact>]
let ``a silo without UseTransactions refuses to host a transactional definition`` () =
    let reported = deployExpectingFailure<MissingTransactionsSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "no Orleans transaction agent"))
    Assert.Contains(reported, (fun message -> message.Contains "UseTransactions()"))
    Assert.Contains(reported, (fun message -> message.Contains TxHostGrainType))

[<Fact>]
let ``a silo without the named transactional storage refuses to host the definition`` () =
    let reported = deployExpectingFailure<MissingTransactionalStorageSiloConfigurator> ()

    Assert.Contains(
        reported,
        (fun message ->
            message.Contains "resolves to neither a named ITransactionalStateStorageFactory nor a named IGrainStorage")
    )

    Assert.Contains(reported, (fun message -> message.Contains TxHostStorage))

/// <remarks>
/// The non-vacuity control for both refusals above: the same definition on a silo that HAS
/// transactions and the named storage starts and serves a transactional call.
/// </remarks>
[<Fact>]
let ``a silo with transactions and the named storage hosts the definition`` () =
    let cluster = deploy<TransactionsPresentSiloConfigurator> ()

    try
        // Bound through the silo's own grain factory: this test cluster's client builder is not
        // configured for functional grains, and the point here is the silo, not the client.
        let factory = (siloServices cluster).GetRequiredService<IGrainFactory>()
        let grain = FunctionalGrain.ref txHostContract factory (Guid.NewGuid().ToString "N")
        grain.bump(4).GetAwaiter().GetResult()
    finally
        cluster.StopAllSilos()
        cluster.Dispose()
