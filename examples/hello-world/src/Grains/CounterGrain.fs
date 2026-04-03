namespace HelloWorld.Grains

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// State of the counter grain.
/// </summary>
type CounterState = { Count: int }

/// <summary>
/// Commands that the counter grain can handle.
/// </summary>
type CounterCommand =
    | Increment
    | Decrement
    | GetValue

/// <summary>
/// Grain interface for the counter grain.
/// </summary>
type ICounterGrain =
    inherit IGrainWithStringKey

    /// <summary>Sends a command to the counter grain and returns the result.</summary>
    abstract HandleMessage: CounterCommand -> Task<obj>

/// <summary>
/// Counter grain definition using the grain computation expression.
/// </summary>
module CounterGrainDef =

    /// <summary>
    /// The counter grain: increments, decrements, and returns the current count.
    /// </summary>
    let counter =
        grain {
            defaultState { Count = 0 }

            handle (fun state cmd ->
                task {
                    match cmd with
                    | Increment ->
                        let next = { Count = state.Count + 1 }
                        return next, box next.Count
                    | Decrement ->
                        let next = { Count = state.Count - 1 }
                        return next, box next.Count
                    | GetValue ->
                        return state, box state.Count
                })

            persist "Default"
        }
