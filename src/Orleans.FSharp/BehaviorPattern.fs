namespace Orleans.FSharp

open System.Threading.Tasks

/// <summary>
/// Result of a behavior handler indicating what the grain should do next.
/// Used with the behavior pattern where grain state contains a phase/mode discriminated union.
/// The handler matches on (phase, command) and returns a BehaviorResult to express state transitions.
/// </summary>
/// <typeparam name="'State">The grain's state type.</typeparam>
type BehaviorResult<'State> =
    /// <summary>Keep the current phase, update state.</summary>
    | Stay of 'State
    /// <summary>Transition to a new phase by updating state (phase is part of state).</summary>
    | Become of 'State
    /// <summary>Signal that this grain should deactivate.</summary>
    | Stop

/// <summary>
/// Functions for working with the behavior pattern in grain handlers.
/// The behavior pattern uses a discriminated union as a phase field inside the grain state.
/// Handlers match on (state.Phase, command) and return BehaviorResult to express transitions.
/// </summary>
/// <example>
/// <code>
/// type Phase = Idle | Running of maxItems: int | Paused of reason: string
/// type MyState = { Phase: Phase; Items: string list }
/// type MyCommand = Start of int | AddItem of string | Pause of string | Resume
///
/// let myGrain = grain {
///     defaultState { Phase = Idle; Items = [] }
///     handleState (fun state cmd -> task {
///         match state |> Behavior.run (fun s -> s.Phase) cmd (fun s c -> task {
///             match s.Phase, c with
///             | Idle, Start max -> return Become { s with Phase = Running max }
///             | Running max, AddItem item ->
///                 return Stay { s with Items = (item :: s.Items) |> List.truncate max }
///             | Running _, Pause reason -> return Become { s with Phase = Paused reason }
///             | Paused _, Resume -> return Become { s with Phase = Running 50 }
///             | _, _ -> return Stay s
///         }) with
///         | Stay s | Become s -> return s
///         | Stop -> return state
///     })
/// }
/// </code>
/// </example>
[<RequireQualifiedAccess>]
module Behavior =

    /// <summary>
    /// Extracts the resulting state from a BehaviorResult.
    /// For Stop, returns the original state unchanged.
    /// </summary>
    /// <param name="original">The original state to use as fallback for Stop.</param>
    /// <param name="result">The BehaviorResult to unwrap.</param>
    /// <returns>The state from Stay/Become, or the original state for Stop.</returns>
    let unwrap (original: 'State) (result: BehaviorResult<'State>) : 'State =
        match result with
        | Stay s -> s
        | Become s -> s
        | Stop -> original

    /// <summary>
    /// Maps a function over the state inside a BehaviorResult.
    /// Stop is preserved unchanged.
    /// </summary>
    /// <param name="f">The mapping function.</param>
    /// <param name="result">The BehaviorResult to map.</param>
    /// <returns>A new BehaviorResult with the mapped state.</returns>
    let map (f: 'State -> 'State) (result: BehaviorResult<'State>) : BehaviorResult<'State> =
        match result with
        | Stay s -> Stay(f s)
        | Become s -> Become(f s)
        | Stop -> Stop

    /// <summary>
    /// Returns true if the result is a phase transition (Become).
    /// </summary>
    /// <param name="result">The BehaviorResult to check.</param>
    /// <returns>True if the result is Become.</returns>
    let isTransition (result: BehaviorResult<'State>) : bool =
        match result with
        | Become _ -> true
        | _ -> false

    /// <summary>
    /// Returns true if the result signals deactivation (Stop).
    /// </summary>
    /// <param name="result">The BehaviorResult to check.</param>
    /// <returns>True if the result is Stop.</returns>
    let isStopped (result: BehaviorResult<'State>) : bool =
        match result with
        | Stop -> true
        | _ -> false

    /// <summary>
    /// Converts a BehaviorResult to a grain handler result (state * boxed result).
    /// Stay/Become return the new state; Stop returns the original state.
    /// The boxed result is always the resulting state.
    /// </summary>
    /// <param name="original">The original state to use as fallback for Stop.</param>
    /// <param name="result">The BehaviorResult to convert.</param>
    /// <returns>A tuple of (new state, boxed state) suitable for grain handlers.</returns>
    let toHandlerResult (original: 'State) (result: BehaviorResult<'State>) : 'State * obj =
        let s = unwrap original result
        s, box s
