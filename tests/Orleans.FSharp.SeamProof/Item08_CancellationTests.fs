/// Phase 0 item 8 — cross-silo target cancellation through the fixed request
/// type.
module Orleans.FSharp.SeamProof.Item08_CancellationTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit

let private field (reply: string) (name: string) =
    reply.Split '|'
    |> Array.pick (fun part ->
        if part.StartsWith(name + "=", StringComparison.Ordinal) then
            Some(part.Substring(name.Length + 1))
        else
            None)

let private siloOfIdentity (reply: string) = field reply "silo"

[<Collection("SeamCluster")>]
type CancellationTests(fixture: SeamClusterFixture) =

    /// Finds a peer key whose activation lands on a silo other than `callerSilo`.
    /// Orleans' placement spread is probabilistic and can stay local while the
    /// cluster is still warming up, so this retries in rounds.
    let findPeerOnOtherSilo (callerSilo: string) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 60.0
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let mutable i = 0

                while found.IsNone && i < 20 do
                    let key = $"peer-{Guid.NewGuid():N}"
                    let! identity = fixture.Call SeamGrainTypes.Peer key "identity" ""
                    let silo = siloOfIdentity identity

                    if silo <> callerSilo then
                        found <- Some(key, silo)

                    i <- i + 1

                if found.IsNone then
                    do! Task.Delay 1000

            return found
        }

    [<Fact>]
    member _.``a cancelled acknowledged call cancels the target-local token on another silo``() =
        task {
            let callerKey = $"cancel-caller-{Guid.NewGuid():N}"
            let! callerIdentity = fixture.Call SeamGrainTypes.Probe callerKey "identity" ""
            let callerSilo = siloOfIdentity callerIdentity

            let! peer = findPeerOnOtherSilo callerSilo

            let peerKey, peerSilo =
                match peer with
                | Some found -> found
                | None -> failwith "no peer activation landed on a different silo"

            Assert.NotEqual<string>(callerSilo, peerSilo)

            let! reply =
                fixture.Call
                    SeamGrainTypes.Probe
                    callerKey
                    "callPeerCancel"
                    $"{SeamGrainTypes.Peer}|{peerKey}|8000|400"

            let self = field reply "self"
            let observed = field reply "peerObserved"

            // The target-local token fired on the *other* silo: the observation
            // records where the cancelled handler actually ran.
            Assert.True(observed.StartsWith("cancelled@", StringComparison.Ordinal), reply)
            let observedSilo = observed.Substring "cancelled@".Length
            Assert.NotEqual<string>(self, observedSilo)
            Assert.Equal(peerSilo, observedSilo)
        }

    [<Fact>]
    member _.``an uncancelled call completes normally on the remote silo``() =
        task {
            let! reply = fixture.Call SeamGrainTypes.Peer "cancel-control" "waitCancel" "10"
            Assert.Equal("completed", reply)
        }

    [<Fact>]
    member _.``a client-supplied token cancels the target of a direct call``() =
        task {
            let key = "cancel-direct"
            use cts = new CancellationTokenSource()
            let call = fixture.CallCancellable SeamGrainTypes.Probe key "waitCancel" "8000" cts.Token
            do! Task.Delay 400
            cts.Cancel()

            let! outcome =
                task {
                    try
                        let! reply = call
                        return reply
                    with :? OperationCanceledException ->
                        return "caller-cancelled"
                }

            // Either the caller observes cancellation or the target's cooperative
            // reply arrives — both are acceptable; what must hold is that the
            // target-local token fired.
            Assert.True(outcome = "cancelled" || outcome = "caller-cancelled", outcome)

            let deadline = DateTime.UtcNow.AddSeconds 5.0
            let observationKey = $"waitCancel:{SeamGrainTypes.Probe}/{key}"
            let mutable observed = SeamObservations.tryGet observationKey

            while observed.IsNone && DateTime.UtcNow < deadline do
                do! Task.Delay 100
                observed <- SeamObservations.tryGet observationKey

            Assert.StartsWith("cancelled@", Option.defaultValue "none" observed)
        }

    [<Fact>]
    member _.``the fixed request reports cancellability by admission flags``() =
        let acknowledged =
            new FunctionalRequest(
                FunctionalRequestEnvelope("g", 1, "echo", Array.zeroCreate 32, AdmissionFlags.None, [||]),
                CancellationToken.None
            )

        let oneWay =
            new FunctionalRequest(
                FunctionalRequestEnvelope("g", 1, "bump", Array.zeroCreate 32, AdmissionFlags.OneWay, [||]),
                CancellationToken.None
            )

        Assert.True((acknowledged :> Orleans.Serialization.Invocation.IInvokable).IsCancellable)
        Assert.False((oneWay :> Orleans.Serialization.Invocation.IInvokable).IsCancellable)
