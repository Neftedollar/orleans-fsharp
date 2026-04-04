/// <summary>
/// Integration tests for the <c>handleTypedWithContextCancellable</c> CE variant.
///
/// These tests verify that grains defined with <c>handleTypedWithContextCancellable</c>
/// — which combines GrainContext, CancellationToken, and a typed result (no manual
/// <c>box</c>) — are dispatched correctly through the universal <c>AddFSharpGrain</c>
/// pattern and that <c>FSharpGrain.ask</c> correctly unboxes the declared result type.
/// </summary>
module Orleans.FSharp.Integration.HandleTypedWithContextCancellableIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleTypedWithContextCancellableIntegrationTests(fixture: ClusterFixture) =

    // ── typed result via ask ──────────────────────────────────────────────────

    [<Fact>]
    let ``TWCCAdd: returns correct int result`` () =
        task {
            let g = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-add-1"
            let! result = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> (TWCCAdd(3, 4))
            test <@ result = 7 @>
        }

    [<Fact>]
    let ``TWCCMul: returns correct int result`` () =
        task {
            let g = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-mul-1"
            let! result = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> (TWCCMul(6, 7))
            test <@ result = 42 @>
        }

    // ── state evolution ───────────────────────────────────────────────────────

    [<Fact>]
    let ``TWCCAdd: state is updated after operation`` () =
        task {
            let g = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-state-1"
            let! _ = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> (TWCCAdd(10, 5))
            let! last = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> GetTWCCLastResult
            test <@ last = 15 @>
        }

    [<Fact>]
    let ``GetTWCCOps: increments after each operation`` () =
        task {
            let g = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-ops-1"
            let! _ = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> (TWCCAdd(1, 2))
            let! _ = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> (TWCCMul(3, 4))
            let! ops = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> GetTWCCOps
            test <@ ops = 2 @>
        }

    [<Fact>]
    let ``GetTWCCLastResult: zero for fresh grain`` () =
        task {
            let g = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-fresh"
            let! last = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> GetTWCCLastResult
            test <@ last = 0 @>
        }

    // ── isolation ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``two grains are isolated`` () =
        task {
            let g1 = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-iso1"
            let g2 = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-iso2"
            let! _ = g1 |> FSharpGrain.ask<TWCCState, TWCCCommand, int> (TWCCAdd(100, 200))
            let! ops2 = g2 |> FSharpGrain.ask<TWCCState, TWCCCommand, int> GetTWCCOps
            test <@ ops2 = 0 @>
        }

    // ── post (fire-and-forget) variant ────────────────────────────────────────

    [<Fact>]
    let ``post TWCCAdd completes without error`` () =
        task {
            let g = FSharpGrain.ref<TWCCState, TWCCCommand> fixture.GrainFactory "twcc-post"
            do! FSharpGrain.post (TWCCAdd(10, 20)) g
            let! last = g |> FSharpGrain.ask<TWCCState, TWCCCommand, int> GetTWCCLastResult
            test <@ last = 30 @>
        }
