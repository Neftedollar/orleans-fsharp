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
/// // Write a behavior handler that returns BehaviorResult
/// let handler (state: MyState) (cmd: MyCommand) = task {
///     match state.Phase, cmd with
///     | Idle, Start max    -> return Become { state with Phase = Running max }
///     | Running max, AddItem item ->
///         return Stay { state with Items = (item :: state.Items) |> List.truncate max }
///     | Running _, Pause reason -> return Become { state with Phase = Paused reason }
///     | Paused _, Resume   -> return Become { state with Phase = Running 50 }
///     | _, _               -> return Stay state
/// }
///
/// // Plug it into handleState with Behavior.run — no manual unwrapping needed
/// let myGrain = grain {
///     defaultState { Phase = Idle; Items = [] }
///     handleState (Behavior.run handler)
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

    /// <summary>
    /// Adapts a <c>BehaviorResult</c>-returning handler for direct use in a <c>handleState</c>
    /// computation expression block.
    /// </summary>
    /// <remarks>
    /// <c>Stay s</c> and <c>Become s</c> both return the new state.
    /// <c>Stop</c> returns the <em>original</em> state unchanged (no deactivation side-effect,
    /// since <c>handleState</c> has no access to the grain context).
    /// Use <see cref="runWithContext"/> when you need the grain to actually deactivate on Stop.
    /// </remarks>
    /// <param name="handler">A function <c>'State -&gt; 'Message -&gt; Task&lt;BehaviorResult&lt;'State&gt;&gt;</c>.</param>
    /// <param name="state">The current grain state.</param>
    /// <param name="msg">The incoming message.</param>
    /// <returns>A <c>Task&lt;'State&gt;</c> of the resulting state.</returns>
    /// <example>
    /// <code>
    /// let myGrain = grain {
    ///     defaultState initialState
    ///     handleState (Behavior.run myBehaviorHandler)
    /// }
    /// </code>
    /// </example>
    let run
        (handler: 'State -> 'Message -> Task<BehaviorResult<'State>>)
        (state: 'State)
        (msg: 'Message)
        : Task<'State> =
        task {
            let! result = handler state msg
            return unwrap state result
        }

    /// <summary>
    /// Adapts a <c>BehaviorResult</c>-returning handler for use in a <c>handleStateWithContext</c>
    /// computation expression block.
    /// </summary>
    /// <remarks>
    /// <c>Stay s</c> and <c>Become s</c> return the new state.
    /// <c>Stop</c> calls <c>ctx.DeactivateOnIdle()</c> to schedule grain deactivation,
    /// then returns the original state.
    /// </remarks>
    /// <param name="handler">
    /// A function <c>GrainContext -&gt; 'State -&gt; 'Message -&gt; Task&lt;BehaviorResult&lt;'State&gt;&gt;</c>.
    /// </param>
    /// <param name="ctx">The grain context, providing access to <c>DeactivateOnIdle</c> and <c>GrainFactory</c>.</param>
    /// <param name="state">The current grain state.</param>
    /// <param name="msg">The incoming message.</param>
    /// <returns>A <c>Task&lt;'State&gt;</c> of the resulting state.</returns>
    /// <example>
    /// <code>
    /// let myGrain = grain {
    ///     defaultState initialState
    ///     handleStateWithContext (Behavior.runWithContext myBehaviorHandler)
    /// }
    /// </code>
    /// </example>
    let runWithContext
        (handler: GrainContext -> 'State -> 'Message -> Task<BehaviorResult<'State>>)
        (ctx: GrainContext)
        (state: 'State)
        (msg: 'Message)
        : Task<'State> =
        task {
            let! result = handler ctx state msg

            match result with
            | Stop ->
                match ctx.DeactivateOnIdle with
                | Some deactivate -> deactivate ()
                | None -> ()

                return state
            | _ -> return unwrap state result
        }
