/// Phase 0 item 7 — stock `ReadOnly`, `OneWay` and `AlwaysInterleave`
/// scheduling behavior, demonstrated under concurrency rather than by flags.
module Orleans.FSharp.SeamProof.Item07_SchedulingTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Xunit

/// "slowWrite:entered=N:max=M" → (N, M)
let private counters (reply: string) =
    let parts = reply.Split ':'
    int (parts[1].Split('=')[1]), int (parts[2].Split('=')[1])

[<Collection("SeamCluster")>]
type SchedulingTests(fixture: SeamClusterFixture) =

    [<Fact>]
    member _.``default requests are sequential: never more than one in flight``() =
        task {
            let key = "sched-seq"
            let first = fixture.Call SeamGrainTypes.Probe key "slowWrite" "400"
            let second = fixture.Call SeamGrainTypes.Probe key "slowWrite" "400"
            let! replies = Task.WhenAll [| first; second |]

            for reply in replies do
                let entered, max = counters reply
                Assert.Equal(1, entered)
                Assert.Equal(1, max)
        }

    [<Fact>]
    member _.``read-only requests interleave with each other``() =
        task {
            let key = "sched-readonly"
            let watch = Stopwatch.StartNew()
            let first = fixture.Call SeamGrainTypes.Probe key "readSlow" "600"
            let second = fixture.Call SeamGrainTypes.Probe key "readSlow" "600"
            let! replies = Task.WhenAll [| first; second |]
            watch.Stop()

            let maxObserved = replies |> Array.map (counters >> snd) |> Array.max
            Assert.Equal(2, maxObserved)
            // Two interleaved 600 ms calls finish well before 1200 ms.
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds 1100.0, string watch.Elapsed)
        }

    /// Waits until the parked default-policy request is actually executing.
    /// The poll itself uses an AlwaysInterleave operation, so reaching `True`
    /// already demonstrates interleaving.
    member private _.WaitForGate key =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            let mutable entered = "False"

            while entered <> "True" && DateTime.UtcNow < deadline do
                let! current = fixture.Call SeamGrainTypes.Probe key "gateEntered" ""
                entered <- current

                if entered <> "True" then
                    do! Task.Delay 50

            return entered
        }

    [<Fact>]
    member this.``an always-interleave request reaches an activation parked in a default request``() =
        task {
            let key = $"gate-interleave-{Guid.NewGuid():N}"
            let parked = fixture.Call SeamGrainTypes.Probe key "awaitGate" "6000"

            let! entered = this.WaitForGate key
            Assert.Equal("True", entered)

            let! released = fixture.Call SeamGrainTypes.Probe key "releaseGateInterleave" ""
            Assert.Equal("ok", released)

            let! outcome = parked
            Assert.Equal("released", outcome)
        }

    [<Fact>]
    member this.``a read-only request cannot reach an activation parked in a default request``() =
        task {
            // ReadOnly interleaves with other ReadOnly calls (see the test above)
            // but is still blocked by a running default-policy call. Only
            // AlwaysInterleave breaks into a parked default request.
            let key = $"gate-readonly-{Guid.NewGuid():N}"
            let parked = fixture.Call SeamGrainTypes.Probe key "awaitGate" "2500"

            let! entered = this.WaitForGate key
            Assert.Equal("True", entered)

            let release = fixture.Call SeamGrainTypes.Probe key "releaseGateReadOnly" ""

            let! outcome = parked
            Assert.Equal("timeout", outcome)

            let! released = release
            Assert.Equal("ok", released)
        }

    [<Fact>]
    member this.``a default request cannot reach an activation parked in a default request``() =
        task {
            // The discriminator: without a policy flag the second call waits its
            // turn, so the parked request times out before the release runs.
            let key = $"gate-default-{Guid.NewGuid():N}"
            let parked = fixture.Call SeamGrainTypes.Probe key "awaitGate" "2500"

            let! entered = this.WaitForGate key
            Assert.Equal("True", entered)

            let release = fixture.Call SeamGrainTypes.Probe key "releaseGateDefault" ""

            let! outcome = parked
            Assert.Equal("timeout", outcome)

            let! released = release
            Assert.Equal("ok", released)
        }

    [<Fact>]
    member _.``a one-way send acknowledges locally and the target runs afterwards``() =
        task {
            let key = "sched-oneway"
            let watch = Stopwatch.StartNew()
            fixture.OneWay SeamGrainTypes.Probe key "bump" "800"
            watch.Stop()

            Assert.True(
                watch.Elapsed < TimeSpan.FromMilliseconds 300.0,
                $"one-way send took {watch.Elapsed}"
            )

            let! immediate = fixture.Call SeamGrainTypes.Probe key "counter" ""
            Assert.Equal("0", immediate)

            let deadline = DateTime.UtcNow.AddSeconds 10.0
            let mutable observed = immediate

            while observed <> "1" && DateTime.UtcNow < deadline do
                do! Task.Delay 100
                let! current = fixture.Call SeamGrainTypes.Probe key "counter" ""
                observed <- current

            Assert.Equal("1", observed)
        }
