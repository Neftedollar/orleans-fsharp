namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Commands that can be sent to the aggregator grain.
/// </summary>
[<GenerateSerializer>]
type AggregatorCommand =
    /// <summary>Add a value to the running total. Simulates a slow operation for timing tests.</summary>
    | AddValue of value: int
    /// <summary>Get the current total without changing state.</summary>
    | GetTotal

/// <summary>
/// Grain interface for the aggregator grain. Uses string key.
/// </summary>
type IAggregatorGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle an aggregator command and return the result.</summary>
    abstract HandleMessage: AggregatorCommand -> Task<obj>

/// <summary>
/// Module containing the aggregator grain definition built with the grain { } CE.
/// </summary>
module AggregatorGrainDef =

    /// <summary>
    /// The aggregator grain definition.
    /// Processes AddValue commands with a simulated delay for concurrency timing tests.
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
        }
