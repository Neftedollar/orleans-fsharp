/// Phase 0 — extra probes beyond the 11 gates. These hunt for assumptions the
/// spec makes that no numbered item exercises: the golden protocol-token
/// vectors, the fixed argument surface, dispatch validation ordering, and the
/// local copier's target/cancellation reset.
module Orleans.FSharp.SeamProof.ExtraProbeTests

open System
open System.Threading
open Microsoft.Extensions.DependencyInjection
open Orleans.Serialization
open Orleans.Serialization.Cloning
open Orleans.Serialization.Invocation
open Xunit

module ProtocolTokenVectors =

    [<Fact>]
    let ``golden vector: chat.room 1 join request`` () =
        Assert.Equal(
            "525f112d5114016be421e973fee8aa7e4b439b560f29b419fd374e48336c430e",
            ProtocolToken.toHex (ProtocolToken.request "chat.room" 1 "join")
        )

    [<Fact>]
    let ``golden vector: chat.room 1 join reply`` () =
        Assert.Equal(
            "2a2e7b5513cb992ef81759d0e761ef0071ec634be2d8d3b0931f961641ad61bf",
            ProtocolToken.toHex (ProtocolToken.reply "chat.room" 1 "join")
        )

    [<Fact>]
    let ``tokens are 32 bytes and direction-sensitive`` () =
        let request = ProtocolToken.request "chat.room" 1 "join"
        let reply = ProtocolToken.reply "chat.room" 1 "join"
        Assert.Equal(32, request.Length)
        Assert.Equal(32, reply.Length)
        Assert.NotEqual<string>(ProtocolToken.toHex request, ProtocolToken.toHex reply)

module FixedArgumentSurface =

    let private envelope () =
        FunctionalRequestEnvelope("seam.probe", 1, "echo", Array.zeroCreate 32, AdmissionFlags.None, [| 1uy |])

    [<Fact>]
    let ``GetArgumentCount is 2 and the arguments are the envelope and the token`` () =
        let request = new FunctionalRequest(envelope (), CancellationToken.None) :> IInvokable
        Assert.Equal(2, request.GetArgumentCount())
        Assert.IsType<FunctionalRequestEnvelope>(request.GetArgument 0, exactMatch = true) |> ignore
        Assert.IsType<CancellationToken>(request.GetArgument 1, exactMatch = true) |> ignore

    [<Fact>]
    let ``SetArgument accepts only the exact types and rejects other indices`` () =
        let request = new FunctionalRequest(envelope (), CancellationToken.None) :> IInvokable

        let replacement =
            FunctionalRequestEnvelope("seam.probe", 1, "counter", Array.zeroCreate 32, AdmissionFlags.ReadOnly, [||])

        request.SetArgument(0, box replacement)
        Assert.Equal("counter", (request.GetArgument 0 :?> FunctionalRequestEnvelope).OperationId)

        use cts = new CancellationTokenSource()
        request.SetArgument(1, box cts.Token)
        Assert.Equal(cts.Token, request.GetArgument 1 :?> CancellationToken)

        Assert.Throws<ArgumentException>(fun () -> request.SetArgument(0, box "not-an-envelope")) |> ignore
        Assert.Throws<ArgumentException>(fun () -> request.SetArgument(1, box 42)) |> ignore
        Assert.Throws<ArgumentOutOfRangeException>(fun () -> request.SetArgument(2, box 42)) |> ignore
        Assert.Throws<ArgumentOutOfRangeException>(fun () -> request.GetArgument 2 |> ignore) |> ignore

    [<Fact>]
    let ``admission flags map onto Orleans invoke-method options`` () =
        let build flags =
            let request =
                new FunctionalRequest(
                    FunctionalRequestEnvelope("seam.probe", 1, "op", Array.zeroCreate 32, flags, [||]),
                    CancellationToken.None
                )

            request.ApplyOptions()

        Assert.Equal(Orleans.CodeGeneration.InvokeMethodOptions.None, build AdmissionFlags.None)
        Assert.Equal(Orleans.CodeGeneration.InvokeMethodOptions.ReadOnly, build AdmissionFlags.ReadOnly)

        Assert.Equal(
            Orleans.CodeGeneration.InvokeMethodOptions.OneWay
            ||| Orleans.CodeGeneration.InvokeMethodOptions.AlwaysInterleave,
            build (AdmissionFlags.OneWay ||| AdmissionFlags.AlwaysInterleave)
        )

[<Collection("SeamCluster")>]
type TransportProbeTests(fixture: SeamClusterFixture) =

    [<Fact>]
    member _.``the local copier preserves the envelope and options while clearing the target``() =
        let copier = fixture.ClientServices.GetRequiredService<DeepCopier>()

        let request =
            new FunctionalRequest(
                FunctionalRequestEnvelope("seam.probe", 1, "echo", Array.zeroCreate 32, AdmissionFlags.ReadOnly, [| 7uy |]),
                CancellationToken.None
            )

        request.AddInvokeMethodOptions(request.ApplyOptions())

        let copy = copier.Copy<FunctionalRequest> request

        Assert.NotSame(request, copy)
        Assert.Equal("echo", copy.Envelope.OperationId)
        Assert.Equal(AdmissionFlags.ReadOnly, copy.Envelope.AdmissionFlags)
        Assert.Equal(request.Options, copy.Options)
        Assert.False copy.HasTarget

    [<Fact>]
    member _.``the fixed envelope and reply round-trip through the Orleans serializer``() =
        let serializer = fixture.ClientServices.GetRequiredService<Serializer>()

        let envelope =
            FunctionalRequestEnvelope(
                "seam.probe",
                1,
                "echo",
                ProtocolToken.request "seam.probe" 1 "echo",
                AdmissionFlags.ReadOnly ||| AdmissionFlags.AlwaysInterleave,
                [| 1uy; 2uy; 3uy |]
            )

        let bytes = serializer.SerializeToArray<FunctionalRequestEnvelope> envelope
        let restored = serializer.Deserialize<FunctionalRequestEnvelope> bytes

        Assert.Equal(envelope.GrainType, restored.GrainType)
        Assert.Equal(envelope.ContractVersion, restored.ContractVersion)
        Assert.Equal(envelope.OperationId, restored.OperationId)
        Assert.Equal(envelope.AdmissionFlags, restored.AdmissionFlags)
        Assert.Equal<byte[]>(envelope.ProtocolToken, restored.ProtocolToken)
        Assert.Equal<byte[]>(envelope.Payload, restored.Payload)

        let reply = FunctionalReply(ProtocolToken.reply "seam.probe" 1 "echo", [| 9uy |])
        let restoredReply = serializer.Deserialize<FunctionalReply>(serializer.SerializeToArray<FunctionalReply> reply)
        Assert.Equal<byte[]>(reply.ProtocolToken, restoredReply.ProtocolToken)
        Assert.Equal<byte[]>(reply.Payload, restoredReply.Payload)

    [<Fact>]
    member _.``dispatch validation rejects a bad token, version, flags and operation before the handler``() =
        task {
            let serializer = fixture.ClientServices.GetRequiredService<Serializer>()
            let reference = SeamClient.functionalReference fixture.Client SeamGrainTypes.Probe "validation-1"
            let closed = SeamClient.closedInterface SeamGrainTypes.Probe

            let send (envelope: FunctionalRequestEnvelope) =
                task {
                    let! _ = reference.SendAsyncTyped(closed, envelope, CancellationToken.None)
                    return ()
                }

            let payload = serializer.SerializeToArray<string> "x"

            let badToken =
                FunctionalRequestEnvelope(SeamGrainTypes.Probe, 1, "echo", Array.zeroCreate 32, 0uy, payload)

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> send badToken)
            Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase)

            let badVersion =
                FunctionalRequestEnvelope(
                    SeamGrainTypes.Probe,
                    99,
                    "echo",
                    ProtocolToken.request SeamGrainTypes.Probe 99 "echo",
                    0uy,
                    payload
                )

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> send badVersion)
            Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase)

            let reserved =
                FunctionalRequestEnvelope(
                    SeamGrainTypes.Probe,
                    1,
                    "echo",
                    ProtocolToken.request SeamGrainTypes.Probe 1 "echo",
                    0x08uy,
                    payload
                )

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> send reserved)
            Assert.Contains("reserved", error.Message, StringComparison.OrdinalIgnoreCase)

            let unknown =
                FunctionalRequestEnvelope(
                    SeamGrainTypes.Probe,
                    1,
                    "nope",
                    ProtocolToken.request SeamGrainTypes.Probe 1 "nope",
                    0uy,
                    payload
                )

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> send unknown)
            Assert.Contains("nope", error.Message, StringComparison.OrdinalIgnoreCase)

            let wrongGrainType =
                FunctionalRequestEnvelope(
                    SeamGrainTypes.Peer,
                    1,
                    "echo",
                    ProtocolToken.request SeamGrainTypes.Peer 1 "echo",
                    0uy,
                    payload
                )

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> send wrongGrainType)
            Assert.Contains("grain type", error.Message, StringComparison.OrdinalIgnoreCase)
        }

    [<Fact>]
    member _.``a client call really crosses the explicit transport codec``() =
        task {
            // Guards against a false positive: if TestingHost handed the request
            // object over without serializing it, every transport proof above
            // would be about an in-process object graph, not the wire.
            let writesBefore = SeamCodecCounters.writes ()
            let readsBefore = SeamCodecCounters.reads ()

            let! reply = fixture.Call SeamGrainTypes.Probe "codec-path" "echo" "through-the-wire"
            Assert.Equal("through-the-wire", reply)

            Assert.True(
                SeamCodecCounters.writes () > writesBefore,
                "the explicit transport codec never wrote the request"
            )

            Assert.True(
                SeamCodecCounters.reads () > readsBefore,
                "the explicit transport codec never read the request"
            )
        }
