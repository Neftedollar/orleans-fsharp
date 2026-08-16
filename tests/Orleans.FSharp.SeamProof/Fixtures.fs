/// Phase 0 seam proof — TestingHost fixtures and the client-side call helper.
namespace Orleans.FSharp.SeamProof

open System
open System.Collections.Concurrent
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Serialization.Invocation
open Orleans.TestingHost
open Xunit

// ── C# codegen fixture handles ──────────────────────────────────────────────

/// Handles onto `Orleans.FSharp.SeamProof.CodegenFixture` — the only C# (and therefore
/// the only Orleans-codegen'd) assembly in this proof. Its open generic grain class and
/// grain interface are what real Orleans discovery contributes to `GrainTypeOptions`.
[<RequireQualifiedAccess>]
module CodegenFixtureTypes =
    let OpenMarker: Type =
        typedefof<Orleans.FSharp.SeamProof.CodegenFixture.CodegenProbeMarker<_>>

    let OpenInterface: Type =
        typedefof<Orleans.FSharp.SeamProof.CodegenFixture.ICodegenProbeTarget<_>>

    let Assembly = OpenMarker.Assembly

// ── GrainTypeOptions pipeline replay (item 2 controls) ──────────────────────

/// Replays the silo's real options pipeline stage by stage, and rebuilds a real Orleans
/// silo grain manifest from arbitrary `GrainTypeOptions`. Used to isolate the effect of
/// `SeamGrainTypeOptionsPostConfigure`: two manifests built from the same live providers
/// and resolvers, differing only in whether the post-configure stage ran.
[<RequireQualifiedAccess>]
module SeamOptionsPipeline =

    /// The state `IPostConfigureOptions<GrainTypeOptions>` observes on the live silo.
    let runConfigure (services: IServiceProvider) (options: GrainTypeOptions) =
        for configure in services.GetServices<IConfigureOptions<GrainTypeOptions>>() do
            match configure with
            | :? IConfigureNamedOptions<GrainTypeOptions> as named -> named.Configure(Options.DefaultName, options)
            | _ -> configure.Configure options

        options

    let runPostConfigure (services: IServiceProvider) (options: GrainTypeOptions) =
        for post in services.GetServices<IPostConfigureOptions<GrainTypeOptions>>() do
            post.PostConfigure(Options.DefaultName, options)

        options

    /// `Orleans.Metadata.SiloManifestProvider` is internal but has a single public
    /// constructor; every parameter except the options comes from the live silo container.
    let buildManifest (services: IServiceProvider) (options: GrainTypeOptions) =
        let providerType =
            match Type.GetType "Orleans.Metadata.SiloManifestProvider, Orleans.Runtime" with
            | null -> invalidOp "Orleans.Metadata.SiloManifestProvider was not found in Orleans.Runtime."
            | t -> t

        let ctor = providerType.GetConstructors() |> Array.exactlyOne

        let args =
            ctor.GetParameters()
            |> Array.map (fun p ->
                if p.ParameterType = typeof<IOptions<GrainTypeOptions>> then
                    box (Options.Create options)
                else
                    ServiceProviderServiceExtensions.GetRequiredService(services, p.ParameterType))

        let instance = ctor.Invoke args

        providerType.GetProperty("SiloManifest").GetValue instance :?> Orleans.Metadata.GrainManifest

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
            // TestingHost publishes the silo name under "Orleans:Name".
            let siloName = siloBuilder.Configuration["Orleans:Name"]

            let registry = SeamRegistry()
            registry.Add(SeamDefinition.create<ProbeActor> SeamGrainTypes.Probe)
            registry.Add(SeamDefinition.create<PeerActor> SeamGrainTypes.Peer)

            // Heterogeneous hosting: only non-primary silos advertise "seam.other".
            if siloName <> SeamGrainTypes.PrimarySiloName then
                registry.Add(SeamDefinition.create<OtherActor> SeamGrainTypes.Other)

            // An all-F# assembly never gets an Orleans codegen type manifest, so Orleans'
            // own discovery would never put the OPEN generic functional marker/interface
            // into GrainTypeOptions and the removal half of the seam would be a no-op
            // against a live silo. Production puts these types in a C# codegen assembly
            // (spec 003: `src/Orleans.FSharp.Abstractions`), where discovery DOES add them.
            // Seeding them here through `IConfigureOptions<GrainTypeOptions>` reproduces
            // that state exactly — `IConfigureOptions` always runs before
            // `IPostConfigureOptions`, so the removal really fires on the live silo.
            // The CLR shape used here (generic type definition) is the shape real Orleans
            // codegen discovery produces; that is proven independently in
            // `Item02_CodegenDiscoveryTests` from the referenced C# fixture assembly.
            siloBuilder.Services.Configure<GrainTypeOptions>(fun (options: GrainTypeOptions) ->
                options.Classes.Add typedefof<FunctionalGrainMarker<_>> |> ignore
                options.Interfaces.Add typedefof<IFunctionalGrainTarget<_>> |> ignore)
            |> ignore

            SeamRegistration.addSiloServices siloBuilder.Services registry |> ignore

            siloBuilder.Services.AddSerializer(fun builder ->
                SeamTransportCodecRegistration.addToSerializerBuilder builder |> ignore

                // Pull in the C# codegen fixture's generated type manifest. A referenced
                // assembly is not loaded until something touches it, and Orleans only sees
                // `TypeManifestProviderAttribute` on assemblies it knows about — without
                // this the fixture's open generic grain class/interface never reach
                // GrainTypeOptions and `Item02_CodegenDiscoveryTests` would be vacuous.
                builder.AddAssembly CodegenFixtureTypes.Assembly |> ignore)
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
        // Silos advertise different definitions, so the homogeneity shortcut must be off.
        builder.Options.AssumeHomogenousSilosForTesting <- false
        builder.AddSiloBuilderConfigurator<SeamSiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<SeamClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        // Placement is only spread across silos once every silo is Active in the
        // membership view; without this the first test can see a one-silo cluster.
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    /// Placement only spreads once every silo's cluster manifest carries every
    /// other silo's grain manifest; until then a silo believes it is the only
    /// host of a grain type and keeps every activation local.
    let waitForManifestPropagation () =
        let deadline = DateTime.UtcNow.AddSeconds 60.0

        let propagated () =
            cluster.Silos
            |> Seq.forall (fun handle ->
                let services = (handle :?> InProcessSiloHandle).SiloHost.Services
                let current = services.GetRequiredService<IClusterManifestProvider>().Current

                current.Silos.Count = cluster.Silos.Count
                && current.Silos
                   |> Seq.forall (fun kv -> kv.Value.Grains.ContainsKey(GrainType.Create SeamGrainTypes.Peer)))

        while not (propagated ()) && DateTime.UtcNow < deadline do
            Thread.Sleep 200

        if not (propagated ()) then
            failwith "cluster manifests did not propagate to every silo"

    do waitForManifestPropagation ()

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
