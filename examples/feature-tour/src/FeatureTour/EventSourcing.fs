/// <summary>
/// Experiment 15 — functional event sourcing on the functional runtime: a
/// <c>journaledGrainFor</c> definition whose state is the fold of an event journal kept by an
/// Orleans log-consistency provider. Both built-in providers are driven, because they store
/// completely different things and only their application-visible behaviour agrees.
/// </summary>
namespace FeatureTour.EventSourcing

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans.FSharp

// ── The domain ───────────────────────────────────────────────────────────────

/// <summary>The state. It is never written anywhere: it is what <c>apply</c> folds out of the journal.</summary>
type Balance =
    { amount: decimal
      entries: string list }

/// <summary>The events. Handlers raise these; only <c>apply</c> turns them into state.</summary>
type BalanceEvent =
    | Deposited of decimal
    | Withdrawn of decimal

[<NoEquality; NoComparison>]
type BalanceApi =
    { /// Raises Deposited and replies with the balance the handler computed itself.
      deposit: decimal -> Task<decimal>
      /// Raises Withdrawn only when the funds are there.
      withdraw: decimal -> Task<bool>
      /// A query: no events, and therefore no storage write at all.
      snapshot: unit -> Task<Balance * int>
      /// Declared readOnly and yet raises an event: the negative control.
      readOnlyRaise: unit -> Task<string>
      /// Ends the activation, so the next call has to replay the journal.
      goIdle: unit -> Task<unit> }

/// <summary>The one fold both provider variants share.</summary>
module BalanceFold =
    let apply (state: Balance) event =
        match event with
        | Deposited amount ->
            { state with
                amount = state.amount + amount
                entries = state.entries @ [ $"+{amount}" ] }
        | Withdrawn amount ->
            { state with
                amount = state.amount - amount
                entries = state.entries @ [ $"-{amount}" ] }

// ── Two definitions, one per built-in provider ───────────────────────────────

type LogJournalActor = private LogJournalActor of unit
type StateJournalActor = private StateJournalActor of unit

[<RequireQualifiedAccess>]
module JournalProviders =
    /// Stores the whole event log; every activation folds all of it.
    [<Literal>]
    let LogStorage = "TourLogStorage"

    /// Stores the folded view and the log position; nothing is replayed.
    [<Literal>]
    let StateStorage = "TourStateStorage"

    [<Literal>]
    let Store = "TourJournalStore"

[<RequireQualifiedAccess>]
module BalanceApi =
    [<Literal>]
    let LogGrainType = "tour.journal.log"

    [<Literal>]
    let StateGrainType = "tour.journal.state"

    let logContract =
        grainContract<LogJournalActor, string, BalanceApi> () {
            grainType LogGrainType
            version 1
            stringKey
            readOnly (_.snapshot)
            readOnly (_.readOnlyRaise)
        }

    let stateContract =
        grainContract<StateJournalActor, string, BalanceApi> () {
            grainType StateGrainType
            version 1
            stringKey
            readOnly (_.snapshot)
            readOnly (_.readOnlyRaise)
        }

    let logRef = FunctionalGrain.ref logContract
    let stateRef = FunctionalGrain.ref stateContract

[<RequireQualifiedAccess>]
module BalanceDefinition =

    /// <summary>One journaled definition, instantiated once per provider.</summary>
    let forProvider (contract: GrainContract<'Actor, string, BalanceApi>) (provider: string) =
        journaledGrainFor contract {
            initialEventState (fun (key: string) ->
                { amount = 0m
                  entries = [ $"opened:{key}" ] })

            apply BalanceFold.apply

            logProvider provider
            journalStorage JournalProviders.Store

            // The activation hook raises no events and returns no state: on a journaled
            // definition the journal is the only way to change anything.
            onActivate (fun context _state ->
                task {
                    context.logger.LogDebug(
                        "journaled activation of {GrainId} replayed to version {Version}",
                        context.grainId,
                        context.journalVersion
                    )
                })

            handle (_.deposit) (fun _ state (amount: decimal) ->
                task { return [ Deposited amount ], state.amount + amount })

            handle (_.withdraw) (fun _ state (amount: decimal) ->
                task {
                    if state.amount < amount then
                        // No event: a refused command leaves no trace in the journal.
                        return [], false
                    else
                        return [ Withdrawn amount ], true
                })

            handle (_.snapshot) (fun context state () -> task { return [], (state, context.journalVersion) })

            handle (_.readOnlyRaise) (fun _ state () -> task { return [ Deposited 1m ], "the append was ACCEPTED" })

            handle (_.goIdle) (fun context state () ->
                task {
                    context.deactivateOnIdle ()
                    return [], ()
                })
        }

    let log = forProvider BalanceApi.logContract JournalProviders.LogStorage
    let state = forProvider BalanceApi.stateContract JournalProviders.StateStorage
