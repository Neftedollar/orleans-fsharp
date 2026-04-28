namespace Orleans.FSharp.Sample

// FS44: the `reentrant` CE keyword is currently deprecated/non-functional in the universal pattern.
// This sample still exercises it to demonstrate the API surface; the runtime semantics are sequential.
#nowarn "44"

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Commands that can be sent to the aggregator grain.
/// </summary>
[<GenerateSerializer>]
type AggregatorCommand =
    /// <summary>Add a value to the running total. Simulates a slow operation for reentrancy testing.</summary>
    | AddValue of value: int
    /// <summary>Get the current total without changing state.</summary>
    | GetTotal

/// <summary>
/// Grain interface for the reentrant aggregator grain. Uses string key.
/// </summary>
type IAggregatorGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle an aggregator command and return the result.</summary>
    abstract HandleMessage: AggregatorCommand -> Task<obj>

/// <summary>
/// Module containing the reentrant aggregator grain definition built with the grain { } CE.
/// This grain demonstrates the reentrant keyword for concurrent message processing.
/// </summary>
module AggregatorGrainDef =

    /// <summary>
    /// The reentrant aggregator grain definition.
    /// Being reentrant, this grain can process multiple AddValue commands concurrently.
    /// </summary>
    let aggregator =
        grain {
            defaultState 0

            handle (fun state cmd ->
                task {
                    match cmd with
                    | AddValue v ->
                        do! Task.Delay(500)
                        let newState = state + v
                        return newState, box newState
                    | GetTotal -> return state, box state
                })

            reentrant
        }
