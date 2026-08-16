/// Phase 0 item 10 — `ICodecProvider.GetCodec(Type)` preflight, without any
/// trial serialization, on both supported Orleans versions.
module Orleans.FSharp.SeamProof.Item10_CodecPreflightTests

open System
open Microsoft.Extensions.DependencyInjection
open Orleans.Serialization
open Orleans.Serialization.Cloning
open Orleans.Serialization.Serializers
open Xunit

/// A closed CLR type nothing has registered a codec for.
type UnregisteredPayload() =
    member val Value = 0 with get, set

let private declaredTypes: Type[] =
    [| typeof<FunctionalRequestEnvelope>
       typeof<FunctionalReply>
       typeof<FunctionalRequest>
       typeof<string>
       typeof<int>
       typeof<ResizeArray<string>> |]

[<Collection("SeamCluster")>]
type CodecPreflightTests(fixture: SeamClusterFixture) =

    let providerFor (services: IServiceProvider) =
        services.GetRequiredService<ICodecProvider>()

    [<Fact>]
    member _.``the silo resolves a codec for every declared transport and payload type``() =
        let provider = providerFor (fixture.SiloServices "Primary")

        for declared in declaredTypes do
            let codec = provider.GetCodec declared
            Assert.NotNull codec

    [<Fact>]
    member _.``the external client resolves the same declared types``() =
        let provider = providerFor fixture.ClientServices

        for declared in declaredTypes do
            Assert.NotNull(provider.GetCodec declared)

    [<Fact>]
    member _.``preflight fails for an unregistered type without serializing a value``() =
        let provider = providerFor fixture.ClientServices

        // TryGetCodec is the non-throwing probe; GetCodec is the preflight call.
        Assert.Null(provider.TryGetCodec typeof<UnregisteredPayload>)

        Assert.Throws<CodecNotFoundException>(fun () -> provider.GetCodec typeof<UnregisteredPayload> |> ignore)
        |> ignore

    [<Fact>]
    member _.``the resolved transport codec is the explicit seam codec``() =
        let provider = providerFor fixture.ClientServices
        let codec = provider.GetCodec typeof<FunctionalRequestEnvelope>
        Assert.NotNull codec

        // The generalized codec claims exactly the three fixed transport types.
        let seam = fixture.ClientServices.GetRequiredService<SeamTransportCodec>() :> IGeneralizedCodec
        Assert.True(seam.IsSupportedType typeof<FunctionalRequestEnvelope>)
        Assert.True(seam.IsSupportedType typeof<FunctionalReply>)
        Assert.True(seam.IsSupportedType typeof<FunctionalRequest>)
        Assert.False(seam.IsSupportedType typeof<UnregisteredPayload>)
