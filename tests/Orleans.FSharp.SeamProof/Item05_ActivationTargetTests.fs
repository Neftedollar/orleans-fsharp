/// Phase 0 item 5 — invocation of a custom-activated target whose CLR type
/// differs from the marker and whose `IGrainBase.GrainContext` is the supplied
/// context.
module Orleans.FSharp.SeamProof.Item05_ActivationTargetTests

open System
open Microsoft.Extensions.DependencyInjection
open Orleans.Runtime
open Xunit

let internal parse (reply: string) =
    reply.Split '|'
    |> Array.map (fun part ->
        let i = part.IndexOf '='
        part.Substring(0, i), part.Substring(i + 1))
    |> Map.ofArray

[<Collection("SeamCluster")>]
type ActivationTargetTests(fixture: SeamClusterFixture) =

    [<Fact>]
    member _.``the invoked target is not the marker and holds the supplied context``() =
        task {
            let! reply = fixture.Call SeamGrainTypes.Probe "act-1" "identity" ""
            let fields = parse reply

            // The marker's DispatchAsync throws, so a successful reply already
            // proves the marker instance was not the one invoked.
            Assert.NotEqual<string>(fields["marker"], fields["type"])
            Assert.DoesNotContain("FunctionalGrainMarker", fields["type"])
            Assert.Equal("True", fields["ctx"])
            Assert.Equal($"{SeamGrainTypes.Probe}/act-1", fields["grain"])
        }

    [<Fact>]
    member _.``the functional activator is installed only for registered grain types``() =
        let resolver =
            (fixture.SiloServices "Primary").GetRequiredService<GrainTypeSharedContextResolver>()

        let functionalActivator =
            resolver
                .GetComponents(GrainType.Create SeamGrainTypes.Probe)
                .GetComponent<IGrainActivator>()

        Assert.Equal(
            typedefof<SeamGrainActivator<_>>.MakeGenericType(typeof<ProbeActor>),
            functionalActivator.GetType()
        )

        let stockActivator =
            resolver
                .GetComponents(GrainType.Create "management")
                .GetComponent<IGrainActivator>()

        Assert.False(
            stockActivator.GetType().IsGenericType
            && stockActivator.GetType().GetGenericTypeDefinition() = typedefof<SeamGrainActivator<_>>
        )

    [<Fact>]
    member _.``the live activation uses a SeamGrainActivator-produced target``() =
        task {
            CallCapture.clear ()
            let! _ = fixture.Call SeamGrainTypes.Probe "act-2" "echo" "x"

            let captured =
                CallCapture.incoming
                |> Seq.filter (fun c -> c.MetadataOperationId = "echo")
                |> Seq.toArray

            Assert.NotEmpty captured
            Assert.DoesNotContain("FunctionalGrainMarker", captured[0].GrainInstanceType)
        }

    [<Fact>]
    member _.``a marker instance refuses to serve a call``() =
        let marker = FunctionalGrainMarker<ProbeActor>() :> IFunctionalGrainTarget<ProbeActor>

        let error =
            Assert.Throws<Exception>(fun () ->
                marker.DispatchAsync(
                    FunctionalRequestEnvelope("x", 1, "echo", Array.zeroCreate 32, 0uy, [||]),
                    Threading.CancellationToken.None
                )
                |> ignore)

        Assert.Contains("FunctionalGrainMarker", error.Message)
