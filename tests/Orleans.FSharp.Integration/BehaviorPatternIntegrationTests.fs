/// <summary>
/// Integration tests for the <c>Behavior</c> pattern when plugged into the universal grain
/// dispatch via <c>Behavior.run</c>.
///
/// These tests verify that a grain defined with
/// <code>handleState (Behavior.run myHandler)</code>
/// behaves correctly inside a real Orleans silo: phase transitions are persisted, illegal
/// transitions leave state unchanged, and the grain is isolated per key.
/// </summary>
module Orleans.FSharp.Integration.BehaviorPatternIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type BehaviorPatternIntegrationTests(fixture: ClusterFixture) =

    // ── phase transitions ────────────────────────────────────────────────────

    [<Fact>]
    member _.``fresh grain starts in Idle phase`` () =
        task {
            let g = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-idle-1"
            let! state = g |> FSharpGrain.send GetPhase
            test <@ state.Phase = Idle @>
        }

    [<Fact>]
    member _.``Start transitions Idle to Running`` () =
        task {
            let g = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-start-1"
            let! state = g |> FSharpGrain.send Start
            test <@ state.Phase = Running 0 @>
        }

    [<Fact>]
    member _.``CompleteStep increments step counter`` () =
        task {
            let g = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-steps-1"
            let! _ = g |> FSharpGrain.send Start
            let! s1 = g |> FSharpGrain.send CompleteStep
            let! s2 = g |> FSharpGrain.send CompleteStep
            test <@ s1.Phase = Running 1 @>
            test <@ s2.Phase = Running 2 @>
        }

    [<Fact>]
    member _.``Finish transitions Running to Done with summary`` () =
        task {
            let g = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-finish-1"
            let! _ = g |> FSharpGrain.send Start
            let! _ = g |> FSharpGrain.send CompleteStep
            let! state = g |> FSharpGrain.send (Finish "report")
            match state.Phase with
            | Done s -> test <@ s.Contains("report") && s.Contains("1 steps") @>
            | other  -> failwith $"Expected Done, got {other}"
        }

    // ── illegal transitions are silently ignored ─────────────────────────────

    [<Fact>]
    member _.``CompleteStep in Idle is a no-op`` () =
        task {
            let g = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-noop-1"
            let! state = g |> FSharpGrain.send CompleteStep
            test <@ state.Phase = Idle @>   // Stay state from default arm
        }

    [<Fact>]
    member _.``Start in Running is a no-op`` () =
        task {
            let g = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-noop-2"
            let! _ = g |> FSharpGrain.send Start
            let! _ = g |> FSharpGrain.send CompleteStep
            let! state = g |> FSharpGrain.send Start    // ignored in Running
            test <@ state.Phase = Running 1 @>
        }

    // ── isolation ────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``two grains are isolated`` () =
        task {
            let g1 = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-iso-1"
            let g2 = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-iso-2"
            let! _ = g1 |> FSharpGrain.send Start
            let! _ = g1 |> FSharpGrain.send CompleteStep
            let! s2 = g2 |> FSharpGrain.send GetPhase
            test <@ s2.Phase = Idle @>
        }

    // ── post (fire-and-forget) ───────────────────────────────────────────────

    [<Fact>]
    member _.``post Start completes without error`` () =
        task {
            let g = FSharpGrain.ref<WorkflowState, WorkflowCommand> fixture.GrainFactory "wf-post-1"
            do! FSharpGrain.post Start g
            let! state = g |> FSharpGrain.send GetPhase
            test <@ state.Phase = Running 0 @>
        }
