namespace Orleans.FSharp.Sample

open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Commands that can be sent to the declarative timer test grain.
/// </summary>
[<GenerateSerializer>]
type TimerCommand =
    /// <summary>Get the current timer fire count.</summary>
    | GetTimerFireCount
    /// <summary>Get the current state value.</summary>
    | GetTimerState

/// <summary>
/// Grain interface for the declarative timer test grain. Uses string key.
/// </summary>
type ITimerTestGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle a timer test command.</summary>
    abstract HandleMessage: TimerCommand -> Task<obj>

/// <summary>
/// Module containing the declarative timer test grain definition.
/// Uses onTimer to declaratively register timers that fire on activation.
/// </summary>
module TimerTestGrainDef =

    /// <summary>
    /// The declarative timer test grain definition.
    /// State tracks how many times the timer fired.
    /// </summary>
    let timerTestGrain =
        grain {
            defaultState 0

            handle (fun state cmd ->
                task {
                    match cmd with
                    | GetTimerFireCount -> return state, box state
                    | GetTimerState -> return state, box state
                })

            onTimer "TestTimer" (TimeSpan.FromMilliseconds 500.) (TimeSpan.FromMilliseconds 500.) (fun state ->
                task { return state + 1 })
        }
