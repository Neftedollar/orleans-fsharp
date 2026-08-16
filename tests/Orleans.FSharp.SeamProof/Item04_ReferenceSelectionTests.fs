/// Phase 0 item 4 — exact-ID `GetGrain` selection of the custom `GrainReference`
/// subclass using only `IGrainFactory`.
module Orleans.FSharp.SeamProof.Item04_ReferenceSelectionTests

open System
open Orleans.Runtime
open Xunit

[<Collection("SeamCluster")>]
type ReferenceSelectionTests(fixture: SeamClusterFixture) =

    [<Fact>]
    member _.``exact functional ID returns FunctionalGrainReference from the external client``() =
        let addressable =
            SeamClient.reference fixture.Client SeamGrainTypes.Probe "ref-1"

        Assert.IsType<FunctionalGrainReference>(addressable, exactMatch = true) |> ignore

        let reference = addressable :?> FunctionalGrainReference
        Assert.Equal(FunctionalIds.interfaceId SeamGrainTypes.Probe, string reference.InterfaceType)
        Assert.Equal(SeamGrainTypes.Probe, string reference.GrainId.Type)
        Assert.Equal("ref-1", reference.GrainId.Key.ToString())
        Assert.Equal(int FunctionalIds.InterfaceVersion, int reference.InterfaceVersion)

    [<Fact>]
    member _.``a non-functional interface ID is declined``() =
        // Declining means no provider claims the ID at all — there is no
        // generated stock reference for an unknown interface, so Orleans throws.
        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                fixture.Client.GetGrain(
                    GrainId.Create(GrainType.Create SeamGrainTypes.Probe, "ref-2"),
                    GrainInterfaceType.Create "some.other.interface"
                )
                |> ignore)

        Assert.Contains("IGrainReferenceActivatorProvider", error.Message)

    [<Fact>]
    member _.``a functional prefix whose suffix differs from the grain type is declined``() =
        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                fixture.Client.GetGrain(
                    GrainId.Create(GrainType.Create SeamGrainTypes.Probe, "ref-3"),
                    FunctionalIds.grainInterfaceType SeamGrainTypes.Peer
                )
                |> ignore)

        Assert.Contains("IGrainReferenceActivatorProvider", error.Message)

    [<Fact>]
    member _.``the client can call the target through the custom reference``() =
        task {
            let! reply = fixture.Call SeamGrainTypes.Probe "ref-4" "echo" "hello-seam"
            Assert.Equal("hello-seam", reply)
        }
