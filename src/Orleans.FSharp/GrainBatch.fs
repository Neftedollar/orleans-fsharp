namespace Orleans.FSharp

open System
open System.Threading.Tasks

/// <summary>
/// Functions for executing operations on multiple Orleans grains concurrently.
/// Provides fan-out (scatter/gather), parallel iteration, and fault-tolerant batch patterns
/// that are common in distributed Orleans architectures.
/// </summary>
/// <remarks>
/// <para>
/// All functions in this module execute grain calls concurrently using <c>Task.WhenAll</c>.
/// Results are returned in the same order as the input collection.
/// </para>
/// <para>
/// For a small, fixed set of grains (2–4), consider the F# <c>and!</c> applicative CE syntax
/// instead — it is more ergonomic and compiles to the same parallel execution:
/// </para>
/// <code>
/// task {
///     let! balance1 = account1.GetBalance()
///     and! balance2 = account2.GetBalance()
///     and! balance3 = account3.GetBalance()
///     return balance1 + balance2 + balance3
/// }
/// </code>
/// Use <c>GrainBatch</c> when the number of grains is dynamic or comes from a collection.
/// </remarks>
[<RequireQualifiedAccess>]
module GrainBatch =

    /// <summary>
    /// Executes a task-returning function on each grain concurrently and collects all results.
    /// Results are returned in the same order as the input sequence.
    /// If any grain call throws, the entire batch fails with that exception.
    /// </summary>
    /// <typeparam name="TGrain">The grain interface type.</typeparam>
    /// <typeparam name="TResult">The result type returned by each grain call.</typeparam>
    /// <param name="grains">The sequence of grain references to call.</param>
    /// <param name="f">The function to invoke on each grain.</param>
    /// <returns>
    /// A <c>Task&lt;TResult list&gt;</c> that completes when all grain calls complete,
    /// containing results in the same order as <paramref name="grains"/>.
    /// </returns>
    let map<'TGrain, 'TResult> (grains: 'TGrain seq) (f: 'TGrain -> Task<'TResult>) : Task<'TResult list> =
        task {
            let tasks = grains |> Seq.map f |> Seq.toArray
            let! results = Task.WhenAll(tasks)
            return results |> Array.toList
        }

    /// <summary>
    /// Executes a task-returning function on each grain concurrently and collects results as
    /// <c>Result&lt;TResult, exn&gt;</c> values.
    /// Unlike <see cref="map"/>, individual grain failures do not abort the entire batch —
    /// each result is independently <c>Ok</c> or <c>Error</c>.
    /// </summary>
    /// <typeparam name="TGrain">The grain interface type.</typeparam>
    /// <typeparam name="TResult">The result type returned by each grain call.</typeparam>
    /// <param name="grains">The sequence of grain references to call.</param>
    /// <param name="f">The function to invoke on each grain.</param>
    /// <returns>
    /// A <c>Task&lt;Result&lt;TResult, exn&gt; list&gt;</c> with one entry per grain,
    /// in the same order as <paramref name="grains"/>.
    /// </returns>
    let tryMap<'TGrain, 'TResult>
        (grains: 'TGrain seq)
        (f: 'TGrain -> Task<'TResult>)
        : Task<Result<'TResult, exn> list> =
        task {
            let wrap (grain: 'TGrain) : Task<Result<'TResult, exn>> =
                task {
                    try
                        let! result = f grain
                        return Ok result
                    with ex ->
                        return Error ex
                }

            let tasks = grains |> Seq.map wrap |> Seq.toArray
            let! results = Task.WhenAll(tasks)
            return results |> Array.toList
        }

    /// <summary>
    /// Executes a task-returning function on each grain concurrently and aggregates
    /// all results using a fold function.
    /// If any grain call throws, the aggregation fails with that exception.
    /// </summary>
    /// <typeparam name="TGrain">The grain interface type.</typeparam>
    /// <typeparam name="TResult">The result type returned by each grain call.</typeparam>
    /// <typeparam name="TAgg">The aggregated result type.</typeparam>
    /// <param name="grains">The sequence of grain references to call.</param>
    /// <param name="f">The function to invoke on each grain.</param>
    /// <param name="aggregate">The aggregation function applied to all collected results.</param>
    /// <returns>A <c>Task&lt;TAgg&gt;</c> containing the aggregated result.</returns>
    let aggregate<'TGrain, 'TResult, 'TAgg>
        (grains: 'TGrain seq)
        (f: 'TGrain -> Task<'TResult>)
        (aggregate: 'TResult list -> 'TAgg)
        : Task<'TAgg> =
        task {
            let! results = map grains f
            return aggregate results
        }

    /// <summary>
    /// Executes a fire-and-forget operation on each grain concurrently.
    /// Waits for all operations to complete before returning.
    /// If any grain call throws, the entire batch fails with that exception.
    /// </summary>
    /// <typeparam name="TGrain">The grain interface type.</typeparam>
    /// <param name="grains">The sequence of grain references to call.</param>
    /// <param name="f">The unit-returning function to invoke on each grain.</param>
    /// <returns>A <c>Task</c> that completes when all grain calls complete.</returns>
    let iter<'TGrain> (grains: 'TGrain seq) (f: 'TGrain -> Task) : Task =
        task {
            let tasks = grains |> Seq.map f |> Seq.toArray
            do! Task.WhenAll(tasks)
        }
        :> Task

    /// <summary>
    /// Executes a fire-and-forget operation on each grain concurrently, capturing
    /// any exceptions as <c>Error exn</c> values. All grains are called regardless of failures.
    /// </summary>
    /// <typeparam name="TGrain">The grain interface type.</typeparam>
    /// <param name="grains">The sequence of grain references to call.</param>
    /// <param name="f">The unit-returning function to invoke on each grain.</param>
    /// <returns>
    /// A <c>Task&lt;Result&lt;unit, exn&gt; list&gt;</c> with one entry per grain,
    /// in the same order as <paramref name="grains"/>.
    /// </returns>
    let tryIter<'TGrain> (grains: 'TGrain seq) (f: 'TGrain -> Task) : Task<Result<unit, exn> list> =
        task {
            let wrap (grain: 'TGrain) : Task<Result<unit, exn>> =
                task {
                    try
                        do! f grain
                        return Ok()
                    with ex ->
                        return Error ex
                }

            let tasks = grains |> Seq.map wrap |> Seq.toArray
            let! results = Task.WhenAll(tasks)
            return results |> Array.toList
        }

    /// <summary>
    /// Executes a task-returning function on each grain concurrently, filtering out
    /// <c>None</c> results and returning only the <c>Some</c> values.
    /// If any grain call throws, the entire batch fails with that exception.
    /// </summary>
    /// <typeparam name="TGrain">The grain interface type.</typeparam>
    /// <typeparam name="TResult">The inner result type (inside the <c>option</c>).</typeparam>
    /// <param name="grains">The sequence of grain references to call.</param>
    /// <param name="f">The function to invoke on each grain, returning an option.</param>
    /// <returns>A <c>Task&lt;TResult list&gt;</c> containing only the <c>Some</c> results.</returns>
    let choose<'TGrain, 'TResult>
        (grains: 'TGrain seq)
        (f: 'TGrain -> Task<'TResult option>)
        : Task<'TResult list> =
        task {
            let! results = map grains f
            return results |> List.choose id
        }

    /// <summary>
    /// Partitions the results of concurrent grain calls into successes and failures.
    /// </summary>
    /// <typeparam name="TGrain">The grain interface type.</typeparam>
    /// <typeparam name="TResult">The result type returned by each grain call.</typeparam>
    /// <param name="grains">The sequence of grain references to call.</param>
    /// <param name="f">The function to invoke on each grain.</param>
    /// <returns>
    /// A <c>Task</c> containing a tuple of <c>(successes: TResult list, failures: exn list)</c>.
    /// </returns>
    let partition<'TGrain, 'TResult>
        (grains: 'TGrain seq)
        (f: 'TGrain -> Task<'TResult>)
        : Task<'TResult list * exn list> =
        task {
            let! results = tryMap grains f

            let successes = results |> List.choose (function Ok v -> Some v | _ -> None)
            let failures  = results |> List.choose (function Error e -> Some e | _ -> None)

            return successes, failures
        }
