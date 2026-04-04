/// <summary>
/// Integration tests for the <c>handleWithContext</c> CE variant.
///
/// These tests verify that <c>GrainContext.GrainFactory</c> (and via it the entire Orleans
/// grain-to-grain communication stack) is correctly threaded through the universal handler
/// dispatch chain when a grain is registered via <c>AddFSharpGrain</c>.
///
/// Pattern under test: a Relay grain (defined with <c>handleWithContext</c>) receives a
/// <c>ForwardPing peerKey</c> command, creates a reference to a Ping grain, sends it a
/// <c>Ping</c> message, and stores the peer's updated count in its own state.
/// </summary>
module Orleans.FSharp.Integration.HandleWithContextIntegrationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ──────────────────────────────────────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Shared cluster fixture for the handleWithContext integration tests.
/// All tests in this class share a single in-process silo — the same fixture
/// used by the other integration test suites.
/// </summary>
[<Collection("ClusterCollection")>]
type HandleWithContextIntegrationTests(fixture: ClusterFixture) =

    // ── basic grain-to-grain forwarding ──────────────────────────────────────

    [<Fact>]
    let ``ForwardPing calls peer grain and records peer count`` () =
        task {
            let gf = fixture.GrainFactory
            let relay = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-a"

            // Ping the relay — it should forward to peer "ping-a" and record count=1
            let! state = FSharpGrain.send (ForwardPing "ping-a") relay
            test <@ state.PingsSent = 1 @>
            test <@ state.LastPeerCount = 1 @>
        }

    [<Fact>]
    let ``ForwardPing twice increments relay PingsSent`` () =
        task {
            let gf = fixture.GrainFactory
            let relay = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-b"

            let! s1 = FSharpGrain.send (ForwardPing "ping-b") relay
            let! s2 = FSharpGrain.send (ForwardPing "ping-b") relay
            test <@ s1.PingsSent = 1 @>
            test <@ s2.PingsSent = 2 @>
        }

    [<Fact>]
    let ``ForwardPing accumulates peer count correctly`` () =
        task {
            let gf = fixture.GrainFactory
            let relay = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-c"
            // Send 3 ForwardPings — each call pings the peer, peer count grows 1, 2, 3
            let! _ = FSharpGrain.send (ForwardPing "ping-c") relay
            let! _ = FSharpGrain.send (ForwardPing "ping-c") relay
            let! s3 = FSharpGrain.send (ForwardPing "ping-c") relay
            test <@ s3.LastPeerCount = 3 @>
            test <@ s3.PingsSent = 3 @>
        }

    [<Fact>]
    let ``GetRelayState returns current state without side effects`` () =
        task {
            let gf = fixture.GrainFactory
            let relay = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-d"
            let! initial = FSharpGrain.send GetRelayState relay
            test <@ initial.PingsSent = 0 @>
            test <@ initial.LastPeerCount = 0 @>
            let! _ = FSharpGrain.send (ForwardPing "ping-d") relay
            let! afterOne = FSharpGrain.send GetRelayState relay
            test <@ afterOne.PingsSent = 1 @>
        }

    [<Fact>]
    let ``ForwardPing to different peers tracks last peer count`` () =
        task {
            let gf = fixture.GrainFactory
            let relay = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-e"
            // Ping two different peers — relay stores the LAST peer's count
            let! _ = FSharpGrain.send (ForwardPing "ping-e1") relay
            let! _ = FSharpGrain.send (ForwardPing "ping-e1") relay
            // Now forward to a fresh peer — that peer starts at count 1
            let! s = FSharpGrain.send (ForwardPing "ping-e2") relay
            test <@ s.LastPeerCount = 1 @>
            test <@ s.PingsSent = 3 @>
        }

    [<Fact>]
    let ``Two relay grains are isolated`` () =
        task {
            let gf = fixture.GrainFactory
            let r1 = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-iso1"
            let r2 = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-iso2"
            let! _ = FSharpGrain.send (ForwardPing "ping-iso1") r1
            let! _ = FSharpGrain.send (ForwardPing "ping-iso1") r1
            let! s2 = FSharpGrain.send GetRelayState r2
            test <@ s2.PingsSent = 0 @>
        }

    // ── post (fire-and-forget) variant ────────────────────────────────────────

    [<Fact>]
    let ``post ForwardPing completes without error`` () =
        task {
            let gf = fixture.GrainFactory
            let relay = FSharpGrain.ref<RelayState, RelayCommand> gf "relay-post"
            // FSharpGrain.post awaits the RPC but discards the return value (not a true
            // one-way call). State is observable immediately after the awaited post.
            do! FSharpGrain.post (ForwardPing "ping-post") relay
            let! s = FSharpGrain.send GetRelayState relay
            test <@ s.PingsSent = 1 @>
        }
