namespace Orleans.FSharp.Testing

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging

/// <summary>
/// Represents a captured structured log entry with level, template, properties, and timestamp.
/// Used for test assertions on log output.
/// </summary>
type CapturedLogEntry =
    {
        /// <summary>The log level of the entry (Information, Warning, Error, Debug, etc.).</summary>
        Level: LogLevel
        /// <summary>The original structured message template.</summary>
        Template: string
        /// <summary>The properties (template argument values) associated with the log entry.</summary>
        Properties: Map<string, obj>
        /// <summary>The timestamp when the log entry was captured.</summary>
        Timestamp: DateTimeOffset
        /// <summary>The optional exception associated with the log entry.</summary>
        Exception: exn option
    }

/// <summary>
/// An in-memory logger that captures structured log entries for test assertions.
/// Implements ILogger so it can be used as a drop-in replacement in tests.
/// </summary>
type CapturingLogger(categoryName: string) =
    let entries = Collections.Concurrent.ConcurrentBag<CapturedLogEntry>()

    /// <summary>Gets all captured log entries.</summary>
    member _.Entries: CapturedLogEntry list =
        entries |> Seq.toList |> List.rev

    /// <summary>Clears all captured log entries.</summary>
    member _.Clear() = entries.Clear()

    /// <summary>Gets the category name of this logger.</summary>
    member _.CategoryName = categoryName

    interface ILogger with
        member _.BeginScope<'TState>(state: 'TState) : IDisposable =
            { new IDisposable with
                member _.Dispose() = ()
            }

        member _.IsEnabled(_logLevel: LogLevel) : bool = true

        member _.Log<'TState>
            (
                logLevel: LogLevel,
                _eventId: EventId,
                state: 'TState,
                exn: exn,
                formatter: Func<'TState, exn, string>
            ) : unit =
            let message = formatter.Invoke(state, exn)

            let properties =
                match box state with
                | :? IReadOnlyList<KeyValuePair<string, obj>> as kvps ->
                    kvps
                    |> Seq.fold
                        (fun acc kvp ->
                            if kvp.Key = "{OriginalFormat}" then
                                acc
                            else
                                acc |> Map.add kvp.Key kvp.Value)
                        Map.empty
                | _ -> Map.empty |> Map.add "Message" (box message)

            let template =
                match box state with
                | :? IReadOnlyList<KeyValuePair<string, obj>> as kvps ->
                    kvps
                    |> Seq.tryFind (fun kvp -> kvp.Key = "{OriginalFormat}")
                    |> Option.map (fun kvp -> string kvp.Value)
                    |> Option.defaultValue message
                | _ -> message

            let entry =
                {
                    Level = logLevel
                    Template = template
                    Properties = properties
                    Timestamp = DateTimeOffset.UtcNow
                    Exception = if isNull (box exn) then None else Some exn
                }

            entries.Add(entry)

/// <summary>
/// An ILoggerFactory that creates CapturingLogger instances and tracks all created loggers.
/// </summary>
type CapturingLoggerFactory() =
    let loggers = Collections.Concurrent.ConcurrentDictionary<string, CapturingLogger>()

    /// <summary>Gets all log entries from all loggers created by this factory.</summary>
    member _.AllEntries: CapturedLogEntry list =
        loggers.Values
        |> Seq.collect (fun l -> l.Entries)
        |> Seq.sortBy (fun e -> e.Timestamp)
        |> Seq.toList

    /// <summary>Clears all entries from all loggers.</summary>
    member _.Clear() =
        loggers.Values |> Seq.iter (fun l -> l.Clear())

    interface ILoggerFactory with
        member _.CreateLogger(categoryName: string) : ILogger =
            loggers.GetOrAdd(categoryName, fun name -> CapturingLogger(name)) :> ILogger

        member _.AddProvider(_provider: ILoggerProvider) : unit = ()
        member _.Dispose() = ()

/// <summary>
/// Functions for capturing and asserting on log entries in tests.
/// </summary>
[<RequireQualifiedAccess>]
module LogCapture =

    /// <summary>
    /// Creates a new CapturingLoggerFactory for use in tests.
    /// </summary>
    /// <returns>A new CapturingLoggerFactory instance.</returns>
    let create () : CapturingLoggerFactory = new CapturingLoggerFactory()

    /// <summary>
    /// Gets all captured log entries from a CapturingLoggerFactory.
    /// Entries are returned sorted by timestamp.
    /// </summary>
    /// <param name="factory">The capturing logger factory.</param>
    /// <returns>A list of captured log entries sorted by timestamp.</returns>
    let captureLogs (factory: CapturingLoggerFactory) : CapturedLogEntry list = factory.AllEntries
