/// <summary>
/// Tour section 16 (spec 004 item 6) — server-streaming replies: an API field that returns
/// <c>IAsyncEnumerable&lt;'Item&gt;</c> instead of <c>Task&lt;'Reply&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism is Orleans' own <c>IAsyncEnumerableGrainExtension</c>, the same extension a
/// codegen grain method returning <c>IAsyncEnumerable&lt;T&gt;</c> uses. Every message of an
/// enumeration carries Orleans' <c>[AlwaysInterleave]</c>, which is what the section's third
/// observation demonstrates: an ordinary call to the same activation completes while a stream is
/// open.
/// </para>
/// <para>
/// The producers are gated rather than timed. Orleans drains up to <c>MaxBatchSize</c>
/// synchronously-available elements into one reply, so a producer that never awaits genuinely does
/// produce everything before the first item reaches the caller — a section that only counted items
/// could not tell the two apart. Holding the producer at a gate per item makes "the caller saw
/// item n while the producer was still blocked before n+1" a checkable statement.
/// </para>
/// </remarks>
namespace FeatureTour.StreamingReplies

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Orleans.FSharp

/// <summary>One gate per (stream, item), so a producer can be held between items.</summary>
[<RequireQualifiedAccess>]
module Gates =
    let private gates = ConcurrentDictionary<string, TaskCompletionSource>()

    let private cell (name: string) =
        gates.GetOrAdd(name, fun _ -> TaskCompletionSource TaskCreationOptions.RunContinuationsAsynchronously)

    let gate (label: string) (index: int) = (cell $"{label}#{index}").Task
    let release (label: string) (index: int) = (cell $"{label}#{index}").TrySetResult() |> ignore

    let releaseAll (label: string) (count: int) =
        for index in 0 .. count - 1 do
            release label index

/// <summary>How many items each labelled producer has produced, and what it observed at the end.</summary>
[<RequireQualifiedAccess>]
module Produced =
    let private counts = ConcurrentDictionary<string, int ref>()
    let private endings = ConcurrentDictionary<string, string>()

    let record (label: string) =
        let cell = counts.GetOrAdd(label, fun _ -> ref 0)
        Interlocked.Increment cell |> ignore

    let count (label: string) =
        match counts.TryGetValue label with
        | true, cell -> cell.Value
        | _ -> 0

    let ended (label: string) (outcome: string) = endings.[label] <- outcome

    let ending (label: string) =
        match endings.TryGetValue label with
        | true, outcome -> outcome
        | _ -> "(still running)"

type TickerActor = private TickerActor of unit

/// <summary>One streamed item; an application record, not a primitive.</summary>
type Tick = { index: int; note: string }

[<NoEquality; NoComparison>]
type TickerApi =
    { /// Yields <c>count</c> ticks, waiting at a per-item gate before each one.
      watch: string * int -> IAsyncEnumerable<Tick>
      /// Yields forever until the enumeration is cancelled, then records what it saw.
      follow: string -> IAsyncEnumerable<int>
      /// An ordinary call, so the section can show one completing while a stream is open.
      ping: unit -> Task<int>
      /// Publishes a new state value.
      bump: int -> Task<int> }

[<RequireQualifiedAccess>]
module TickerApi =
    [<Literal>]
    let GrainType = "tour.ticker"

    let contract =
        grainContract<TickerActor, string, TickerApi> () {
            grainType GrainType
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module TickerDefinition =
    let definition =
        grainFor TickerApi.contract {
            defaultState (fun () -> 0)

            // `handleStream`, not `handle`: the field returns IAsyncEnumerable<'Item>, the handler
            // returns items only, and there is no replacement state — a stream produces across many
            // turns of the activation, so a whole-state replacement published when it ended would
            // overwrite everything the turns it overlapped had done.
            handleStream (_.watch) (fun _ _ ((label: string), (count: int)) ->
                taskSeq {
                    for index in 0 .. count - 1 do
                        do! Gates.gate label index
                        Produced.record label
                        yield { index = index; note = $"{label}#{index}" }
                })

            handleStream (_.follow) (fun context _ (label: string) ->
                taskSeq {
                    try
                        let mutable index = 0

                        while true do
                            Produced.record label
                            yield index
                            index <- index + 1
                            // The enumeration's own token: Orleans cancels it when the caller
                            // disposes the enumerator.
                            do! Task.Delay(25, context.cancellationToken)
                    finally
                        Produced.ended
                            label
                            (if context.cancellationToken.IsCancellationRequested then
                                 "cancelled, finally ran"
                             else
                                 "completed")
                })

            handle (_.ping) (fun _ state () -> task { return state, state })
            handle (_.bump) (fun _ state (amount: int) -> task { return state + amount, state + amount })
        }

[<RequireQualifiedAccess>]
module Refusals =
    /// <summary>
    /// The negative control: an open enumeration lives in ONE activation's grain extension, and a
    /// stateless worker routes each message to whichever local worker is free, on whichever silo
    /// the caller reached — so the pairing is refused while the definition is being sealed rather
    /// than left to abort a stream mid-flight under load.
    /// </summary>
    let statelessWorkerWithAStream () =
        try
            grainFor TickerApi.contract {
                defaultState (fun () -> 0)
                statelessWorker 4
                handleStream (_.watch) (fun _ _ ((_: string), (_: int)) -> taskSeq { () })
                handleStream (_.follow) (fun _ _ (_: string) -> taskSeq { () })
                handle (_.ping) (fun _ state () -> task { return state, state })
                handle (_.bump) (fun _ state (amount: int) -> task { return state + amount, state + amount })
            }
            |> ignore

            "ACCEPTED — which it must not be"
        with error ->
            error.Message
