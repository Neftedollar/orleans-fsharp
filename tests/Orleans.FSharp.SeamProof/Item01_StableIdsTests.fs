/// Phase 0 item 1 — stable IDs for the closed marker and closed target
/// interface types.
module Orleans.FSharp.SeamProof.Item01_StableIdsTests

open Microsoft.Extensions.DependencyInjection
open Orleans.Metadata
open Orleans.Runtime
open Xunit

[<Collection("SeamCluster")>]
type StableIdTests(fixture: SeamClusterFixture) =

    let markerOf (actorType: System.Type) =
        typedefof<FunctionalGrainMarker<_>>.MakeGenericType actorType

    let interfaceOf (actorType: System.Type) =
        typedefof<IFunctionalGrainTarget<_>>.MakeGenericType actorType

    let grainTypeIn siloName (t: System.Type) =
        (fixture.SiloServices siloName)
            .GetRequiredService<GrainTypeResolver>()
            .GetGrainType t
        |> string

    let interfaceIdIn siloName (t: System.Type) =
        (fixture.SiloServices siloName)
            .GetRequiredService<GrainInterfaceTypeResolver>()
            .GetGrainInterfaceType t
        |> string

    [<Fact>]
    member _.``closed marker resolves to the explicit grain type``() =
        Assert.Equal(SeamGrainTypes.Probe, grainTypeIn "Primary" (markerOf typeof<ProbeActor>))
        Assert.Equal(SeamGrainTypes.Peer, grainTypeIn "Primary" (markerOf typeof<PeerActor>))

    [<Fact>]
    member _.``closed interface resolves to the stable functional interface ID``() =
        Assert.Equal(
            FunctionalIds.interfaceId SeamGrainTypes.Probe,
            interfaceIdIn "Primary" (interfaceOf typeof<ProbeActor>)
        )

        Assert.Equal(
            FunctionalIds.interfaceId SeamGrainTypes.Peer,
            interfaceIdIn "Primary" (interfaceOf typeof<PeerActor>)
        )

    [<Fact>]
    member _.``both silos resolve the same IDs from independent service providers``() =
        let marker = markerOf typeof<ProbeActor>
        let iface = interfaceOf typeof<ProbeActor>

        Assert.Equal(grainTypeIn "Primary" marker, grainTypeIn "Secondary_1" marker)
        Assert.Equal(interfaceIdIn "Primary" iface, interfaceIdIn "Secondary_1" iface)

    [<Fact>]
    member _.``IDs carry no CLR, module or actor-brand names``() =
        let grainType = grainTypeIn "Primary" (markerOf typeof<ProbeActor>)
        let interfaceId = interfaceIdIn "Primary" (interfaceOf typeof<ProbeActor>)

        for id in [ grainType; interfaceId ] do
            Assert.DoesNotContain("FunctionalGrainMarker", id)
            Assert.DoesNotContain("IFunctionalGrainTarget", id)
            Assert.DoesNotContain("ProbeActor", id)
            Assert.DoesNotContain("Orleans.FSharp.SeamProof", id)

    [<Fact>]
    member _.``distinct actor brands produce distinct IDs``() =
        Assert.NotEqual<string>(
            grainTypeIn "Primary" (markerOf typeof<ProbeActor>),
            grainTypeIn "Primary" (markerOf typeof<PeerActor>)
        )

        Assert.NotEqual<string>(
            interfaceIdIn "Primary" (interfaceOf typeof<ProbeActor>),
            interfaceIdIn "Primary" (interfaceOf typeof<PeerActor>)
        )

    [<Fact>]
    member _.``the published interface properties carry version 1 and the default grain type``() =
        let manifest =
            (fixture.SiloServices "Primary")
                .GetRequiredService<IClusterManifestProvider>()
                .LocalGrainManifest

        let properties =
            manifest.Interfaces[GrainInterfaceType.Create(FunctionalIds.interfaceId SeamGrainTypes.Probe)]
                .Properties

        Assert.Equal("1", properties[WellKnownGrainInterfaceProperties.Version])
        Assert.Equal(SeamGrainTypes.Probe, properties[WellKnownGrainInterfaceProperties.DefaultGrainType])
