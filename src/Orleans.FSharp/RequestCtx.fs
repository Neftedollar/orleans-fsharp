namespace Orleans.FSharp

open System.Threading.Tasks
open Orleans.Runtime

/// <summary>
/// F#-idiomatic wrapper around Orleans <see cref="RequestContext"/> for propagating
/// key-value pairs across grain calls. Values set in the request context are
/// automatically propagated from callers to callees by the Orleans runtime.
/// </summary>
module RequestCtx =

    /// <summary>
    /// Set a value in the Orleans request context.
    /// The value is propagated across grain calls automatically.
    /// </summary>
    /// <param name="key">The context key.</param>
    /// <param name="value">The value to associate with the key.</param>
    let set (key: string) (value: obj) : unit =
        RequestContext.Set(key, value)

    /// <summary>
    /// Get a typed value from the Orleans request context.
    /// Returns <c>Some value</c> if the key exists and can be cast to <typeparamref name="T"/>,
    /// or <c>None</c> if the key is missing or the value is null.
    /// </summary>
    /// <typeparam name="T">The expected type of the value.</typeparam>
    /// <param name="key">The context key.</param>
    /// <returns>An option containing the value if found.</returns>
    let get<'T> (key: string) : 'T option =
        match RequestContext.Get(key) with
        | null -> None
        | :? 'T as value -> Some value
        | _ -> None

    /// <summary>
    /// Get a typed value from the Orleans request context, or return a default value
    /// if the key is missing or the value cannot be cast.
    /// </summary>
    /// <typeparam name="T">The expected type of the value.</typeparam>
    /// <param name="key">The context key.</param>
    /// <param name="defaultValue">The value to return if the key is not found.</param>
    /// <returns>The context value if found, otherwise <paramref name="defaultValue"/>.</returns>
    let getOrDefault<'T> (key: string) (defaultValue: 'T) : 'T =
        match get<'T> key with
        | Some v -> v
        | None -> defaultValue

    /// <summary>
    /// Remove a value from the Orleans request context.
    /// </summary>
    /// <param name="key">The context key to remove.</param>
    let remove (key: string) : unit =
        RequestContext.Remove(key) |> ignore

    /// <summary>
    /// Execute an async function with a scoped request context value.
    /// The value is set before execution and removed after, regardless of whether
    /// the function succeeds or throws.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="key">The context key.</param>
    /// <param name="value">The value to set for the duration of the function.</param>
    /// <param name="f">The async function to execute.</param>
    /// <returns>A Task containing the result of the function.</returns>
    let withValue<'T> (key: string) (value: obj) (f: unit -> Task<'T>) : Task<'T> =
        task {
            RequestContext.Set(key, value)

            try
                return! f ()
            finally
                RequestContext.Remove(key) |> ignore
        }
