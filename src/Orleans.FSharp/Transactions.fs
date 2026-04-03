namespace Orleans.FSharp.Transactions

open System
open System.Threading.Tasks
open Orleans
open Orleans.Transactions.Abstractions

/// <summary>
/// Transaction options for grain methods that mirror Orleans <see cref="Orleans.TransactionOption"/>.
/// In CodeGen, these map to <c>[Transaction(option)]</c> attributes on grain interface methods.
/// </summary>
type TransactionOption =
    /// <summary>Always creates a new transaction, even if one already exists.</summary>
    | Create
    /// <summary>Must be called within an existing transaction context.</summary>
    | Join
    /// <summary>Joins an existing transaction if available, otherwise creates a new one.</summary>
    | CreateOrJoin
    /// <summary>The call is not transactional but supports being called within a transaction.</summary>
    | Supported
    /// <summary>The call is not transactional and cannot be called within a transaction.</summary>
    | NotAllowed
    /// <summary>The call is not transactional; any ambient transaction context is suppressed.</summary>
    | Suppress

/// <summary>
/// Functions for converting F# transaction options to Orleans TransactionOption values.
/// </summary>
[<RequireQualifiedAccess>]
module TransactionOption =

    /// <summary>
    /// Converts an F# <see cref="TransactionOption"/> to the corresponding Orleans <see cref="Orleans.TransactionOption"/> enum value.
    /// </summary>
    /// <param name="option">The F# transaction option.</param>
    /// <returns>The Orleans TransactionOption enum value.</returns>
    let toOrleans (option: TransactionOption) : Orleans.TransactionOption =
        match option with
        | Create -> Orleans.TransactionOption.Create
        | Join -> Orleans.TransactionOption.Join
        | CreateOrJoin -> Orleans.TransactionOption.CreateOrJoin
        | Supported -> Orleans.TransactionOption.Supported
        | NotAllowed -> Orleans.TransactionOption.NotAllowed
        | Suppress -> Orleans.TransactionOption.Suppress

/// <summary>
/// F# wrapper functions for <see cref="ITransactionalState{T}"/> operations.
/// Provides idiomatic F# access to Orleans transactional state reads and updates.
/// </summary>
/// <remarks>
/// The <c>'T</c> type parameter must be a reference type with a parameterless constructor
/// (Orleans constraint: <c>'T : not struct</c> and <c>'T : (new : unit -> 'T)</c>).
/// </remarks>
[<RequireQualifiedAccess>]
module TransactionalState =

    /// <summary>
    /// Reads the current transactional state value.
    /// </summary>
    /// <param name="state">The Orleans transactional state.</param>
    /// <typeparam name="T">The state type (must be a reference type with a default constructor).</typeparam>
    /// <returns>A Task containing the current state value.</returns>
    let read<'T when 'T : not struct and 'T : (new : unit -> 'T)> (state: ITransactionalState<'T>) : Task<'T> =
        state.PerformRead(Func<'T, 'T>(id))

    /// <summary>
    /// Updates the transactional state by applying a transformation function.
    /// The function receives the current state and returns the new state.
    /// </summary>
    /// <param name="action">A function that transforms the current state into the new state.</param>
    /// <param name="state">The Orleans transactional state.</param>
    /// <typeparam name="T">The state type (must be a reference type with a default constructor).</typeparam>
    /// <returns>A Task that completes when the update is recorded in the transaction.</returns>
    let update<'T when 'T : not struct and 'T : (new : unit -> 'T)>
        (action: 'T -> 'T)
        (state: ITransactionalState<'T>)
        : Task<unit> =
        task {
            let! _ = state.PerformUpdate(Func<'T, 'T>(action))
            return ()
        }

    /// <summary>
    /// Performs a read within the transaction by applying a reader function to extract a value.
    /// </summary>
    /// <param name="reader">A function that extracts a result from the current state.</param>
    /// <param name="state">The Orleans transactional state.</param>
    /// <typeparam name="T">The state type (must be a reference type with a default constructor).</typeparam>
    /// <typeparam name="R">The type of value extracted by the reader function.</typeparam>
    /// <returns>A Task containing the extracted value.</returns>
    let performRead<'T, 'R when 'T : not struct and 'T : (new : unit -> 'T)>
        (reader: 'T -> 'R)
        (state: ITransactionalState<'T>)
        : Task<'R> =
        state.PerformRead(Func<'T, 'R>(reader))
