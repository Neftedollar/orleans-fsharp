namespace SignalRRealtime.Grains

open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp
open SignalRRealtime.Shared

/// <summary>
/// State of the dashboard grain, tracking the sequence number for updates.
/// </summary>
type DashboardState =
    { SequenceNumber: int64 }

/// <summary>
/// Commands for the dashboard grain.
/// </summary>
type DashboardCommand =
    /// <summary>Get the latest dashboard update with current metrics.</summary>
    | GetLatestUpdate
    /// <summary>Get the current sequence number.</summary>
    | GetSequenceNumber

/// <summary>
/// Grain interface for the dashboard grain.
/// The grain generates metric events on a timer and exposes them via HandleMessage.
/// The Web project's SignalR hub polls this grain to push updates to browsers.
/// </summary>
type IDashboardGrain =
    inherit IGrainWithStringKey

    /// <summary>Sends a command to the dashboard grain and returns the result.</summary>
    abstract HandleMessage: DashboardCommand -> Task<obj>

/// <summary>
/// Module containing the dashboard grain definition.
/// Uses the grain CE with a declarative timer for periodic metric generation.
/// </summary>
module DashboardGrainDef =

    /// <summary>
    /// Generates a random set of metrics simulating a live system dashboard.
    /// </summary>
    /// <param name="sequenceNumber">The current sequence number for the update.</param>
    /// <returns>A DashboardUpdate with randomly generated metrics.</returns>
    let generateMetrics (sequenceNumber: int64) : DashboardUpdate =
        let rng = Random.Shared
        let now = DateTime.UtcNow

        let metrics =
            [
                { Name = "cpu"; Value = Math.Round(rng.NextDouble() * 100.0, 1); Timestamp = now }
                { Name = "memory"; Value = Math.Round(40.0 + rng.NextDouble() * 50.0, 1); Timestamp = now }
                { Name = "requests_per_sec"; Value = Math.Round(rng.NextDouble() * 1000.0, 0); Timestamp = now }
                { Name = "latency_ms"; Value = Math.Round(5.0 + rng.NextDouble() * 95.0, 1); Timestamp = now }
            ]

        { Metrics = metrics; SequenceNumber = sequenceNumber }

    /// <summary>
    /// The dashboard grain definition using the grain computation expression.
    /// Includes a declarative timer that generates metrics every 2 seconds.
    /// </summary>
    let dashboard =
        grain {
            defaultState { SequenceNumber = 0L }

            handle (fun state cmd ->
                task {
                    match cmd with
                    | GetLatestUpdate ->
                        let next = { SequenceNumber = state.SequenceNumber + 1L }
                        let update = generateMetrics next.SequenceNumber
                        return next, box update
                    | GetSequenceNumber ->
                        return state, box state.SequenceNumber
                })

            onTimer
                "MetricTick"
                (TimeSpan.FromSeconds(2.0))
                (TimeSpan.FromSeconds(2.0))
                (fun state ->
                    task {
                        let next = { SequenceNumber = state.SequenceNumber + 1L }
                        return next
                    })
        }
