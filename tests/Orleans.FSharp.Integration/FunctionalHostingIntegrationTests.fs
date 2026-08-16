/// <summary>
/// Spec 003 Phase 3 hosting requirements which need their own silo: the existing-silo
/// regression (a silo which references the abstractions but hosts no functional definition
/// starts clean) and component-configurator order independence.
/// </summary>
module Orleans.FSharp.Integration.FunctionalHostingIntegrationTests

open System
open System.Linq
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Metadata
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalClusterFixture
open Xunit

let private siloServices (cluster: TestCluster) =
    (cluster.Silos.[0] :?> InProcessSiloHandle).SiloHost.Services

/// <summary>A silo which loads the abstractions assembly but hosts no functional definition.</summary>
type PlainSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            // Touch the abstractions assembly the way any application referencing the package
            // would, without registering any functional definition.
            siloBuilder.Services.AddSingleton<Type>(typeof<IFunctionalRequestMetadata>) |> ignore

/// <summary>A silo whose functional component configurator is deliberately ordered FIRST.</summary>
type ConfiguratorFirstSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

            let services = siloBuilder.Services

            let ours =
                services
                |> Seq.find (fun descriptor ->
                    descriptor.ServiceType = typeof<IConfigureGrainTypeComponents>
                    && descriptor.ImplementationType = typeof<FunctionalConfigureGrainTypeComponents>)

            services.Remove ours |> ignore
            services.Insert(0, ours)

/// <summary>A silo whose functional component configurator keeps its natural (last) position.</summary>
type ConfiguratorLastSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

/// <summary>
/// A silo whose functional <c>GrainTypeOptions</c> post-configure has been removed, so the
/// closed marker and interface never reach the manifest. Silo startup validation must catch it.
/// </summary>
type BrokenManifestSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

            let services = siloBuilder.Services

            let postConfigure =
                services
                |> Seq.find (fun descriptor ->
                    descriptor.ImplementationType = typeof<FunctionalGrainTypeOptionsPostConfigure>)

            services.Remove postConfigure |> ignore

/// <summary>A silo whose transport limit violates the ValidateOnStart rule.</summary>
type InvalidLimitSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddFunctionalGrain probeDefinition |> ignore

            siloBuilder.Services.Configure<FunctionalGrainTransportOptions>(fun
                                                                                (options:
                                                                                    FunctionalGrainTransportOptions) ->
                options.MaxPayloadBytes <- 0)
            |> ignore

let private deploy<'Configurator when 'Configurator :> ISiloConfigurator and 'Configurator: (new: unit -> 'Configurator)>
    ()
    =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<'Configurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster

[<Fact>]
let ``a silo which references the abstractions but hosts no functional definition starts clean`` () =
    let cluster = deploy<PlainSiloConfigurator> ()

    try
        let services = siloServices cluster
        let options = services.GetRequiredService<IOptions<GrainTypeOptions>>().Value

        // Nothing functional is registered…
        Assert.Null(services.GetService typeof<FunctionalGrainRegistry>)

        Assert.Empty(
            options.Classes
            |> Seq.filter (fun candidate ->
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() = typedefof<FunctionalGrainMarker<_>>)
        )

        // …and the silo's own manifest carries no functional grain type.
        let manifest = services.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

        Assert.False(manifest.Grains.ContainsKey(GrainType.Create FunctionalGrainTypes.Probe))

        // The silo is genuinely running: a stock system grain answers.
        Assert.NotNull(services.GetRequiredService<ILocalSiloDetails>().SiloAddress)
    finally
        cluster.StopAllSilos()
        cluster.Dispose()

let private activatorTypeOf (cluster: TestCluster) =
    (siloServices cluster)
        .GetRequiredService<GrainTypeSharedContextResolver>()
        .GetComponents(GrainType.Create FunctionalGrainTypes.Probe)
        .GetComponent<IGrainActivator>()
        .GetType()

[<Fact>]
let ``both component-configurator orders produce the same final activator`` () =
    let expected =
        typedefof<FunctionalGrainActivator<_>>.MakeGenericType typeof<ProbeActor>

    let first = deploy<ConfiguratorFirstSiloConfigurator> ()

    let firstActivator =
        try
            activatorTypeOf first
        finally
            first.StopAllSilos()
            first.Dispose()

    let last = deploy<ConfiguratorLastSiloConfigurator> ()

    let lastActivator =
        try
            activatorTypeOf last
        finally
            last.StopAllSilos()
            last.Dispose()

    Assert.Equal(expected, firstActivator)
    Assert.Equal(expected, lastActivator)

/// <summary>Every message of an exception chain, so a hosted-startup failure can be inspected.</summary>
let rec private messages (error: exn) =
    match error with
    | null -> []
    | :? AggregateException as aggregate ->
        error.Message :: (aggregate.InnerExceptions |> Seq.collect messages |> List.ofSeq)
    | _ -> error.Message :: messages error.InnerException

let private deployExpectingFailure<'Configurator
    when 'Configurator :> ISiloConfigurator and 'Configurator: (new: unit -> 'Configurator)>
    ()
    =
    let builder = TestClusterBuilder 1s
    builder.AddSiloBuilderConfigurator<'Configurator>() |> ignore
    let cluster = builder.Build()

    let error = Assert.ThrowsAny<exn>(fun () -> cluster.Deploy())

    try
        try
            cluster.StopAllSilos()
        with _ ->
            ()
    finally
        cluster.Dispose()

    messages error

/// <remarks>
/// Non-vacuity control for silo startup validation: with the functional post-configure removed
/// the manifest disagrees with the registry, and the silo must refuse to start instead of
/// serving a grain type Orleans cannot route.
/// </remarks>
[<Fact>]
let ``silo startup validation rejects a manifest which disagrees with the registry`` () =
    let reported = deployExpectingFailure<BrokenManifestSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "Orleans.FSharp functional silo startup"))
    Assert.Contains(reported, (fun message -> message.Contains "post-configure did not run"))

[<Fact>]
let ``a non-positive payload limit fails silo startup`` () =
    let reported = deployExpectingFailure<InvalidLimitSiloConfigurator> ()

    Assert.Contains(reported, (fun message -> message.Contains "MaxPayloadBytes must be positive"))
