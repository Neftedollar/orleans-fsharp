/// <summary>
/// Integration tests for the <c>handleTypedWithContext</c> CE variant combined with
/// <c>FSharpGrain.ask</c>.
///
/// These tests verify that grains defined with <c>handleTypedWithContext</c> — which
/// receives a <c>GrainContext</c> and returns a typed <c>'Result</c> without manual
/// <c>box</c> — are dispatched correctly through the universal <c>AddFSharpGrain</c>
/// pattern and that <c>FSharpGrain.ask</c> correctly unboxes the declared result type.
/// </summary>
module Orleans.FSharp.Integration.HandleTypedWithContextIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleTypedWithContextIntegrationTests(fixture: ClusterFixture) =

    // ── typed result access via ask ───────────────────────────────────────────

    [<Fact>]
    let ``TWCAdd: returns correct int result`` () =
        task {
            let g = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-add-1"
            let! result = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCAdd(3, 4))
            test <@ result = 7 @>
        }

    [<Fact>]
    let ``TWCMul: returns correct int result`` () =
        task {
            let g = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-mul-1"
            let! result = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCMul(6, 7))
            test <@ result = 42 @>
        }

    // ── state evolution ───────────────────────────────────────────────────────

    [<Fact>]
    let ``TWCAdd: state is updated after operation`` () =
        task {
            let g = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-state-1"
            let! _ = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCAdd(10, 5))
            let! last = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> GetTWCLastResult
            test <@ last = 15 @>
        }

    [<Fact>]
    let ``GetTWCOps: increments after each operation`` () =
        task {
            let g = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-ops-1"
            let! _ = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCAdd(1, 2))
            let! _ = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCMul(3, 4))
            let! ops = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> GetTWCOps
            test <@ ops = 2 @>
        }

    [<Fact>]
    let ``GetTWCLastResult: zero for fresh grain`` () =
        task {
            let g = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-fresh"
            let! last = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> GetTWCLastResult
            test <@ last = 0 @>
        }

    // ── mixed operations ──────────────────────────────────────────────────────

    [<Fact>]
    let ``mixed Add and Mul: results are computed correctly`` () =
        task {
            let g = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-mixed-1"
            let! r1 = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCAdd(2, 3))
            let! r2 = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCMul(4, 5))
            let! ops = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> GetTWCOps
            test <@ r1 = 5 @>
            test <@ r2 = 20 @>
            test <@ ops = 2 @>
        }

    // ── isolation ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``two grains are isolated`` () =
        task {
            let g1 = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-iso1"
            let g2 = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-iso2"
            let! _ = g1 |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> (TWCAdd(100, 200))
            let! ops2 = g2 |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> GetTWCOps
            test <@ ops2 = 0 @>
        }

    // ── post (fire-and-forget) variant ────────────────────────────────────────

    [<Fact>]
    let ``post TWCAdd completes without error`` () =
        task {
            let g = FSharpGrain.ref<TypedWithCtxState, TypedWithCtxCommand> fixture.GrainFactory "twc-post"
            do! FSharpGrain.post (TWCAdd(10, 20)) g
            let! last = g |> FSharpGrain.ask<TypedWithCtxState, TypedWithCtxCommand, int> GetTWCLastResult
            test <@ last = 30 @>
        }
