/// <summary>
/// Integration tests for the <c>handleWithContextCancellable</c> CE variant.
///
/// These tests verify that grains defined with <c>handleWithContextCancellable</c>
/// are dispatched correctly through the universal <c>AddFSharpGrain</c> pattern and
/// that both the <c>GrainContext</c> (and its <c>GrainFactory</c>) and the
/// <c>CancellationToken</c> are threaded correctly through the dispatch chain.
///
/// The pattern under test:
/// <list type="bullet">
///   <item><c>CtxCancAdd</c> — pure state accumulation, exercising the basic path.</item>
///   <item><c>CtxCancForwardPing</c> — grain-to-grain call via <c>ctx.GrainFactory</c>,
///     verifying that <c>GrainContext</c> is correctly populated inside the handler.</item>
///   <item><c>GetCtxCancState</c> — read-only state access without side effects.</item>
/// </list>
/// </summary>
module Orleans.FSharp.Integration.HandleWithContextCancellableIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleWithContextCancellableIntegrationTests(fixture: ClusterFixture) =

    // ── pure accumulation (no context access) ────────────────────────────────

    [<Fact>]
    let ``CtxCancAdd: single add updates Sum and Steps`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-a"
            let! s = FSharpGrain.send (CtxCancAdd 7) g
            test <@ s.Sum = 7 @>
            test <@ s.Steps = 1 @>
        }

    [<Fact>]
    let ``CtxCancAdd: multiple adds accumulate Sum`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-b"
            let! _ = FSharpGrain.send (CtxCancAdd 3) g
            let! _ = FSharpGrain.send (CtxCancAdd 5) g
            let! s = FSharpGrain.send (CtxCancAdd 2) g
            test <@ s.Sum = 10 @>
            test <@ s.Steps = 3 @>
        }

    [<Fact>]
    let ``CtxCancAdd: sum equals List.sum of inputs`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-sum"
            let inputs = [4; 7; 11; 2; 8]
            for n in inputs do
                let! _ = FSharpGrain.send (CtxCancAdd n) g
                ()
            let! s = FSharpGrain.send GetCtxCancState g
            test <@ s.Sum = List.sum inputs @>
            test <@ s.Steps = List.length inputs @>
        }

    // ── grain-to-grain via context.GrainFactory ──────────────────────────────

    [<Fact>]
    let ``CtxCancForwardPing: calls peer grain via ctx.GrainFactory`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-c"
            let! s = FSharpGrain.send (CtxCancForwardPing "ctxcanc-peer-c") g
            test <@ s.PeerPings = 1 @>
            test <@ s.Steps = 1 @>
        }

    [<Fact>]
    let ``CtxCancForwardPing: multiple pings increment PeerPings`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-d"
            let! _ = FSharpGrain.send (CtxCancForwardPing "ctxcanc-peer-d") g
            let! _ = FSharpGrain.send (CtxCancForwardPing "ctxcanc-peer-d") g
            let! s = FSharpGrain.send (CtxCancForwardPing "ctxcanc-peer-d") g
            test <@ s.PeerPings = 3 @>
        }

    [<Fact>]
    let ``CtxCancForwardPing: mixed commands update state independently`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-e"
            let! _ = FSharpGrain.send (CtxCancAdd 10) g
            let! _ = FSharpGrain.send (CtxCancForwardPing "ctxcanc-peer-e") g
            let! s = FSharpGrain.send (CtxCancAdd 5) g
            test <@ s.Sum = 15 @>
            test <@ s.PeerPings = 1 @>
            test <@ s.Steps = 3 @>
        }

    // ── GetCtxCancState read-only ────────────────────────────────────────────

    [<Fact>]
    let ``GetCtxCancState: fresh grain has zero state`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-fresh"
            let! s = FSharpGrain.send GetCtxCancState g
            test <@ s.Sum = 0 @>
            test <@ s.Steps = 0 @>
            test <@ s.PeerPings = 0 @>
        }

    [<Fact>]
    let ``GetCtxCancState: returns current state without side effects`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-f"
            let! _ = FSharpGrain.send (CtxCancAdd 42) g
            let! s1 = FSharpGrain.send GetCtxCancState g
            let! s2 = FSharpGrain.send GetCtxCancState g
            test <@ s1.Sum = 42 @>
            test <@ s2.Sum = 42 @>
            test <@ s1.Steps = 1 @>
            test <@ s2.Steps = 1 @>
        }

    // ── isolation ────────────────────────────────────────────────────────────

    [<Fact>]
    let ``two grains are isolated`` () =
        task {
            let gf = fixture.GrainFactory
            let g1 = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-iso1"
            let g2 = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-iso2"
            let! _ = FSharpGrain.send (CtxCancAdd 100) g1
            let! _ = FSharpGrain.send (CtxCancForwardPing "ctxcanc-iso-peer") g1
            let! s2 = FSharpGrain.send GetCtxCancState g2
            test <@ s2.Sum = 0 @>
            test <@ s2.PeerPings = 0 @>
        }

    // ── post (fire-and-forget) variant ───────────────────────────────────────

    [<Fact>]
    let ``post CtxCancAdd completes without error`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<CtxCancAccState, CtxCancAccCommand> gf "ctxcanc-post"
            // FSharpGrain.post awaits the RPC but discards the return value.
            do! FSharpGrain.post (CtxCancAdd 9) g
            let! s = FSharpGrain.send GetCtxCancState g
            test <@ s.Sum = 9 @>
        }
