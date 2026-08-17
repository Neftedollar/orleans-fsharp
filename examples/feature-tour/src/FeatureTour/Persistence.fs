/// <summary>
/// Feature 1 — persistence: a primary <c>stateFrom</c> holder plus a second, independently
/// typed <c>usePersistentState</c> holder on a different provider, with explicit read / write /
/// clear and a <c>RecordExists</c> observation that survives a deactivation.
/// </summary>
namespace FeatureTour.Persistence

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Orleans.FSharp

/// <summary>
/// Counts activations per grain id. Nothing about the activation number belongs in durable
/// state, so it is observed out of band, in this process, by the <c>onActivate</c> hook.
/// </summary>
[<RequireQualifiedAccess>]
module ActivationProbe =
    let private counts = ConcurrentDictionary<string, int ref>()

    /// <summary>Record one activation of the given grain id and return the new count.</summary>
    let record (grainId: string) =
        let cell = counts.GetOrAdd(grainId, fun _ -> ref 0)
        Interlocked.Increment cell

    /// <summary>How many times this process has activated the given grain id.</summary>
    let count (grainId: string) =
        match counts.TryGetValue grainId with
        | true, cell -> cell.Value
        | _ -> 0

type LedgerActor = private LedgerActor of unit

/// <summary>The primary durable state, loaded by <c>stateFrom</c>.</summary>
type LedgerState = { balance: int64; entries: int }

/// <summary>The second holder's stored type — a different type, a different provider.</summary>
type AuditState = { events: string list }

/// <summary>Everything one <c>snapshot</c> call reports back to the driver.</summary>
type LedgerSnapshot =
    { balance: int64
      entries: int
      activations: int
      primaryRecordExists: bool
      auditRecordExists: bool
      auditEvents: string list }

[<NoEquality; NoComparison>]
type LedgerApi =
    { /// Appends an amount, then writes BOTH holders explicitly.
      deposit: int64 -> Task<int64>
      /// Read-only view of both holders plus the activation counter.
      snapshot: unit -> Task<LedgerSnapshot>
      /// Explicit ReadStateAsync on the primary holder, re-reading storage under the activation.
      reload: unit -> Task<int64>
      /// Explicit ClearStateAsync on both holders — RecordExists goes back to false.
      /// Replies with what Orleans left in the cleared facets (see the handler).
      clear: unit -> Task<string>
      /// Requests deactivation once this turn completes.
      goIdle: unit -> Task<unit> }

[<RequireQualifiedAccess>]
module LedgerApi =
    let contract =
        grainContract<LedgerActor, string, LedgerApi> () {
            // Explicit grainType is REQUIRED here, not stylistic: a definition that attaches
            // stateFrom / usePersistentState / onReminder may not rely on the derived default,
            // because renaming the brand would silently move the durable record.
            grainType "tour.ledger"
            version 1
            stringKey

            readOnly (_.snapshot)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module LedgerDefinition =

    /// The primary holder: its loaded value IS the handler's `state` argument.
    let primary = PersistentState.create<LedgerState> "ledger" "Default"

    /// A second, independently typed holder on a DIFFERENT provider. State names must be unique
    /// within a definition even across providers — Orleans derives the per-facet
    /// activation-migration key from the state name.
    let audit = PersistentState.create<AuditState> "audit" "Audit"

    let definition =
        grainFor LedgerApi.contract {
            defaultState (fun () -> { balance = 0L; entries = 0 })

            stateFrom primary
            usePersistentState audit (fun _key -> { events = [] })

            onActivate (fun context state ->
                task {
                    ActivationProbe.record (string context.grainId) |> ignore
                    return state
                })

            handle
                (_.deposit)
                (fun context state amount ->
                    task {
                        let next =
                            { balance = state.balance + amount
                              entries = state.entries + 1 }

                        // Nothing writes storage on your behalf: returning `next` publishes it in
                        // memory only. Both writes below are explicit, and they are NOT atomic
                        // with respect to each other.
                        let ledger = context.persistentState primary
                        ledger.State <- next
                        do! ledger.WriteStateAsync()

                        let trail = context.persistentState audit

                        trail.State <-
                            { events = trail.State.events @ [ $"deposit {amount} -> {next.balance}" ] }

                        do! trail.WriteStateAsync()

                        return next, next.balance
                    })

            handle
                (_.snapshot)
                (fun context state () ->
                    task {
                        let ledger = context.persistentState primary
                        let trail = context.persistentState audit

                        return
                            state,
                            { balance = state.balance
                              entries = state.entries
                              activations = ActivationProbe.count (string context.grainId)
                              primaryRecordExists = ledger.RecordExists
                              auditRecordExists = trail.RecordExists
                              // Defensive: see the `clear` handler — a cleared facet comes back
                              // from Orleans with null collection fields, and a null F# list
                              // cannot be serialized.
                              auditEvents =
                                if isNull (box trail.State) || isNull (box trail.State.events) then
                                    []
                                else
                                    trail.State.events }
                    })

            handle
                (_.reload)
                (fun context _state () ->
                    task {
                        let ledger = context.persistentState primary
                        do! ledger.ReadStateAsync()
                        return ledger.State, ledger.State.balance
                    })

            handle
                (_.clear)
                (fun context _state () ->
                    task {
                        let ledger = context.persistentState primary
                        do! ledger.ClearStateAsync()

                        let trail = context.persistentState audit
                        do! trail.ClearStateAsync()

                        // A real hazard worth showing rather than hiding. Orleans re-seeds a
                        // cleared facet with an UNINITIALIZED instance of the stored type, so an
                        // F# record comes back with null reference fields — and `null` is not a
                        // legal value of an F# list. Serializing it later throws
                        // ArgumentNullException from deep inside the codec, far from the clear
                        // that caused it. Re-seed explicitly after every ClearStateAsync.
                        let observed =
                            if isNull (box trail.State) then "cleared facet State is null"
                            elif isNull (box trail.State.events) then
                                "cleared facet State is a record whose 'events' list field is null"
                            else
                                $"cleared facet State survived with {List.length trail.State.events} events"

                        trail.State <- { events = [] }

                        return { balance = 0L; entries = 0 }, observed
                    })

            handle
                (_.goIdle)
                (fun context state () ->
                    task {
                        context.deactivateOnIdle ()
                        return state, ()
                    })
        }
