/// Phase 0 item 6 — valid global-filter interface and implementation method
/// metadata on both sides of the call.
module Orleans.FSharp.SeamProof.Item06_CallFilterMetadataTests

open System.Threading.Tasks
open Xunit

[<Collection("SeamCluster")>]
type CallFilterMetadataTests(fixture: SeamClusterFixture) =

    let closedInterfaceName =
        (typedefof<IFunctionalGrainTarget<_>>.MakeGenericType typeof<ProbeActor>).FullName

    [<Fact>]
    member _.``the incoming filter sees interface and implementation method metadata``() =
        task {
            CallCapture.clear ()
            let! _ = fixture.Call SeamGrainTypes.Probe "filter-1" "readSlow" "0"

            let captured =
                CallCapture.incoming
                |> Seq.filter (fun c -> c.MetadataOperationId = "readSlow")
                |> Seq.head

            Assert.Equal("DispatchAsync", captured.MethodName)
            Assert.Equal("DispatchAsync", captured.InterfaceMethodName)
            // F# implements interfaces explicitly, so the implementation method
            // carries the mangled explicit-interface name.
            Assert.EndsWith("DispatchAsync", captured.ImplementationMethodName)
            Assert.NotEqual<string>("<null>", captured.ImplementationMethodName)
            Assert.Equal(closedInterfaceName, captured.InterfaceName)
            Assert.Equal("IFunctionalGrainTarget`1", captured.InterfaceTypeName)
            Assert.Equal($"{closedInterfaceName}/DispatchAsync", captured.ActivityName)
            Assert.NotEqual<string>("<null>", captured.ImplementationDeclaringType)
            Assert.Equal(2, captured.ArgumentCount)
            Assert.True captured.Argument1IsToken
        }

    [<Fact>]
    member _.``the incoming filter reads the public functional request metadata``() =
        task {
            CallCapture.clear ()
            let! _ = fixture.Call SeamGrainTypes.Probe "filter-2" "readSlow" "0"

            let captured =
                CallCapture.incoming
                |> Seq.filter (fun c -> c.MetadataOperationId = "readSlow")
                |> Seq.head

            Assert.Equal(SeamGrainTypes.Probe, captured.MetadataGrainType)
            Assert.Equal(1, captured.MetadataVersion)
            Assert.True captured.MetadataReadOnly
            Assert.False captured.MetadataOneWay
            Assert.False captured.MetadataAlwaysInterleave
            Assert.True(captured.MetadataPayloadLength > 0)
        }

    [<Fact>]
    member _.``policy flags reach the filter for every declared combination``() =
        task {
            CallCapture.clear ()
            let! _ = fixture.Call SeamGrainTypes.Probe "filter-3" "echo" "a"
            let! _ = fixture.Call SeamGrainTypes.Probe "filter-3" "peekInterleave" ""
            fixture.OneWay SeamGrainTypes.Probe "filter-3" "bump" "0"

            // Give the one-way message time to be delivered.
            do! Task.Delay 500

            let byOperation =
                CallCapture.incoming
                |> Seq.map (fun c -> c.MetadataOperationId, c)
                |> Map.ofSeq

            let echo = byOperation["echo"]
            Assert.False echo.MetadataReadOnly
            Assert.False echo.MetadataOneWay
            Assert.False echo.MetadataAlwaysInterleave

            let interleave = byOperation["peekInterleave"]
            Assert.True interleave.MetadataAlwaysInterleave

            let bump = byOperation["bump"]
            Assert.True bump.MetadataOneWay
            Assert.True bump.MetadataAlwaysInterleave
        }

    [<Fact>]
    member _.``the outgoing filter on the client sees the closed interface metadata``() =
        task {
            CallCapture.clear ()
            let! _ = fixture.Call SeamGrainTypes.Probe "filter-4" "echo" "b"

            let captured =
                CallCapture.outgoing
                |> Seq.filter (fun c -> c.MetadataOperationId = "echo")
                |> Seq.head

            Assert.Equal("DispatchAsync", captured.MethodName)
            Assert.Equal("DispatchAsync", captured.InterfaceMethodName)
            Assert.Equal(closedInterfaceName, captured.InterfaceName)
            Assert.Equal(SeamGrainTypes.Probe, captured.MetadataGrainType)
            Assert.Equal(2, captured.ArgumentCount)
        }
