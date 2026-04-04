/// <summary>
/// Integration tests for the <c>handleTypedCancellable</c> CE variant combined with
/// <c>FSharpGrain.ask</c>.
///
/// These tests verify that grains defined with <c>handleTypedCancellable</c> — which
/// combines the automatic boxing of <c>handleTyped</c> with CancellationToken support —
/// are dispatched correctly end-to-end through the universal <c>AddFSharpGrain</c> pattern.
///
/// The handler returns <c>Task&lt;'State * 'Result&gt;</c>; the framework boxes the result
/// automatically, and <c>FSharpGrain.ask</c> unboxes it to the declared <c>'Result</c> type.
/// </summary>
module Orleans.FSharp.Integration.HandleTypedCancellableIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleTypedCancellableIntegrationTests(fixture: ClusterFixture) =

    // ── typed result access via ask ───────────────────────────────────────────

    [<Fact>]
    let ``TypedCancAdd: returns correct int result`` () =
        task {
            let g = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-add-1"
            let! result = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancAdd(3, 4))
            test <@ result = 7 @>
        }

    [<Fact>]
    let ``TypedCancMul: returns correct int result`` () =
        task {
            let g = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-mul-1"
            let! result = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancMul(6, 7))
            test <@ result = 42 @>
        }

    // ── state evolution ───────────────────────────────────────────────────────

    [<Fact>]
    let ``TypedCancAdd: state is updated after operation`` () =
        task {
            let g = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-state-1"
            let! _ = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancAdd(10, 5))
            let! last = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> GetTypedCancLastResult
            test <@ last = 15 @>
        }

    [<Fact>]
    let ``GetTypedCancOps: increments after each operation`` () =
        task {
            let g = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-ops-1"
            let! _ = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancAdd(1, 2))
            let! _ = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancMul(3, 4))
            let! ops = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> GetTypedCancOps
            test <@ ops = 2 @>
        }

    [<Fact>]
    let ``GetTypedCancLastResult: zero for fresh grain`` () =
        task {
            let g = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-fresh"
            let! last = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> GetTypedCancLastResult
            test <@ last = 0 @>
        }

    // ── mixed operations ──────────────────────────────────────────────────────

    [<Fact>]
    let ``mixed Add and Mul: state tracks last result`` () =
        task {
            let g = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-mixed-1"
            let! r1 = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancAdd(2, 3))
            let! r2 = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancMul(4, 5))
            let! ops = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> GetTypedCancOps
            test <@ r1 = 5 @>
            test <@ r2 = 20 @>
            test <@ ops = 2 @>
        }

    // ── isolation ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``two grains are isolated`` () =
        task {
            let g1 = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-iso1"
            let g2 = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-iso2"
            let! _ = g1 |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> (TypedCancAdd(100, 200))
            let! ops2 = g2 |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> GetTypedCancOps
            test <@ ops2 = 0 @>
        }

    // ── post (fire-and-forget) variant ────────────────────────────────────────

    [<Fact>]
    let ``post TypedCancAdd completes without error`` () =
        task {
            let g = FSharpGrain.ref<TypedCancState, TypedCancCommand> fixture.GrainFactory "tc-post"
            do! FSharpGrain.post (TypedCancAdd(10, 20)) g
            let! last = g |> FSharpGrain.ask<TypedCancState, TypedCancCommand, int> GetTypedCancLastResult
            test <@ last = 30 @>
        }
