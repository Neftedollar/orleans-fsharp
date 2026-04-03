namespace Orleans.FSharp

open System.Threading.Tasks
open Orleans.Runtime

/// <summary>
/// Functions for working with Orleans IPersistentState in an idiomatic F# style.
/// Wraps the mutable IPersistentState interface with immutable-friendly operations
/// suitable for F# discriminated union state machines.
/// </summary>
[<RequireQualifiedAccess>]
module GrainState =

    /// <summary>
    /// Read the current state from persistent storage.
    /// Calls ReadStateAsync on the underlying IPersistentState and returns the state value.
    /// </summary>
    /// <param name="state">The Orleans persistent state wrapper.</param>
    /// <returns>A Task containing the current persisted state value.</returns>
    let read<'T> (state: IPersistentState<'T>) : Task<'T> =
        task {
            do! state.ReadStateAsync()
            return state.State
        }

    /// <summary>
    /// Write a new state value to persistent storage, replacing the current value.
    /// Sets the in-memory state and then calls WriteStateAsync to persist.
    /// </summary>
    /// <param name="state">The Orleans persistent state wrapper.</param>
    /// <param name="value">The new state value to persist.</param>
    /// <returns>A Task that completes when the state has been written.</returns>
    let write<'T> (state: IPersistentState<'T>) (value: 'T) : Task<unit> =
        task {
            state.State <- value
            do! state.WriteStateAsync()
        }

    /// <summary>
    /// Clear the persisted state from storage.
    /// Calls ClearStateAsync on the underlying IPersistentState.
    /// </summary>
    /// <param name="state">The Orleans persistent state wrapper.</param>
    /// <returns>A Task that completes when the state has been cleared.</returns>
    let clear<'T> (state: IPersistentState<'T>) : Task<unit> =
        task { do! state.ClearStateAsync() }

    /// <summary>
    /// Get the current in-memory state value without performing a storage read.
    /// Returns the state as it exists in the grain's memory.
    /// </summary>
    /// <param name="state">The Orleans persistent state wrapper.</param>
    /// <returns>The current in-memory state value.</returns>
    let current<'T> (state: IPersistentState<'T>) : 'T = state.State
