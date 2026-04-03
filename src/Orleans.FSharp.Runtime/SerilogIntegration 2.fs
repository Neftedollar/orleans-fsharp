namespace Orleans.FSharp.Runtime

open Microsoft.Extensions.Logging
open Orleans.FSharp

/// <summary>
/// Serilog integration helpers for enriching log entries with grain context properties.
/// Provides functions to create loggers enriched with grain type and grain ID.
/// </summary>
[<RequireQualifiedAccess>]
module SerilogIntegration =

    /// <summary>
    /// Creates a logger scope that adds GrainType and GrainId properties to all log entries.
    /// Use this within grain handlers to automatically attach grain context to logs.
    /// </summary>
    /// <param name="logger">The ILogger to create the scope on.</param>
    /// <param name="grainType">The grain type name.</param>
    /// <param name="grainId">The grain identity string.</param>
    /// <returns>An IDisposable scope that adds grain context properties.</returns>
    let withGrainContext (logger: ILogger) (grainType: string) (grainId: string) : System.IDisposable =
        logger.BeginScope(dict [ "GrainType", box grainType; "GrainId", box grainId ])

    /// <summary>
    /// Logs a structured message with grain context properties attached.
    /// Combines grain context enrichment with the Orleans.FSharp.Log module.
    /// </summary>
    /// <param name="logger">The ILogger instance to log to.</param>
    /// <param name="grainType">The grain type name.</param>
    /// <param name="grainId">The grain identity string.</param>
    /// <param name="template">The structured log message template.</param>
    /// <param name="args">The template arguments.</param>
    let logWithGrainContext
        (logger: ILogger)
        (grainType: string)
        (grainId: string)
        (template: string)
        ([<System.ParamArray>] args: obj[])
        : unit =
        let enrichedTemplate = template + " {GrainType} {GrainId}"
        let enrichedArgs = Array.append args [| box grainType; box grainId |]
        Log.logInfo logger enrichedTemplate enrichedArgs
