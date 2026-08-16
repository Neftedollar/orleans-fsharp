/// <summary>
/// Spec 003 Phase 3, production parity for Phase-0 items 1-4 and 11: stable closed IDs, final
/// manifest removal and replacement, exact-ID reference selection from an external client, and
/// heterogeneous silo manifests.
/// </summary>
module Orleans.FSharp.Integration.FunctionalManifestIntegrationTests

open System
open System.Collections.Generic
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans
open Orleans.Configuration
open Orleans.GrainReferences
open Orleans.Metadata
open Orleans.Runtime
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalClusterFixture
open Xunit

let private markerOf (actorType: Type) =
    typedefof<FunctionalGrainMarker<_>>.MakeGenericType actorType

let private interfaceOf (actorType: Type) =
    typedefof<IFunctionalGrainTarget<_>>.MakeGenericType actorType

let private isOpenFunctional (candidate: Type) =
    candidate.IsGenericTypeDefinition
    && (candidate = typedefof<FunctionalGrainMarker<_>>
        || candidate = typedefof<IFunctionalGrainTarget<_>>)

let private functionalInterfaceId (grainType: string) =
    "orleans.fsharp.functional/" + grainType

[<Collection("FunctionalCluster")>]
type ManifestTests(fixture: FunctionalClusterFixture) =

    let primary = fixture.SiloServices FunctionalGrainTypes.PrimarySiloName

    let localManifest (services: IServiceProvider) =
        services.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

    // ── item 1: stable IDs ──────────────────────────────────────────────────

    [<Fact>]
    member _.``the closed marker and interface resolve to the explicit stable IDs``() =
        let grainTypeResolver = primary.GetRequiredService<GrainTypeResolver>()
        let interfaceResolver = primary.GetRequiredService<GrainInterfaceTypeResolver>()

        Assert.Equal<string>(
            FunctionalGrainTypes.Probe,
            grainTypeResolver.GetGrainType(markerOf typeof<ProbeActor>).ToString()
        )

        Assert.Equal<string>(
            functionalInterfaceId FunctionalGrainTypes.Probe,
            interfaceResolver.GetGrainInterfaceType(interfaceOf typeof<ProbeActor>).ToString()
        )

        // The IDs do not depend on the CLR names of the marker or the interface.
        Assert.DoesNotContain("FunctionalGrainMarker", FunctionalGrainTypes.Probe)
        Assert.DoesNotContain("IFunctionalGrainTarget", functionalInterfaceId FunctionalGrainTypes.Probe)

    // ── item 2: final-manifest removal ──────────────────────────────────────

    [<Fact>]
    member _.``the live GrainTypeOptions hold the closed types and no open functional type``() =
        let options = primary.GetRequiredService<IOptions<GrainTypeOptions>>().Value

        Assert.Empty(options.Classes |> Seq.filter isOpenFunctional)
        Assert.Empty(options.Interfaces |> Seq.filter isOpenFunctional)

        Assert.Contains(markerOf typeof<ProbeActor>, options.Classes)
        Assert.Contains(markerOf typeof<PeerActor>, options.Classes)
        Assert.Contains(interfaceOf typeof<ProbeActor>, options.Interfaces)
        Assert.Contains(interfaceOf typeof<PeerActor>, options.Interfaces)

    /// <remarks>
    /// Non-vacuity: the open generic target interface really is contributed by Orleans code
    /// generation from the C# <c>Orleans.FSharp.Abstractions</c> assembly, so the removal step
    /// has something to remove on every silo which references that assembly.
    /// </remarks>
    [<Fact>]
    member _.``Orleans code generation really contributes the open functional interface``() =
        let manifest =
            primary.GetRequiredService<IOptions<Orleans.Serialization.Configuration.TypeManifestOptions>>().Value

        Assert.Contains(typedefof<IFunctionalGrainTarget<_>>, manifest.Interfaces)

    [<Fact>]
    member _.``the final manifest has no open functional grain or interface entry``() =
        let manifest = localManifest primary
        let openMarkerName = typedefof<FunctionalGrainMarker<_>>.FullName
        let openInterfaceName = typedefof<IFunctionalGrainTarget<_>>.FullName

        for pair in manifest.Grains do
            let fullTypeName =
                match pair.Value.Properties.TryGetValue WellKnownGrainTypeProperties.FullTypeName with
                | true, value -> value
                | _ -> ""

            Assert.NotEqual<string>(openMarkerName, fullTypeName)

        for pair in manifest.Interfaces do
            Assert.NotEqual<string>(openInterfaceName, string pair.Key)

        Assert.True(manifest.Grains.ContainsKey(GrainType.Create FunctionalGrainTypes.Probe))

        Assert.True(
            manifest.Interfaces.ContainsKey(
                GrainInterfaceType.Create(functionalInterfaceId FunctionalGrainTypes.Probe)
            )
        )

    [<Fact>]
    member _.``the published interface entry carries the fixed version and default grain type``() =
        let manifest = localManifest primary

        let properties =
            manifest.Interfaces.[GrainInterfaceType.Create(functionalInterfaceId FunctionalGrainTypes.Probe)]
                .Properties

        Assert.Equal<string>("1", properties.[WellKnownGrainInterfaceProperties.Version])

        Assert.Equal<string>(
            FunctionalGrainTypes.Probe,
            properties.[WellKnownGrainInterfaceProperties.DefaultGrainType]
        )

    // ── item 3: normalized property replacement ─────────────────────────────

    [<Fact>]
    member _.``the functional properties provider runs after Orleans' implemented-interface provider``() =
        let providers = primary.GetServices<IGrainPropertiesProvider>() |> Seq.toArray

        let ourIndex =
            providers |> Array.findIndex (fun provider -> provider :? FunctionalGrainPropertiesProvider)

        let implementedIndex =
            providers
            |> Array.findIndex (fun provider -> provider.GetType().Name = "ImplementedInterfaceProvider")

        Assert.True(ourIndex > implementedIndex)

    [<Fact>]
    member _.``the normalized open-interface property is replaced by the closed ID``() =
        let markerType = markerOf typeof<ProbeActor>
        let grainType = GrainType.Create FunctionalGrainTypes.Probe
        let closedId = functionalInterfaceId FunctionalGrainTypes.Probe
        let providers = primary.GetServices<IGrainPropertiesProvider>() |> Seq.toArray

        // Before: exactly one implemented-interface entry names the functional target
        // interface, and it holds the OPEN generic definition's full name.
        let properties = Dictionary<string, string>()

        for provider in providers do
            if not (provider :? FunctionalGrainPropertiesProvider) then
                provider.Populate(markerType, grainType, properties)

        let before =
            properties
            |> Seq.filter (fun pair ->
                pair.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)
                && pair.Value = typedefof<IFunctionalGrainTarget<_>>.FullName)
            |> Seq.toArray

        Assert.Single before |> ignore
        Assert.NotEqual<string>(closedId, before.[0].Value)

        let normalizedKey = before.[0].Key

        let remindableBefore =
            properties |> Seq.filter (fun pair -> pair.Value = "Orleans.IRemindable") |> Seq.toArray

        Assert.Single remindableBefore |> ignore

        // After: the same property key holds the registered closed ID and IRemindable survives.
        (providers |> Array.find (fun provider -> provider :? FunctionalGrainPropertiesProvider))
            .Populate(markerType, grainType, properties)

        Assert.Equal<string>(closedId, properties.[normalizedKey])

        Assert.Single(properties |> Seq.filter (fun pair -> pair.Value = "Orleans.IRemindable") |> Seq.toArray)
        |> ignore

    [<Fact>]
    member _.``the live manifest publishes the closed interface ID on the grain entry``() =
        let manifest = localManifest primary
        let properties = manifest.Grains.[GrainType.Create FunctionalGrainTypes.Probe].Properties
        let closedId = functionalInterfaceId FunctionalGrainTypes.Probe

        let published =
            properties
            |> Seq.filter (fun pair ->
                pair.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)
                && pair.Value = closedId)
            |> Seq.toArray

        Assert.Single published |> ignore
        Assert.Contains(properties, (fun pair -> pair.Value = "Orleans.IRemindable"))

        // No entry still names the open generic definition.
        Assert.DoesNotContain(properties, (fun pair -> pair.Value = typedefof<IFunctionalGrainTarget<_>>.FullName))

    // ── item 4: exact-ID reference selection ────────────────────────────────

    [<Fact>]
    member _.``an external client binds the exact functional reference without a definition registry``() =
        // The client process registers only AddFunctionalGrainClient; it never sees a
        // FunctionalGrainDefinition, and the contract value alone supplies binding metadata.
        Assert.Null(fixture.Client.ServiceProvider.GetService(typeof<FunctionalGrainRegistry>))

        let reference = fixture.Probe "manifest-ref"
        Assert.Equal(ProbeId.create "manifest-ref", reference.key)

        let addressable =
            fixture.Client.GetGrain(
                GrainId.Create(GrainType.Create FunctionalGrainTypes.Probe, "manifest-ref"),
                GrainInterfaceType.Create(functionalInterfaceId FunctionalGrainTypes.Probe)
            )

        Assert.IsType<FunctionalGrainReference>(addressable, exactMatch = true) |> ignore

        let functional = addressable :?> FunctionalGrainReference
        Assert.Equal<string>(functionalInterfaceId FunctionalGrainTypes.Probe, string functional.InterfaceType)
        Assert.Equal<string>(FunctionalGrainTypes.Probe, string functional.GrainId.Type)
        Assert.Equal<string>("manifest-ref", functional.GrainId.Key.ToString())
        Assert.Equal(1, int functional.InterfaceVersion)

    [<Fact>]
    member _.``the external client's own configuration is independent of application contracts``() =
        let client = fixture.Client.ServiceProvider

        // No registry, no manifest providers, no post-configure: the client installs the fixed
        // transport only, and the contract value supplies every piece of binding metadata.
        Assert.Null(client.GetService typeof<FunctionalGrainRegistry>)

        Assert.Empty(
            client.GetServices<IGrainPropertiesProvider>()
            |> Seq.filter (fun provider -> provider :? FunctionalGrainPropertiesProvider)
        )

        Assert.Empty(
            client.GetServices<IGrainTypeProvider>()
            |> Seq.filter (fun provider -> provider :? FunctionalGrainTypeProvider)
        )

        match client.GetService typeof<IOptions<GrainTypeOptions>> with
        | :? IOptions<GrainTypeOptions> as options ->
            Assert.Empty(
                options.Value.Classes
                |> Seq.filter (fun candidate ->
                    candidate.IsConstructedGenericType
                    && candidate.GetGenericTypeDefinition() = typedefof<FunctionalGrainMarker<_>>)
            )

            Assert.Empty(
                options.Value.Interfaces
                |> Seq.filter (fun candidate ->
                    candidate.IsConstructedGenericType
                    && candidate.GetGenericTypeDefinition() = typedefof<IFunctionalGrainTarget<_>>)
            )
        | _ -> ()

    [<Fact>]
    member _.``a non-functional interface ID is declined``() =
        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                fixture.Client.GetGrain(
                    GrainId.Create(GrainType.Create FunctionalGrainTypes.Probe, "manifest-decline"),
                    GrainInterfaceType.Create "some.other.interface"
                )
                |> ignore)

        Assert.Contains("IGrainReferenceActivatorProvider", error.Message)

    [<Fact>]
    member _.``a functional prefix whose suffix differs from the grain type is declined``() =
        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                fixture.Client.GetGrain(
                    GrainId.Create(GrainType.Create FunctionalGrainTypes.Probe, "manifest-suffix"),
                    GrainInterfaceType.Create(functionalInterfaceId FunctionalGrainTypes.Peer)
                )
                |> ignore)

        Assert.Contains("IGrainReferenceActivatorProvider", error.Message)

    [<Fact>]
    member _.``the functional provider precedes every stock provider on client and silo``() =
        for services in [ fixture.Client.ServiceProvider; primary ] do
            let providers = services.GetServices<IGrainReferenceActivatorProvider>() |> Seq.toArray
            Assert.True(providers.Length > 1, "Orleans must already have registered its own providers")

            Assert.IsType<FunctionalGrainReferenceActivatorProvider>(providers.[0], exactMatch = true)
            |> ignore

            Assert.DoesNotContain(
                providers |> Array.skip 1,
                fun (provider: IGrainReferenceActivatorProvider) ->
                    provider :? FunctionalGrainReferenceActivatorProvider
            )

    // ── item 11: heterogeneous manifests ────────────────────────────────────

    [<Fact>]
    member _.``a silo publishes only its own definitions``() =
        let primaryManifest = localManifest primary
        let secondaryManifest = localManifest (fixture.SiloServices "Secondary_1")

        let hasGrain (manifest: GrainManifest) grainType =
            manifest.Grains.ContainsKey(GrainType.Create grainType)

        let hasInterface (manifest: GrainManifest) grainType =
            manifest.Interfaces.ContainsKey(GrainInterfaceType.Create(functionalInterfaceId grainType))

        Assert.True(hasGrain primaryManifest FunctionalGrainTypes.Probe)
        Assert.True(hasGrain secondaryManifest FunctionalGrainTypes.Probe)

        Assert.False(hasGrain primaryManifest FunctionalGrainTypes.Other)
        Assert.False(hasInterface primaryManifest FunctionalGrainTypes.Other)
        Assert.True(hasGrain secondaryManifest FunctionalGrainTypes.Other)
        Assert.True(hasInterface secondaryManifest FunctionalGrainTypes.Other)

    [<Fact>]
    member _.``the cluster manifest keeps per-silo grain manifests separate``() =
        let silos =
            primary.GetRequiredService<IClusterManifestProvider>().Current.Silos

        let hosting =
            silos
            |> Seq.filter (fun pair -> pair.Value.Grains.ContainsKey(GrainType.Create FunctionalGrainTypes.Other))
            |> Seq.toArray

        Assert.Equal(2, silos.Count)
        Assert.Single hosting |> ignore
