/// <summary>
/// The spec 004 Phase C fixture: a two-silo cluster hosting functional definitions that declare
/// <c>reentrant</c>, <c>mayInterleave</c>, and <c>acceptsVersions</c> / <c>sinceVersion</c>,
/// together with the client-side contracts (older versions over the same grain types) the version
/// tests bind.
/// </summary>
module Orleans.FSharp.Integration.FunctionalPhaseCFixture

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Metadata
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Grain types
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module PhaseCGrainTypes =
    /// A whole-grain reentrant definition.
    [<Literal>]
    let Reentrant = "phasec.reentrant"

    /// The same API with no interleaving policy at all: the negative control.
    [<Literal>]
    let Plain = "phasec.plain"

    /// A definition whose predicate admits exactly one operation.
    [<Literal>]
    let Selective = "phasec.selective"

    /// A definition whose predicate throws for one operation.
    [<Literal>]
    let Throwing = "phasec.throwing"

    /// A version-4 definition admitting versions 3 and 4.
    [<Literal>]
    let Tolerant = "phasec.tolerant"

    /// A version-4 definition on the default Exact policy.
    [<Literal>]
    let Strict = "phasec.strict"

    /// A single-worker stateless-worker definition that is ALSO reentrant.
    [<Literal>]
    let ReentrantWorker = "phasec.reentrantworker"

// ──────────────────────────────────────────────────────────────────────────────
// Out-of-band gate observation
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The gate one parked handler waits on. Silos of a <c>TestCluster</c> share one process, so a
/// test observes the gate WITHOUT calling the grain — which matters for every negative control:
/// a call that cannot interleave cannot report anything either, so a grain call is not a usable
/// way to learn that a handler has parked.
/// </summary>
[<Sealed>]
type PhaseCGate() =
    let mutable entered = 0

    member val Gate =
        TaskCompletionSource<bool> TaskCreationOptions.RunContinuationsAsynchronously with get

    member _.Entered = Volatile.Read(&entered) = 1
    member _.Enter() = Volatile.Write(&entered, 1)
    member _.Leave() = Volatile.Write(&entered, 0)

[<RequireQualifiedAccess>]
module PhaseCGates =
    let private cells = ConcurrentDictionary<string, PhaseCGate>()

    /// <summary>The gate of one domain key. Tests use a fresh key per case.</summary>
    let cell (key: string) = cells.GetOrAdd(key, fun _ -> PhaseCGate())

    /// <summary>Wait until a handler for that key has parked; false if it never did.</summary>
    let waitForEntry (key: string) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 10.0

            while not (cell key).Entered && DateTime.UtcNow < deadline do
                do! Task.Delay 25

            return (cell key).Entered
        }

/// <summary>Park until released or until the timeout expires, reporting which happened.</summary>
let private parkOn (key: string) (timeout: int) =
    task {
        let cell = PhaseCGates.cell key
        cell.Enter()

        try
            let! finished = Task.WhenAny(cell.Gate.Task, Task.Delay timeout)

            return
                if obj.ReferenceEquals(finished, cell.Gate.Task) then
                    "released"
                else
                    "timeout"
        finally
            cell.Leave()
    }

// ──────────────────────────────────────────────────────────────────────────────
// Item 5 — reentrancy variants
// ──────────────────────────────────────────────────────────────────────────────

type ReentrantActor = private ReentrantActor of unit
type PlainActor = private PlainActor of unit
type SelectiveActor = private SelectiveActor of unit
type ThrowingActor = private ThrowingActor of unit

[<NoEquality; NoComparison>]
type GateApi =
    { /// Parks the activation until released or the millisecond timeout expires.
      park: int -> Task<string>
      /// Releases whatever is parked on this grain.
      release: unit -> Task<string>
      /// Reads the whole state, parks, then appends and publishes the replacement.
      slowAppend: string -> Task<unit>
      /// Appends and publishes immediately.
      fastAppend: string -> Task<unit>
      /// Every published note.
      notes: unit -> Task<string list> }

/// <summary>The gate handlers shared by the reentrant definition and its plain control.</summary>
let private gateDefinition (contract: GrainContract<'Actor, string, GateApi>) =
    grainFor contract {
        defaultState (fun () -> ([]: string list))

        handle (_.park) (fun context state (timeout: int) ->
            task {
                let! outcome = parkOn context.key timeout
                return state, outcome
            })

        handle (_.release) (fun context state () ->
            task {
                (PhaseCGates.cell context.key).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        // Reads the state it started with, waits, and then publishes a replacement built from
        // that stale snapshot. On a reentrant activation this is the lost-update shape.
        handle (_.slowAppend) (fun context state (note: string) ->
            task {
                let snapshot = state
                let! _ = parkOn context.key 4000
                return snapshot @ [ note ], ()
            })

        handle (_.fastAppend) (fun _ state (note: string) -> task { return state @ [ note ], () })

        handle (_.notes) (fun _ state () -> task { return state, state })
    }

let reentrantContract =
    grainContract<ReentrantActor, string, GateApi> () {
        grainType PhaseCGrainTypes.Reentrant
        version 1
        stringKey
        reentrant
        readOnly (_.notes)
    }

let plainContract =
    grainContract<PlainActor, string, GateApi> () {
        grainType PhaseCGrainTypes.Plain
        version 1
        stringKey
        readOnly (_.notes)
    }

let reentrantDefinition = gateDefinition reentrantContract
let plainDefinition = gateDefinition plainContract

type ReentrantWorkerActor = private ReentrantWorkerActor of unit

/// <summary>
/// Reentrancy composed with Phase A's <c>statelessWorker</c>. Orleans routes a message for a
/// stateless worker through <c>StatelessWorkerGrainContext</c>, which special-cases
/// <c>GrainCanInterleave</c> in <c>SetComponent</c> and forwards it to the shared context — so the
/// component the reentrant property installs is visible to each worker's own
/// <c>ActivationData.MayInvokeRequest</c>. With <c>maxLocalWorkers = 1</c> there is exactly one
/// worker, so a second call has nowhere else to go: it either interleaves on that worker or waits.
/// </summary>
let reentrantWorkerContract =
    grainContract<ReentrantWorkerActor, string, GateApi> () {
        grainType PhaseCGrainTypes.ReentrantWorker
        version 1
        stringKey
        reentrant
        readOnly (_.notes)
    }

let reentrantWorkerDefinition =
    grainFor reentrantWorkerContract {
        defaultState (fun () -> ([]: string list))
        statelessWorker 1

        handle (_.park) (fun context state (timeout: int) ->
            task {
                let! outcome = parkOn context.key timeout
                return state, outcome
            })

        handle (_.release) (fun context state () ->
            task {
                (PhaseCGates.cell context.key).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        handle (_.slowAppend) (fun context state (note: string) ->
            task {
                let snapshot = state
                let! _ = parkOn context.key 4000
                return snapshot @ [ note ], ()
            })

        handle (_.fastAppend) (fun _ state (note: string) -> task { return state @ [ note ], () })
        handle (_.notes) (fun _ state () -> task { return state, state })
    }

[<NoEquality; NoComparison>]
type SelectiveApi =
    { park: int -> Task<string>
      release: unit -> Task<string>
      /// The operation the predicate refuses.
      blocked: unit -> Task<string> }

/// <summary>
/// The predicate is a statement about which operations are safe to overlap, not a one-sided
/// allow-list: Orleans admits an incoming request when the predicate accepts EITHER it or the
/// request currently executing.
/// </summary>
let selectiveContract =
    grainContract<SelectiveActor, string, SelectiveApi> () {
        grainType PhaseCGrainTypes.Selective
        version 1
        stringKey
        mayInterleave (fun metadata -> metadata.OperationId = "release")
    }

let selectiveDefinition =
    grainFor selectiveContract {
        defaultState (fun () -> 0)

        handle (_.park) (fun context state (timeout: int) ->
            task {
                let! outcome = parkOn context.key timeout
                return state, outcome
            })

        handle (_.release) (fun context state () ->
            task {
                (PhaseCGates.cell context.key).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        handle (_.blocked) (fun _ state () -> task { return state, "blocked-ran" })
    }

[<Literal>]
let ThrowingPredicateMessage = "the phase-C probe predicate refuses to decide"

[<NoEquality; NoComparison>]
type ThrowingApi =
    { park: int -> Task<string>
      release: unit -> Task<string>
      /// The operation whose admission decision throws.
      boom: unit -> Task<string> }

let throwingContract =
    grainContract<ThrowingActor, string, ThrowingApi> () {
        grainType PhaseCGrainTypes.Throwing
        version 1
        stringKey

        mayInterleave (fun metadata ->
            if metadata.OperationId = "boom" then
                raise (ApplicationException ThrowingPredicateMessage)
            else
                metadata.OperationId = "release")
    }

let throwingDefinition =
    grainFor throwingContract {
        defaultState (fun () -> 0)

        handle (_.park) (fun context state (timeout: int) ->
            task {
                let! outcome = parkOn context.key timeout
                return state, outcome
            })

        handle (_.release) (fun context state () ->
            task {
                (PhaseCGates.cell context.key).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        handle (_.boom) (fun _ state () -> task { return state, "boom-ran" })
    }

// ──────────────────────────────────────────────────────────────────────────────
// Item 7 — version-tolerant contracts
// ──────────────────────────────────────────────────────────────────────────────

type TolerantActor = private TolerantActor of unit
type StrictActor = private StrictActor of unit

[<NoEquality; NoComparison>]
type VersionedApi =
    { echo: string -> Task<string>
      /// Writes to the activation's state, so a v3 and a v4 call can be shown to address the
      /// same activation and the same state.
      stash: string -> Task<unit>
      peek: unit -> Task<string>
      /// Introduced at version 4: refused for a call admitted at version 3.
      fresh: unit -> Task<string> }

/// <summary>The hosted contract: version 4, admitting 3 and 4, with one v4-only operation.</summary>
let tolerantV4 =
    grainContract<TolerantActor, string, VersionedApi> () {
        grainType PhaseCGrainTypes.Tolerant
        version 4
        stringKey
        acceptsVersions (BackwardCompatible 3)
        sinceVersion 4 (_.fresh)
        readOnly (_.peek)
    }

/// <summary>
/// A version-3 caller's shape over the SAME grain type. Nothing hosts it; it is what a silo still
/// running the previous release sends during a rolling deploy, and it is bound here from the
/// client so the whole request — its version, its version-derived protocol tokens, its reply-token
/// expectation — is a genuine v3 request and not a v4 one with a field overwritten.
/// </summary>
let tolerantV3 =
    grainContract<TolerantActor, string, VersionedApi> () {
        grainType PhaseCGrainTypes.Tolerant
        version 3
        stringKey
        readOnly (_.peek)
    }

/// <summary>
/// A version-3 caller whose contract declares the SAME operations at the SAME IDs but drops
/// <c>readOnly</c> from <c>peek</c>. Admission flags travel in the envelope and are compared
/// against the hosted descriptor, so this is a wire-shape change inside the accepted range —
/// exactly what "accepting a version asserts wire compatibility" is about.
/// </summary>
let tolerantV3Reflagged =
    grainContract<TolerantActor, string, VersionedApi> () {
        grainType PhaseCGrainTypes.Tolerant
        version 3
        stringKey
    }

/// <summary>One version below the admitted floor.</summary>
let tolerantV2 =
    grainContract<TolerantActor, string, VersionedApi> () {
        grainType PhaseCGrainTypes.Tolerant
        version 2
        stringKey
        readOnly (_.peek)
    }

let private versionedHandlers (contract: GrainContract<'Actor, string, VersionedApi>) =
    grainFor contract {
        defaultState (fun () -> "")
        handle (_.echo) (fun _ state (message: string) -> task { return state, "echo:" + message })
        handle (_.stash) (fun _ _ (value: string) -> task { return value, () })
        handle (_.peek) (fun _ state () -> task { return state, state })
        handle (_.fresh) (fun _ state () -> task { return state, "fresh-ran" })
    }

let tolerantDefinition = versionedHandlers tolerantV4

/// <summary>The same shape at version 4 on the DEFAULT policy, so the old rejection is pinned.</summary>
let strictV4 =
    grainContract<StrictActor, string, VersionedApi> () {
        grainType PhaseCGrainTypes.Strict
        version 4
        stringKey
        readOnly (_.peek)
    }

let strictV3 =
    grainContract<StrictActor, string, VersionedApi> () {
        grainType PhaseCGrainTypes.Strict
        version 3
        stringKey
        readOnly (_.peek)
    }

let strictDefinition = versionedHandlers strictV4

// ──────────────────────────────────────────────────────────────────────────────
// Bound references
// ──────────────────────────────────────────────────────────────────────────────

let reentrantRef = FunctionalGrain.ref reentrantContract
let reentrantWorkerRef = FunctionalGrain.ref reentrantWorkerContract
let plainRef = FunctionalGrain.ref plainContract
let selectiveRef = FunctionalGrain.ref selectiveContract
let throwingRef = FunctionalGrain.ref throwingContract
let tolerantV4Ref = FunctionalGrain.ref tolerantV4
let tolerantV3Ref = FunctionalGrain.ref tolerantV3
let tolerantV3ReflaggedRef = FunctionalGrain.ref tolerantV3Reflagged
let tolerantV2Ref = FunctionalGrain.ref tolerantV2
let strictV4Ref = FunctionalGrain.ref strictV4
let strictV3Ref = FunctionalGrain.ref strictV3

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type PhaseCSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain reentrantDefinition |> ignore
            siloBuilder.AddFunctionalGrain plainDefinition |> ignore
            siloBuilder.AddFunctionalGrain reentrantWorkerDefinition |> ignore
            siloBuilder.AddFunctionalGrain selectiveDefinition |> ignore
            siloBuilder.AddFunctionalGrain throwingDefinition |> ignore
            siloBuilder.AddFunctionalGrain tolerantDefinition |> ignore
            siloBuilder.AddFunctionalGrain strictDefinition |> ignore

type PhaseCClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

/// <summary>
/// Two silos: an interleaving decision is taken on whichever silo the activation lives on, and
/// the version policy is published in the grain manifest that gossips between them, so neither
/// feature is allowed to work only in the single-silo case.
/// </summary>
[<Sealed>]
type FunctionalPhaseCFixture() =
    let cluster =
        let builder = TestClusterBuilder 2s
        builder.AddSiloBuilderConfigurator<PhaseCSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<PhaseCClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Cluster = cluster
    member _.Client = cluster.Client

    /// <summary>Every silo's own local grain manifest, by silo name.</summary>
    member _.LocalManifests =
        cluster.Silos
        |> Seq.map (fun handle ->
            handle.Name,
            (handle :?> InProcessSiloHandle)
                .SiloHost.Services.GetRequiredService<IClusterManifestProvider>()
                .LocalGrainManifest)
        |> Seq.toList

    /// <summary>The published grain properties of one grain type, on the primary silo.</summary>
    member this.PropertiesOf(grainTypeName: string) =
        let _, manifest = this.LocalManifests |> List.head

        match manifest.Grains.TryGetValue(GrainType.Create grainTypeName) with
        | true, properties -> properties.Properties |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
        | _ -> Map.empty

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalPhaseC")>]
type FunctionalPhaseCCollection() =
    interface ICollectionFixture<FunctionalPhaseCFixture>
