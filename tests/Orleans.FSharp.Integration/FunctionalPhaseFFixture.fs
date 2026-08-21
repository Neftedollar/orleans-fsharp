/// <summary>
/// The spec 004 Phase F fixture: server-streaming replies (<c>IAsyncEnumerable&lt;'Item&gt;</c>)
/// over Orleans' own <c>IAsyncEnumerableGrainExtension</c>.
/// </summary>
/// <remarks>
/// <para>
/// The producers are gated rather than timed. A test that only counted items could not tell
/// "delivered incrementally" from "delivered in one batch at the end" — Orleans drains up to
/// <c>MaxBatchSize</c> synchronously-available elements into a single reply, so a producer that
/// never awaits genuinely does produce everything before the first item reaches the caller. Every
/// producer here therefore waits on a per-item <c>TaskCompletionSource</c> the test releases, and
/// records how many items it has produced, so the test can assert that the caller saw item
/// <c>n</c> while the producer was still blocked before item <c>n+1</c>.
/// </para>
/// <para>
/// The silo's payload limit is deliberately larger than the client's. A single limit could only
/// ever prove one of the two per-item boundaries; two limits let one oversized item trip the
/// caller-side check and a larger one trip the silo-side check.
/// </para>
/// </remarks>
module Orleans.FSharp.Integration.FunctionalPhaseFFixture

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Names and limits
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module PhaseFGrainTypes =
    /// The main streaming grain.
    [<Literal>]
    let Ticker = "phasef.ticker"

    /// A grain whose stream is produced by enumerating another grain's stream.
    [<Literal>]
    let Relay = "phasef.relay"

    /// A version-2 contract hosting one streaming operation introduced at version 2.
    [<Literal>]
    let Versioned = "phasef.versioned"

    /// A journaled definition with a streaming operation.
    [<Literal>]
    let Ledger = "phasef.ledger"

    /// A definition with an explicit stock placement strategy.
    [<Literal>]
    let Placed = "phasef.placed"

    /// A contract declaring `mayInterleave` alongside a streaming field.
    [<Literal>]
    let Selective = "phasef.selective"

[<RequireQualifiedAccess>]
module PhaseFLimits =
    /// The silo's functional payload limit: big enough that a 200 KiB item reaches the caller.
    [<Literal>]
    let Silo = 1048576

    /// The client's, deliberately smaller, so the caller-side per-item check has something to
    /// reject that the silo happily sent.
    [<Literal>]
    let Client = 65536

// ──────────────────────────────────────────────────────────────────────────────
// Out-of-band observation
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>One release gate per (stream, item), so a producer can be held between items.</summary>
[<RequireQualifiedAccess>]
module PhaseFGates =
    let private gates = ConcurrentDictionary<string, TaskCompletionSource>()

    let private cell (name: string) =
        gates.GetOrAdd(name, fun _ -> TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously)

    /// <summary>The task a producer awaits before yielding item <paramref name="index"/>.</summary>
    let gate (label: string) (index: int) = (cell $"{label}#{index}").Task

    /// <summary>Let the producer past the gate for one item.</summary>
    let release (label: string) (index: int) = (cell $"{label}#{index}").TrySetResult() |> ignore

    /// <summary>Open every gate of one stream, up to <paramref name="count"/>.</summary>
    let releaseAll (label: string) (count: int) =
        for index in 0 .. count - 1 do
            release label index

/// <summary>How many items each labelled producer has actually produced.</summary>
[<RequireQualifiedAccess>]
module PhaseFProduced =
    let private counts = ConcurrentDictionary<string, StrongBox<int>>()

    let private cell (label: string) =
        counts.GetOrAdd(label, fun _ -> StrongBox<int> 0)

    let record (label: string) =
        let box = cell label
        Interlocked.Increment(&box.Value) |> ignore

    let count (label: string) =
        let box = cell label
        Volatile.Read(&box.Value)

/// <summary>What each labelled producer observed when its enumeration ended.</summary>
[<RequireQualifiedAccess>]
module PhaseFObserved =
    let private outcomes = ConcurrentDictionary<string, string>()

    let record (label: string) (outcome: string) = outcomes.[label] <- outcome

    let tryGet (label: string) =
        match outcomes.TryGetValue label with
        | true, outcome -> Some outcome
        | _ -> None

// ──────────────────────────────────────────────────────────────────────────────
// The domain
// ──────────────────────────────────────────────────────────────────────────────

/// One streamed item. A record, so the item type is an ordinary application type crossing the
/// exact-type payload codec rather than a primitive Orleans already knows.
type Tick = { index: int; note: string }

type TickerState = { counter: int }

[<NoEquality; NoComparison>]
type TickerApi =
    { /// Yields <c>count</c> ticks, waiting at a per-item gate before each one.
      watch: string * int -> IAsyncEnumerable<Tick>
      /// Yields forever until the enumeration is cancelled; records what it observed in `finally`.
      follow: string -> IAsyncEnumerable<int>
      /// Yields one item of the requested size in bytes.
      oversized: int -> IAsyncEnumerable<string>
      /// Yields the state counter `count` times, gated, so a concurrent `bump` is visible or not.
      snapshot: string * int -> IAsyncEnumerable<int>
      /// Throws on the item after `failAt`.
      faulty: int -> IAsyncEnumerable<int>
      /// Yields nothing at all.
      empty: unit -> IAsyncEnumerable<Tick>
      /// An ordinary call, to prove one proceeds while an enumeration is open.
      ping: unit -> Task<int>
      /// Publishes a new state counter.
      bump: int -> Task<int>
      /// Reads the state counter.
      current: unit -> Task<int>
      /// The address of the silo this activation lives on.
      whereAmI: unit -> Task<string> }

type TickerActor = private TickerActor of unit

let tickerContract =
    grainContract<TickerActor, string, TickerApi> {
        grainType PhaseFGrainTypes.Ticker
        version 1
        stringKey
        readOnly (_.current)
        readOnly (_.whereAmI)
    }

let tickerRef = FunctionalGrain.ref tickerContract

/// <summary>The raw reference exposes <c>streamCancellable</c>, which the cancellation test needs.</summary>
let tickerRawRef = FunctionalGrain.rawRef tickerContract

let tickerDefinition =
    grainFor tickerContract {
        defaultState (fun () -> { counter = 0 })

        handleStream (_.watch) (fun _ _ ((label: string), (count: int)) ->
            taskSeq {
                for index in 0 .. count - 1 do
                    do! PhaseFGates.gate label index
                    PhaseFProduced.record label
                    yield { index = index; note = $"{label}#{index}" }
            })

        handleStream (_.follow) (fun context _ (label: string) ->
            taskSeq {
                try
                    let mutable index = 0

                    while true do
                        PhaseFProduced.record label
                        yield index
                        index <- index + 1
                        do! Task.Delay(25, context.cancellationToken)
                finally
                    PhaseFObserved.record
                        label
                        (if context.cancellationToken.IsCancellationRequested then
                             "cancelled"
                         else
                             "completed")
            })

        handleStream (_.oversized) (fun _ _ (sizeInBytes: int) -> taskSeq { yield String('x', sizeInBytes) })

        handleStream (_.snapshot) (fun _ state ((label: string), (count: int)) ->
            taskSeq {
                for index in 0 .. count - 1 do
                    do! PhaseFGates.gate label index
                    PhaseFProduced.record label
                    yield state.counter
            })

        handleStream (_.faulty) (fun _ _ (failAt: int) ->
            taskSeq {
                for index in 0..99 do
                    if index = failAt then
                        failwith "the streaming handler failed mid-enumeration"

                    yield index
            })

        handleStream (_.empty) (fun _ _ () -> taskSeq { () })

        handle (_.ping) (fun _ state () -> task { return state, state.counter })

        handle (_.bump) (fun _ state (amount: int) ->
            let next = { state with counter = state.counter + amount }
            task { return next, next.counter })

        // `current` is declared readOnly, so its reply is all the runtime keeps -- bound with
        // `handleQuery`, which is the same dispatch path with the discarded state left unwritten.
        // `whereAmI` next door stays on `handle` over a readOnly operation, so both bindings of the
        // same declaration are exercised on a live cluster.
        handleQuery (_.current) (fun _ state () -> task { return state.counter })

        handle (_.whereAmI) (fun context state () ->
            task {
                let details = context.services.GetRequiredService<ILocalSiloDetails>()
                return state, details.SiloAddress.ToString()
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// A relay, so one grain enumerates another grain's stream
// ──────────────────────────────────────────────────────────────────────────────

[<NoEquality; NoComparison>]
type RelayApi =
    { /// Opens `watch` on the named ticker key and re-yields every item's note.
      relay: string * string * int -> IAsyncEnumerable<string>
      /// Consumes the same upstream stream twice INSIDE the grain — once directly, once through
      /// `taskSeq { for … }` — and reports both counts. Nothing of the streaming reply path is
      /// involved, so a difference is a property of the consuming construct alone.
      probe: string * string * int -> Task<int * int * int>
      whereAmI: unit -> Task<string> }

type RelayActor = private RelayActor of unit

let relayContract =
    grainContract<RelayActor, string, RelayApi> {
        grainType PhaseFGrainTypes.Relay
        version 1
        stringKey
        readOnly (_.whereAmI)
    }

let relayRef = FunctionalGrain.ref relayContract

let relayDefinition =
    grainFor relayContract {
        defaultState (fun () -> ())

        // Hand-written rather than `taskSeq { for tick in upstream.watch … do yield tick.note }`.
        // FSharp.Control.TaskSeq 0.6.0's wrapping combinators over an IAsyncEnumerable produced by
        // this runtime yield the last item twice when they run under an activation's task
        // scheduler — see the `probe` operation below and the discriminator test that measures it.
        // This is the shape a `taskSeq` relay is meant to be, and it is exactly `await foreach`.
        handleStream (_.relay) (fun context _ ((tickerKey: string), (label: string), (count: int)) ->
            { new IAsyncEnumerable<string> with
                member _.GetAsyncEnumerator(ct) =
                    let upstream = tickerRef context.grainFactory tickerKey
                    let inner = (upstream.watch (label, count)).GetAsyncEnumerator ct
                    let mutable current = ""

                    { new IAsyncEnumerator<string> with
                        member _.Current = current

                        member _.MoveNextAsync() =
                            ValueTask<bool>(
                                task {
                                    match! inner.MoveNextAsync() with
                                    | false -> return false
                                    | true ->
                                        PhaseFProduced.record $"{label}:in"
                                        current <- inner.Current.note
                                        return true
                                })
                      interface IAsyncDisposable with
                          member _.DisposeAsync() = inner.DisposeAsync() } })

        handle (_.probe) (fun context state ((tickerKey: string), (label: string), (count: int)) ->
            task {
                let upstream = tickerRef context.grainFactory tickerKey
                let! direct = TaskSeq.toListAsync (upstream.watch ($"{label}-direct", count))

                let! viaMap =
                    upstream.watch ($"{label}-map", count)
                    |> TaskSeq.map (fun tick -> tick.note)
                    |> TaskSeq.toListAsync

                let! viaFor =
                    TaskSeq.toListAsync (
                        taskSeq {
                            for tick in upstream.watch ($"{label}-for", count) do
                                yield tick.note
                        }
                    )

                return state, (List.length direct, List.length viaMap, List.length viaFor)
            })

        handle (_.whereAmI) (fun context state () ->
            task {
                let details = context.services.GetRequiredService<ILocalSiloDetails>()
                return state, details.SiloAddress.ToString()
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Version tolerance composed with streaming
// ──────────────────────────────────────────────────────────────────────────────

[<NoEquality; NoComparison>]
type VersionedApi =
    { /// Present since version 1.
      ticks: int -> IAsyncEnumerable<int>
      /// Introduced at version 2.
      extras: int -> IAsyncEnumerable<int> }

type VersionedActor = private VersionedActor of unit

/// The hosted contract: version 2, admitting version 1 as well.
let versionedContract =
    grainContract<VersionedActor, string, VersionedApi> {
        grainType PhaseFGrainTypes.Versioned
        version 2
        acceptsVersions (BackwardCompatible 1)
        stringKey
        sinceVersion 2 (_.extras)
    }

/// The same grain type as a version-1 caller sees it. Its stream-request token is computed at
/// version 1, which is exactly the token the version-tolerant host expects for a version-1 call.
let versionedV1Contract =
    grainContract<VersionedActor, string, VersionedApi> {
        grainType PhaseFGrainTypes.Versioned
        version 1
        stringKey
    }

let versionedRef = FunctionalGrain.ref versionedContract
let versionedV1Ref = FunctionalGrain.ref versionedV1Contract

let versionedDefinition =
    grainFor versionedContract {
        defaultState (fun () -> ())

        handleStream (_.ticks) (fun _ _ (count: int) ->
            taskSeq {
                for index in 1..count do
                    yield index
            })

        handleStream (_.extras) (fun _ _ (count: int) ->
            taskSeq {
                for index in 1..count do
                    yield index * 100
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Placement composed with streaming
// ──────────────────────────────────────────────────────────────────────────────

[<NoEquality; NoComparison>]
type PlacedApi =
    { numbers: int -> IAsyncEnumerable<int>
      whereAmI: unit -> Task<string> }

type PlacedActor = private PlacedActor of unit

let placedContract =
    grainContract<PlacedActor, string, PlacedApi> {
        grainType PhaseFGrainTypes.Placed
        version 1
        stringKey
        readOnly (_.whereAmI)
    }

let placedRef = FunctionalGrain.ref placedContract

let placedDefinition =
    grainFor placedContract {
        defaultState (fun () -> ())
        placement PreferLocal

        handleStream (_.numbers) (fun _ _ (count: int) ->
            taskSeq {
                for index in 1..count do
                    yield index
            })

        handle (_.whereAmI) (fun context state () ->
            task {
                let details = context.services.GetRequiredService<ILocalSiloDetails>()
                return state, details.SiloAddress.ToString()
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// mayInterleave composed with streaming
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Every operation ID the interleave predicate was consulted for.</summary>
[<RequireQualifiedAccess>]
module PhaseFPredicate =
    let private seen = ConcurrentQueue<string>()

    let record (operationId: string) = seen.Enqueue operationId

    let observed () = seen.ToArray() |> Array.toList

[<NoEquality; NoComparison>]
type SelectiveApi =
    { numbers: int -> IAsyncEnumerable<int>
      peek: unit -> Task<int>
      park: int -> Task<int> }

type SelectiveActor = private SelectiveActor of unit

/// <summary>
/// The predicate is metadata-only and is consulted on Orleans' own scheduling path, for the
/// incoming request AND for the one currently executing. An enumeration's messages are Orleans'
/// extension invokables, not functional requests — if the predicate could be confused by one it
/// would throw, and Orleans would reject the call that triggered it as transient. This contract
/// exists to make that a test rather than an assumption.
/// </summary>
let selectiveContract =
    grainContract<SelectiveActor, string, SelectiveApi> {
        grainType PhaseFGrainTypes.Selective
        version 1
        stringKey

        mayInterleave (fun metadata ->
            PhaseFPredicate.record metadata.OperationId
            metadata.OperationId = "peek")
    }

let selectiveRef = FunctionalGrain.ref selectiveContract

let selectiveDefinition =
    grainFor selectiveContract {
        defaultState (fun () -> 0)

        handleStream (_.numbers) (fun _ _ (count: int) ->
            taskSeq {
                for index in 1..count do
                    yield index
            })

        handle (_.peek) (fun _ state () -> task { return state, state })

        handle (_.park) (fun _ state (milliseconds: int) ->
            task {
                do! Task.Delay milliseconds
                return state + 1, state + 1
            })
    }

// ──────────────────────────────────────────────────────────────────────────────
// A journaled definition with a streaming operation
// ──────────────────────────────────────────────────────────────────────────────

type LedgerState = { entries: int list }

type LedgerEvent = Recorded of int

[<NoEquality; NoComparison>]
type LedgerApi =
    { record: int -> Task<int>
      /// Streams the confirmed entries, newest last. Raises nothing.
      entries: unit -> IAsyncEnumerable<int>
      count: unit -> Task<int> }

type LedgerActor = private LedgerActor of unit

let ledgerContract =
    grainContract<LedgerActor, string, LedgerApi> {
        grainType PhaseFGrainTypes.Ledger
        version 1
        stringKey
        readOnly (_.count)
    }

let ledgerRef = FunctionalGrain.ref ledgerContract

[<Literal>]
let LedgerProvider = "PhaseFLogStorage"

let ledgerDefinition =
    journaledGrainFor ledgerContract {
        initialEventState (fun _ -> { entries = [] })

        apply (fun state (Recorded value) ->
            { state with
                entries = state.entries @ [ value ] })

        logProvider LedgerProvider

        handle (_.record) (fun _ state (value: int) -> task { return [ Recorded value ], List.length state.entries + 1 })

        handleStream (_.entries) (fun _ state () ->
            taskSeq {
                for entry in state.entries do
                    yield entry
            })

        // Journaled `handleQuery`: `count` is readOnly, so it could never have appended anything
        // anyway, and the empty annotated event list it used to return is left unwritten.
        handleQuery (_.count) (fun _ state () -> task { return List.length state.entries })
    }

// ──────────────────────────────────────────────────────────────────────────────
// A C#-shaped facade over the streaming contract
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The claim under test: a consumer that knows nothing about this library writes
/// <c>await foreach</c> over a BCL <c>IAsyncEnumerable&lt;T&gt;</c>.
/// </summary>
type ITickerFacade =
    abstract Watch: label: string * count: int -> IAsyncEnumerable<Tick>
    abstract Ping: unit -> Task<int>

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type PhaseFSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.Services.Configure<FunctionalGrainTransportOptions>(fun
                                                                               (options:
                                                                                   FunctionalGrainTransportOptions) ->
                options.MaxPayloadBytes <- PhaseFLimits.Silo)
            |> ignore

            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider LedgerProvider |> ignore

            siloBuilder.AddFunctionalGrain tickerDefinition |> ignore
            siloBuilder.AddFunctionalGrain relayDefinition |> ignore
            siloBuilder.AddFunctionalGrain versionedDefinition |> ignore
            siloBuilder.AddFunctionalGrain placedDefinition |> ignore
            siloBuilder.AddFunctionalGrain selectiveDefinition |> ignore
            siloBuilder.AddFunctionalJournaledGrain ledgerDefinition |> ignore

type PhaseFClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.Services.Configure<FunctionalGrainTransportOptions>(fun
                                                                                 (options:
                                                                                     FunctionalGrainTransportOptions) ->
                options.MaxPayloadBytes <- PhaseFLimits.Client)
            |> ignore

            clientBuilder.AddFunctionalGrainClient() |> ignore

/// <summary>
/// Two silos, so a relayed enumeration can be a genuine silo-to-silo one rather than a hopeful
/// same-process call.
/// </summary>
[<Sealed>]
type FunctionalPhaseFFixture() =
    let cluster =
        let builder = TestClusterBuilder 2s
        builder.AddSiloBuilderConfigurator<PhaseFSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<PhaseFClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Cluster = cluster
    member _.Client = cluster.Client

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalPhaseF")>]
type FunctionalPhaseFCollection() =
    interface ICollectionFixture<FunctionalPhaseFFixture>
