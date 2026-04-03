namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Orleans

/// <summary>
/// Module for managing grain timers -- in-memory periodic triggers
/// that do NOT survive grain deactivation. Timers are automatically
/// disposed when the grain deactivates.
/// All functions use Task (not Async) to align with Orleans runtime conventions.
/// </summary>
[<RequireQualifiedAccess>]
module Timers =

    /// <summary>
    /// Register a grain timer with a callback function.
    /// The timer fires periodically and the callback receives a CancellationToken
    /// that is cancelled when the timer is disposed or the grain starts to deactivate.
    /// Dispose the returned IGrainTimer to cancel the timer.
    /// </summary>
    /// <param name="grain">The grain instance (must inherit from Grain).</param>
    /// <param name="callback">The function to invoke on each timer tick. Receives a CancellationToken.</param>
    /// <param name="dueTime">The time delay before the first firing.</param>
    /// <param name="period">The interval between subsequent firings.</param>
    /// <returns>An IGrainTimer handle that can be disposed to cancel the timer.</returns>
    let register
        (grain: Grain)
        (callback: CancellationToken -> Task<unit>)
        (dueTime: TimeSpan)
        (period: TimeSpan)
        : Orleans.Runtime.IGrainTimer =
        grain.RegisterGrainTimer(
            (fun (_state: obj) (ct: CancellationToken) -> callback ct :> Task),
            (null: obj),
            Orleans.Runtime.GrainTimerCreationOptions(DueTime = dueTime, Period = period)
        )

    /// <summary>
    /// Register a grain timer with a typed state and callback function.
    /// The timer fires periodically and the callback receives the state value
    /// and a CancellationToken that is cancelled when the timer is disposed
    /// or the grain starts to deactivate.
    /// Dispose the returned IGrainTimer to cancel the timer.
    /// </summary>
    /// <param name="grain">The grain instance (must inherit from Grain).</param>
    /// <param name="callback">The function to invoke on each timer tick. Receives state and CancellationToken.</param>
    /// <param name="state">The state object passed to each callback invocation.</param>
    /// <param name="dueTime">The time delay before the first firing.</param>
    /// <param name="period">The interval between subsequent firings.</param>
    /// <typeparam name="'TState">The type of the state object.</typeparam>
    /// <returns>An IGrainTimer handle that can be disposed to cancel the timer.</returns>
    let registerWithState<'TState>
        (grain: Grain)
        (callback: 'TState -> CancellationToken -> Task<unit>)
        (state: 'TState)
        (dueTime: TimeSpan)
        (period: TimeSpan)
        : Orleans.Runtime.IGrainTimer =
        grain.RegisterGrainTimer(
            (fun (s: 'TState) (ct: CancellationToken) -> callback s ct :> Task),
            state,
            Orleans.Runtime.GrainTimerCreationOptions(DueTime = dueTime, Period = period)
        )
