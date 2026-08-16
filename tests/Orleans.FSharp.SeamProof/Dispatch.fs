/// Phase 0 seam proof — the spike "application": a fixed operation table and the
/// dispatch body used by the custom-activated target.
///
/// Every operation takes a `string` argument and returns a `string` reply. That
/// keeps payload serialization on stock Orleans codecs so the proofs isolate the
/// Orleans *seams* rather than F# codec work (which is Phase 2).
namespace Orleans.FSharp.SeamProof

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Orleans.Serialization

/// Cross-activation observation table. Silos in a `TestCluster` share one
/// process, so a remote target can record what it observed for the test to read
/// back. Only used for facts that cannot travel in a reply (e.g. a cancelled
/// call's target-side observation).
[<RequireQualifiedAccess>]
module SeamObservations =
    let table = ConcurrentDictionary<string, string>()

    let record key value = table[key] <- value

    let tryGet key =
        match table.TryGetValue key with
        | true, v -> Some v
        | _ -> None

/// The durable state shape. A plain `string` cannot be used: Orleans' default
/// reference-type activator calls `RuntimeHelpers.GetUninitializedObject`, which
/// rejects `String`. A `List<string>` holding at most one element keeps the
/// proof readable while staying on stock Orleans codecs.
[<RequireQualifiedAccess>]
module StateBox =
    let read (box: ResizeArray<string>) =
        if isNull box || box.Count = 0 then "" else box[0]

    let write (box: ResizeArray<string>) (value: string) =
        box.Clear()
        box.Add value

/// Per-activation mutable probe used by the seam assertions.
[<Sealed>]
type ActivationProbe() =
    let mutable inFlight = 0
    let mutable maxInFlight = 0
    let mutable counter = 0
    let mutable gateEntered = 0

    member val TargetTypeName = "" with get, set
    member val ContextIsSuppliedContext = false with get, set
    member val RecordExistsAtActivation = false with get, set
    member val StateAtActivation = "" with get, set
    member val SecondRecordExistsAtActivation = false with get, set
    member val SecondStateAtActivation = "" with get, set
    member val Deactivate: unit -> unit = id with get, set

    member _.Counter = Volatile.Read(&counter)
    member _.Bump() = Interlocked.Increment(&counter) |> ignore
    member _.InFlight = Volatile.Read(&inFlight)
    member _.MaxInFlight = Volatile.Read(&maxInFlight)

    member _.Enter() =
        let now = Interlocked.Increment(&inFlight)
        let mutable spin = true

        while spin do
            let observed = Volatile.Read(&maxInFlight)

            if now <= observed then
                spin <- false
            elif Interlocked.CompareExchange(&maxInFlight, now, observed) = observed then
                spin <- false

        now

    member _.Leave() = Interlocked.Decrement(&inFlight) |> ignore

    /// Deterministic interleaving probe: a default-policy request parks on the
    /// gate while another request tries to release it from the same activation.
    member val Gate = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) with get

    member _.GateEntered = Volatile.Read(&gateEntered) = 1
    member _.EnterGate() = Volatile.Write(&gateEntered, 1)
    member _.LeaveGate() = Volatile.Write(&gateEntered, 0)

/// Everything a dispatch body needs from its activation.
type ActivationEnv =
    { Context: IGrainContext
      GrainType: string
      Serializer: Serializer
      GrainFactory: IGrainFactory
      /// Facet created by the custom activator, before the lifecycle runs.
      /// `ResizeArray<string>` (not bare `string`) because Orleans' default
      /// reference-type activator cannot create an uninitialized `String`.
      EarlyState: IPersistentState<ResizeArray<string>>
      /// A second attached facet, also created by the activator.
      SecondState: IPersistentState<ResizeArray<string>>
      /// Negative control for item 9: attempts to create a facet *now*, i.e.
      /// after the activation lifecycle already started.
      CreateFacetNow: unit -> string
      Probe: ActivationProbe }

/// Immutable operation descriptor.
type OperationDescriptor =
    { OperationId: string
      Flags: byte }

[<RequireQualifiedAccess>]
module Operations =

    [<Literal>]
    let ContractVersion = 1

    let all: OperationDescriptor list =
        [ { OperationId = "echo"; Flags = AdmissionFlags.None }
          { OperationId = "identity"; Flags = AdmissionFlags.None }
          { OperationId = "slowWrite"; Flags = AdmissionFlags.None }
          { OperationId = "readSlow"; Flags = AdmissionFlags.ReadOnly }
          { OperationId = "peekReadOnly"; Flags = AdmissionFlags.ReadOnly }
          { OperationId = "peekInterleave"; Flags = AdmissionFlags.AlwaysInterleave }
          { OperationId = "bump"
            Flags = AdmissionFlags.OneWay ||| AdmissionFlags.AlwaysInterleave }
          { OperationId = "counter"; Flags = AdmissionFlags.ReadOnly }
          { OperationId = "awaitGate"; Flags = AdmissionFlags.None }
          { OperationId = "gateEntered"; Flags = AdmissionFlags.AlwaysInterleave }
          { OperationId = "releaseGateInterleave"; Flags = AdmissionFlags.AlwaysInterleave }
          { OperationId = "releaseGateReadOnly"; Flags = AdmissionFlags.ReadOnly }
          { OperationId = "releaseGateDefault"; Flags = AdmissionFlags.None }
          { OperationId = "waitCancel"; Flags = AdmissionFlags.None }
          { OperationId = "callPeerCancel"; Flags = AdmissionFlags.None }
          { OperationId = "stateRead"; Flags = AdmissionFlags.None }
          { OperationId = "stateWrite"; Flags = AdmissionFlags.None }
          { OperationId = "stateInfo"; Flags = AdmissionFlags.ReadOnly }
          { OperationId = "secondWrite"; Flags = AdmissionFlags.None }
          { OperationId = "secondRead"; Flags = AdmissionFlags.None }
          { OperationId = "secondInfo"; Flags = AdmissionFlags.ReadOnly }
          { OperationId = "lateCreate"; Flags = AdmissionFlags.None }
          { OperationId = "deactivate"; Flags = AdmissionFlags.None } ]

    let private byId =
        all |> List.map (fun d -> d.OperationId, d) |> dict

    let tryFind (operationId: string) =
        match byId.TryGetValue operationId with
        | true, d -> Some d
        | _ -> None

    let find (operationId: string) =
        match tryFind operationId with
        | Some d -> d
        | None -> invalidOp $"Unknown operation '{operationId}'."

/// Envelope construction shared by client and grain-to-grain callers.
[<RequireQualifiedAccess>]
module Envelope =

    let build (serializer: Serializer) (grainType: string) (operationId: string) (argument: string) =
        let descriptor = Operations.find operationId

        FunctionalRequestEnvelope(
            grainType,
            Operations.ContractVersion,
            operationId,
            ProtocolToken.request grainType Operations.ContractVersion operationId,
            descriptor.Flags,
            serializer.SerializeToArray<string> argument
        )

    let readReply (serializer: Serializer) (grainType: string) (operationId: string) (reply: FunctionalReply) =
        let expected = ProtocolToken.reply grainType Operations.ContractVersion operationId

        if not (Linq.Enumerable.SequenceEqual(expected, reply.ProtocolToken)) then
            invalidOp $"Reply protocol token mismatch for '{grainType}'/'{operationId}'."

        serializer.Deserialize<string> reply.Payload

[<RequireQualifiedAccess>]
module Dispatcher =

    let private siloOf (env: ActivationEnv) =
        match env.Context.Address with
        | null -> "?"
        | address -> string address.SiloAddress

    let private handle (env: ActivationEnv) (operationId: string) (argument: string) (ct: CancellationToken) : Task<string> =
        let probe = env.Probe

        match operationId with
        | "echo" -> Task.FromResult argument

        | "identity" ->
            Task.FromResult(
                String.Join(
                    "|",
                    [| $"type={probe.TargetTypeName}"
                       $"marker={typedefof<FunctionalGrainMarker<_>>.Name}"
                       $"ctx={probe.ContextIsSuppliedContext}"
                       $"silo={siloOf env}"
                       $"grain={env.Context.GrainId}" |]
                )
            )

        | "slowWrite"
        | "readSlow" ->
            task {
                let entered = probe.Enter()

                try
                    do! Task.Delay(int argument, ct)
                    return $"{operationId}:entered={entered}:max={probe.MaxInFlight}"
                finally
                    probe.Leave()
            }

        | "peekReadOnly"
        | "peekInterleave" -> Task.FromResult $"{operationId}:inFlight={probe.InFlight}"

        | "bump" ->
            task {
                do! Task.Delay(int argument, CancellationToken.None)
                probe.Bump()
                return "bumped"
            }

        | "counter" -> Task.FromResult(string probe.Counter)

        | "awaitGate" ->
            task {
                probe.EnterGate()

                try
                    let! finished = Task.WhenAny(probe.Gate.Task, Task.Delay(int argument))
                    return if Object.ReferenceEquals(finished, probe.Gate.Task) then "released" else "timeout"
                finally
                    probe.LeaveGate()
            }

        | "gateEntered" -> Task.FromResult(string probe.GateEntered)

        | "releaseGateInterleave"
        | "releaseGateReadOnly"
        | "releaseGateDefault" ->
            probe.Gate.TrySetResult true |> ignore
            Task.FromResult "ok"

        | "waitCancel" ->
            task {
                let observationKey = $"waitCancel:{env.Context.GrainId}"

                try
                    do! Task.Delay(int argument, ct)
                    SeamObservations.record observationKey $"completed@{siloOf env}"
                    return "completed"
                with :? OperationCanceledException ->
                    SeamObservations.record observationKey $"cancelled@{siloOf env}"
                    return "cancelled"
            }

        | "callPeerCancel" ->
            task {
                // argument: "<peerGrainType>|<peerKey>|<peerDelayMs>|<cancelAfterMs>"
                let parts = argument.Split '|'
                let peerGrainType = parts[0]
                let peerKey = parts[1]
                let peerDelay = parts[2]
                let cancelAfter = int parts[3]

                SeamObservations.table.TryRemove($"waitCancel:{FunctionalIds.grainId peerGrainType peerKey}")
                |> ignore

                let peer =
                    env.GrainFactory.GetGrain(
                        FunctionalIds.grainId peerGrainType peerKey,
                        FunctionalIds.grainInterfaceType peerGrainType
                    )
                    :?> FunctionalGrainReference

                use cts = new CancellationTokenSource()
                let envelope = Envelope.build env.Serializer peerGrainType "waitCancel" peerDelay

                let call =
                    peer
                        .SendAsyncTyped(
                            typedefof<IFunctionalGrainTarget<_>>.MakeGenericType(typeof<PeerActor>),
                            envelope,
                            cts.Token
                        )
                        .AsTask()

                cts.CancelAfter cancelAfter

                let! outcome =
                    task {
                        try
                            let! reply = call
                            return "reply:" + Envelope.readReply env.Serializer peerGrainType "waitCancel" reply
                        with
                        | :? OperationCanceledException -> return "caller-cancelled"
                        | ex -> return "error:" + ex.GetType().Name
                    }

                // The caller can observe cancellation before the target finishes
                // recording it, so wait briefly for the target-side observation.
                let observationKey = $"waitCancel:{FunctionalIds.grainId peerGrainType peerKey}"
                let deadline = DateTime.UtcNow.AddSeconds 10.0
                let mutable observed = SeamObservations.tryGet observationKey

                while observed.IsNone && DateTime.UtcNow < deadline do
                    do! Task.Delay 100
                    observed <- SeamObservations.tryGet observationKey

                let peerObserved = Option.defaultValue "none" observed
                return $"self={siloOf env}|outcome={outcome}|peerObserved={peerObserved}"
            }

        | "stateRead" -> Task.FromResult(StateBox.read env.EarlyState.State)

        | "stateWrite" ->
            task {
                StateBox.write env.EarlyState.State argument
                do! env.EarlyState.WriteStateAsync()
                return "ok"
            }

        | "stateInfo" ->
            Task.FromResult
                $"recordExistsAtActivation={probe.RecordExistsAtActivation}|stateAtActivation={probe.StateAtActivation}"

        | "secondRead" -> Task.FromResult(StateBox.read env.SecondState.State)

        | "secondWrite" ->
            task {
                StateBox.write env.SecondState.State argument
                do! env.SecondState.WriteStateAsync()
                return "ok"
            }

        | "secondInfo" ->
            Task.FromResult
                $"recordExistsAtActivation={probe.SecondRecordExistsAtActivation}|stateAtActivation={probe.SecondStateAtActivation}"

        | "lateCreate" -> Task.FromResult(env.CreateFacetNow())

        | "deactivate" ->
            probe.Deactivate()
            Task.FromResult "ok"

        | other -> invalidOp $"Unhandled operation '{other}'."

    /// Full target-side dispatch: fixed-envelope validation, descriptor
    /// resolution, token/flag comparison, typed payload deserialization, handler
    /// invocation, then the descriptor's reply token plus a fresh payload.
    let dispatch (env: ActivationEnv) (envelope: FunctionalRequestEnvelope) (ct: CancellationToken) : ValueTask<FunctionalReply> =
        if envelope.GrainType <> env.GrainType then
            invalidOp $"Envelope grain type '{envelope.GrainType}' does not match hosted '{env.GrainType}'."

        if envelope.ContractVersion <> Operations.ContractVersion then
            invalidOp $"Envelope contract version {envelope.ContractVersion} is not hosted."

        if isNull envelope.ProtocolToken || envelope.ProtocolToken.Length <> 32 then
            invalidOp "Envelope protocol token must be exactly 32 bytes."

        if envelope.AdmissionFlags &&& AdmissionFlags.Reserved <> 0uy then
            invalidOp "Envelope sets a reserved admission flag."

        let descriptor =
            match Operations.tryFind envelope.OperationId with
            | Some d -> d
            | None -> invalidOp $"Unknown operation '{envelope.OperationId}'."

        let expectedRequestToken =
            ProtocolToken.request env.GrainType Operations.ContractVersion descriptor.OperationId

        if not (Linq.Enumerable.SequenceEqual(expectedRequestToken, envelope.ProtocolToken)) then
            invalidOp $"Request protocol token mismatch for '{descriptor.OperationId}'."

        if envelope.AdmissionFlags <> descriptor.Flags then
            invalidOp $"Admission flags {envelope.AdmissionFlags} do not match descriptor {descriptor.Flags}."

        let argument = env.Serializer.Deserialize<string> envelope.Payload

        let work =
            task {
                let! reply = handle env descriptor.OperationId argument ct

                return
                    FunctionalReply(
                        ProtocolToken.reply env.GrainType Operations.ContractVersion descriptor.OperationId,
                        env.Serializer.SerializeToArray<string> reply
                    )
            }

        ValueTask<FunctionalReply>(work)
