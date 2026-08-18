/// <summary>
/// Spec 004 item 3, step 0: the seam probe. It proves — against the shipped Orleans assemblies,
/// on both supported Orleans versions — that an activation which does NOT derive from
/// <c>JournaledGrain</c> or <c>LogConsistentGrain</c> can host an Orleans log-view adaptor
/// obtained from a NAMED log-consistency provider, drive it through append/confirm, and replay
/// its state from the journal after a deactivation.
/// </summary>
/// <remarks>
/// <para>
/// The probe host is shaped like a functional activation rather than like a journaled grain: it
/// is an ordinary <c>Grain</c> that installs the adaptor itself from the activation's service
/// provider, using only public Orleans surface —
/// <c>Factory&lt;IGrainContext, ILogConsistencyProtocolServices&gt;</c> (registered by every
/// <c>Add*BasedLogConsistencyProvider</c> call through
/// <c>LogConsistencyProtocolSiloBuilderExtensions.AddLogConsistencyProtocolServicesFactory</c>),
/// a keyed <c>ILogViewAdaptorFactory</c>, a keyed <c>IGrainStorage</c>, and
/// <c>ILogViewAdaptorFactory.MakeLogViewAdaptor</c>. Orleans' own
/// <c>LogConsistentGrain.OnSetupState</c> does exactly these four things; nothing on that path is
/// internal.
/// </para>
/// <para>
/// It also pins the two facts the functional design depends on and that no documentation states:
/// the LogStorage adaptor folds into the very view object it was handed, so a key-derived initial
/// state survives; the StateStorage adaptor replaces the view with <c>new TView()</c> on its first
/// read, so a key-derived initial state does NOT survive and has to be re-materialized by the host.
/// </para>
/// </remarks>
module Orleans.FSharp.Integration.FunctionalJournalSeamProbe

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Configuration
open Orleans.EventSourcing
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Serialization.Session
open Orleans.Storage
open Orleans.TestingHost
open Orleans.FSharp
open Swensen.Unquote
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Names
// ──────────────────────────────────────────────────────────────────────────────

[<Literal>]
let private LogStorageProvider = "ProbeLogStorage"

[<Literal>]
let private StateStorageProvider = "ProbeStateStorage"

[<Literal>]
let private ProbeStore = "ProbeJournalStore"

[<Literal>]
let private ProbeGrainType = "journal-probe"

// ──────────────────────────────────────────────────────────────────────────────
// The view and the entry
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// What the probe folds: the shipped <c>FunctionalJournalView</c> cell carries it as exact-type
/// payload bytes, so the probe exercises the real view and entry shapes rather than stand-ins.
/// </summary>
type ProbeState =
    { total: int
      trail: string
      seeded: bool }

/// One probe event.
type ProbeEvent = { amount: int; tag: string }

// ──────────────────────────────────────────────────────────────────────────────
// The probe protocol
// ──────────────────────────────────────────────────────────────────────────────

type ProbeMessage =
    /// Submit one entry and confirm it before replying (the "confirm per turn" shape).
    | Append of int * string
    /// Submit several entries atomically and confirm them before replying.
    | AppendMany of (int * string) list
    /// Submit one entry WITHOUT confirming it.
    | AppendUnconfirmed of int * string
    /// Confirm whatever is outstanding.
    | Confirm
    /// (confirmedVersion, confirmedTotal, confirmedTrail, seeded)
    | Confirmed
    /// (tentativeTotal, unconfirmedCount)
    | Tentative
    /// Conditional append at the current position; reports whether it was accepted.
    | Conditional of int * string
    /// Retrieve a log segment, or the exception type name when the provider cannot.
    | Segment of int * int
    /// Clear the whole log stream, or the exception type name when the provider cannot.
    | Clear
    /// Deactivate this activation, so the next call replays from the journal.
    | Bounce
    /// Ask the Orleans serializer to deep-copy the view type, reporting what happened.
    | CopyProbe
    /// Whether the CONFIRMED view cell is one this host seeded, before any substitution.
    | RawSeedSurvived

// ──────────────────────────────────────────────────────────────────────────────
// The host
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Installs one Orleans log-view adaptor on an activation that is not a journaled grain, using
/// only the public seam. The provider name is taken from the grain key's prefix, so one grain
/// class probes both built-in providers.
/// </summary>
[<GrainType(ProbeGrainType)>]
type JournalProbeGrain() =
    inherit Grain()

    let mutable adaptor: ILogViewAdaptor<FunctionalJournalView, FunctionalJournalEntry> = null
    let mutable codec: FunctionalPayloadCodec = Unchecked.defaultof<_>
    let mutable applied = 0

    // The exact-type byte boundary is the runtime's own FunctionalPayloadCodec, not a raw
    // Orleans Serializer: it publishes the expected payload type for the duration of a read, which
    // is what lets the F# binary codec resolve a wire type name that Type.GetType cannot find from
    // inside Orleans.FSharp.
    member private _.EncodeState(value: ProbeState) : byte[] = codec.Serialize<ProbeState> value

    member private _.DecodeState(payload: byte[]) : ProbeState = codec.Deserialize<ProbeState> payload

    member private _.EncodeEvent(value: ProbeEvent) : byte[] = codec.Serialize<ProbeEvent> value

    member private _.DecodeEvent(payload: byte[]) : ProbeEvent = codec.Deserialize<ProbeEvent> payload

    member private this.ViewOf(view: FunctionalJournalView) : ProbeState =
        if view.HasValue then this.DecodeState view.Payload else this.Seed()

    /// The key is "<provider>/<name>": the provider half names the log-consistency provider.
    member private this.ProviderName =
        let key = (this :> IGrainBase).GrainContext.GrainId.Key.ToString()

        match key.IndexOf '/' with
        | -1 -> LogStorageProvider
        | index -> key.Substring(0, index)

    /// The seed a functional definition's <c>initialEventState</c> would produce from the key.
    member private _.Seed() : ProbeState =
        { total = 1000; trail = "seed"; seeded = true }

    member private this.Install() =
        let services = (this :> IGrainBase).GrainContext.ActivationServices
        // The state and event types must be DECLARED top-level payload types, or the F# binary
        // codec cannot resolve the type name a stored payload carries: its fallback is
        // Type.GetType, which searches only Orleans.FSharp and the core library. Silo startup
        // validation is where the shipped runtime does this (SerializerPreflight).
        FSharpBinaryFormat.declareType typeof<ProbeState>
        FSharpBinaryFormat.declareType typeof<ProbeEvent>

        codec <-
            FunctionalPayloadCodec(
                services.GetRequiredService<Serializer>(),
                services.GetRequiredService<SerializerSessionPool>()
            )

        // Exactly LogConsistentGrain.OnSetupState, minus the attribute lookup: the provider is
        // named explicitly instead of being read off a [LogConsistencyProvider] attribute.
        let factory =
            match services.GetKeyedService<ILogViewAdaptorFactory> this.ProviderName with
            | null -> failwith $"no ILogViewAdaptorFactory named '{this.ProviderName}'"
            | value -> value

        let protocolServices =
            match services.GetService typeof<Factory<IGrainContext, ILogConsistencyProtocolServices>> with
            | :? Factory<IGrainContext, ILogConsistencyProtocolServices> as make ->
                make.Invoke this.GrainContext
            | _ -> failwith "no Factory<IGrainContext, ILogConsistencyProtocolServices> is registered"

        let storage =
            if factory.UsesStorageProvider then
                match services.GetKeyedService<IGrainStorage> ProbeStore with
                | null -> failwith $"no IGrainStorage named '{ProbeStore}'"
                | value -> value
            else
                null

        let seed =
            FunctionalJournalView(Payload = this.EncodeState(this.Seed()), HasValue = true)

        adaptor <-
            factory.MakeLogViewAdaptor<FunctionalJournalView, FunctionalJournalEntry>(
                this :> ILogViewAdaptorHost<FunctionalJournalView, FunctionalJournalEntry>,
                seed,
                ProbeGrainType,
                storage,
                protocolServices
            )

    interface IConnectionIssueListener with
        member _.OnConnectionIssue(_issue: ConnectionIssue) = ()
        member _.OnConnectionIssueResolved(_issue: ConnectionIssue) = ()

    interface ILogViewAdaptorHost<FunctionalJournalView, FunctionalJournalEntry> with
        /// The replay fold, run at replay AND at submission time, on the very object handed in.
        member this.UpdateView(view: FunctionalJournalView, entry: FunctionalJournalEntry) =
            applied <- applied + 1
            let current = this.ViewOf view
            let event = this.DecodeEvent entry.Payload

            let next =
                { current with
                    total = current.total + event.amount
                    trail = (if current.trail = "" then event.tag else current.trail + "," + event.tag) }

            view.Payload <- this.EncodeState next
            view.HasValue <- true

        member _.OnViewChanged(_tentative: bool, _confirmed: bool) = ()

    interface ILifecycleParticipant<IGrainLifecycle> with
        member this.Participate(lifecycle: IGrainLifecycle) =
            lifecycle.Subscribe(
                "JournalProbe.SetupState",
                GrainLifecycleStage.SetupState,
                Func<CancellationToken, Task>(fun _ ->
                    this.Install()
                    Task.CompletedTask),
                Func<CancellationToken, Task>(fun _ -> adaptor.PostOnDeactivate())
            )
            |> ignore

            lifecycle.Subscribe(
                "JournalProbe.PreActivate",
                GrainLifecycleStage.Activate - 1,
                Func<CancellationToken, Task>(fun _ -> adaptor.PreOnActivate())
            )
            |> ignore

            lifecycle.Subscribe(
                "JournalProbe.PostActivate",
                GrainLifecycleStage.Activate + 1,
                Func<CancellationToken, Task>(fun _ ->
                    task {
                        do! adaptor.PostOnActivate()

                        // PostOnActivate only NOTIFIES the adaptor's batch worker; the initial
                        // read is not awaited, so an activation that returns here can still serve
                        // a call against an empty view. JournaledGrain lives with that; a
                        // functional handler is handed the state as an argument and cannot, so the
                        // replay is forced to completion before activation finishes.
                        do! adaptor.Synchronize()
                    }
                    :> Task)
            )
            |> ignore

#nowarn "44" // IFSharpGrain is the deprecated universal transport; the probe uses it only as a
             // ready-made callable interface, because Orleans' code generator does not run on F#
             // assemblies and this probe deliberately adds no shipping C# surface.

    interface IFSharpGrain with
        member this.HandleMessage(message: obj) : Task<obj> =
            task {
                match unbox<ProbeMessage> message with
                | Append(amount, tag) ->
                    adaptor.Submit(FunctionalJournalEntry(Payload = this.EncodeEvent { amount = amount; tag = tag }))
                    do! adaptor.ConfirmSubmittedEntries()
                    return box adaptor.ConfirmedVersion

                | AppendMany entries ->
                    adaptor.SubmitRange(
                        entries
                        |> List.map (fun (amount, tag) ->
                            FunctionalJournalEntry(Payload = this.EncodeEvent { amount = amount; tag = tag }))
                    )
                    do! adaptor.ConfirmSubmittedEntries()
                    return box adaptor.ConfirmedVersion

                | AppendUnconfirmed(amount, tag) ->
                    adaptor.Submit(
                        FunctionalJournalEntry(Payload = this.EncodeEvent { amount = amount; tag = tag })
                    )
                    return box adaptor.ConfirmedVersion

                | Confirm ->
                    do! adaptor.ConfirmSubmittedEntries()
                    return box adaptor.ConfirmedVersion

                | Confirmed ->
                    let view = this.ViewOf adaptor.ConfirmedView
                    return box (adaptor.ConfirmedVersion, view.total, view.trail, view.seeded)

                | Tentative ->
                    let view = this.ViewOf adaptor.TentativeView
                    return box (view.total, Seq.length adaptor.UnconfirmedSuffix)

                | Conditional(amount, tag) ->
                    let! accepted =
                        adaptor.TryAppend(FunctionalJournalEntry(Payload = this.EncodeEvent { amount = amount; tag = tag }))

                    return box accepted

                | Segment(from, until) ->
                    try
                        let! segment = adaptor.RetrieveLogSegment(from, until)

                        return
                            box (
                                segment
                                |> Seq.map (fun entry -> (this.DecodeEvent entry.Payload).tag)
                                |> String.concat ","
                            )
                    with error ->
                        return box (error.GetType().Name)

                | Clear ->
                    try
                        do! adaptor.ClearLogAsync CancellationToken.None
                        return box "cleared"
                    with error ->
                        return box (error.GetType().Name)

                | Bounce ->
                    this.DeactivateOnIdle()
                    return box applied

                | RawSeedSurvived -> return box adaptor.ConfirmedView.HasValue

                | CopyProbe ->
                    let copier =
                        (this :> IGrainBase).GrainContext.ActivationServices.GetRequiredService<DeepCopier>()

                    try
                        let source = FunctionalJournalView(Payload = [| 1uy; 2uy |], HasValue = true)
                        let copy: FunctionalJournalView = copier.Copy source
                        copy.Payload <- [| 3uy |]
                        return box $"copied:{source.Payload.Length}:{copy.Payload.Length}"
                    with error ->
                        return box (error.GetType().Name)
            }

        member _.HandleMessageOneWay(_message: obj) : Task = Task.CompletedTask

#warnon "44"

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type JournalProbeSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorage ProbeStore |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider LogStorageProvider |> ignore
            siloBuilder.AddStateStorageBasedLogConsistencyProvider StateStorageProvider |> ignore

            // The functional runtime's own registration: the F# generalized codec WITHOUT the
            // generalized copier. Whether the adaptor's DeepCopy survives that is one of the
            // things this probe has to answer.
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                siloBuilder.Services,
                Action<Orleans.Serialization.ISerializerBuilder>(fun builder ->
                    FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore)
            )
            |> ignore

            siloBuilder.Configure<GrainTypeOptions>(fun (options: GrainTypeOptions) ->
                options.Classes.Add typeof<JournalProbeGrain> |> ignore)
            |> ignore

type JournalProbeClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                Action<Orleans.Serialization.ISerializerBuilder>(fun builder ->
                    FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore)
            )
            |> ignore

[<Sealed>]
type FunctionalJournalProbeFixture() =
    let cluster =
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<JournalProbeSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<JournalProbeClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Client = cluster.Client

#nowarn "44"

    member this.Probe(provider: string, name: string) =
        this.Client.GetGrain<IFSharpGrain>(GrainId.Create(ProbeGrainType, $"{provider}/{name}"))

#warnon "44"

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalJournalProbe")>]
type FunctionalJournalProbeCollection() =
    interface ICollectionFixture<FunctionalJournalProbeFixture>

// ──────────────────────────────────────────────────────────────────────────────
// The probe
// ──────────────────────────────────────────────────────────────────────────────

[<Collection("FunctionalJournalProbe")>]
type FunctionalJournalSeamProbeTests(fixture: FunctionalJournalProbeFixture) =

    static member Providers: obj[] seq =
        seq {
            yield [| box LogStorageProvider |]
            yield [| box StateStorageProvider |]
        }

    [<Theory>]
    [<MemberData("Providers")>]
    member _.``a non-journaled activation appends through a named provider and replays after deactivation``
        (provider: string)
        =
        task {
            let name = $"replay-{Guid.NewGuid():N}"
            let grain = fixture.Probe(provider, name)

            let! _ = grain.HandleMessage(box (Append(5, "a")))
            let! _ = grain.HandleMessage(box (Append(7, "b")))
            let! before = grain.HandleMessage(box Confirmed)
            let versionBefore, totalBefore, trailBefore, _ = unbox<int * int * string * bool> before

            test <@ versionBefore = 2 @>
            test <@ trailBefore = "seed,a,b" @>

            let! _ = grain.HandleMessage(box Bounce)
            do! Task.Delay 1500

            let! after = grain.HandleMessage(box Confirmed)
            let versionAfter, totalAfter, trailAfter, _ = unbox<int * int * string * bool> after

            // The journal, not the memory of the previous activation, is what the replay reads.
            test <@ versionAfter = 2 @>
            test <@ trailAfter = "seed,a,b" @>
            test <@ totalAfter = totalBefore @>
        }

    [<Theory>]
    [<MemberData("Providers")>]
    member _.``the tentative view carries unconfirmed entries and the confirmed view does not``
        (provider: string)
        =
        task {
            let name = $"tentative-{Guid.NewGuid():N}"
            let grain = fixture.Probe(provider, name)

            let! _ = grain.HandleMessage(box (Append(3, "a")))
            let! _ = grain.HandleMessage(box (AppendUnconfirmed(4, "b")))

            let! tentative = grain.HandleMessage(box Tentative)
            let tentativeTotal, _ = unbox<int * int> tentative

            let! confirmed = grain.HandleMessage(box Confirmed)
            let confirmedVersion, confirmedTotal, _, _ = unbox<int * int * string * bool> confirmed

            // The adaptor's batch worker confirms submitted entries on its own schedule and the
            // reply crosses a turn boundary, so "b" may already have been confirmed by the time
            // these two reads run. What is normative is that the tentative view is never BEHIND
            // the confirmed one, and that both eventually agree.
            test <@ tentativeTotal >= confirmedTotal @>
            test <@ confirmedVersion >= 1 @>

            let! _ = grain.HandleMessage(box Confirm)
            let! settled = grain.HandleMessage(box Confirmed)
            let settledVersion, settledTotal, settledTrail, _ = unbox<int * int * string * bool> settled

            test <@ settledVersion = 2 @>
            test <@ settledTotal = 1007 @>
            test <@ settledTrail = "seed,a,b" @>
        }

    [<Theory>]
    [<MemberData("Providers")>]
    member _.``a conditional append at the current position is accepted``(provider: string) =
        task {
            let name = $"conditional-{Guid.NewGuid():N}"
            let grain = fixture.Probe(provider, name)

            let! accepted = grain.HandleMessage(box (Conditional(9, "c")))
            test <@ unbox<bool> accepted @>

            let! confirmed = grain.HandleMessage(box Confirmed)
            let version, _, trail, _ = unbox<int * int * string * bool> confirmed
            test <@ version = 1 @>
            test <@ trail = "seed,c" @>
        }

    [<Theory>]
    [<MemberData("Providers")>]
    member _.``the Orleans deep copier handles the view type under a codec-only registration``
        (provider: string)
        =
        task {
            let grain = fixture.Probe(provider, $"copy-{Guid.NewGuid():N}")
            let! outcome = grain.HandleMessage(box CopyProbe)
            // Recorded either way: the answer decides whether the functional journal view needs a
            // copier of its own.
            test <@ unbox<string> outcome = "copied:2:1" @>
        }

    [<Fact>]
    member _.``LogStorage keeps the seeded view cell, StateStorage replaces it with a fresh one``() =
        task {
            let logGrain = fixture.Probe(LogStorageProvider, $"seed-{Guid.NewGuid():N}")
            let stateGrain = fixture.Probe(StateStorageProvider, $"seed-{Guid.NewGuid():N}")

            // Read the RAW cell of a grain with no stored record, before anything is appended.
            let! logRaw = logGrain.HandleMessage(box RawSeedSurvived)
            let! stateRaw = stateGrain.HandleMessage(box RawSeedSurvived)

            // LogStorage folds into the very cell it was handed, so the seeded cell survives.
            test <@ unbox<bool> logRaw @>

            // StateStorage reads into a fresh GrainStateWithMetaData, whose constructor does
            // State = new TView(): the seeded cell is discarded on the first read.
            test <@ not (unbox<bool> stateRaw) @>
        }

    [<Fact>]
    member _.``the substituted initial state makes both providers agree``() =
        task {
            let logGrain = fixture.Probe(LogStorageProvider, $"agree-{Guid.NewGuid():N}")
            let stateGrain = fixture.Probe(StateStorageProvider, $"agree-{Guid.NewGuid():N}")

            let! _ = logGrain.HandleMessage(box (Append(5, "a")))
            let! _ = stateGrain.HandleMessage(box (Append(5, "a")))

            let! log = logGrain.HandleMessage(box Confirmed)
            let! state = stateGrain.HandleMessage(box Confirmed)

            // Identical on both providers only because the host re-materializes the declared
            // initial state whenever it meets a cell that was never written.
            test <@ unbox<int * int * string * bool> log = (1, 1005, "seed,a", true) @>
            test <@ unbox<int * int * string * bool> state = (1, 1005, "seed,a", true) @>
        }

    [<Fact>]
    member _.``only LogStorage can read the log back``() =
        task {
            let logGrain = fixture.Probe(LogStorageProvider, $"segment-{Guid.NewGuid():N}")
            let stateGrain = fixture.Probe(StateStorageProvider, $"segment-{Guid.NewGuid():N}")

            let! _ = logGrain.HandleMessage(box (AppendMany [ 1, "a"; 2, "b" ]))
            let! _ = stateGrain.HandleMessage(box (AppendMany [ 1, "a"; 2, "b" ]))

            let! logSegment = logGrain.HandleMessage(box (Segment(0, 2)))
            let! stateSegment = stateGrain.HandleMessage(box (Segment(0, 2)))

            test <@ unbox<string> logSegment = "a,b" @>
            test <@ unbox<string> stateSegment = "NotSupportedException" @>
        }

    [<Theory>]
    [<MemberData("Providers")>]
    member _.``both providers can clear the log stream``(provider: string) =
        task {
            let grain = fixture.Probe(provider, $"clear-{Guid.NewGuid():N}")

            let! _ = grain.HandleMessage(box (Append(5, "a")))
            let! outcome = grain.HandleMessage(box Clear)
            test <@ unbox<string> outcome = "cleared" @>

            let! confirmed = grain.HandleMessage(box Confirmed)
            let version, total, trail, seeded = unbox<int * int * string * bool> confirmed

            test <@ version = 0 @>
            test <@ trail = "seed" @>
            test <@ total = 1000 @>
            // ClearLog re-seeds from the InitialState the adaptor deep-copied at construction, on
            // BOTH providers — including StateStorage, which had discarded that seed on its read.
            test <@ seeded @>
        }
