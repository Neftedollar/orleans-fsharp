/// <summary>
/// Experiment 13 — reentrancy variants on the functional runtime: <c>reentrant</c> makes a whole
/// grain admit overlapping calls, and <c>mayInterleave</c> decides it per request from the
/// request's own protocol metadata. Both are contract operations; neither needs code generation
/// or an attribute in application code.
/// </summary>
namespace FeatureTour.Interleaving

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Orleans.FSharp

/// <summary>
/// The gate a parked handler waits on, keyed by grain key. The tour reads it WITHOUT calling the
/// grain, because a call that is not allowed to interleave cannot report anything either — asking
/// the grain "have you parked yet?" would be the very thing the negative control forbids.
/// </summary>
[<RequireQualifiedAccess>]
module Gate =
    [<Sealed>]
    type Cell() =
        let mutable entered = 0

        member val Release =
            TaskCompletionSource<bool> TaskCreationOptions.RunContinuationsAsynchronously with get

        member _.Entered = Volatile.Read(&entered) = 1
        member _.Enter() = Volatile.Write(&entered, 1)
        member _.Leave() = Volatile.Write(&entered, 0)

    let private cells = ConcurrentDictionary<string, Cell>()

    let cell (key: string) = cells.GetOrAdd(key, fun _ -> Cell())

    /// <summary>Park until released or until the millisecond budget runs out.</summary>
    let park (key: string) (budgetMs: int) =
        task {
            let cell = cell key
            cell.Enter()

            try
                let! finished = Task.WhenAny(cell.Release.Task, Task.Delay budgetMs)

                return
                    if obj.ReferenceEquals(finished, cell.Release.Task) then
                        "released while still inside the activation"
                    else
                        "timed out — nothing reached the activation"
            finally
                cell.Leave()
        }

    /// <summary>Wait for a handler on that key to park; false if none did.</summary>
    let waitForEntry (key: string) =
        task {
            let deadline = System.DateTime.UtcNow.AddSeconds 15.0

            while not (cell key).Entered && System.DateTime.UtcNow < deadline do
                do! Task.Delay 25

            return (cell key).Entered
        }

// ── Whole-grain reentrancy, and the control that has none ────────────────────

type ReentrantActor = private ReentrantActor of unit
type SerialActor = private SerialActor of unit

[<NoEquality; NoComparison>]
type GateApi =
    { /// Parks the activation for up to N milliseconds.
      park: int -> Task<string>
      /// Releases whatever is parked. Reaching it AT ALL is the observation.
      release: unit -> Task<string>
      /// Reads the state it starts with, parks, then publishes a replacement built from it.
      slowAppend: string -> Task<unit>
      /// Appends and publishes immediately.
      fastAppend: string -> Task<unit>
      /// Everything currently published.
      notes: unit -> Task<string list> }

[<RequireQualifiedAccess>]
module GateApi =
    [<Literal>]
    let ReentrantGrainType = "tour.reentrant"

    [<Literal>]
    let SerialGrainType = "tour.serial"

    /// <summary>One operation on the contract makes the whole activation reentrant.</summary>
    let reentrant =
        grainContract<ReentrantActor, string, GateApi> {
            grainType ReentrantGrainType
            version 1
            stringKey
            reentrant
            readOnly (_.notes)
        }

    /// <summary>The identical contract WITHOUT it: the negative control.</summary>
    let serial =
        grainContract<SerialActor, string, GateApi> {
            grainType SerialGrainType
            version 1
            stringKey
            readOnly (_.notes)
        }

    let refReentrant = FunctionalGrain.ref reentrant
    let refSerial = FunctionalGrain.ref serial

[<RequireQualifiedAccess>]
module GateDefinition =

    let private handlers (contract: GrainContract<'Actor, string, GateApi>) =
        grainFor contract {
            defaultState (fun () -> ([]: string list))

            handle (_.park) (fun context state (budget: int) ->
                task {
                    let! outcome = Gate.park context.key budget
                    return state, outcome
                })

            handle (_.release) (fun context state () ->
                task {
                    (Gate.cell context.key).Release.TrySetResult true |> ignore
                    return state, "ok"
                })

            handle (_.slowAppend) (fun context state (note: string) ->
                task {
                    // The snapshot this handler started with. Whatever an interleaved handler
                    // publishes meanwhile is invisible to it.
                    let snapshot = state
                    let! _ = Gate.park context.key 8000
                    return snapshot @ [ note ], ()
                })

            handle (_.fastAppend) (fun _ state (note: string) -> task { return state @ [ note ], () })

            handle (_.notes) (fun _ state () -> task { return state, state })
        }

    let reentrant = handlers GateApi.reentrant
    let serial = handlers GateApi.serial

// ── A per-request predicate ──────────────────────────────────────────────────

type SelectiveActor = private SelectiveActor of unit

[<NoEquality; NoComparison>]
type SelectiveApi =
    { park: int -> Task<string>
      /// Named by the predicate: allowed to enter a busy activation.
      release: unit -> Task<string>
      /// Not named by the predicate: queued behind whatever is running.
      audit: unit -> Task<string> }

/// <summary>Which operations the predicate saw, so the transcript can show it really ran.</summary>
[<RequireQualifiedAccess>]
module PredicateLog =
    let private entries = ConcurrentQueue<string>()

    let add (entry: string) = entries.Enqueue entry
    let all () = entries |> List.ofSeq

    let countOf (prefix: string) =
        entries
        |> Seq.filter (fun entry -> entry.StartsWith(prefix, System.StringComparison.Ordinal))
        |> Seq.length

[<RequireQualifiedAccess>]
module SelectiveApi =
    [<Literal>]
    let GrainType = "tour.selective"

    let contract =
        grainContract<SelectiveActor, string, SelectiveApi> {
            grainType GrainType
            version 1
            stringKey

            // METADATA ONLY. IFunctionalRequestMetadata carries the grain type, the contract
            // version, the operation id, the three admission flags, and the payload LENGTH --
            // never the payload itself, which is not deserialized until dispatch admits the
            // request. That is spec 003's protocol-before-payload invariant, held on a path
            // Orleans runs before dispatch is reached at all.
            mayInterleave (fun metadata ->
                let admitted = metadata.OperationId = "release"
                PredicateLog.add $"{metadata.OperationId} -> {admitted}"
                admitted)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module SelectiveDefinition =
    let definition =
        grainFor SelectiveApi.contract {
            defaultState (fun () -> 0)

            handle (_.park) (fun context state (budget: int) ->
                task {
                    let! outcome = Gate.park context.key budget
                    return state, outcome
                })

            handle (_.release) (fun context state () ->
                task {
                    (Gate.cell context.key).Release.TrySetResult true |> ignore
                    return state, "ok"
                })

            handle (_.audit) (fun _ state () -> task { return state, "audit ran" })
        }
