/// <summary>
/// Integration tests for the <c>handleStateWithContextCancellable</c> CE variant.
///
/// These tests verify that grains defined with <c>handleStateWithContextCancellable</c>
/// — the maximum combination: GrainContext + CancellationToken, with state-only return
/// and no manual <c>box</c> — are dispatched correctly through the universal
/// <c>AddFSharpGrain</c> pattern.
///
/// The grain exercises:
/// <list type="bullet">
///   <item><c>SWCCAdd</c> — pure state accumulation (no context access)</item>
///   <item><c>SWCCForwardPing</c> — grain-to-grain via <c>ctx.GrainFactory</c></item>
///   <item><c>GetSWCCState</c> — read-only state access</item>
/// </list>
/// </summary>
module Orleans.FSharp.Integration.HandleStateWithContextCancellableIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleStateWithContextCancellableIntegrationTests(fixture: ClusterFixture) =

    // ── pure state accumulation ───────────────────────────────────────────────

    [<Fact>]
    let ``SWCCAdd: single add updates SWCCSum`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-a"
            let! s = FSharpGrain.send (SWCCAdd 6) g
            test <@ s.SWCCSum = 6 @>
        }

    [<Fact>]
    let ``SWCCAdd: multiple adds accumulate SWCCSum`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-b"
            let! _ = FSharpGrain.send (SWCCAdd 4) g
            let! _ = FSharpGrain.send (SWCCAdd 6) g
            let! s = FSharpGrain.send (SWCCAdd 2) g
            test <@ s.SWCCSum = 12 @>
        }

    // ── grain-to-grain via context.GrainFactory ──────────────────────────────

    [<Fact>]
    let ``SWCCForwardPing: calls peer grain via ctx.GrainFactory`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-c"
            let! s = FSharpGrain.send (SWCCForwardPing "swcc-peer-c") g
            test <@ s.SWCCPeerPings = 1 @>
        }

    [<Fact>]
    let ``SWCCForwardPing: multiple pings increment SWCCPeerPings`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-d"
            let! _ = FSharpGrain.send (SWCCForwardPing "swcc-peer-d") g
            let! _ = FSharpGrain.send (SWCCForwardPing "swcc-peer-d") g
            let! s = FSharpGrain.send (SWCCForwardPing "swcc-peer-d") g
            test <@ s.SWCCPeerPings = 3 @>
        }

    // ── read-only state ───────────────────────────────────────────────────────

    [<Fact>]
    let ``GetSWCCState: fresh grain has zero state`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-fresh"
            let! s = FSharpGrain.send GetSWCCState g
            test <@ s.SWCCSum = 0 @>
            test <@ s.SWCCPeerPings = 0 @>
        }

    // ── isolation ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``two grains are isolated`` () =
        task {
            let gf = fixture.GrainFactory
            let g1 = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-iso1"
            let g2 = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-iso2"
            let! _ = FSharpGrain.send (SWCCAdd 100) g1
            let! s2 = FSharpGrain.send GetSWCCState g2
            test <@ s2.SWCCSum = 0 @>
        }

    // ── post (fire-and-forget) ────────────────────────────────────────────────

    [<Fact>]
    let ``post SWCCAdd completes without error`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<SWCCState, SWCCCommand> gf "swcc-post"
            do! FSharpGrain.post (SWCCAdd 7) g
            let! s = FSharpGrain.send GetSWCCState g
            test <@ s.SWCCSum = 7 @>
        }
