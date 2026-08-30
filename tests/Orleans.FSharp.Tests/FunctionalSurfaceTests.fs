/// <summary>
/// Surface tests for spec 003: the per-invocation context, the bound-reference wrapper,
/// client and silo registration, and construction-stage caching behaviour that the shape and
/// contract suites do not cover.
/// </summary>
module Orleans.FSharp.Tests.FunctionalSurfaceTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.GrainReferences
open Orleans.EventSourcing
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Streams
open Orleans.FSharp

type SurfaceActor = private SurfaceActor of unit

[<NoEquality; NoComparison>]
type SurfaceApi =
    { first: int -> Task<unit>
      second: int -> Task<unit> }

[<CLIMutable>]
type SurfaceState = { count: int }

type SurfaceJournalEvent = Incremented of int

let private contract =
    grainContract<SurfaceActor, string, SurfaceApi> {
        grainType "surface.test"
        stringKey
    }

let private attached = PersistentState.create<SurfaceState> "state" "Default"
let private unattached = PersistentState.create<SurfaceState> "missing" "Default"

// ──────────────────────────────────────────────────────────────────────────────
// Per-invocation context
// ──────────────────────────────────────────────────────────────────────────────

/// Returns a strictly increasing UtcNow on every call (one tick later each time), so a test
/// asserting "not recomputed" cannot pass by accident on a clock whose granularity happens to
/// be coarser than the gap between two reads -- unlike TimeProvider.System + Thread.Sleep,
/// this is deterministic regardless of the host clock's resolution.
type private StrictlyIncreasingTimeProvider(start: DateTimeOffset) =
    inherit TimeProvider()
    let mutable current = start

    override _.GetUtcNow() =
        current <- current.AddTicks 1L
        current

let private makeContextWithTokenAndJournal
    (timeProvider: TimeProvider)
    (resolve: PersistentStateDescriptor -> obj)
    (token: CancellationToken)
    (sequenceToken: StreamSequenceToken)
    (journal: IFunctionalJournalAccess)
    =
    let mutable deactivated = 0
    let mutable delayed = TimeSpan.Zero

    let core =
        { GrainId = contract.GrainIdOf "general"
          GrainFactory = Unchecked.defaultof<IGrainFactory>
          Services = ServiceCollection().BuildServiceProvider() :> IServiceProvider
          Logger = NullLogger.Instance
          TimeProvider = timeProvider
          UtcNow = timeProvider.GetUtcNow()
          CancellationToken = token
          StreamSequenceToken = sequenceToken
          DeactivateOnIdle = fun () -> deactivated <- deactivated + 1
          DelayDeactivation = fun span -> delayed <- span
          MigrateOnIdle = ignore
          ResolvePersistentState = resolve
          ResolveTransactionalState = fun _ -> null
          Journal = journal }

    let context = FunctionalGrainContext<SurfaceActor, string>("general", core)
    context, (fun () -> deactivated), (fun () -> delayed)

let private makeContextWithToken
    (timeProvider: TimeProvider)
    (resolve: PersistentStateDescriptor -> obj)
    (token: CancellationToken)
    (sequenceToken: StreamSequenceToken)
    =
    makeContextWithTokenAndJournal timeProvider resolve token sequenceToken null

let private makeContextWith
    (timeProvider: TimeProvider)
    (resolve: PersistentStateDescriptor -> obj)
    (token: CancellationToken)
    =
    makeContextWithToken timeProvider resolve token null

let private makeContext (resolve: PersistentStateDescriptor -> obj) (token: CancellationToken) =
    makeContextWith TimeProvider.System resolve token

type private SurfaceJournalStub() =
    let stats = LogConsistencyStatistics()
    let mutable snapshotRequested = false

    member _.SnapshotRequested = snapshotRequested

    interface IFunctionalJournalAccess with
        member _.Current = box { count = 0 }
        member _.Tentative = box { count = 1 }
        member _.ConfirmedVersion = 0
        member _.Unconfirmed = [ box (Incremented 1) ]
        member _.StateType = typeof<SurfaceState>
        member _.EventType = typeof<SurfaceJournalEvent>
        member _.Raise _ = ()
        member _.RaiseAndConfirm(_, _) = Task.CompletedTask
        member _.RequestSnapshot() = snapshotRequested <- true
        member _.RaiseConditional _ = Task.FromResult true
        member _.Confirm() = Task.CompletedTask
        member _.Refresh() = Task.CompletedTask
        member _.Retrieve(_, _) = Task.FromResult<obj list> []
        member _.Clear _ = Task.CompletedTask
        member _.EnableStats() = ()
        member _.DisableStats() = ()
        member _.GetStats() = stats

let private makeJournalContext (journal: IFunctionalJournalAccess) =
    let context, _, _ =
        makeContextWithTokenAndJournal
            TimeProvider.System
            (fun _ -> null)
            CancellationToken.None
            null
            journal

    context

let private journalActions (context: FunctionalGrainContext<SurfaceActor, string>) : (string * (unit -> Task)) list =
    [ "journalVersion",
      fun () ->
          context.journalVersion |> ignore
          Task.CompletedTask
      "journalState",
      fun () ->
          context.journalState<SurfaceState>() |> ignore
          Task.CompletedTask
      "journalTentativeState",
      fun () ->
          context.journalTentativeState<SurfaceState>() |> ignore
          Task.CompletedTask
      "unconfirmedEvents",
      fun () ->
          context.unconfirmedEvents<SurfaceJournalEvent>() |> ignore
          Task.CompletedTask
      "raiseEvent",
      fun () ->
          context.raiseEvent (Incremented 1)
          Task.CompletedTask
      "raiseEvents",
      fun () ->
          context.raiseEvents [ Incremented 1 ]
          Task.CompletedTask
      "confirmEvents", context.confirmEvents
      "snapshotNow",
      fun () ->
          context.snapshotNow ()
          Task.CompletedTask
      "refreshJournal", context.refreshJournal
      "retrieveConfirmedEvents",
      fun () -> context.retrieveConfirmedEvents<SurfaceJournalEvent>(0, 1) :> Task
      "clearJournal", context.clearJournal
      "enableJournalStats",
      fun () ->
          context.enableJournalStats ()
          Task.CompletedTask
      "disableJournalStats",
      fun () ->
          context.disableJournalStats ()
          Task.CompletedTask
      "getJournalStats",
      fun () ->
          context.getJournalStats () |> ignore
          Task.CompletedTask
      "raiseConditional",
      fun () -> context.raiseConditional [ Incremented 1 ] :> Task
      "raiseConditionalEvent",
      fun () -> context.raiseConditionalEvent (Incremented 1) :> Task ]

[<Fact>]
let ``the context exposes the decoded key, grain identity, and clock`` () =
    let before = DateTimeOffset.UtcNow.AddSeconds -1.0
    let context, _, _ = makeContext (fun _ -> null) CancellationToken.None
    let after = DateTimeOffset.UtcNow.AddSeconds 1.0
    let grainTypeText = context.grainId.Type.ToString()

    test <@ context.key = "general" @>
    test <@ grainTypeText = "surface.test" @>
    test <@ obj.ReferenceEquals(context.timeProvider, TimeProvider.System) @>
    test <@ context.utcNow > before && context.utcNow < after @>
    test <@ not (isNull (box context.services)) @>
    test <@ not (isNull (box context.logger)) @>

/// <remarks>
/// Task-7 close-out A.3: <c>utcNow</c> is frozen once at context creation ("contains
/// <c>utcNow = timeProvider.GetUtcNow()</c>" — a value, not a re-evaluated property), so two
/// reads through the same context must be byte-for-byte identical even though the
/// TimeProvider itself advances on every call. Uses a stub TimeProvider that returns a
/// strictly increasing value per call (task 8 close-out E4) rather than TimeProvider.System +
/// Thread.Sleep, so the assertion is clock-granularity-independent: were <c>utcNow</c>
/// re-read instead of frozen, <c>first</c> and <c>second</c> would differ on every run, not
/// just on a coarse-clocked CI runner.
/// </remarks>
[<Fact>]
let ``utcNow is a single frozen value, not recomputed on each read`` () =
    let timeProvider = StrictlyIncreasingTimeProvider(DateTimeOffset.UtcNow)

    let context, _, _ =
        makeContextWith timeProvider (fun _ -> null) CancellationToken.None

    let first = context.utcNow
    let second = context.utcNow

    test <@ first = second @>

[<Fact>]
let ``the context carries the callback cancellation token`` () =
    use source = new CancellationTokenSource()
    let context, _, _ = makeContext (fun _ -> null) source.Token

    test <@ not context.cancellationToken.IsCancellationRequested @>
    source.Cancel()
    test <@ context.cancellationToken.IsCancellationRequested @>

/// <remarks>
/// Spec 004 item 1: the stream cursor is exposed on the context and is <c>None</c> everywhere
/// except an <c>onStream</c> delivery on a rewindable provider — which is exactly the difference
/// between a null and a non-null <c>StreamSequenceToken</c> in the context core.
/// </remarks>
[<Fact>]
let ``the context exposes the stream sequence token only when a delivery carries one`` () =
    let withoutToken, _, _ = makeContext (fun _ -> null) CancellationToken.None

    let sequenceToken =
        Orleans.Providers.Streams.Common.EventSequenceTokenV2(7L, 3) :> StreamSequenceToken

    let withToken, _, _ =
        makeContextWithToken TimeProvider.System (fun _ -> null) CancellationToken.None sequenceToken

    test <@ withoutToken.streamSequenceToken = None @>
    test <@ withToken.streamSequenceToken = Some sequenceToken @>
    test <@ withToken.streamSequenceToken |> Option.map (fun token -> token.SequenceNumber) = Some 7L @>

[<Fact>]
let ``the context wraps the Orleans deactivation methods`` () =
    let context, deactivations, delay =
        makeContext (fun _ -> null) CancellationToken.None

    context.deactivateOnIdle ()
    context.delayDeactivation (TimeSpan.FromMinutes 2.0)

    test <@ deactivations () = 1 @>
    test <@ delay () = TimeSpan.FromMinutes 2.0 @>

[<Fact>]
let ``an unattached persistent state fails deterministically`` () =
    let context, _, _ =
        makeContext (fun descriptor -> if descriptor.StateName = "state" then box 1 else null) CancellationToken.None

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> context.persistentState unattached |> ignore)

    test <@ error.Message.Contains "missing" @>
    test <@ error.Message.Contains "is attached to this definition" @>

[<Fact>]
let ``a persistent state descriptor is resolved by its logical identity`` () =
    let mutable requested: PersistentStateDescriptor option = None

    let context, _, _ =
        makeContext
            (fun descriptor ->
                requested <- Some descriptor
                null)
            CancellationToken.None

    Assert.Throws<InvalidOperationException>(fun () -> context.persistentState attached |> ignore)
    |> ignore

    test <@ requested.IsSome @>
    test <@ requested.Value.StateName = "state" @>
    test <@ requested.Value.ProviderName = "Default" @>
    test <@ requested.Value.StoredType = typeof<SurfaceState> @>

[<Fact>]
let ``the context reads, writes, and removes request context values`` () =
    let context, _, _ = makeContext (fun _ -> null) CancellationToken.None

    try
        test <@ context.tryGetRequestContext<string> "tenant" = None @>

        context.setRequestContext "tenant" "acme"
        test <@ context.tryGetRequestContext<string> "tenant" = Some "acme" @>
        test <@ context.tryGetRequestContext<int> "tenant" = None @>

        context.removeRequestContext "tenant"
        test <@ context.tryGetRequestContext<string> "tenant" = None @>
    finally
        RequestContext.Clear()

[<Fact>]
let ``every journal context member rejects an ordinary grain context`` () =
    task {
        let context, _, _ = makeContext (fun _ -> null) CancellationToken.None

        for operation, invoke in journalActions context do
            let! error = Assert.ThrowsAsync<InvalidOperationException>(fun () -> invoke ())
            test <@ error.Message.Contains operation @>
            test <@ error.Message.Contains "journaledGrainFor" @>
    }

[<Fact>]
let ``typed journal context members reject the wrong state or event type`` () =
    task {
        let context = makeJournalContext (SurfaceJournalStub() :> IFunctionalJournalAccess)

        let wrongTypeActions: (string * (unit -> Task)) list =
            [ "journalState",
              fun () ->
                  context.journalState<int>() |> ignore
                  Task.CompletedTask
              "journalTentativeState",
              fun () ->
                  context.journalTentativeState<int>() |> ignore
                  Task.CompletedTask
              "unconfirmedEvents",
              fun () ->
                  context.unconfirmedEvents<string>() |> ignore
                  Task.CompletedTask
              "raiseEvent",
              fun () ->
                  context.raiseEvent "wrong"
                  Task.CompletedTask
              "raiseEvents",
              fun () ->
                  context.raiseEvents [ "wrong" ]
                  Task.CompletedTask
              "retrieveConfirmedEvents",
              fun () -> context.retrieveConfirmedEvents<string>(0, 1) :> Task
              "raiseConditional",
              fun () -> context.raiseConditional [ "wrong" ] :> Task
              "raiseConditionalEvent",
              fun () -> context.raiseConditionalEvent "wrong" :> Task ]

        for operation, invoke in wrongTypeActions do
            let! error = Assert.ThrowsAsync<InvalidOperationException>(fun () -> invoke ())
            test <@ error.Message.Contains operation @>
            test <@ error.Message.Contains "declared" @>
    }

[<Fact>]
let ``snapshotNow is forwarded to the callback journal`` () =
    let journal = SurfaceJournalStub()
    let context = makeJournalContext (journal :> IFunctionalJournalAccess)

    context.snapshotNow ()

    test <@ journal.SnapshotRequested @>

[<Fact>]
let ``snapshotNow is rejected by a state-neutral callback`` () =
    let scope = FunctionalStateScope("surface.test", "read", false)

    let journal =
        FunctionalScopedJournal(SurfaceJournalStub() :> IFunctionalJournalAccess, scope)
        :> IFunctionalJournalAccess

    let context = makeJournalContext journal
    let error = Assert.Throws<InvalidOperationException>(fun () -> context.snapshotNow ())

    test <@ error.Message.Contains "snapshotNow" @>
    test <@ error.Message.Contains "state-neutral" @>

[<Fact>]
let ``snapshotNow is rejected by a synchronous journal notification`` () =
    let scope = FunctionalStateScope("surface.test", "onStateChanged", true, Unavailable, false)

    let journal =
        FunctionalScopedJournal(SurfaceJournalStub() :> IFunctionalJournalAccess, scope)
        :> IFunctionalJournalAccess

    let context = makeJournalContext journal
    let error = Assert.Throws<InvalidOperationException>(fun () -> context.snapshotNow ())

    test <@ error.Message.Contains "snapshotNow" @>
    test <@ error.Message.Contains "synchronous journal notification" @>

[<Fact>]
let ``every journal context member rejects an expired invocation scope`` () =
    task {
        let scope = FunctionalStateScope("surface.test", "expired-test", true)

        let journal =
            FunctionalScopedJournal(SurfaceJournalStub() :> IFunctionalJournalAccess, scope)
            :> IFunctionalJournalAccess

        let context = makeJournalContext journal
        scope.Expire()

        for operation, invoke in journalActions context do
            let! error = Assert.ThrowsAsync<InvalidOperationException>(fun () -> invoke ())
            test <@ error.Message.Contains operation @>
            test <@ error.Message.Contains "had already completed" @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Bound reference wrapper
// ──────────────────────────────────────────────────────────────────────────────

let private surfaceServices =
    lazy (FunctionalTransportHarness.buildServices true None)

let private surfaceTransport () =
    let services = surfaceServices.Value
    let target = FunctionalTransportHarness.InMemoryTarget(services, "surface.test", 1)
    target.Handle<int, unit>("first", fun _ -> ())
    target.Handle<int, unit>("second", fun _ -> ())
    FunctionalTransportHarness.InMemoryTransport(services, target.Dispatch)

[<Fact>]
let ``the reference wrapper exposes the key and the cached API instance`` () =
    let transportA = surfaceTransport ()
    let transportB = surfaceTransport ()
    let referenceA = FunctionalGrain.rawRef contract transportA "general"
    let referenceB = FunctionalGrain.rawRef contract transportB "general"

    // Obtained once, independently of the accesses compared below.
    let capturedA = referenceA.api

    test <@ referenceA.key = "general" @>

    // The same instance on every access…
    test <@ obj.ReferenceEquals(referenceA.api, capturedA) @>

    // …and not a process-wide singleton: an independent binding builds its own record.
    test <@ not (obj.ReferenceEquals(referenceA.api, referenceB.api)) @>

    task {
        // The captured record really carries this reference's bound closures: the call it
        // makes appears on referenceA's transport and nowhere else.
        do! capturedA.first 1

        test <@ transportA.Calls.Length = 1 @>
        test <@ transportB.Calls.Length = 0 @>
        test <@ transportA.LastCall.Envelope.OperationId = "first" @>
    }

[<Fact>]
let ``selector-based calls reach the bound closure of the selected field`` () =
    let transport = surfaceTransport ()
    let reference = FunctionalGrain.rawRef contract transport "general"

    task {
        do! reference.call (_.first) 1
        do! reference.callCancellable (_.second) 2 CancellationToken.None

        let sent = transport.Calls |> Array.map (fun call -> call.Envelope.OperationId)
        test <@ sent = [| "first"; "second" |] @>
    }

[<Fact>]
let ``rawRef through a factory without the functional transport fails with a configuration diagnostic`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalGrain.rawRef contract (FunctionalTransportHarness.UnconfiguredFactory()) "general"
            |> ignore)

    test <@ error.Message.Contains "AddFunctionalGrainClient" @>
    test <@ error.Message.Contains "surface.test" @>

[<Fact>]
let ``rawRef without a grain factory fails with a binding diagnostic`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalGrain.rawRef contract Unchecked.defaultof<IGrainFactory> "general"
            |> ignore)

    test <@ error.Message.Contains "requires a grain factory" @>

/// <remarks>
/// Cross-file pin for the specification's point-free bindings. <c>Chat.PointFree.Lobby.ref</c>
/// and <c>rawRef</c> are declared in <c>FunctionalPointFreeFixture.fs</c> with no use site, so
/// they were generalized in that file before this one was compiled. The annotated bindings
/// below therefore assert their *inferred* types, not merely that they can be constrained.
/// </remarks>
[<Fact>]
let ``the point-free bindings infer the specification's concrete types`` () =
    let inferredRef: IGrainFactory -> Chat.PointFree.LobbyId -> Chat.PointFree.LobbyApi =
        Chat.PointFree.Lobby.ref

    let inferredRawRef
        : IGrainFactory
              -> Chat.PointFree.LobbyId
              -> FunctionalGrainRef<Chat.PointFree.LobbyActor, Chat.PointFree.LobbyId, Chat.PointFree.LobbyApi> =
        Chat.PointFree.Lobby.rawRef

    // The runtime types carry no type parameters left open either.
    let refType = inferredRef.GetType()
    let rawRefType = inferredRawRef.GetType()

    test <@ not refType.ContainsGenericParameters @>
    test <@ not rawRefType.ContainsGenericParameters @>

    // Applying any IGrainFactory implementation reaches the binding path rather than a type
    // error; `null` stands in for a factory, which the binding stage rejects by name.
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            inferredRef Unchecked.defaultof<IClusterClient> (Chat.PointFree.LobbyId "general")
            |> ignore)

    test <@ error.Message.Contains "chat.lobby" @>

// ──────────────────────────────────────────────────────────────────────────────
// Hosting stubs
// ──────────────────────────────────────────────────────────────────────────────

let private fakeClientBuilder (services: IServiceCollection) =
    { new IClientBuilder with
        member _.Services = services

        member _.Configuration =
            Unchecked.defaultof<Microsoft.Extensions.Configuration.IConfiguration> }

let private fakeSiloBuilder (services: IServiceCollection) =
    { new ISiloBuilder with
        member _.Services = services

        member _.Configuration =
            Unchecked.defaultof<Microsoft.Extensions.Configuration.IConfiguration> }

/// <summary>
/// Orleans installs its own reference activator providers before a builder extension runs.
/// A bare service collection therefore has to be seeded with a stand-in stock provider
/// descriptor to reproduce the state the extensions are specified against.
/// </summary>
let private seedStockReferenceActivatorProvider (services: IServiceCollection) =
    services.AddSingleton<IGrainReferenceActivatorProvider>(
        { new IGrainReferenceActivatorProvider with
            member _.TryGet(_grainType, _interfaceType, activator) =
                activator <- Unchecked.defaultof<IGrainReferenceActivator>
                false }
    )
    |> ignore

    services

[<Fact>]
let ``client registration returns the builder and installs the functional transport`` () =
    let services = seedStockReferenceActivatorProvider (ServiceCollection())
    let builder = fakeClientBuilder services
    let returned = builder.AddFunctionalGrainClient()

    test <@ obj.ReferenceEquals(returned, builder) @>
    test <@ FunctionalClientServices.isRegistered services @>

    let providers =
        services
        |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<IGrainReferenceActivatorProvider>)
        |> Seq.toArray

    test <@ providers.Length = 2 @>

    // The functional descriptor is inserted immediately before the first existing provider.
    let index =
        services
        |> Seq.findIndex (fun descriptor -> descriptor.ServiceType = typeof<IGrainReferenceActivatorProvider>)

    test <@ obj.ReferenceEquals(services.[index], providers.[0]) @>
    test <@ providers.[0].ImplementationFactory <> null @>
    test <@ providers.[1].ImplementationInstance <> null @>

[<Fact>]
let ``client registration is idempotent`` () =
    let services = seedStockReferenceActivatorProvider (ServiceCollection())
    let builder = fakeClientBuilder services
    builder.AddFunctionalGrainClient() |> ignore
    let after = services.Count
    builder.AddFunctionalGrainClient() |> ignore

    test <@ services.Count = after @>

[<Fact>]
let ``registration without an existing reference activator provider is a configuration error`` () =
    let services = ServiceCollection() :> IServiceCollection
    let builder = fakeClientBuilder services

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> builder.AddFunctionalGrainClient() |> ignore)

    test <@ error.Message.Contains "IGrainReferenceActivatorProvider" @>

[<Fact>]
let ``silo registration returns the builder and registers the definition once`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            handle (_.first) (fun _ state (_: int) -> task { return state, () })
            handle (_.second) (fun _ state (_: int) -> task { return state, () })
        }

    let services = seedStockReferenceActivatorProvider (ServiceCollection())
    let builder = fakeSiloBuilder services
    let returned = builder.AddFunctionalGrain definition

    test <@ obj.ReferenceEquals(returned, builder) @>
    test <@ FunctionalClientServices.isRegistered services @>

    // Repeated registration of the same definition value is idempotent.
    builder.AddFunctionalGrain definition |> ignore

    let registry =
        services
        |> Seq.pick (fun descriptor ->
            match descriptor.ImplementationInstance with
            | :? FunctionalGrainRegistry as registry -> Some registry
            | _ -> None)

    test <@ registry.Snapshot.Length = 1 @>
    test <@ registry.Snapshot.[0].GrainTypeName = "surface.test" @>

// ──────────────────────────────────────────────────────────────────────────────
// Construction-stage behaviour
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``policy order does not matter`` () =
    let interleaveFirst =
        grainContract<SurfaceActor, string, SurfaceApi> {
            grainType "surface.order"
            stringKey
            alwaysInterleave (_.second)
            oneWay (_.second)
        }

    let oneWayFirst =
        grainContract<SurfaceActor, string, SurfaceApi> {
            grainType "surface.order"
            stringKey
            oneWay (_.second)
            alwaysInterleave (_.second)
        }

    test
        <@
            interleaveFirst.Operations.[1].IsOneWay
            && interleaveFirst.Operations.[1].IsAlwaysInterleave
        @>

    test
        <@
            oneWayFirst.Operations.[1].IsOneWay
            && oneWayFirst.Operations.[1].IsAlwaysInterleave
        @>

[<Fact>]
let ``a null selector fails with the required diagnostic`` () =
    let shape = ApiShape.of'<SurfaceApi> ()

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.resolve shape "readOnly" Unchecked.defaultof<OperationSelector<SurfaceApi, int, unit>>
            |> ignore)

    test <@ error.Message.Contains "Use a direct API field selector such as _.join." @>

[<Fact>]
let ``a rejected API shape reports the same diagnostic on every attempt`` () =
    let first =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.ofType typeof<FunctionalShapeTests.AsyncApi> |> ignore)

    let second =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.ofType typeof<FunctionalShapeTests.AsyncApi> |> ignore)

    // Instance identity, not message equality: two independently constructed diagnostics with
    // the same text would satisfy an equality check while proving nothing about caching, which
    // is what this test is named for.
    test <@ obj.ReferenceEquals(first, second) @>

/// <summary>
/// The API record the race below runs on. Nothing else in the suite mentions it.
/// </summary>
/// <remarks>
/// The race used to run on <c>SurfaceApi</c>, whose shape this module's own <c>contract</c>
/// value builds during module initialisation — long before any of the 64 threads start. Sixty-
/// four readers agreeing on an already-cached instance is not a race; this type keeps the cache
/// genuinely cold until the parallel map reaches it.
/// </remarks>
[<NoEquality; NoComparison>]
type ConcurrentShapeApi = { onlyOperation: string -> Task<int> }

[<Fact>]
let ``concurrent shape construction yields one cached instance`` () =
    let counters = FunctionalInstrumentation.start ()

    try
        let shapes =
            [| 1..64 |]
            |> Array.Parallel.map (fun _ -> ApiShape.ofType typeof<ConcurrentShapeApi>)

        let first = shapes.[0]

        test <@ shapes |> Array.forall (fun shape -> obj.ReferenceEquals(shape, first)) @>
        // One build across all 64, which is the claim the shared instance is evidence for.
        test <@ counters.ApiShapeBuilds = 1 @>
    finally
        FunctionalInstrumentation.stop ()

[<Fact>]
let ``two contracts over the same API share one cached shape`` () =
    let other =
        grainContract<SurfaceActor, string, SurfaceApi> {
            grainType "surface.other"
            stringKey
        }

    test <@ obj.ReferenceEquals(contract.Shape, other.Shape) @>
    test <@ obj.ReferenceEquals(contract.Shape.Probe, other.Shape.Probe) @>

// ──────────────────────────────────────────────────────────────────────────────
// Conformance with the normative public API
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Every custom-operation name declared by a computation-expression builder.</summary>
let private customOperations (builderType: Type) =
    builderType.GetMethods(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)
    |> Array.collect (fun method' ->
        method'.GetCustomAttributes(typeof<Microsoft.FSharp.Core.CustomOperationAttribute>, false)
        |> Array.map (fun attribute' -> (attribute' :?> Microsoft.FSharp.Core.CustomOperationAttribute).Name))
    |> Array.sort

[<Fact>]
let ``every Orleans JournaledGrain member is mapped and the baseline has not drifted`` () =
    // This is the source side of the parity contract. If a supported Orleans version adds,
    // removes, or renames a declared JournaledGrain member, this fails beside the exact F#
    // context and builder surface pins below instead of letting the wrapper silently lag.
    let mapping =
        [| "ClearLogAsync", [ "clearJournal" ]
           "ConfirmEvents", [ "confirmEvents" ]
           "DefaultAdaptorFactory", [ "logProvider"; "journalStorage" ]
           "DisableStatsCollection", [ "disableJournalStats" ]
           "EnableStatsCollection", [ "enableJournalStats" ]
           "GetStats", [ "getJournalStats" ]
           "InstallAdaptor", [ "logProvider"; "journalStorage" ]
           // The adaptor itself and pre-activation protocol stage are runtime-owned seams.
           "LogViewAdaptor", []
           "OnActivateAsync", [ "onActivate" ]
           "OnConnectionIssue", [ "onConnectionIssue" ]
           "OnConnectionIssueResolved", [ "onConnectionIssueResolved" ]
           "OnStateChanged", [ "onStateChanged" ]
           "OnTentativeStateChanged", [ "onTentativeStateChanged" ]
           "Orleans.EventSourcing.IConnectionIssueListener.OnConnectionIssue", [ "onConnectionIssue" ]
           "Orleans.EventSourcing.IConnectionIssueListener.OnConnectionIssueResolved",
           [ "onConnectionIssueResolved" ]
           "Orleans.EventSourcing.ILogConsistencyProtocolParticipant.DeactivateProtocolParticipant",
           [ "onDeactivate" ]
           "Orleans.EventSourcing.ILogConsistencyProtocolParticipant.PostActivateProtocolParticipant",
           [ "onActivate" ]
           "Orleans.EventSourcing.ILogConsistencyProtocolParticipant.PreActivateProtocolParticipant", []
           "Orleans.EventSourcing.ILogViewAdaptorHost<TGrainState,TEventBase>.OnViewChanged",
           [ "onTentativeStateChanged"; "onStateChanged" ]
           "Orleans.EventSourcing.ILogViewAdaptorHost<TGrainState,TEventBase>.UpdateView", [ "apply" ]
           "RaiseConditionalEvent", [ "raiseConditionalEvent" ]
           "RaiseConditionalEvents", [ "raiseConditional" ]
           "RaiseEvent", [ "raiseEvent" ]
           "RaiseEvents", [ "raiseEvents" ]
           "RefreshNow", [ "refreshJournal" ]
           "RetrieveConfirmedEvents", [ "retrieveConfirmedEvents" ]
           "State", [ "journalState" ]
           "TentativeState", [ "journalTentativeState" ]
           "TransitionState", [ "apply" ]
           "UnconfirmedEvents", [ "unconfirmedEvents" ]
           "Version", [ "journalVersion" ] |]

    let expected = mapping |> Array.map fst |> Array.sort

    let flags =
        Reflection.BindingFlags.Instance
        ||| Reflection.BindingFlags.Public
        ||| Reflection.BindingFlags.NonPublic
        ||| Reflection.BindingFlags.DeclaredOnly

    let journaledType = typeof<JournaledGrain<SurfaceState, SurfaceJournalEvent>>

    let actual =
        Array.append
            (journaledType.GetMethods flags
             |> Array.map (fun method' -> method'.Name)
             |> Array.filter (fun name -> not (name.StartsWith "get_" || name.StartsWith "set_")))
            (journaledType.GetProperties flags |> Array.map (fun property' -> property'.Name))
        |> Array.distinct
        |> Array.sort

    test <@ actual = expected @>

    let contextSurface =
        typeof<FunctionalGrainContext<SurfaceActor, string>>
            .GetMembers(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)
        |> Array.map (fun member' -> member'.Name)
        |> Array.filter (fun name -> not (name.StartsWith "get_" || name.StartsWith "set_"))
        |> Set.ofArray

    let builderSurface =
        customOperations typeof<FunctionalJournaledGrainDefinitionBuilder<SurfaceActor, string, SurfaceApi>>
        |> Set.ofArray

    let fsharpSurface = Set.union contextSurface builderSurface

    for source, targets in mapping do
        for target in targets do
            test <@ Set.contains target fsharpSurface @>

[<Fact>]
let ``the builders module exposes exactly the specified entry points`` () =
    // Module-level public bindings are pinned by name: an entry point appearing in (or vanishing
    // from) the AutoOpen'd FunctionalGrainBuilders module is a public-surface change nothing else
    // in the suite would notice -- a binding once appeared here unauthored and 2,566 tests stayed
    // green, which is exactly the gap this pin closes.
    let expected = [| "contract"; "grainContract"; "grainFor"; "journaledGrainFor" |]

    let moduleType =
        typeof<GrainContract<SurfaceActor, string, SurfaceApi>>.Assembly.GetType
            "Orleans.FSharp.FunctionalGrainBuilders"

    let actual =
        moduleType.GetMethods(
            Reflection.BindingFlags.Public
            ||| Reflection.BindingFlags.Static
            ||| Reflection.BindingFlags.DeclaredOnly
        )
        |> Array.map (fun method' -> method'.Name)
        |> Array.sort

    test <@ actual = expected @>

/// <summary>The observer brand and handler record the freshness pin below pushes through.</summary>
type SurfaceObserver = private SurfaceObserver of unit

[<NoEquality; NoComparison>]
type SurfaceObserverApi = { onSurface: int -> Task<unit> }

[<Fact>]
let ``each entry point mention yields its own builder`` () =
    // grainContract / contract / observerContract are TYPE FUNCTIONS (generic values), not
    // functions: `grainContract<A, K, Api>` takes no argument. A generic value is re-evaluated
    // at every mention, so each contract expression starts from its own builder instance. Were
    // the compiler to cache one instance per instantiation instead, every contract expression
    // over the same type arguments would share it -- and this file's two `grainContract<
    // SurfaceActor, string, SurfaceApi>` mentions would be handing the same object to two CEs.
    let first = grainContract<SurfaceActor, string, SurfaceApi>
    let second = grainContract<SurfaceActor, string, SurfaceApi>
    test <@ not (obj.ReferenceEquals(first, second)) @>

    // Qualified, because this module's own `contract` binding shadows the AutoOpen'd entry
    // point -- see "the contract short form brands the contract with its own API record".
    let firstShort = FunctionalGrainBuilders.contract<string, SurfaceApi>
    let secondShort = FunctionalGrainBuilders.contract<string, SurfaceApi>
    test <@ not (obj.ReferenceEquals(firstShort, secondShort)) @>

    let firstObserver = observerContract<SurfaceObserver, SurfaceObserverApi>
    let secondObserver = observerContract<SurfaceObserver, SurfaceObserverApi>
    test <@ not (obj.ReferenceEquals(firstObserver, secondObserver)) @>

[<Fact>]
let ``two contract expressions over one entry point stay independent`` () =
    // The consequence the identity pin above exists for, asserted on observable metadata rather
    // than on object identity: two expressions off the same entry point must not see each
    // other's declarations. GrainContractBuilder is itself stateless today (every custom
    // operation returns a fresh draft), so a shared builder would not actually leak -- this pin
    // is what would catch a future builder that starts carrying state, at which point sharing
    // one instance across expressions becomes a real cross-contract bug.
    let left =
        grainContract<SurfaceActor, string, SurfaceApi> {
            grainType "surface.independent.left"
            version 2
            stringKey
            readOnly (_.first)
        }

    let right =
        grainContract<SurfaceActor, string, SurfaceApi> {
            grainType "surface.independent.right"
            stringKey
        }

    test <@ left.GrainTypeName = "surface.independent.left" @>
    test <@ right.GrainTypeName = "surface.independent.right" @>
    test <@ left.Version = 2 @>
    test <@ right.Version = 1 @>
    test <@ left.Operations |> Array.exists (fun operation -> operation.IsReadOnly) @>
    test <@ right.Operations |> Array.forall (fun operation -> not operation.IsReadOnly) @>

[<Fact>]
let ``the contract short form brands the contract with its own API record`` () =
    // contract<'Key, 'Api> is grainContract<'Api, 'Key, 'Api>: the record IS the brand. Qualified
    // access here because this file's own module-level `contract` binding shadows the AutoOpen'd
    // type function -- which is itself worth demonstrating: user code that names a local binding
    // `contract` keeps working unchanged.
    let shortForm =
        FunctionalGrainBuilders.contract<string, SurfaceApi> {
            grainType "surface.short-form"
            version 1
            stringKey
        }

    test <@ shortForm.GetType() = typeof<GrainContract<SurfaceApi, string, SurfaceApi>> @>

[<Fact>]
let ``the contract builder declares exactly the specified custom operations`` () =
    let expected =
        [| "acceptsVersions"
           "alwaysInterleave"
           "grainType"
           "guidCompoundKey"
           "guidCompoundKeyMapped"
           "guidKey"
           "guidKeyMapped"
           "int64CompoundKey"
           "int64CompoundKeyMapped"
           "int64Key"
           "int64KeyMapped"
           "mayInterleave"
           "oneWay"
           // Spec 004 item 6: 'operationId' and 'sinceVersion' each have a second overload taking
           // a StreamSelector, so a streaming API field can be renamed and version-gated exactly
           // like a unary one. They are the ONLY two of the contract's per-operation declarations
           // that compose with a streaming field; the four admission policies are refused at
           // sealing, and their selectors would not type-check against one in the first place.
           "operationId"
           "operationId"
           "readOnly"
           "reentrant"
           "sinceVersion"
           "sinceVersion"
           "stringKey"
           "stringKeyMapped"
           "transactional"
           "version" |]

    test <@ customOperations typeof<GrainContractBuilder<SurfaceActor, string, SurfaceApi>> = expected @>

[<Fact>]
let ``the definition builder declares exactly the specified custom operations`` () =
    let expected =
        [| "collectionAge"
           "defaultState"
           "handle"
           // Reply-only sugar over 'handle', admitted only on a readOnly operation. A SEPARATE
           // operation for the same reason 'handleStream' is one, below.
           "handleQuery"
           // Spec 004 item 6: a SEPARATE operation rather than an overload of 'handle'. An
           // overloaded 'handle' makes F# resolve the handler lambda before the selector, which
           // breaks record-field inference inside existing handler bodies.
           "handleStream"
           "initialState"
           "migrationParticipant"
           "onActivate"
           "onBroadcast"
           "onDeactivate"
           "onLifecycle"
           "onReminder"
           "onStream"
           "onTimer"
           "persistenceCodec"
           "placement"
           "stateFrom"
           "statelessWorker"
           "transactionalStateFrom"
           "usePersistentState" |]

    test <@ customOperations typeof<FunctionalGrainDefinitionBuilder<SurfaceActor, string, SurfaceApi>> = expected @>

/// <remarks>
/// Spec 004 item 3. Whole-state hooks have journal-aware counterparts which return events, while
/// transactional facets, lifecycle-stage hooks, and stateless workers remain absent. An operation
/// appearing or disappearing here without that decision being made is exactly what this pin
/// exists to catch.
/// </remarks>
[<Fact>]
let ``the journaled definition builder declares exactly the specified custom operations`` () =
    let expected =
        [| "apply"
           "collectionAge"
           "customStorage"
           "eventSchema"
           "handle"
           // Reply-only sugar over 'handle', admitted only on a readOnly operation. It takes the
           // same QueryHandler shape the ordinary builder's does: a query raises no events and
           // replaces no state, so neither builder has anything of its own to add to the return
           // type.
           "handleQuery"
           // Spec 004 item 6: a journaled definition streams too. Its streaming handler has the
           // ordinary StreamHandler shape and raises no events, for the same reason it publishes
           // no replacement state.
           "handleStream"
           "initialEventState"
           "journalCodec"
           "journalStorage"
           "logProvider"
           "migrationParticipant"
           "onActivate"
           "onBroadcast"
           "onConnectionIssue"
           "onConnectionIssueResolved"
           "onDeactivate"
           "onReminder"
           "onStateChanged"
           "onStream"
           "onTentativeStateChanged"
           "onTimer"
           "placement"
           "snapshotPolicy"
           "stateSchema" |]

    test
        <@
            customOperations typeof<FunctionalJournaledGrainDefinitionBuilder<SurfaceActor, string, SurfaceApi>> = expected
        @>

[<Fact>]
let ``the invocation context declares exactly the specified public members`` () =
    let expected =
        [| "cancellationToken"
           "clearJournal"
           "confirmEvents"
           "deactivateOnIdle"
           "delayDeactivation"
           "disableJournalStats"
           "enableJournalStats"
           "getJournalStats"
           "grainFactory"
           "grainId"
           // The complete JournaledGrain lifecycle lives on the ONE context type rather than on a
           // journaled variant. An ordinary grainFor definition can name these members, and each
           // refuses with a definition-stage diagnostic when invoked there.
           "journalState"
           "journalTentativeState"
           "journalVersion"
           "key"
           "logger"
           "migrateOnIdle"
           "persistentState"
           "raiseConditional"
           "raiseConditionalEvent"
           "raiseEvent"
           "raiseEvents"
           "refreshJournal"
           "removeRequestContext"
           "retrieveConfirmedEvents"
           "services"
           "setRequestContext"
           "snapshotNow"
           "streamSequenceToken"
           "timeProvider"
           "transactionalState"
           "tryGetRequestContext"
           "unconfirmedEvents"
           "utcNow" |]

    let actual =
        typeof<FunctionalGrainContext<SurfaceActor, string>>
            .GetMembers(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)
        |> Array.map (fun member' -> member'.Name)
        |> Array.filter (fun name -> not (name.StartsWith "get_" || name.StartsWith "set_"))
        |> Array.filter (fun name -> not (List.contains name [ "ToString"; "Equals"; "GetHashCode"; "GetType" ]))
        |> Array.distinct
        |> Array.sort

    test <@ actual = expected @>

/// <remarks>
/// Spec 004 item 1: an implicit delivery is routed to <c>GrainId.Create(grainType,
/// streamId.Key)</c> — the stream key bytes verbatim — so the stream key must be the grain key in
/// the contract's OWN encoding. <c>FunctionalGrain.streamId</c> / <c>channelId</c> exist because
/// <c>StreamId.Create</c>'s own overloads do not always produce it, and this test is the proof
/// for both directions: byte equality with the grain key for the codec where they agree
/// (<c>stringKey</c>), and a demonstrated DISAGREEMENT for <c>int64Key</c>, where
/// <c>StreamId.Create(ns, 42L)</c> writes decimal "42" and the codec writes hexadecimal "2A".
/// </remarks>
[<Fact>]
let ``FunctionalGrain.streamId and channelId carry the contract's own grain-key bytes`` () =
    let streamNamespace =
        (FunctionalGrain.streamId contract "surface.ns" "general").GetNamespace()

    let streamKey =
        (FunctionalGrain.streamId contract "surface.ns" "general").GetKeyAsString()

    let channelNamespace =
        (FunctionalGrain.channelId contract "surface.ns" "general").GetNamespace()

    let channelKey =
        (FunctionalGrain.channelId contract "surface.ns" "general").GetKeyAsString()

    let grainKey = contract.GrainIdOf("general").Key.ToString()

    test <@ streamNamespace = "surface.ns" @>
    test <@ streamKey = grainKey @>
    test <@ streamKey = "general" @>
    test <@ channelNamespace = "surface.ns" @>
    test <@ channelKey = grainKey @>

    // The int64 codec is where the naive overload and the contract disagree.
    let numeric =
        grainContract<SurfaceActor, int64, SurfaceApi> {
            grainType "surface.numeric"
            int64Key
        }

    let numericKey =
        (FunctionalGrain.streamId numeric "surface.ns" 42L).GetKeyAsString()

    let naiveKey = StreamId.Create("surface.ns", 42L).GetKeyAsString()
    let numericGrainKey = numeric.GrainIdOf(42L).Key.ToString()

    test <@ numericKey = numericGrainKey @>
    test <@ numericKey = "2A" @>
    test <@ naiveKey = "42" @>
    test <@ numericKey <> naiveKey @>

[<Fact>]
let ``FunctionalGrain.streamId and channelId reject a blank namespace`` () =
    let blankStream =
        Assert.Throws<InvalidOperationException>(fun () -> FunctionalGrain.streamId contract "  " "general" |> ignore)

    let blankChannel =
        Assert.Throws<InvalidOperationException>(fun () -> FunctionalGrain.channelId contract "" "general" |> ignore)

    test <@ blankStream.Message.Contains "stream namespace" @>
    test <@ blankChannel.Message.Contains "channel namespace" @>

[<Fact>]
let ``the bound reference declares exactly the specified public members`` () =
    // Spec 004 item 6 adds the two streaming forms; 'stream' and 'streamCancellable' are to a
    // streaming field exactly what 'call' and 'callCancellable' are to a unary one.
    let expected =
        [| "api"; "call"; "callCancellable"; "key"; "stream"; "streamCancellable" |]

    let actual =
        typeof<FunctionalGrainRef<SurfaceActor, string, SurfaceApi>>
            .GetMembers(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)
        |> Array.map (fun member' -> member'.Name)
        |> Array.filter (fun name -> not (name.StartsWith "get_" || name.StartsWith "set_"))
        |> Array.filter (fun name -> not (List.contains name [ "ToString"; "Equals"; "GetHashCode"; "GetType" ]))
        |> Array.distinct
        |> Array.sort

    test <@ actual = expected @>
