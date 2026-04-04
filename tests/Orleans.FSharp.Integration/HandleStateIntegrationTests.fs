/// <summary>
/// Integration tests for the <c>handleState</c> CE variant.
///
/// <c>handleState</c> is a simplified handler that returns only the updated state —
/// no explicit result value, no manual <c>box</c> call required.
/// These tests verify end-to-end behaviour through <c>FSharpGrain.send</c> and
/// <c>FSharpGrain.post</c> using a score-accumulator grain registered in
/// <c>ClusterFixture.TestSiloConfigurator</c>.
/// </summary>
module Orleans.FSharp.Integration.HandleStateIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleStateTests(fixture: ClusterFixture) =

    // ── Basic send returning state ────────────────────────────────────────────

    [<Fact>]
    member _.``handleState: send returns updated state after AddPoints`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-add-1"
            let! state = handle |> FSharpGrain.send (AddPoints 10)
            test <@ state.Score = 10 @>
            test <@ state.Moves = 1 @>
        }

    [<Fact>]
    member _.``handleState: send returns updated state after SubtractPoints`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-sub-1"
            do! handle |> FSharpGrain.post (AddPoints 20) // warm up
            let! state = handle |> FSharpGrain.send (SubtractPoints 5)
            test <@ state.Score = 15 @>
        }

    [<Fact>]
    member _.``handleState: ResetScore clears score to zero`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-reset-1"
            do! handle |> FSharpGrain.post (AddPoints 100)
            let! state = handle |> FSharpGrain.send ResetScore
            test <@ state.Score = 0 @>
        }

    // ── State accumulation across multiple calls ─────────────────────────────

    [<Fact>]
    member _.``handleState: score accumulates correctly across sequential calls`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-accum-1"
            let! s1 = handle |> FSharpGrain.send (AddPoints 5)
            let! s2 = handle |> FSharpGrain.send (AddPoints 3)
            let! s3 = handle |> FSharpGrain.send (SubtractPoints 2)
            test <@ s1.Score = 5 @>
            test <@ s2.Score = 8 @>
            test <@ s3.Score = 6 @>
        }

    [<Fact>]
    member _.``handleState: move counter increments on every command`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-moves-1"
            do! handle |> FSharpGrain.post (AddPoints 1)
            do! handle |> FSharpGrain.post (AddPoints 2)
            do! handle |> FSharpGrain.post ResetScore
            let! state = handle |> FSharpGrain.send (AddPoints 0)
            test <@ state.Moves = 4 @>
        }

    // ── Fresh grain default state ─────────────────────────────────────────────

    [<Fact>]
    member _.``handleState: fresh grain returns default state on first send`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-fresh-1"
            let! state = handle |> FSharpGrain.send (AddPoints 0)
            test <@ state.Score = 0 @>
            test <@ state.Moves = 1 @>
        }

    // ── Fire-and-forget with post ─────────────────────────────────────────────

    [<Fact>]
    member _.``handleState: post followed by send reflects accumulated state`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-post-1"
            // Fire-and-forget 3 adds
            do! handle |> FSharpGrain.post (AddPoints 10)
            do! handle |> FSharpGrain.post (AddPoints 20)
            do! handle |> FSharpGrain.post (AddPoints 30)
            // A final send to read back state
            let! state = handle |> FSharpGrain.send (AddPoints 0)
            test <@ state.Score = 60 @>
        }

    // ── Negative scores are valid ─────────────────────────────────────────────

    [<Fact>]
    member _.``handleState: score can go negative via SubtractPoints`` () =
        task {
            let handle = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-neg-1"
            let! state = handle |> FSharpGrain.send (SubtractPoints 7)
            test <@ state.Score = -7 @>
        }

    // ── Multiple grains are independent ──────────────────────────────────────

    [<Fact>]
    member _.``handleState: two grains with different keys maintain independent state`` () =
        task {
            let h1 = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-isolate-A"
            let h2 = FSharpGrain.ref<ScoreState, ScoreCommand> fixture.GrainFactory "score-isolate-B"
            do! h1 |> FSharpGrain.post (AddPoints 100)
            do! h2 |> FSharpGrain.post (AddPoints 50)
            let! s1 = h1 |> FSharpGrain.send (AddPoints 0)
            let! s2 = h2 |> FSharpGrain.send (AddPoints 0)
            test <@ s1.Score = 100 @>
            test <@ s2.Score = 50  @>
        }
