namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Primary state for the additional-state test grain.
/// Holds a simple integer counter that the grain increments on command.
/// </summary>
[<GenerateSerializer>]
type AdditionalState =
    { /// <summary>Main counter incremented by <c>IncrCounter</c>.</summary>
      [<Id(0u)>] Counter: int }

/// <summary>
/// Secondary (additional) state stored under the name <c>"audit"</c>.
/// Tracks the number of mutating events independently of the primary counter.
/// </summary>
[<GenerateSerializer>]
type AuditState =
    { /// <summary>Total number of write operations performed on this grain instance.</summary>
      [<Id(0u)>] EventCount: int }

/// <summary>
/// Commands for the additional-state test grain.
/// </summary>
[<GenerateSerializer>]
type AdditionalStateCommand =
    /// <summary>Increments the main counter AND the audit event count.</summary>
    | [<Id(0u)>] IncrCounter
    /// <summary>Increments only the audit event count without changing the counter.</summary>
    | [<Id(1u)>] IncrAudit
    /// <summary>Returns a tuple of (counter, auditEventCount) without mutating state.</summary>
    | [<Id(2u)>] GetBoth
    /// <summary>Resets both the counter and the audit count to zero.</summary>
    | [<Id(3u)>] ResetAll

/// <summary>
/// Grain interface for the additional-state test grain.
/// Keyed by string so individual integration tests can each use an isolated grain instance.
/// </summary>
type IAdditionalStateTestGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle an <see cref="AdditionalStateCommand"/> and return the boxed result.</summary>
    abstract HandleMessage: AdditionalStateCommand -> Task<obj>

/// <summary>
/// Module containing the grain definition that exercises the <c>additionalState</c> CE keyword.
/// The grain uses <c>handleWithContext</c> to access a secondary <c>"audit"</c> state
/// (of type <see cref="AuditState"/>) alongside its primary <see cref="AdditionalState"/> counter.
/// </summary>
module AdditionalStateGrainDef =

    /// <summary>
    /// Definition for the additional-state integration test grain.
    /// <list type="bullet">
    ///   <item><description>Primary state (<see cref="AdditionalState"/>): integer counter, persisted under <c>"Default"</c>.</description></item>
    ///   <item><description>Secondary state (<c>"audit"</c>, <see cref="AuditState"/>): event count, also persisted under <c>"Default"</c>.</description></item>
    /// </list>
    /// The handler uses <c>GrainContext.getState&lt;AuditState&gt;</c> to access the audit state
    /// and writes both states on mutating commands.
    /// </summary>
    let additionalStateGrain =
        grain {
            defaultState { Counter = 0 }

            persist "Default"

            additionalState "audit" "Default" { EventCount = 0 }

            handleWithContext (fun ctx state cmd ->
                task {
                    let auditPs = GrainContext.getState<AuditState> ctx "audit"

                    match cmd with
                    | IncrCounter ->
                        let ns = { state with Counter = state.Counter + 1 }
                        auditPs.State <- { auditPs.State with EventCount = auditPs.State.EventCount + 1 }
                        do! auditPs.WriteStateAsync()
                        return ns, box ns

                    | IncrAudit ->
                        auditPs.State <- { auditPs.State with EventCount = auditPs.State.EventCount + 1 }
                        do! auditPs.WriteStateAsync()
                        return state, box ()

                    | GetBoth ->
                        return state, box (state.Counter, auditPs.State.EventCount)

                    | ResetAll ->
                        let ns = { Counter = 0 }
                        auditPs.State <- { EventCount = 0 }
                        do! auditPs.WriteStateAsync()
                        return ns, box ()
                })
        }
