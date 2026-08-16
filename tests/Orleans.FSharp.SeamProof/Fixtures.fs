/// Phase 0 seam proof — TestingHost fixtures and the client-side call helper.
namespace Orleans.FSharp.SeamProof

open System
open System.Collections.Concurrent
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Serialization.Invocation
open Orleans.TestingHost
open Xunit

// ── Grain types hosted by the seam-proof cluster ────────────────────────────

[<RequireQualifiedAccess>]
module SeamGrainTypes =
    [<Literal>]
    let Probe = "seam.probe"

    [<Literal>]
    let Peer = "seam.peer"

    /// Hosted only by the secondary silo — the heterogeneous-manifest proof.
    [<Literal>]
    let Other = "seam.other"

    [<Literal>]
    let PrimarySiloName = "Primary"

// ── Global call-filter capture (item 6) ─────────────────────────────────────

type CapturedCall =
    { InterfaceName: string
      MethodName: string
      ActivityName: string
      InterfaceTypeName: string
      InterfaceMethodName: string
      ImplementationMethodName: string
      ImplementationDeclaringType: string
      GrainInstanceType: string
      MetadataGrainType: string
      MetadataOperationId: string
      MetadataVersion: int
      MetadataReadOnly: bool
      MetadataOneWay: bool
      MetadataAlwaysInterleave: bool
      MetadataPayloadLength: int
      ArgumentCount: int
      Argument1IsToken: bool }

[<RequireQualifiedAccess>]
module CallCapture =
    let incoming = ConcurrentQueue<CapturedCall>()
    let outgoing = ConcurrentQueue<CapturedCall>()

    let clear () =
        incoming.Clear()
        outgoing.Clear()

    let internal capture (request: IInvokable) (interfaceMethod: MethodInfo) (implementationMethod: MethodInfo) (grainInstance: obj) =
        let metadata = request.GetArgument 0 :?> IFunctionalRequestMetadata

        { InterfaceName = request.GetInterfaceName()
          MethodName = request.GetMethodName()
          ActivityName = request.GetActivityName()
          InterfaceTypeName =
            match request.GetInterfaceType() with
            | null -> "<null>"
            | t -> t.Name
          InterfaceMethodName =
            match interfaceMethod with
            | null -> "<null>"
            | m -> m.Name
          ImplementationMethodName =
            match implementationMethod with
            | null -> "<null>"
            | m -> m.Name
          ImplementationDeclaringType =
            match implementationMethod with
            | null -> "<null>"
            | m -> m.DeclaringType.Name
          GrainInstanceType =
            match grainInstance with
            | null -> "<null>"
            | g -> g.GetType().Name
          MetadataGrainType = metadata.GrainType
          MetadataOperationId = metadata.OperationId
          MetadataVersion = metadata.ContractVersion
          MetadataReadOnly = metadata.IsReadOnly
          MetadataOneWay = metadata.IsOneWay
          MetadataAlwaysInterleave = metadata.IsAlwaysInterleave
          MetadataPayloadLength = metadata.PayloadLength
          ArgumentCount = request.GetArgumentCount()
          Argument1IsToken = (request.GetArgument 1) :? CancellationToken }

[<Sealed>]
type SeamIncomingCallFilter() =
    interface IIncomingGrainCallFilter with
        member _.Invoke(context: IIncomingGrainCallContext) =
            task {
                if (context.Request :? FunctionalRequest) then
                    CallCapture.incoming.Enqueue(
                        CallCapture.capture
                            context.Request
                            context.InterfaceMethod
                            context.ImplementationMethod
                            context.Grain
                    )

                do! context.Invoke()
            }
            :> Task

[<Sealed>]
type SeamOutgoingCallFilter() =
    interface IOutgoingGrainCallFilter with
        member _.Invoke(context: IOutgoingGrainCallContext) =
            task {
                if (context.Request :? FunctionalRequest) then
                    CallCapture.outgoing.Enqueue(
                        CallCapture.capture context.Request context.InterfaceMethod null null
                    )

                do! context.Invoke()
            }
            :> Task

// ── TestCluster configurators ───────────────────────────────────────────────

type SeamSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            let siloName = siloBuilder.Configuration["SiloName"]

            let registry = SeamRegistry()
            registry.Add(SeamDefinition.create<ProbeActor> SeamGrainTypes.Probe)
            registry.Add(SeamDefinition.create<PeerActor> SeamGrainTypes.Peer)

            // Heterogeneous hosting: only non-primary silos advertise "seam.other".
            if siloName <> SeamGrainTypes.PrimarySiloName then
                registry.Add(SeamDefinition.create<OtherActor> SeamGrainTypes.Other)

            SeamRegistration.addSiloServices siloBuilder.Services registry |> ignore

            siloBuilder.Services.AddSerializer(fun builder ->
                SeamTransportCodecRegistration.addToSerializerBuilder builder |> ignore)
            |> ignore

            siloBuilder.Services.AddSingleton<IIncomingGrainCallFilter, SeamIncomingCallFilter>()
            |> ignore

            siloBuilder.Services.AddSingleton<IOutgoingGrainCallFilter, SeamOutgoingCallFilter>()
            |> ignore

            siloBuilder.AddMemoryGrainStorage "Default" |> ignore

type SeamClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            SeamRegistration.addClientServices clientBuilder.Services |> ignore

            clientBuilder.Services.AddSerializer(fun builder ->
                SeamTransportCodecRegistration.addToSerializerBuilder builder |> ignore)
            |> ignore

            clientBuilder.Services.AddSingleton<IOutgoingGrainCallFilter, SeamOutgoingCallFilter>()
            |> ignore

// ── Client-side call helper ─────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module SeamClient =

    let reference (factory: IGrainFactory) (grainType: string) (key: string) =
        factory.GetGrain(FunctionalIds.grainId grainType key, FunctionalIds.grainInterfaceType grainType)

    let functionalReference (factory: IGrainFactory) (grainType: string) (key: string) =
        reference factory grainType key :?> FunctionalGrainReference

    let closedInterface (grainType: string) =
        match grainType with
        | SeamGrainTypes.Probe -> typedefof<IFunctionalGrainTarget<_>>.MakeGenericType(typeof<ProbeActor>)
        | SeamGrainTypes.Peer -> typedefof<IFunctionalGrainTarget<_>>.MakeGenericType(typeof<PeerActor>)
        | SeamGrainTypes.Other -> typedefof<IFunctionalGrainTarget<_>>.MakeGenericType(typeof<OtherActor>)
        | other -> invalidOp $"Unknown seam grain type '{other}'."

    let callWith
        (services: IServiceProvider)
        (factory: IGrainFactory)
        (grainType: string)
        (key: string)
        (operationId: string)
        (argument: string)
        (cancellationToken: CancellationToken)
        : Task<string> =
        task {
            let serializer = services.GetRequiredService<Serializer>()
            let reference = functionalReference factory grainType key
            let envelope = Envelope.build serializer grainType operationId argument
            let! reply = reference.SendAsyncTyped(closedInterface grainType, envelope, cancellationToken)
            return Envelope.readReply serializer grainType operationId reply
        }

    let oneWay
        (services: IServiceProvider)
        (factory: IGrainFactory)
        (grainType: string)
        (key: string)
        (operationId: string)
        (argument: string)
        =
        let serializer = services.GetRequiredService<Serializer>()
        let reference = functionalReference factory grainType key
        reference.SendOneWay(Envelope.build serializer grainType operationId argument)

// ── Cluster fixture ─────────────────────────────────────────────────────────

[<Sealed>]
type SeamClusterFixture() =
    let cluster =
        let builder = TestClusterBuilder(2s)
        builder.AddSiloBuilderConfigurator<SeamSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<SeamClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster

    member _.Cluster = cluster
    member _.Client = cluster.Client
    member _.ClientServices = cluster.Client.ServiceProvider

    member this.Call grainType key operationId argument =
        SeamClient.callWith this.ClientServices this.Client grainType key operationId argument CancellationToken.None

    member this.CallCancellable grainType key operationId argument token =
        SeamClient.callWith this.ClientServices this.Client grainType key operationId argument token

    member this.OneWay grainType key operationId argument =
        SeamClient.oneWay this.ClientServices this.Client grainType key operationId argument

    /// Services of the silo whose name matches, or the primary silo.
    member _.SiloServices(siloName: string) =
        cluster.Silos
        |> Seq.pick (fun s ->
            if s.Name = siloName then
                Some (s :?> InProcessSiloHandle).SiloHost.Services
            else
                None)

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("SeamCluster")>]
type SeamClusterCollection() =
    interface ICollectionFixture<SeamClusterFixture>
