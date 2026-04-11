namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// State for the lifecycle hook test grain.
/// Tracks how many times the grain has been activated and an arbitrary value
/// that handlers can modify — letting tests observe both activation hooks and
/// normal message processing through the same interface.
/// </summary>
[<GenerateSerializer>]
type LifecycleState =
    { /// <summary>Incremented by the <c>onActivate</c> hook each time the grain is activated.</summary>
      [<Id(0u)>] ActivationCount: int
      /// <summary>Arbitrary counter modified by <c>IncrementValue</c> commands.</summary>
      [<Id(1u)>] Value: int }

/// <summary>
/// Commands for the lifecycle hook test grain.
/// </summary>
[<GenerateSerializer>]
type LifecycleTestCommand =
    /// <summary>Returns the current state snapshot without side effects.</summary>
    | GetLifecycleState
    /// <summary>Adds <c>n</c> to the <c>Value</c> field and returns the updated state.</summary>
    | IncrementValue of n: int

/// <summary>
/// Grain interface for the lifecycle hook test grain.
/// Keyed by string so tests can create isolated instances without integer key management.
/// </summary>
type ILifecycleTestGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle a lifecycle command and return the boxed result.</summary>
    abstract HandleMessage: LifecycleTestCommand -> Task<obj>

/// <summary>
/// Module containing the lifecycle-hook grain definition built with the grain { } CE.
/// The <c>onActivate</c> hook increments <c>ActivationCount</c> on every activation,
/// providing an observable side effect that integration tests can verify via the grain's state.
/// The <c>onDeactivate</c> hook demonstrates the no-op deactivation path (real apps use it
/// for cleanup, resource release, etc.).
/// </summary>
module LifecycleGrainDef =

    /// <summary>
    /// Lifecycle hook test grain.  Uses the typed <c>FSharpGrain&lt;State, Message&gt;</c>
    /// path (wired in <c>LifecycleTestGrainImpl</c> in the CodeGen project) so that
    /// <c>OnActivateAsync</c> and <c>OnDeactivateAsync</c> call the F# hooks.
    /// </summary>
    let lifecycleGrain =
        grain {
            defaultState { ActivationCount = 0; Value = 0 }

            onActivate (fun state ->
                task {
                    // Increment ActivationCount each time the grain wakes up.
                    // This value is observable via GetLifecycleState and lets tests verify
                    // the hook was invoked without any external shared mutable state.
                    return { state with ActivationCount = state.ActivationCount + 1 }
                })

            onDeactivate (fun _state ->
                task {
                    // Real grains would release resources, flush buffers, etc.
                    // For testing, a no-op confirms that a deactivation hook does not
                    // prevent normal grain operation or throw.
                    return ()
                })

            persist "Default"

            handle (fun state cmd ->
                task {
                    match cmd with
                    | GetLifecycleState ->
                        return state, box state
                    | IncrementValue n ->
                        let ns = { state with Value = state.Value + n }
                        return ns, box ns
                })
        }
