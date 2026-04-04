/// <summary>
/// Integration tests for the <c>handleStateCancellable</c> CE variant.
///
/// These tests verify that grains defined with <c>handleStateCancellable</c>
/// are dispatched correctly through the universal <c>AddFSharpGrain</c> pattern
/// and that the <c>CancellationToken</c> is threaded through the dispatch chain.
///
/// Note: the integration tests do not exercise cancellation mid-flight (that would
/// require the handler to check the token), but they confirm that the normal path
/// works correctly and the token is accepted without errors.
/// </summary>
module Orleans.FSharp.Integration.HandleCancellableIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleCancellableIntegrationTests(fixture: ClusterFixture) =

    // ── basic accumulation ────────────────────────────────────────────────────

    [<Fact>]
    let ``handleStateCancellable: Accumulate adds to Sum`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-a"
            let! s = FSharpGrain.send (Accumulate 10) g
            test <@ s.Sum = 10 @>
            test <@ s.Steps = 1 @>
        }

    [<Fact>]
    let ``handleStateCancellable: multiple Accumulate calls accumulate sum`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-b"
            let! _ = FSharpGrain.send (Accumulate 5) g
            let! _ = FSharpGrain.send (Accumulate 3) g
            let! s = FSharpGrain.send (Accumulate 2) g
            test <@ s.Sum = 10 @>
            test <@ s.Steps = 3 @>
        }

    [<Fact>]
    let ``handleStateCancellable: GetAcc returns state without side effects`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-c"
            let! _ = FSharpGrain.send (Accumulate 7) g
            let! s = FSharpGrain.send GetAcc g
            test <@ s.Sum = 7 @>
            test <@ s.Steps = 1 @>
        }

    [<Fact>]
    let ``handleStateCancellable: fresh grain starts at zero`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-fresh"
            let! s = FSharpGrain.send GetAcc g
            test <@ s.Sum = 0 @>
            test <@ s.Steps = 0 @>
        }

    [<Fact>]
    let ``handleStateCancellable: two grains are isolated`` () =
        task {
            let gf = fixture.GrainFactory
            let g1 = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-iso1"
            let g2 = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-iso2"
            let! _ = FSharpGrain.send (Accumulate 100) g1
            let! s2 = FSharpGrain.send GetAcc g2
            test <@ s2.Sum = 0 @>
        }

    [<Fact>]
    let ``handleStateCancellable: sum equals List.sum of inputs`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-sum"
            let inputs = [4; 7; 11; 2; 8]
            for n in inputs do
                let! _ = FSharpGrain.send (Accumulate n) g
                ()
            let! s = FSharpGrain.send GetAcc g
            test <@ s.Sum = List.sum inputs @>
            test <@ s.Steps = List.length inputs @>
        }

    // ── post (fire-and-forget) variant ────────────────────────────────────────

    [<Fact>]
    let ``handleStateCancellable: post Accumulate completes without error`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CancellableAccState, CancellableAccCommand> gf "cacc-post"
            do! FSharpGrain.post (Accumulate 5) g
            let! s = FSharpGrain.send GetAcc g
            test <@ s.Sum = 5 @>
        }
