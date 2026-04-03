namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

/// <summary>
/// Structured logging functions with correlation ID propagation for Orleans grains.
/// Uses AsyncLocal for ambient correlation context that flows across grain calls.
/// All logging functions use structured templates (no string interpolation).
/// </summary>
[<RequireQualifiedAccess>]
module Log =

    /// <summary>
    /// AsyncLocal storage for the current correlation ID.
    /// Flows automatically across task continuations and grain calls.
    /// </summary>
    let private correlationIdStore = new AsyncLocal<string>()

    /// <summary>
    /// Gets the current correlation ID from the ambient context.
    /// Returns None if no correlation scope is active.
    /// </summary>
    /// <returns>The current correlation ID, or None if not set.</returns>
    let currentCorrelationId () : string option =
        match correlationIdStore.Value with
        | null -> None
        | "" -> None
        | id -> Some id

    /// <summary>
    /// Creates a correlation scope. All logs emitted within the scope
    /// share the specified correlation ID. The previous correlation ID
    /// is restored when the scope completes.
    /// </summary>
    /// <param name="correlationId">The correlation ID to set for this scope.</param>
    /// <param name="body">The async function to execute within the correlation scope.</param>
    /// <typeparam name="'T">The return type of the scoped function.</typeparam>
    /// <returns>A Task containing the result of the scoped function.</returns>
    let withCorrelation (correlationId: string) (body: unit -> Task<'T>) : Task<'T> =
        task {
            let previous = correlationIdStore.Value
            correlationIdStore.Value <- correlationId

            try
                return! body ()
            finally
                correlationIdStore.Value <- previous
        }

    /// <summary>
    /// Logs an informational structured message.
    /// Automatically attaches the current correlation ID if one is active.
    /// </summary>
    /// <param name="logger">The ILogger instance to log to.</param>
    /// <param name="template">The structured log message template.</param>
    /// <param name="args">The template arguments.</param>
    let logInfo (logger: ILogger) (template: string) ([<ParamArray>] args: obj[]) : unit =
        match currentCorrelationId () with
        | Some cid ->
            let enrichedTemplate = template + " {CorrelationId}"
            let enrichedArgs = Array.append args [| box cid |]
            logger.LogInformation(enrichedTemplate, enrichedArgs)
        | None -> logger.LogInformation(template, args)

    /// <summary>
    /// Logs a warning structured message.
    /// Automatically attaches the current correlation ID if one is active.
    /// </summary>
    /// <param name="logger">The ILogger instance to log to.</param>
    /// <param name="template">The structured log message template.</param>
    /// <param name="args">The template arguments.</param>
    let logWarning (logger: ILogger) (template: string) ([<ParamArray>] args: obj[]) : unit =
        match currentCorrelationId () with
        | Some cid ->
            let enrichedTemplate = template + " {CorrelationId}"
            let enrichedArgs = Array.append args [| box cid |]
            logger.LogWarning(enrichedTemplate, enrichedArgs)
        | None -> logger.LogWarning(template, args)

    /// <summary>
    /// Logs an error structured message with an exception.
    /// Automatically attaches the current correlation ID if one is active.
    /// </summary>
    /// <param name="logger">The ILogger instance to log to.</param>
    /// <param name="exn">The exception associated with the error.</param>
    /// <param name="template">The structured log message template.</param>
    /// <param name="args">The template arguments.</param>
    let logError (logger: ILogger) (exn: exn) (template: string) ([<ParamArray>] args: obj[]) : unit =
        match currentCorrelationId () with
        | Some cid ->
            let enrichedTemplate = template + " {CorrelationId}"
            let enrichedArgs = Array.append args [| box cid |]
            logger.LogError(exn, enrichedTemplate, enrichedArgs)
        | None -> logger.LogError(exn, template, args)

    /// <summary>
    /// Logs a debug structured message.
    /// Automatically attaches the current correlation ID if one is active.
    /// </summary>
    /// <param name="logger">The ILogger instance to log to.</param>
    /// <param name="template">The structured log message template.</param>
    /// <param name="args">The template arguments.</param>
    let logDebug (logger: ILogger) (template: string) ([<ParamArray>] args: obj[]) : unit =
        match currentCorrelationId () with
        | Some cid ->
            let enrichedTemplate = template + " {CorrelationId}"
            let enrichedArgs = Array.append args [| box cid |]
            logger.LogDebug(enrichedTemplate, enrichedArgs)
        | None -> logger.LogDebug(template, args)
