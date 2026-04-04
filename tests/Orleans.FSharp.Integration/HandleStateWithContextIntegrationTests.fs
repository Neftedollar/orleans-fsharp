/// <summary>
/// Integration tests for the <c>handleStateWithContext</c> CE variant.
///
/// These tests verify that grains defined with <c>handleStateWithContext</c> — which
/// receives a <c>GrainContext</c> but returns only the new state (no manual <c>box</c>
/// needed) — are dispatched correctly through the universal <c>AddFSharpGrain</c> pattern.
///
/// The grain exercises both the pure state-accumulation path (<c>SWCAdd</c>) and the
/// grain-to-grain forwarding path (<c>SWCForwardPing</c>) to confirm that
/// <c>ctx.GrainFactory</c> is accessible from inside the handler.
/// </summary>
module Orleans.FSharp.Integration.HandleStateWithContextIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleStateWithContextIntegrationTests(fixture: ClusterFixture) =

    // ── pure state accumulation ───────────────────────────────────────────────

    [<Fact>]
    let ``SWCAdd: single add updates SWCSum and SWCSteps`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-a"
            let! s = FSharpGrain.send (SWCAdd 8) g
            test <@ s.SWCSum = 8 @>
            test <@ s.SWCSteps = 1 @>
        }

    [<Fact>]
    let ``SWCAdd: multiple adds accumulate SWCSum`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-b"
            let! _ = FSharpGrain.send (SWCAdd 3) g
            let! _ = FSharpGrain.send (SWCAdd 7) g
            let! s = FSharpGrain.send (SWCAdd 1) g
            test <@ s.SWCSum = 11 @>
            test <@ s.SWCSteps = 3 @>
        }

    [<Fact>]
    let ``SWCAdd: sum equals List.sum of inputs`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-sum"
            let inputs = [2; 4; 6; 8; 10]
            for n in inputs do
                let! _ = FSharpGrain.send (SWCAdd n) g
                ()
            let! s = FSharpGrain.send GetSWCState g
            test <@ s.SWCSum = List.sum inputs @>
            test <@ s.SWCSteps = List.length inputs @>
        }

    // ── grain-to-grain via context.GrainFactory ──────────────────────────────

    [<Fact>]
    let ``SWCForwardPing: calls peer grain via ctx.GrainFactory`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-c"
            let! s = FSharpGrain.send (SWCForwardPing "swc-peer-c") g
            test <@ s.SWCPeerPings = 1 @>
            test <@ s.SWCSteps = 1 @>
        }

    [<Fact>]
    let ``SWCForwardPing: multiple pings increment SWCPeerPings`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-d"
            let! _ = FSharpGrain.send (SWCForwardPing "swc-peer-d") g
            let! _ = FSharpGrain.send (SWCForwardPing "swc-peer-d") g
            let! s = FSharpGrain.send (SWCForwardPing "swc-peer-d") g
            test <@ s.SWCPeerPings = 3 @>
        }

    [<Fact>]
    let ``SWCForwardPing: mixed adds and pings tracked independently`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-e"
            let! _ = FSharpGrain.send (SWCAdd 5) g
            let! _ = FSharpGrain.send (SWCForwardPing "swc-peer-e") g
            let! s = FSharpGrain.send (SWCAdd 3) g
            test <@ s.SWCSum = 8 @>
            test <@ s.SWCPeerPings = 1 @>
            test <@ s.SWCSteps = 3 @>
        }

    // ── read-only state access ────────────────────────────────────────────────

    [<Fact>]
    let ``GetSWCState: fresh grain has zero state`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-fresh"
            let! s = FSharpGrain.send GetSWCState g
            test <@ s.SWCSum = 0 @>
            test <@ s.SWCSteps = 0 @>
            test <@ s.SWCPeerPings = 0 @>
        }

    // ── isolation ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``two grains are isolated`` () =
        task {
            let gf = fixture.GrainFactory
            let g1 = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-iso1"
            let g2 = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-iso2"
            let! _ = FSharpGrain.send (SWCAdd 100) g1
            let! s2 = FSharpGrain.send GetSWCState g2
            test <@ s2.SWCSum = 0 @>
        }

    // ── post (fire-and-forget) variant ────────────────────────────────────────

    [<Fact>]
    let ``post SWCAdd completes without error`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<StateWithCtxState, StateWithCtxCommand> gf "swc-post"
            do! FSharpGrain.post (SWCAdd 13) g
            let! s = FSharpGrain.send GetSWCState g
            test <@ s.SWCSum = 13 @>
        }
