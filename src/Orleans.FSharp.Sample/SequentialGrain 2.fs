namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Commands that can be sent to the sequential (non-reentrant) grain.
/// </summary>
[<GenerateSerializer>]
type SequentialCommand =
    /// <summary>Add a value with a simulated delay. Used to measure sequential vs concurrent processing.</summary>
    | [<Id(0u)>] SlowAdd of value: int
    /// <summary>Get the current total without changing state.</summary>
    | [<Id(1u)>] GetTotal

/// <summary>
/// Grain interface for the non-reentrant sequential grain. Uses string key.
/// </summary>
type ISequentialGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle a sequential command and return the result.</summary>
    abstract HandleMessage: SequentialCommand -> Task<obj>

/// <summary>
/// Module containing the non-reentrant sequential grain definition.
/// This grain processes messages one at a time (default Orleans behavior).
/// </summary>
module SequentialGrainDef =

    /// <summary>
    /// The sequential (non-reentrant) grain definition.
    /// Messages are processed one at a time, so two 500ms calls take ~1000ms.
    /// </summary>
    let sequential =
        grain {
            defaultState 0

            handle (fun state cmd ->
                task {
                    match cmd with
                    | SlowAdd v ->
                        do! Task.Delay(500)
                        let newState = state + v
                        return newState, box newState
                    | GetTotal -> return state, box state
                })
        }
