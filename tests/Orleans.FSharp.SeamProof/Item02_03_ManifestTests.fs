/// Phase 0 items 2 and 3 — final-manifest removal of the open functional
/// marker/interface entries, and replacement of the Orleans-normalized
/// open-interface property with the registered closed ID.
module Orleans.FSharp.SeamProof.Item02_03_ManifestTests

open System
open System.Collections.Generic
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans.Configuration
open Orleans.Metadata
open Orleans.Runtime
open Xunit

let private markerOf (actorType: Type) =
    typedefof<FunctionalGrainMarker<_>>.MakeGenericType actorType

let private interfaceOf (actorType: Type) =
    typedefof<IFunctionalGrainTarget<_>>.MakeGenericType actorType

let private isOpenFunctional (t: Type) =
    t.IsGenericTypeDefinition
    && (t = typedefof<FunctionalGrainMarker<_>> || t = typedefof<IFunctionalGrainTarget<_>>)

[<Collection("SeamCluster")>]
type ManifestTests(fixture: SeamClusterFixture) =

    let primary = fixture.SiloServices "Primary"

    let grainTypeOptions () =
        primary.GetRequiredService<IOptions<GrainTypeOptions>>().Value

    let localManifest () =
        primary.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

    // ── item 2 ──────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``PostConfigure removes open functional entries and adds only closed ones``() =
        // Reproduces what an Orleans-codegen assembly contributes: the open
        // generic marker and target interface are discovered by default.
        let registry = SeamRegistry()
        registry.Add(SeamDefinition.create<ProbeActor> SeamGrainTypes.Probe)
        registry.Add(SeamDefinition.create<PeerActor> SeamGrainTypes.Peer)

        let options = GrainTypeOptions()
        options.Classes.Add typedefof<FunctionalGrainMarker<_>> |> ignore
        options.Classes.Add typeof<ManifestTests> |> ignore
        options.Interfaces.Add typedefof<IFunctionalGrainTarget<_>> |> ignore
        options.Interfaces.Add typeof<IDisposable> |> ignore

        let postConfigure =
            SeamGrainTypeOptionsPostConfigure registry :> IPostConfigureOptions<GrainTypeOptions>

        postConfigure.PostConfigure(Options.DefaultName, options)

        Assert.DoesNotContain(typedefof<FunctionalGrainMarker<_>>, options.Classes)
        Assert.DoesNotContain(typedefof<IFunctionalGrainTarget<_>>, options.Interfaces)

        Assert.Contains(markerOf typeof<ProbeActor>, options.Classes)
        Assert.Contains(markerOf typeof<PeerActor>, options.Classes)
        Assert.Contains(interfaceOf typeof<ProbeActor>, options.Interfaces)
        Assert.Contains(interfaceOf typeof<PeerActor>, options.Interfaces)

        // Unrelated entries survive untouched.
        Assert.Contains(typeof<ManifestTests>, options.Classes)
        Assert.Contains(typeof<IDisposable>, options.Interfaces)

    [<Fact>]
    member _.``a frozen registry rejects later registration``() =
        let registry = SeamRegistry()
        registry.Add(SeamDefinition.create<ProbeActor> SeamGrainTypes.Probe)
        registry.Freeze() |> ignore

        Assert.Throws<InvalidOperationException>(fun () ->
            registry.Add(SeamDefinition.create<PeerActor> SeamGrainTypes.Peer))
        |> ignore

    [<Fact>]
    member _.``the live silo's GrainTypeOptions hold the closed types and no open functional type``() =
        let options = grainTypeOptions ()

        Assert.Empty(options.Classes |> Seq.filter isOpenFunctional)
        Assert.Empty(options.Interfaces |> Seq.filter isOpenFunctional)

        Assert.Contains(markerOf typeof<ProbeActor>, options.Classes)
        Assert.Contains(interfaceOf typeof<ProbeActor>, options.Interfaces)

    [<Fact>]
    member _.``the final manifest has no open functional grain or interface entry``() =
        let manifest = localManifest ()

        let openMarkerName = typedefof<FunctionalGrainMarker<_>>.FullName
        let openInterfaceName = typedefof<IFunctionalGrainTarget<_>>.FullName

        // No grain type resolves to the bare open generic type name…
        for kv in manifest.Grains do
            let fullTypeName =
                match kv.Value.Properties.TryGetValue WellKnownGrainTypeProperties.FullTypeName with
                | true, v -> v
                | _ -> ""

            Assert.NotEqual<string>(openMarkerName, fullTypeName)

        // …and no interface entry is keyed by the open generic interface ID.
        for kv in manifest.Interfaces do
            Assert.NotEqual<string>(openInterfaceName, string kv.Key)

        // The closed entries are present.
        Assert.True(manifest.Grains.ContainsKey(GrainType.Create SeamGrainTypes.Probe))

        Assert.True(
            manifest.Interfaces.ContainsKey(
                GrainInterfaceType.Create(FunctionalIds.interfaceId SeamGrainTypes.Probe)
            )
        )

    // ── item 3 ──────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``the normalized open-interface property is replaced by the closed ID``() =
        let markerType = markerOf typeof<ProbeActor>
        let grainType = GrainType.Create SeamGrainTypes.Probe
        let closedId = FunctionalIds.interfaceId SeamGrainTypes.Probe

        let providers = primary.GetServices<IGrainPropertiesProvider>() |> Seq.toArray

        // Ours must be appended after Orleans' ImplementedInterfaceProvider.
        let ourIndex = providers |> Array.findIndex (fun p -> p :? SeamGrainPropertiesProvider)

        let implementedIndex =
            providers |> Array.findIndex (fun p -> p.GetType().Name = "ImplementedInterfaceProvider")

        Assert.True(ourIndex > implementedIndex)

        // Before: exactly one functional implemented-interface entry, holding the
        // value Orleans normalized from the closed interface to the OPEN generic ID.
        let properties = Dictionary<string, string>()

        for provider in providers do
            if not (provider :? SeamGrainPropertiesProvider) then
                provider.Populate(markerType, grainType, properties)

        let functionalBefore =
            properties
            |> Seq.filter (fun kv ->
                kv.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)
                && SeamGrainPropertiesProvider.IsFunctionalInterfaceValue kv.Value)
            |> Seq.toArray

        Assert.Single functionalBefore |> ignore
        Assert.Equal(typedefof<IFunctionalGrainTarget<_>>.FullName, functionalBefore[0].Value)
        Assert.NotEqual<string>(closedId, functionalBefore[0].Value)

        let normalizedKey = functionalBefore[0].Key

        // IRemindable and every other property must survive the replacement.
        let remindableEntries =
            properties
            |> Seq.filter (fun kv -> kv.Value = "Orleans.IRemindable")
            |> Seq.toArray

        Assert.Single remindableEntries |> ignore

        // After: the same property key now holds the registered closed ID.
        (providers |> Array.find (fun p -> p :? SeamGrainPropertiesProvider))
            .Populate(markerType, grainType, properties)

        Assert.Equal(closedId, properties[normalizedKey])

        Assert.Single(
            properties
            |> Seq.filter (fun kv -> kv.Value = "Orleans.IRemindable")
            |> Seq.toArray
        )
        |> ignore

    [<Fact>]
    member _.``the live manifest publishes the closed interface ID on the grain entry``() =
        let manifest = localManifest ()
        let properties = manifest.Grains[GrainType.Create SeamGrainTypes.Probe].Properties

        let functional =
            properties
            |> Seq.filter (fun kv ->
                kv.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)
                && SeamGrainPropertiesProvider.IsFunctionalInterfaceValue kv.Value)
            |> Seq.toArray

        Assert.Single functional |> ignore
        Assert.Equal(FunctionalIds.interfaceId SeamGrainTypes.Probe, functional[0].Value)

        // IRemindable survived in the published manifest too.
        Assert.Contains(properties, (fun kv -> kv.Value = "Orleans.IRemindable"))

    [<Fact>]
    member _.``zero or multiple normalized functional entries fail startup``() =
        let registry = SeamRegistry()
        registry.Add(SeamDefinition.create<ProbeActor> SeamGrainTypes.Probe)

        let provider = SeamGrainPropertiesProvider registry :> IGrainPropertiesProvider
        let markerType = markerOf typeof<ProbeActor>
        let grainType = GrainType.Create SeamGrainTypes.Probe

        let none = Dictionary<string, string>()
        none["interface.0"] <- "Orleans.IRemindable"
        Assert.Throws<InvalidOperationException>(fun () -> provider.Populate(markerType, grainType, none))
        |> ignore

        let two = Dictionary<string, string>()
        two["interface.0"] <- typedefof<IFunctionalGrainTarget<_>>.FullName
        two["interface.1"] <- FunctionalIds.interfaceId SeamGrainTypes.Probe
        Assert.Throws<InvalidOperationException>(fun () -> provider.Populate(markerType, grainType, two))
        |> ignore
