/// <summary>
/// The production two-silo fixture for spec 003 Phase 3. It hosts real functional definitions
/// through <c>AddFunctionalGrain</c>, binds them from an external client through
/// <c>AddFunctionalGrainClient</c>, and reproduces the Phase-0 seam-proof cluster shape:
/// heterogeneous hosting, liveness stabilization, and cluster-manifest propagation.
/// </summary>
module Orleans.FSharp.Integration.FunctionalClusterFixture

open System
open System.Collections.Concurrent
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Serialization.Invocation
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Domain
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A mapped domain key, so binding exercises the mapped string codec.</summary>
[<Struct>]
type ProbeId = private ProbeId of string

[<RequireQualifiedAccess>]
module ProbeId =
    let create value = ProbeId value
    let value (ProbeId value) = value

type ProbeActor = private ProbeActor of unit
type PeerActor = private PeerActor of unit
type OtherActor = private OtherActor of unit

type ProbeState = { last: string }

/// <summary>
/// An ordinary F# domain shape: a record holding a list and an option, replied to with a
/// discriminated union. Neither has an Orleans <c>[GenerateSerializer]</c> attribute, so both
/// cross the wire through the F# binary codec — which is exactly the path that needs the
/// silo-side top-level payload type declaration.
/// </summary>
type Note =
    { title: string
      tags: string list
      author: string option }

type NoteResult =
    | Accepted of id: int * echoed: Note
    | Rejected of reason: string

/// <summary>
/// The API record every functional integration contract is built from. One record type,
/// three actor brands, three explicit grain types.
/// </summary>
[<NoEquality; NoComparison>]
type ProbeApi =
    { echo: string -> Task<string>
      identity: unit -> Task<string>
      slow: int -> Task<string>
      readSlow: int -> Task<string>
      peek: unit -> Task<int>
      bump: int -> Task<unit>
      /// A PLAIN one-way operation (no alwaysInterleave): "ok" records what it saw,
      /// "fail" records and then throws on the target.
      signal: string -> Task<unit>
      counter: unit -> Task<int>
      awaitGate: int -> Task<string>
      gateEntered: unit -> Task<bool>
      releaseGateInterleave: unit -> Task<string>
      releaseGateReadOnly: unit -> Task<string>
      releaseGateDefault: unit -> Task<string>
      waitCancel: int -> Task<string>
      callPeerCancel: string -> Task<string>
      stateWrite: string -> Task<unit>
      stateReadOnlyWrite: string -> Task<unit>
      stateRead: unit -> Task<string>
      boom: string -> Task<string>
      big: int -> Task<string>
      sink: byte[] -> Task<int>
      note: Note -> Task<NoteResult>
      /// A tuple whose elements are FSharp.Core generics, in BOTH positions. Orleans owns
      /// System.Tuple, so this argument never reaches the F# codec whole — its elements arrive
      /// one field at a time, each carrying only its own type name. It is the shape that used
      /// to fail on the first call over the real transport.
      blend: (string option * string list) -> Task<string option * string list>
      /// A two-input operation, spelled the only way there is: one tuple argument. Both
      /// elements are strings on purpose — a tuple sent in the wrong order would still
      /// type-check, so only the value the silo observes can prove the order survives the wire.
      tag: (string * string) -> Task<string> }

// ──────────────────────────────────────────────────────────────────────────────
// Cross-activation observation table
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Per-activation concurrency, gate, and counter probe.</summary>
[<Sealed>]
type ProbeCell() =
    let mutable inFlight = 0
    let mutable maxInFlight = 0
    let mutable counter = 0
    let mutable gateEntered = 0

    /// <summary>The gate a parked default-policy request waits on.</summary>
    member val Gate =
        TaskCompletionSource<bool> TaskCreationOptions.RunContinuationsAsynchronously with get

    member _.InFlight = Volatile.Read(&inFlight)
    member _.MaxInFlight = Volatile.Read(&maxInFlight)
    member _.Counter = Volatile.Read(&counter)
    member _.Bump() = Interlocked.Increment(&counter) |> ignore
    member _.GateEntered = Volatile.Read(&gateEntered) = 1
    member _.EnterGate() = Volatile.Write(&gateEntered, 1)
    member _.LeaveGate() = Volatile.Write(&gateEntered, 0)

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

/// <summary>
/// Silos of a <c>TestCluster</c> share one process, so a target can record what it observed for
/// a test to read back. Used only for facts which cannot travel in a reply.
/// </summary>
[<RequireQualifiedAccess>]
module Probe =
    let private cells = ConcurrentDictionary<string, ProbeCell>()
    let observations = ConcurrentDictionary<string, string>()

    let cell (grainId: GrainId) =
        cells.GetOrAdd(string grainId, fun _ -> ProbeCell())

    let record key value = observations.[key] <- value

    let tryGet key =
        match observations.TryGetValue key with
        | true, value -> Some value
        | _ -> None

// ──────────────────────────────────────────────────────────────────────────────
// Contracts and definitions
// ──────────────────────────────────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module FunctionalGrainTypes =
    [<Literal>]
    let Probe = "functional.probe"

    [<Literal>]
    let Peer = "functional.peer"

    /// <summary>Hosted only by non-primary silos — the heterogeneous-manifest arm.</summary>
    [<Literal>]
    let Other = "functional.other"

    [<Literal>]
    let PrimarySiloName = "Primary"

/// <summary>The transport limits the fixture configures: the client is deliberately more
/// permissive than the silos so the silo-side boundaries can be reached.</summary>
[<RequireQualifiedAccess>]
module FunctionalLimits =
    [<Literal>]
    let Client = 1048576

    [<Literal>]
    let Silo = 65536

let private contractFor<'Actor> (name: string) =
    grainContract<'Actor, ProbeId, ProbeApi> () {
        grainType name
        version 1
        stringKeyMapped ProbeId.value ProbeId.create

        readOnly (_.readSlow)
        readOnly (_.counter)
        readOnly (_.stateRead)
        readOnly (_.stateReadOnlyWrite)
        readOnly (_.releaseGateReadOnly)

        readOnly (_.peek)
        alwaysInterleave (_.peek)
        readOnly (_.gateEntered)
        alwaysInterleave (_.gateEntered)
        readOnly (_.releaseGateInterleave)
        alwaysInterleave (_.releaseGateInterleave)

        oneWay (_.bump)
        alwaysInterleave (_.bump)

        oneWay (_.signal)
    }

let probeContract = contractFor<ProbeActor> FunctionalGrainTypes.Probe
let peerContract = contractFor<PeerActor> FunctionalGrainTypes.Peer
let otherContract = contractFor<OtherActor> FunctionalGrainTypes.Other

let probeRef = FunctionalGrain.rawRef probeContract
let peerRef = FunctionalGrain.rawRef peerContract
let otherRef = FunctionalGrain.rawRef otherContract

let private siloOf (context: FunctionalGrainContext<'Actor, ProbeId>) =
    match context.services.GetService typeof<ILocalSiloDetails> with
    | :? ILocalSiloDetails as details -> string details.SiloAddress
    | _ -> "?"

let private definitionFor (contract: GrainContract<'Actor, ProbeId, ProbeApi>) =
    grainFor contract {
        defaultState (fun () -> { last = "" })

        handle (_.echo) (fun _ state (argument: string) -> task { return state, argument })

        handle (_.tag) (fun _ state ((name, value): string * string) ->
            task { return { last = name }, $"{name}={value}" })

        handle (_.identity) (fun context state () ->
            task {
                return
                    state,
                    $"silo={siloOf context}|grain={context.grainId}|cancellable={context.cancellationToken.CanBeCanceled}"
            })

        handle (_.signal) (fun context state (mode: string) ->
            task {
                // Whether the DELIVERED one-way context can be cancelled at all: the spec
                // requires CancellationToken.None here, unlike the acknowledged path.
                Probe.record
                    $"signal:{context.grainId}"
                    $"mode={mode}|cancellable={context.cancellationToken.CanBeCanceled}"

                if mode = "fail" then
                    raise (ApplicationException $"one-way target failure on {context.grainId}")

                return { last = "signal:" + mode }, ()
            })

        handle (_.slow) (fun context state (delay: int) ->
            task {
                let cell = Probe.cell context.grainId
                let entered = cell.Enter()

                try
                    do! Task.Delay(delay, context.cancellationToken)
                    return state, $"slow:entered={entered}:max={cell.MaxInFlight}"
                finally
                    cell.Leave()
            })

        handle (_.readSlow) (fun context state (delay: int) ->
            task {
                let cell = Probe.cell context.grainId
                let entered = cell.Enter()

                try
                    do! Task.Delay(delay, context.cancellationToken)
                    return state, $"readSlow:entered={entered}:max={cell.MaxInFlight}"
                finally
                    cell.Leave()
            })

        handle (_.peek) (fun context state () -> task { return state, (Probe.cell context.grainId).InFlight })

        handle (_.bump) (fun context state (delay: int) ->
            task {
                // Deliberately delays on the CONTEXT token of a one-way invocation. With the
                // spec's CancellationToken.None this is inert; with a target-local token it
                // would be waiting on a CancellationTokenSource the request disposes.
                do! Task.Delay(delay, context.cancellationToken)
                (Probe.cell context.grainId).Bump()
                Probe.record $"bump:{context.grainId}" "delivered"
                return state, ()
            })

        handle (_.counter) (fun context state () -> task { return state, (Probe.cell context.grainId).Counter })

        handle (_.awaitGate) (fun context state (timeout: int) ->
            task {
                let cell = Probe.cell context.grainId
                cell.EnterGate()

                try
                    let! finished = Task.WhenAny(cell.Gate.Task, Task.Delay timeout)

                    return
                        state,
                        (if obj.ReferenceEquals(finished, cell.Gate.Task) then
                             "released"
                         else
                             "timeout")
                finally
                    cell.LeaveGate()
            })

        handle (_.gateEntered) (fun context state () -> task { return state, (Probe.cell context.grainId).GateEntered })

        handle (_.releaseGateInterleave) (fun context state () ->
            task {
                (Probe.cell context.grainId).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        handle (_.releaseGateReadOnly) (fun context state () ->
            task {
                (Probe.cell context.grainId).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        handle (_.releaseGateDefault) (fun context state () ->
            task {
                (Probe.cell context.grainId).Gate.TrySetResult true |> ignore
                return state, "ok"
            })

        handle (_.waitCancel) (fun context state (delay: int) ->
            task {
                let key = $"waitCancel:{context.grainId}"

                try
                    do! Task.Delay(delay, context.cancellationToken)
                    Probe.record key $"completed@{siloOf context}"
                    return state, "completed"
                with :? OperationCanceledException ->
                    Probe.record key $"cancelled@{siloOf context}"
                    return state, "cancelled"
            })

        handle (_.callPeerCancel) (fun context state (argument: string) ->
            task {
                // "<peerKey>|<peerDelayMs>|<cancelAfterMs>"
                let parts = argument.Split '|'
                let peerKey = ProbeId.create parts.[0]
                let peerDelay = int parts.[1]
                let cancelAfter = int parts.[2]
                let peer = peerRef context.grainFactory peerKey

                Probe.observations.TryRemove $"waitCancel:{FunctionalGrainTypes.Peer}/{parts.[0]}"
                |> ignore

                use source = new CancellationTokenSource()
                let call = peer.callCancellable (_.waitCancel) peerDelay source.Token
                source.CancelAfter cancelAfter

                let! outcome =
                    task {
                        try
                            let! reply = call
                            return "reply:" + reply
                        with
                        | :? OperationCanceledException -> return "caller-cancelled"
                        | error -> return "error:" + error.GetType().Name
                    }

                let key = $"waitCancel:{FunctionalGrainTypes.Peer}/{parts.[0]}"
                let deadline = DateTime.UtcNow.AddSeconds 10.0
                let mutable observed = Probe.tryGet key

                while observed.IsNone && DateTime.UtcNow < deadline do
                    do! Task.Delay 100
                    observed <- Probe.tryGet key

                let peerObserved = Option.defaultValue "none" observed

                return state, $"self={siloOf context}|outcome={outcome}|peerObserved={peerObserved}"
            })

        handle (_.stateWrite) (fun _ _ (value: string) -> task { return { last = value }, () })

        handle (_.stateReadOnlyWrite) (fun _ _ (value: string) -> task { return { last = value }, () })

        handle (_.stateRead) (fun _ state () -> task { return state, state.last })

        handle (_.boom) (fun _ state (message: string) ->
            task {
                if message <> "" then
                    raise (ApplicationException message)

                return state, message
            })

        handle (_.big) (fun _ state (size: int) -> task { return state, String('x', size) })

        handle (_.note) (fun _ state (note: Note) ->
            task {
                if String.IsNullOrWhiteSpace note.title then
                    return state, Rejected "the title is blank"
                else
                    return state, Accepted(note.tags.Length, note)
            })

        handle (_.blend) (fun _ state ((author, tags): string option * string list) ->
            task {
                // The reply is a tuple of the same two generic shapes, so one call proves the
                // argument direction and the reply direction over the real silo boundary.
                let echoedAuthor = author |> Option.map (fun name -> name.ToUpperInvariant())
                return state, (echoedAuthor, List.rev tags)
            })

        handle (_.sink) (fun context state (payload: byte[]) ->
            task {
                Probe.record $"sink:{context.grainId}" (string payload.Length)
                return state, payload.Length
            })
    }

let probeDefinition = definitionFor probeContract
let peerDefinition = definitionFor peerContract
let otherDefinition = definitionFor otherContract

// ──────────────────────────────────────────────────────────────────────────────
// Silo log capture
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>One captured silo log entry: category, level, rendered message, exception text.</summary>
type CapturedLog =
    { Category: string
      Level: LogLevel
      Message: string
      Error: string }

/// <summary>
/// Errors logged by the functional runtime itself. A one-way target failure never reaches the
/// caller, so the only observable record of it is the silo log.
/// </summary>
[<RequireQualifiedAccess>]
module LogCapture =
    let entries = ConcurrentQueue<CapturedLog>()

    let clear () = entries.Clear()

[<Sealed>]
type private CaptureLogger(category: string) =
    interface ILogger with
        member _.BeginScope<'TState>(_state: 'TState) =
            { new IDisposable with
                member _.Dispose() = () }

        member _.IsEnabled(level: LogLevel) = level >= LogLevel.Error

        member _.Log<'TState>(level, _eventId, state: 'TState, error: exn, formatter: Func<'TState, exn, string>) =
            if level >= LogLevel.Error then
                LogCapture.entries.Enqueue
                    { Category = category
                      Level = level
                      Message = formatter.Invoke(state, error)
                      Error =
                        match error with
                        | null -> ""
                        | value -> value.ToString() }

[<Sealed>]
type FunctionalLogCaptureProvider() =
    interface ILoggerProvider with
        member _.CreateLogger(category: string) = CaptureLogger category :> ILogger
        member _.Dispose() = ()

// ──────────────────────────────────────────────────────────────────────────────
// Global call-filter capture
// ──────────────────────────────────────────────────────────────────────────────

type CapturedCall =
    { InterfaceName: string
      MethodName: string
      ActivityName: string
      InterfaceTypeName: string
      InterfaceMethodName: string
      ImplementationMethodName: string
      ImplementationDeclaringType: string
      GrainInstanceType: string
      GrainType: string
      OperationId: string
      Version: int
      IsReadOnly: bool
      IsOneWay: bool
      IsAlwaysInterleave: bool
      PayloadLength: int
      ArgumentCount: int
      Argument1IsToken: bool }

[<RequireQualifiedAccess>]
module CallCapture =
    let incoming = ConcurrentQueue<CapturedCall>()
    let outgoing = ConcurrentQueue<CapturedCall>()
    let mutable rejectOperation: string = null

    let clear () =
        incoming.Clear()
        outgoing.Clear()
        rejectOperation <- null

    let internal capture
        (request: IInvokable)
        (interfaceMethod: MethodInfo)
        (implementationMethod: MethodInfo)
        (grainInstance: obj)
        =
        let metadata = request.GetArgument 0 :?> IFunctionalRequestMetadata

        { InterfaceName = request.GetInterfaceName()
          MethodName = request.GetMethodName()
          ActivityName = request.GetActivityName()
          InterfaceTypeName =
            match request.GetInterfaceType() with
            | null -> "<null>"
            | interfaceType -> interfaceType.Name
          InterfaceMethodName =
            match interfaceMethod with
            | null -> "<null>"
            | method -> method.Name
          ImplementationMethodName =
            match implementationMethod with
            | null -> "<null>"
            | method -> method.Name
          ImplementationDeclaringType =
            match implementationMethod with
            | null -> "<null>"
            | method -> method.DeclaringType.Name
          GrainInstanceType =
            match grainInstance with
            | null -> "<null>"
            | grain -> grain.GetType().Name
          GrainType = metadata.GrainType
          OperationId = metadata.OperationId
          Version = metadata.ContractVersion
          IsReadOnly = metadata.IsReadOnly
          IsOneWay = metadata.IsOneWay
          IsAlwaysInterleave = metadata.IsAlwaysInterleave
          PayloadLength = metadata.PayloadLength
          ArgumentCount = request.GetArgumentCount()
          Argument1IsToken = (request.GetArgument 1) :? CancellationToken }

[<Sealed>]
type FunctionalIncomingCallFilter() =
    interface IIncomingGrainCallFilter with
        member _.Invoke(context: IIncomingGrainCallContext) =
            task {
                if context.Request :? FunctionalRequest then
                    let metadata = context.Request.GetArgument 0 :?> IFunctionalRequestMetadata

                    CallCapture.incoming.Enqueue(
                        CallCapture.capture
                            context.Request
                            context.InterfaceMethod
                            context.ImplementationMethod
                            context.Grain
                    )

                    if metadata.OperationId = CallCapture.rejectOperation then
                        RequestContext.Set("functional.rejected", metadata.OperationId)
                        raise (InvalidOperationException $"filter rejected '{metadata.OperationId}'")

                do! context.Invoke()
            }
            :> Task

[<Sealed>]
type FunctionalOutgoingCallFilter() =
    interface IOutgoingGrainCallFilter with
        member _.Invoke(context: IOutgoingGrainCallContext) =
            task {
                if context.Request :? FunctionalRequest then
                    CallCapture.outgoing.Enqueue(CallCapture.capture context.Request context.InterfaceMethod null null)

                do! context.Invoke()
            }
            :> Task

// ──────────────────────────────────────────────────────────────────────────────
// Cluster
// ──────────────────────────────────────────────────────────────────────────────

type FunctionalSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            let siloName = siloBuilder.Configuration.["Orleans:Name"]

            siloBuilder.AddFunctionalGrain probeDefinition |> ignore
            siloBuilder.AddFunctionalGrain peerDefinition |> ignore

            // Heterogeneous hosting: only non-primary silos advertise "functional.other".
            if siloName <> FunctionalGrainTypes.PrimarySiloName then
                siloBuilder.AddFunctionalGrain otherDefinition |> ignore

            siloBuilder.Services.Configure<FunctionalGrainTransportOptions>(fun
                                                                                (options:
                                                                                    FunctionalGrainTransportOptions) ->
                options.MaxPayloadBytes <- FunctionalLimits.Silo)
            |> ignore

            siloBuilder.Services.AddSingleton<ILoggerProvider, FunctionalLogCaptureProvider>()
            |> ignore

            siloBuilder.Services.AddSingleton<IIncomingGrainCallFilter, FunctionalIncomingCallFilter>()
            |> ignore

            siloBuilder.Services.AddSingleton<IOutgoingGrainCallFilter, FunctionalOutgoingCallFilter>()
            |> ignore

type FunctionalClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.AddFunctionalGrainClient() |> ignore

            clientBuilder.Services.Configure<FunctionalGrainTransportOptions>(fun
                                                                                 (options:
                                                                                     FunctionalGrainTransportOptions) ->
                options.MaxPayloadBytes <- FunctionalLimits.Client)
            |> ignore

            clientBuilder.Services.AddSingleton<IOutgoingGrainCallFilter, FunctionalOutgoingCallFilter>()
            |> ignore

[<Sealed>]
type FunctionalClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder 2s
        // Silos advertise different definitions, so the homogeneity shortcut must be off.
        builder.Options.AssumeHomogenousSilosForTesting <- false
        builder.AddSiloBuilderConfigurator<FunctionalSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<FunctionalClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        // Placement only spreads once every silo is Active in the membership view.
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    /// <summary>
    /// Placement only spreads once every silo's cluster manifest carries every other silo's
    /// grain manifest; until then a silo believes it is the only host of a grain type.
    /// </summary>
    let waitForManifestPropagation () =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        let propagated () =
            cluster.Silos
            |> Seq.forall (fun handle ->
                let services = (handle :?> InProcessSiloHandle).SiloHost.Services
                let current = services.GetRequiredService<IClusterManifestProvider>().Current

                current.Silos.Count = cluster.Silos.Count
                && current.Silos
                   |> Seq.forall (fun pair ->
                       pair.Value.Grains.ContainsKey(GrainType.Create FunctionalGrainTypes.Peer)))

        while not (propagated ()) && DateTime.UtcNow < deadline do
            Thread.Sleep 200

        if not (propagated ()) then
            failwith "cluster manifests did not propagate to every silo"

    do waitForManifestPropagation ()

    member _.Cluster = cluster
    member _.Client = cluster.Client

    /// <summary>The bound probe API of one key, from the external client.</summary>
    member _.Probe(key: string) = probeRef cluster.Client (ProbeId.create key)

    member _.Peer(key: string) = peerRef cluster.Client (ProbeId.create key)

    member _.Other(key: string) = otherRef cluster.Client (ProbeId.create key)

    /// <summary>Services of the silo whose name matches.</summary>
    member _.SiloServices(siloName: string) =
        cluster.Silos
        |> Seq.pick (fun handle ->
            if handle.Name = siloName then
                Some (handle :?> InProcessSiloHandle).SiloHost.Services
            else
                None)

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalCluster")>]
type FunctionalClusterCollection() =
    interface ICollectionFixture<FunctionalClusterFixture>
