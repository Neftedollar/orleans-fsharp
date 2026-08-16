/// <summary>
/// Spec 003 Phase 3, production parity for Phase-0 items 5-8 and 11 plus the dispatch,
/// payload-limit, one-way, and filter requirements of "Binding, request, and transport tests".
/// </summary>
module Orleans.FSharp.Integration.FunctionalRuntimeIntegrationTests

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Runtime
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalClusterFixture
open Xunit

let private field (reply: string) (name: string) =
    reply.Split '|'
    |> Array.pick (fun part ->
        if part.StartsWith(name + "=", StringComparison.Ordinal) then
            Some(part.Substring(name.Length + 1))
        else
            None)

/// <summary>"slow:entered=N:max=M" → (N, M)</summary>
let private counters (reply: string) =
    let parts = reply.Split ':'
    int (parts.[1].Split('=').[1]), int (parts.[2].Split('=').[1])

let private closedInterface (actorType: Type) =
    typedefof<IFunctionalGrainTarget<_>>.MakeGenericType actorType

/// <summary>
/// Send a hand-built envelope through the real client reference, bypassing the bound record so
/// negative protocol cases can be constructed exactly.
/// </summary>
let private rawSend (client: IClusterClient) (grainType: string) (actorType: Type) (key: string) envelope =
    let reference =
        client.GetGrain(
            GrainId.Create(GrainType.Create grainType, key),
            GrainInterfaceType.Create("orleans.fsharp.functional/" + grainType)
        )
        :?> FunctionalGrainReference

    let closed = closedInterface actorType
    reference.SendAsync(envelope, closed, closed.GetMethod "DispatchAsync", CancellationToken.None)

[<Collection("FunctionalCluster")>]
type RuntimeTests(fixture: FunctionalClusterFixture) =

    let primary = fixture.SiloServices FunctionalGrainTypes.PrimarySiloName

    let codec =
        fixture.Client.ServiceProvider.GetRequiredService<FunctionalPayloadCodec>()

    let envelope operationId flags (payload: byte[]) =
        FunctionalRequestEnvelope(
            FunctionalGrainTypes.Probe,
            1,
            operationId,
            ProtocolToken.request FunctionalGrainTypes.Probe 1 operationId,
            flags,
            payload
        )

    // ── item 5: custom activation target ────────────────────────────────────

    [<Fact>]
    member _.``the functional activator is installed only for registered grain types``() =
        let resolver = primary.GetRequiredService<GrainTypeSharedContextResolver>()

        let functional =
            resolver
                .GetComponents(GrainType.Create FunctionalGrainTypes.Probe)
                .GetComponent<IGrainActivator>()

        Assert.Equal(
            typedefof<FunctionalGrainActivator<_>>.MakeGenericType typeof<ProbeActor>,
            functional.GetType()
        )

        let stock =
            resolver.GetComponents(GrainType.Create "management").GetComponent<IGrainActivator>()

        Assert.False(
            stock.GetType().IsGenericType
            && stock.GetType().GetGenericTypeDefinition() = typedefof<FunctionalGrainActivator<_>>
        )

    [<Fact>]
    member _.``the invoked target is the custom target, not the manifest marker``() =
        task {
            CallCapture.clear ()
            let probe = fixture.Probe $"activation-{Guid.NewGuid():N}"
            let! reply = probe.api.echo "hello"
            Assert.Equal<string>("hello", reply)

            let captured =
                CallCapture.incoming |> Seq.filter (fun call -> call.OperationId = "echo") |> Seq.head

            Assert.DoesNotContain("FunctionalGrainMarker", captured.GrainInstanceType)
            Assert.NotEqual<string>("<null>", captured.GrainInstanceType)
        }

    [<Fact>]
    member _.``a marker instance refuses to serve a call``() =
        let marker =
            FunctionalGrainMarker<ProbeActor>() :> IFunctionalGrainTarget<ProbeActor>

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                marker.DispatchAsync(
                    FunctionalRequestEnvelope("x", 1, "echo", Array.zeroCreate 32, 0uy, [||]),
                    CancellationToken.None
                )
                |> ignore)

        Assert.Contains("activator was not installed", error.Message)

    [<Fact>]
    member _.``a marker instance refuses a reminder``() =
        let marker = FunctionalGrainMarker<ProbeActor>() :> IRemindable

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                marker.ReceiveReminder("nightly", Unchecked.defaultof<TickStatus>) |> ignore)

        Assert.Contains("activator was not installed", error.Message)

    // ── item 6: call-filter metadata ────────────────────────────────────────

    [<Fact>]
    member _.``the incoming filter sees closed interface and implementation method metadata``() =
        task {
            CallCapture.clear ()
            let probe = fixture.Probe $"filter-{Guid.NewGuid():N}"
            let! _ = probe.api.readSlow 0

            let captured =
                CallCapture.incoming
                |> Seq.filter (fun call -> call.OperationId = "readSlow")
                |> Seq.head

            let expectedInterface = (closedInterface typeof<ProbeActor>).FullName

            Assert.Equal<string>("DispatchAsync", captured.MethodName)
            Assert.Equal<string>("DispatchAsync", captured.InterfaceMethodName)
            Assert.EndsWith("DispatchAsync", captured.ImplementationMethodName)
            Assert.Equal<string>(expectedInterface, captured.InterfaceName)
            Assert.Equal<string>("IFunctionalGrainTarget`1", captured.InterfaceTypeName)
            Assert.Equal<string>($"{expectedInterface}/DispatchAsync", captured.ActivityName)
            Assert.NotEqual<string>("<null>", captured.ImplementationDeclaringType)
            Assert.Equal(2, captured.ArgumentCount)
            Assert.True captured.Argument1IsToken
        }

    [<Fact>]
    member _.``the filter reads the public functional request metadata``() =
        task {
            CallCapture.clear ()
            let probe = fixture.Probe $"filter-meta-{Guid.NewGuid():N}"
            let! _ = probe.api.readSlow 0

            let captured =
                CallCapture.incoming
                |> Seq.filter (fun call -> call.OperationId = "readSlow")
                |> Seq.head

            Assert.Equal<string>(FunctionalGrainTypes.Probe, captured.GrainType)
            Assert.Equal(1, captured.Version)
            Assert.True captured.IsReadOnly
            Assert.False captured.IsOneWay
            Assert.False captured.IsAlwaysInterleave
            Assert.True(captured.PayloadLength > 0)
        }

    [<Fact>]
    member _.``every declared flag combination reaches the filter``() =
        task {
            CallCapture.clear ()
            let key = $"filter-flags-{Guid.NewGuid():N}"
            let probe = fixture.Probe key
            let! _ = probe.api.echo "a"
            let! _ = probe.api.peek ()
            do! probe.api.bump 0

            let deadline = DateTime.UtcNow.AddSeconds 10.0

            while not (CallCapture.incoming |> Seq.exists (fun call -> call.OperationId = "bump"))
                  && DateTime.UtcNow < deadline do
                do! Task.Delay 100

            let byOperation =
                CallCapture.incoming
                |> Seq.map (fun call -> call.OperationId, call)
                |> Map.ofSeq

            let echo = byOperation.["echo"]
            Assert.False echo.IsReadOnly
            Assert.False echo.IsOneWay
            Assert.False echo.IsAlwaysInterleave

            let peek = byOperation.["peek"]
            Assert.True peek.IsReadOnly
            Assert.True peek.IsAlwaysInterleave
            Assert.False peek.IsOneWay

            let bump = byOperation.["bump"]
            Assert.True bump.IsOneWay
            Assert.True bump.IsAlwaysInterleave
            Assert.False bump.IsReadOnly
        }

    [<Fact>]
    member _.``the outgoing filter on the client sees the closed interface metadata``() =
        task {
            CallCapture.clear ()
            let probe = fixture.Probe $"filter-out-{Guid.NewGuid():N}"
            let! _ = probe.api.echo "b"

            let captured =
                CallCapture.outgoing |> Seq.filter (fun call -> call.OperationId = "echo") |> Seq.head

            Assert.Equal<string>("DispatchAsync", captured.MethodName)
            Assert.Equal<string>((closedInterface typeof<ProbeActor>).FullName, captured.InterfaceName)
            Assert.Equal<string>(FunctionalGrainTypes.Probe, captured.GrainType)
            Assert.Equal(2, captured.ArgumentCount)
        }

    [<Fact>]
    member _.``a filter can reject a call and observe the request context``() =
        task {
            CallCapture.clear ()
            CallCapture.rejectOperation <- "echo"

            try
                let probe = fixture.Probe $"filter-reject-{Guid.NewGuid():N}"

                let! error =
                    Assert.ThrowsAnyAsync<Exception>(fun () -> probe.api.echo "rejected" :> Task)

                Assert.Contains("filter rejected", error.Message)
            finally
                CallCapture.rejectOperation <- null
        }

    // ── item 7: scheduling under concurrency ────────────────────────────────

    [<Fact>]
    member _.``default requests are sequential``() =
        task {
            let probe = fixture.Probe $"sched-seq-{Guid.NewGuid():N}"
            let first = probe.api.slow 400
            let second = probe.api.slow 400
            let! replies = Task.WhenAll [| first; second |]

            for reply in replies do
                let entered, maximum = counters reply
                Assert.Equal(1, entered)
                Assert.Equal(1, maximum)
        }

    [<Fact>]
    member _.``read-only requests interleave with each other``() =
        task {
            let probe = fixture.Probe $"sched-ro-{Guid.NewGuid():N}"
            let watch = Stopwatch.StartNew()
            let first = probe.api.readSlow 600
            let second = probe.api.readSlow 600
            let! replies = Task.WhenAll [| first; second |]
            watch.Stop()

            let maxObserved = replies |> Array.map (counters >> snd) |> Array.max
            Assert.Equal(2, maxObserved)
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds 1100.0, string watch.Elapsed)
        }

    member private _.WaitForGate(probe: FunctionalGrainRef<ProbeActor, ProbeId, ProbeApi>) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            let mutable entered = false

            while not entered && DateTime.UtcNow < deadline do
                let! current = probe.api.gateEntered ()
                entered <- current

                if not entered then
                    do! Task.Delay 50

            return entered
        }

    [<Fact>]
    member this.``an always-interleave request reaches an activation parked in a default request``() =
        task {
            let probe = fixture.Probe $"gate-ai-{Guid.NewGuid():N}"
            let parked = probe.api.awaitGate 6000

            let! entered = this.WaitForGate probe
            Assert.True entered

            let! released = probe.api.releaseGateInterleave ()
            Assert.Equal<string>("ok", released)

            let! outcome = parked
            Assert.Equal<string>("released", outcome)
        }

    [<Fact>]
    member this.``a read-only request cannot reach an activation parked in a default request``() =
        task {
            let probe = fixture.Probe $"gate-ro-{Guid.NewGuid():N}"
            let parked = probe.api.awaitGate 2500

            let! entered = this.WaitForGate probe
            Assert.True entered

            let release = probe.api.releaseGateReadOnly ()
            let! outcome = parked
            Assert.Equal<string>("timeout", outcome)

            let! released = release
            Assert.Equal<string>("ok", released)
        }

    [<Fact>]
    member this.``a default request cannot reach an activation parked in a default request``() =
        task {
            let probe = fixture.Probe $"gate-def-{Guid.NewGuid():N}"
            let parked = probe.api.awaitGate 2500

            let! entered = this.WaitForGate probe
            Assert.True entered

            let release = probe.api.releaseGateDefault ()
            let! outcome = parked
            Assert.Equal<string>("timeout", outcome)

            let! released = release
            Assert.Equal<string>("ok", released)
        }

    [<Fact>]
    member _.``a one-way send acknowledges locally and the target runs afterwards``() =
        task {
            let probe = fixture.Probe $"oneway-{Guid.NewGuid():N}"
            let watch = Stopwatch.StartNew()
            do! probe.api.bump 800
            watch.Stop()

            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds 300.0, string watch.Elapsed)

            let! immediate = probe.api.counter ()
            Assert.Equal(0, immediate)

            let deadline = DateTime.UtcNow.AddSeconds 10.0
            let mutable observed = immediate

            while observed <> 1 && DateTime.UtcNow < deadline do
                do! Task.Delay 100
                let! current = probe.api.counter ()
                observed <- current

            Assert.Equal(1, observed)
        }

    [<Fact>]
    member _.``a one-way call with a pre-cancelled token returns a cancelled task``() =
        task {
            let probe = fixture.Probe $"oneway-cancel-{Guid.NewGuid():N}"
            use source = new CancellationTokenSource()
            source.Cancel()

            let call = probe.callCancellable (_.bump) 0 source.Token

            let! error = Assert.ThrowsAnyAsync<OperationCanceledException>(fun () -> call :> Task)

            Assert.NotNull error
            Assert.True call.IsCanceled
        }

    // ── item 8: cross-silo cooperative cancellation ─────────────────────────

    /// <summary>Finds a peer key whose activation lands on a silo other than the caller's.</summary>
    member private _.FindPeerOnOtherSilo(callerSilo: string) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 60.0
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let mutable attempt = 0

                while found.IsNone && attempt < 20 do
                    let key = $"peer-{Guid.NewGuid():N}"
                    let! identity = (fixture.Peer key).api.identity ()
                    let silo = field identity "silo"

                    if silo <> callerSilo then
                        found <- Some(key, silo)

                    attempt <- attempt + 1

                if found.IsNone then
                    do! Task.Delay 1000

            return found
        }

    [<Fact>]
    member this.``a cancelled acknowledged call cancels the target-local token on another silo``() =
        task {
            let callerKey = $"cancel-caller-{Guid.NewGuid():N}"
            let! callerIdentity = (fixture.Probe callerKey).api.identity ()
            let callerSilo = field callerIdentity "silo"

            let! peer = this.FindPeerOnOtherSilo callerSilo

            let peerKey, peerSilo =
                match peer with
                | Some found -> found
                | None -> failwith "no peer activation landed on a different silo"

            Assert.NotEqual<string>(callerSilo, peerSilo)

            let! reply = (fixture.Probe callerKey).api.callPeerCancel $"{peerKey}|8000|400"

            let self = field reply "self"
            let observed = field reply "peerObserved"

            Assert.True(observed.StartsWith("cancelled@", StringComparison.Ordinal), reply)
            let observedSilo = observed.Substring "cancelled@".Length
            Assert.NotEqual<string>(self, observedSilo)
            Assert.Equal<string>(peerSilo, observedSilo)
        }

    [<Fact>]
    member _.``an uncancelled call completes normally``() =
        task {
            let! reply = (fixture.Peer $"cancel-control-{Guid.NewGuid():N}").api.waitCancel 10
            Assert.Equal<string>("completed", reply)
        }

    [<Fact>]
    member _.``a client-supplied token cancels the target of a direct call``() =
        task {
            let key = $"cancel-direct-{Guid.NewGuid():N}"
            let probe = fixture.Probe key
            use source = new CancellationTokenSource()
            let call = probe.callCancellable (_.waitCancel) 8000 source.Token
            do! Task.Delay 400
            source.Cancel()

            let! outcome =
                task {
                    try
                        let! reply = call
                        return reply
                    with :? OperationCanceledException ->
                        return "caller-cancelled"
                }

            Assert.True(outcome = "cancelled" || outcome = "caller-cancelled", outcome)

            let observationKey = $"waitCancel:{FunctionalGrainTypes.Probe}/{key}"
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            let mutable observed = Probe.tryGet observationKey

            while observed.IsNone && DateTime.UtcNow < deadline do
                do! Task.Delay 100
                observed <- Probe.tryGet observationKey

            Assert.StartsWith("cancelled@", Option.defaultValue "none" observed)
        }

    // ── item 11: identity isolation and heterogeneous routing ───────────────

    [<Fact>]
    member _.``equal keys under different grain types are distinct activations``() =
        task {
            let key = $"shared-{Guid.NewGuid():N}"

            do! (fixture.Probe key).api.stateWrite "from-probe"
            do! (fixture.Other key).api.stateWrite "from-other"

            let! probeValue = (fixture.Probe key).api.stateRead ()
            let! otherValue = (fixture.Other key).api.stateRead ()

            Assert.Equal<string>("from-probe", probeValue)
            Assert.Equal<string>("from-other", otherValue)

            let! probeIdentity = (fixture.Probe key).api.identity ()
            let! otherIdentity = (fixture.Other key).api.identity ()

            Assert.Equal<string>($"{FunctionalGrainTypes.Probe}/{key}", field probeIdentity "grain")
            Assert.Equal<string>($"{FunctionalGrainTypes.Other}/{key}", field otherIdentity "grain")
        }

    [<Fact>]
    member _.``a definition hosted on one silo only runs there``() =
        task {
            let! identity = (fixture.Other $"hetero-{Guid.NewGuid():N}").api.identity ()

            let secondaryLocal =
                (fixture.SiloServices "Secondary_1")
                    .GetRequiredService<ILocalSiloDetails>()
                    .SiloAddress
                |> string

            Assert.Equal<string>(secondaryLocal, field identity "silo")
        }

    // ── ephemeral state publication ─────────────────────────────────────────

    [<Fact>]
    member _.``a successful default handler publishes its returned state``() =
        task {
            let probe = fixture.Probe $"state-{Guid.NewGuid():N}"
            do! probe.api.stateWrite "published"
            let! observed = probe.api.stateRead ()
            Assert.Equal<string>("published", observed)
        }

    [<Fact>]
    member _.``a read-only handler's replacement state is discarded``() =
        task {
            let probe = fixture.Probe $"state-ro-{Guid.NewGuid():N}"
            do! probe.api.stateWrite "kept"
            do! probe.api.stateReadOnlyWrite "discarded"
            let! observed = probe.api.stateRead ()
            Assert.Equal<string>("kept", observed)
        }

    // ── dispatch validation and payload limits ──────────────────────────────

    [<Fact>]
    member _.``an unknown operation fails before handler execution``() =
        task {
            let payload = codec.Serialize<string> "x"

            let! error =
                Assert.ThrowsAnyAsync<Exception>(fun () ->
                    rawSend fixture.Client FunctionalGrainTypes.Probe typeof<ProbeActor> "dispatch-unknown"
                    <| envelope "notAnOperation" 0uy payload
                    :> Task)

            Assert.Contains("hosts no operation 'notAnOperation'", error.Message)
        }

    [<Fact>]
    member _.``a wrong contract version is rejected with expected and received versions``() =
        task {
            let payload = codec.Serialize<string> "x"

            let wrongVersion =
                FunctionalRequestEnvelope(
                    FunctionalGrainTypes.Probe,
                    2,
                    "echo",
                    ProtocolToken.request FunctionalGrainTypes.Probe 2 "echo",
                    0uy,
                    payload
                )

            let! error =
                Assert.ThrowsAnyAsync<Exception>(fun () ->
                    rawSend fixture.Client FunctionalGrainTypes.Probe typeof<ProbeActor> "dispatch-version" wrongVersion
                    :> Task)

            Assert.Contains("hosts contract version 1 but received version 2", error.Message)
        }

    [<Fact>]
    member _.``a mismatched protocol token is rejected``() =
        task {
            let payload = codec.Serialize<string> "x"

            let wrongToken =
                FunctionalRequestEnvelope(
                    FunctionalGrainTypes.Probe,
                    1,
                    "echo",
                    ProtocolToken.request FunctionalGrainTypes.Probe 1 "identity",
                    0uy,
                    payload
                )

            let! error =
                Assert.ThrowsAnyAsync<Exception>(fun () ->
                    rawSend fixture.Client FunctionalGrainTypes.Probe typeof<ProbeActor> "dispatch-token" wrongToken
                    :> Task)

            Assert.Contains("carries protocol token", error.Message)
        }

    [<Fact>]
    member _.``mismatched admission flags are rejected before deserialization``() =
        task {
            let payload = codec.Serialize<string> "x"

            let! error =
                Assert.ThrowsAnyAsync<Exception>(fun () ->
                    rawSend fixture.Client FunctionalGrainTypes.Probe typeof<ProbeActor> "dispatch-flags"
                    <| envelope "echo" 0x01uy payload
                    :> Task)

            Assert.Contains("admission flags", error.Message)
        }

    [<Fact>]
    member _.``a corrupt payload fails as a protocol-stage diagnostic``() =
        task {
            let! error =
                Assert.ThrowsAnyAsync<Exception>(fun () ->
                    rawSend fixture.Client FunctionalGrainTypes.Probe typeof<ProbeActor> "dispatch-corrupt"
                    <| envelope "echo" 0uy [| 1uy; 2uy; 3uy |]
                    :> Task)

            Assert.NotNull error
        }

    [<Fact>]
    member _.``an oversized request fails at the silo receive boundary before the handler runs``() =
        task {
            let key = $"limit-request-{Guid.NewGuid():N}"
            let payload = codec.Serialize<byte[]>(Array.zeroCreate 100_000)

            // The client limit is deliberately larger than the silo limit, so the caller-side
            // boundary passes and the silo receive boundary is the one that trips.
            Assert.True(payload.Length > FunctionalLimits.Silo)
            Assert.True(payload.Length < FunctionalLimits.Client)

            let! error =
                Assert.ThrowsAnyAsync<Exception>(fun () ->
                    rawSend fixture.Client FunctionalGrainTypes.Probe typeof<ProbeActor> key
                    <| envelope "sink" 0uy payload
                    :> Task)

            Assert.Contains("silo request receive", error.Message)
            Assert.Contains(string FunctionalLimits.Silo, error.Message)
            Assert.True(
                (Probe.tryGet $"sink:{FunctionalGrainTypes.Probe}/{key}").IsNone,
                "the handler must not run when the request exceeds the silo limit"
            )
        }

    [<Fact>]
    member _.``an oversized reply fails at the silo reply boundary``() =
        task {
            let probe = fixture.Probe $"limit-reply-{Guid.NewGuid():N}"

            let! error = Assert.ThrowsAnyAsync<Exception>(fun () -> probe.api.big 100_000 :> Task)

            Assert.Contains("silo reply send", error.Message)
            Assert.Contains(string FunctionalLimits.Silo, error.Message)
        }

    [<Fact>]
    member _.``an application handler exception follows the Orleans response-exception path``() =
        task {
            let probe = fixture.Probe $"boom-{Guid.NewGuid():N}"

            let! error = Assert.ThrowsAsync<ApplicationException>(fun () -> probe.api.boom "kaboom" :> Task)

            Assert.Contains("kaboom", error.Message)
            // A handler failure is not dressed up as a transport diagnostic.
            Assert.DoesNotContain("Orleans.FSharp functional transport", error.Message)
        }
