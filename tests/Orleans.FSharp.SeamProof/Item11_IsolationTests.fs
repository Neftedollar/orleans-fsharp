/// Phase 0 item 11 — isolation of equal keys under different grain types, plus
/// heterogeneous silo manifests (a silo publishes only its own definitions).
module Orleans.FSharp.SeamProof.Item11_IsolationTests

open System
open Microsoft.Extensions.DependencyInjection
open Orleans.Metadata
open Orleans.Runtime
open Xunit

let private field (reply: string) (name: string) =
    reply.Split '|'
    |> Array.pick (fun part ->
        if part.StartsWith(name + "=", StringComparison.Ordinal) then
            Some(part.Substring(name.Length + 1))
        else
            None)

[<Collection("SeamCluster")>]
type IsolationTests(fixture: SeamClusterFixture) =

    let manifestOf siloName =
        (fixture.SiloServices siloName)
            .GetRequiredService<IClusterManifestProvider>()
            .LocalGrainManifest

    [<Fact>]
    member _.``equal keys under different grain types are distinct activations``() =
        task {
            let key = $"shared-key-{Guid.NewGuid():N}"

            let! _ = fixture.Call SeamGrainTypes.Probe key "stateWrite" "from-probe"
            let! _ = fixture.Call SeamGrainTypes.Other key "stateWrite" "from-other"

            let! probeValue = fixture.Call SeamGrainTypes.Probe key "stateRead" ""
            let! otherValue = fixture.Call SeamGrainTypes.Other key "stateRead" ""

            Assert.Equal("from-probe", probeValue)
            Assert.Equal("from-other", otherValue)

            let! probeIdentity = fixture.Call SeamGrainTypes.Probe key "identity" ""
            let! otherIdentity = fixture.Call SeamGrainTypes.Other key "identity" ""

            Assert.Equal($"{SeamGrainTypes.Probe}/{key}", field probeIdentity "grain")
            Assert.Equal($"{SeamGrainTypes.Other}/{key}", field otherIdentity "grain")
        }

    [<Fact>]
    member _.``equal keys under different grain types produce distinct GrainIds and references``() =
        let key = "id-isolation"
        let probe = SeamClient.functionalReference fixture.Client SeamGrainTypes.Probe key
        let other = SeamClient.functionalReference fixture.Client SeamGrainTypes.Other key

        Assert.NotEqual(probe.GrainId, other.GrainId)
        Assert.Equal(probe.GrainId.Key.ToString(), other.GrainId.Key.ToString())
        Assert.NotEqual(probe.InterfaceType, other.InterfaceType)
        Assert.NotEqual<GrainReference>(probe, other)

    [<Fact>]
    member _.``a silo publishes only its own definitions``() =
        let primary = manifestOf "Primary"
        let secondary = manifestOf "Secondary_1"

        let hasGrain (manifest: GrainManifest) grainType =
            manifest.Grains.ContainsKey(GrainType.Create grainType)

        let hasInterface (manifest: GrainManifest) grainType =
            manifest.Interfaces.ContainsKey(GrainInterfaceType.Create(FunctionalIds.interfaceId grainType))

        // Both silos host probe and peer.
        Assert.True(hasGrain primary SeamGrainTypes.Probe)
        Assert.True(hasGrain secondary SeamGrainTypes.Probe)
        Assert.True(hasInterface primary SeamGrainTypes.Probe)
        Assert.True(hasInterface secondary SeamGrainTypes.Probe)

        // Only the secondary hosts "seam.other".
        Assert.False(hasGrain primary SeamGrainTypes.Other)
        Assert.False(hasInterface primary SeamGrainTypes.Other)
        Assert.True(hasGrain secondary SeamGrainTypes.Other)
        Assert.True(hasInterface secondary SeamGrainTypes.Other)

    [<Fact>]
    member _.``the cluster manifest keeps per-silo grain manifests separate``() =
        let manifests =
            (fixture.SiloServices "Primary")
                .GetRequiredService<IClusterManifestProvider>()
                .Current.Silos

        let hosting =
            manifests
            |> Seq.filter (fun kv -> kv.Value.Grains.ContainsKey(GrainType.Create SeamGrainTypes.Other))
            |> Seq.toArray

        Assert.Equal(2, manifests.Count)
        Assert.Single hosting |> ignore

    [<Fact>]
    member _.``a definition hosted on one silo only is still reachable and runs there``() =
        task {
            let key = $"hetero-{Guid.NewGuid():N}"
            let! identity = fixture.Call SeamGrainTypes.Other key "identity" ""
            let silo = field identity "silo"

            let secondaryLocal =
                (fixture.SiloServices "Secondary_1")
                    .GetRequiredService<ILocalSiloDetails>()
                    .SiloAddress
                |> string

            Assert.Equal(secondaryLocal, silo)
        }
