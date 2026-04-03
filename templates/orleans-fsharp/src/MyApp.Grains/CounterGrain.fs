namespace MyApp.Grains

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Counter state: either zero or a positive count.
/// </summary>
[<GenerateSerializer>]
type CounterState =
    /// <summary>The counter is at zero.</summary>
    | [<Id(0u)>] Zero
    /// <summary>The counter has a positive value.</summary>
    | [<Id(1u)>] Count of int

/// <summary>
/// Wrapper record for CounterState that Orleans can instantiate for persistence.
/// F# discriminated unions compile to abstract classes, which Orleans cannot
/// create via GetUninitializedObject. This wrapper provides a concrete class.
/// </summary>
[<GenerateSerializer>]
[<Sealed>]
type CounterStateHolder() =
    /// <summary>The counter state value.</summary>
    [<Id(0u)>]
    member val State: CounterState = Zero with get, set

/// <summary>
/// Commands that can be sent to the counter grain.
/// </summary>
[<GenerateSerializer>]
type CounterCommand =
    /// <summary>Increment the counter by 1.</summary>
    | [<Id(0u)>] Increment
    /// <summary>Decrement the counter by 1 (minimum is Zero).</summary>
    | [<Id(1u)>] Decrement
    /// <summary>Get the current counter value without changing state.</summary>
    | [<Id(2u)>] GetValue

/// <summary>
/// Grain interface for the counter grain.
/// </summary>
type ICounterGrain =
    inherit IGrainWithIntegerKey

    /// <summary>Handle a counter command and return the result.</summary>
    abstract HandleMessage: CounterCommand -> Task<obj>

/// <summary>
/// Module containing the counter grain definition built with the grain { } CE.
/// </summary>
module CounterGrainDef =

    /// <summary>
    /// Extract the integer value from a CounterState.
    /// </summary>
    let stateValue (state: CounterState) : int =
        match state with
        | Zero -> 0
        | Count n -> n

    /// <summary>
    /// The counter grain definition, built using the grain computation expression.
    /// Demonstrates the v2 API with the grain { } CE.
    /// </summary>
    let counter =
        grain {
            defaultState Zero

            handle (fun state cmd ->
                task {
                    match state, cmd with
                    | Zero, Increment -> return Count 1, box 1
                    | Zero, Decrement -> return Zero, box 0
                    | Count n, Increment -> return Count(n + 1), box (n + 1)
                    | Count n, Decrement when n > 1 -> return Count(n - 1), box (n - 1)
                    | Count _, Decrement -> return Zero, box 0
                    | _, GetValue ->
                        let v = stateValue state
                        return state, box v
                })

            persist "Default"

            // Example: to add a reminder handler, uncomment the following:
            // onReminder "cleanup" (fun state _name _tick ->
            //     task { return state })
        }
