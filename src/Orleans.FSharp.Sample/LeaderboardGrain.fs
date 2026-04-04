namespace Orleans.FSharp.Sample

open System.Collections.Generic
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

// ──────────────────────────────────────────────────────────────────────────────
// Player score grain — tracks a single player's score
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>State for a single player's score grain.</summary>
[<GenerateSerializer>]
type PlayerScoreState =
    { [<Id(0u)>] Score: int
      [<Id(1u)>] GamesPlayed: int }

/// <summary>Commands for the player score grain.</summary>
[<GenerateSerializer>]
type PlayerScoreCommand =
    /// <summary>Add points to the player's total score.</summary>
    | [<Id(0u)>] AddScore of points: int
    /// <summary>Reset the player's score to zero.</summary>
    | [<Id(1u)>] ResetScore
    /// <summary>Get the player's current score (returns PlayerScoreState).</summary>
    | [<Id(2u)>] GetScore

/// <summary>
/// Module containing the player score grain definition.
/// A simple per-player state grain used by the leaderboard as a peer grain.
/// </summary>
module PlayerScoreGrainDef =
    /// <summary>
    /// Player score grain using <c>handleTyped</c>: every command returns
    /// a typed <c>PlayerScoreState</c> result without manual boxing.
    /// </summary>
    let playerScore : GrainDefinition<PlayerScoreState, PlayerScoreCommand> =
        grain {
            defaultState { Score = 0; GamesPlayed = 0 }
            handleTyped (fun state cmd ->
                task {
                    match cmd with
                    | AddScore pts ->
                        let ns = { Score = state.Score + pts; GamesPlayed = state.GamesPlayed + 1 }
                        return ns, ns
                    | ResetScore ->
                        let ns = { Score = 0; GamesPlayed = state.GamesPlayed }
                        return ns, ns
                    | GetScore ->
                        return state, state
                })
            persist "Default"
        }

// ──────────────────────────────────────────────────────────────────────────────
// Leaderboard grain — aggregates top-N players via grain-to-grain calls
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>State for the leaderboard grain.</summary>
[<GenerateSerializer>]
type LeaderboardState =
    { /// <summary>Set of player keys tracked by this leaderboard.</summary>
      [<Id(0u)>] TrackedPlayers: string list
      /// <summary>Cached snapshot of the last computed leaderboard.</summary>
      [<Id(1u)>] LastSnapshot: (string * int) list }

/// <summary>Commands for the leaderboard grain.</summary>
[<GenerateSerializer>]
type LeaderboardCommand =
    /// <summary>Register a new player key to track.</summary>
    | [<Id(0u)>] TrackPlayer of playerKey: string
    /// <summary>
    /// Refresh the leaderboard by querying each tracked player grain
    /// and rebuild the sorted snapshot. Returns the top-N list.
    /// </summary>
    | [<Id(1u)>] RefreshAndGetTop of topN: int
    /// <summary>Return the cached leaderboard snapshot without refreshing.</summary>
    | [<Id(2u)>] GetCachedSnapshot

/// <summary>
/// Module containing the leaderboard grain definition.
/// Demonstrates <c>handleWithContext</c>: on <c>RefreshAndGetTop</c>, the grain
/// calls each tracked <c>PlayerScoreGrain</c> via <c>ctx.GrainFactory</c> to fetch
/// current scores, then assembles a sorted leaderboard.
/// </summary>
module LeaderboardGrainDef =

    /// <summary>
    /// Leaderboard grain defined with <c>handleWithContext</c>.
    /// <para>
    /// <c>RefreshAndGetTop n</c> — for each registered player key, creates a
    /// <c>FSharpGrain.ref&lt;PlayerScoreState, PlayerScoreCommand&gt;</c> using the
    /// grain factory from <c>ctx</c>, fetches the player's current score, then
    /// returns the top-N sorted list as a <c>(string * int) list</c>.
    /// </para>
    /// <para>
    /// This is the canonical example of using <c>handleWithContext</c> for
    /// grain-to-grain communication without any C# interface stubs.
    /// </para>
    /// </summary>
    let leaderboard : GrainDefinition<LeaderboardState, LeaderboardCommand> =
        grain {
            defaultState { TrackedPlayers = []; LastSnapshot = [] }

            handleWithContext (fun ctx state cmd ->
                task {
                    match cmd with
                    | TrackPlayer playerKey ->
                        if List.contains playerKey state.TrackedPlayers then
                            return state, box state.LastSnapshot
                        else
                            let ns = { state with TrackedPlayers = playerKey :: state.TrackedPlayers }
                            return ns, box ns.LastSnapshot

                    | RefreshAndGetTop topN ->
                        // Grain-to-grain fan-out: query each player's score grain in parallel.
                        let fetches =
                            state.TrackedPlayers
                            |> List.map (fun key ->
                                task {
                                    let playerRef =
                                        FSharpGrain.ref<PlayerScoreState, PlayerScoreCommand>
                                            ctx.GrainFactory key
                                    let! score =
                                        FSharpGrain.ask<PlayerScoreState, PlayerScoreCommand, PlayerScoreState>
                                            GetScore playerRef
                                    return key, score.Score
                                })

                        let! allScores = Task.WhenAll(fetches |> List.toArray)
                        let snapshot =
                            allScores
                            |> Array.sortByDescending snd
                            |> Array.truncate topN
                            |> Array.toList

                        let ns = { state with LastSnapshot = snapshot }
                        return ns, box snapshot

                    | GetCachedSnapshot ->
                        return state, box state.LastSnapshot
                })

            persist "Default"
            reentrant
        }
