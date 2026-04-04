/// <summary>
/// Integration tests for the base <c>handleCancellable</c> CE variant.
///
/// These tests verify that grains defined with <c>handleCancellable</c> — the raw variant
/// that requires manual <c>box</c> in the return value — are dispatched correctly through the
/// universal <c>AddFSharpGrain</c> pattern.
///
/// Unlike <c>handleStateCancellable</c> (which automatically boxes the state), this variant
/// exercises the <c>CancellableHandler</c> slot directly, giving the handler full control
/// over both the new state and the separately boxed result.
/// </summary>
module Orleans.FSharp.Integration.HandleCancellableBaseIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

[<Collection("ClusterCollection")>]
type HandleCancellableBaseIntegrationTests(fixture: ClusterFixture) =

    // ── basic accumulation ────────────────────────────────────────────────────

    [<Fact>]
    let ``RawCancAdd: single add updates RawSum and RawSteps`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-a"
            let! s = FSharpGrain.send (RawCancAdd 5) g
            test <@ s.RawSum = 5 @>
            test <@ s.RawSteps = 1 @>
        }

    [<Fact>]
    let ``RawCancAdd: multiple adds accumulate RawSum`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-b"
            let! _ = FSharpGrain.send (RawCancAdd 3) g
            let! _ = FSharpGrain.send (RawCancAdd 7) g
            let! s = FSharpGrain.send (RawCancAdd 2) g
            test <@ s.RawSum = 12 @>
            test <@ s.RawSteps = 3 @>
        }

    [<Fact>]
    let ``RawCancAdd: sum equals List.sum of inputs`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-sum"
            let inputs = [1; 2; 3; 4; 5]
            for n in inputs do
                let! _ = FSharpGrain.send (RawCancAdd n) g
                ()
            let! s = FSharpGrain.send GetRawCancState g
            test <@ s.RawSum = List.sum inputs @>
            test <@ s.RawSteps = List.length inputs @>
        }

    // ── read-only access ──────────────────────────────────────────────────────

    [<Fact>]
    let ``GetRawCancState: fresh grain has zero state`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-fresh"
            let! s = FSharpGrain.send GetRawCancState g
            test <@ s.RawSum = 0 @>
            test <@ s.RawSteps = 0 @>
        }

    [<Fact>]
    let ``GetRawCancState: returns current state without side effects`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-c"
            let! _ = FSharpGrain.send (RawCancAdd 99) g
            let! s1 = FSharpGrain.send GetRawCancState g
            let! s2 = FSharpGrain.send GetRawCancState g
            test <@ s1.RawSum = 99 @>
            test <@ s2.RawSum = 99 @>
            test <@ s1.RawSteps = 1 @>
            test <@ s2.RawSteps = 1 @>
        }

    // ── isolation ─────────────────────────────────────────────────────────────

    [<Fact>]
    let ``two grains are isolated`` () =
        task {
            let gf = fixture.GrainFactory
            let g1 = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-iso1"
            let g2 = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-iso2"
            let! _ = FSharpGrain.send (RawCancAdd 100) g1
            let! s2 = FSharpGrain.send GetRawCancState g2
            test <@ s2.RawSum = 0 @>
        }

    // ── post (fire-and-forget) variant ────────────────────────────────────────

    [<Fact>]
    let ``post RawCancAdd completes without error`` () =
        task {
            let gf = fixture.GrainFactory
            let g = FSharpGrain.ref<RawCancState, RawCancCommand> gf "rawcanc-post"
            do! FSharpGrain.post (RawCancAdd 11) g
            let! s = FSharpGrain.send GetRawCancState g
            test <@ s.RawSum = 11 @>
        }
