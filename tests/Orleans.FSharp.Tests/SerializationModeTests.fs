module Orleans.FSharp.Tests.SerializationModeTests

open System
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.Serialization
open Orleans.Serialization.Cloning
open Orleans.Serialization.Serializers

// ---------------------------------------------------------------------------
// Explicit generalized-serialization policy — CE keyword tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE has no explicit FSharp serialization by default`` () =
    let config = siloConfig { () }
    test <@ config.FSharpSerialization = None @>

[<Fact>]
let ``clientConfig CE has no explicit FSharp serialization by default`` () =
    let config = clientConfig { () }
    test <@ config.FSharpSerialization = None @>

[<Fact>]
let ``siloConfig CE selects FSharp JSON as the primary generalized serializer`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            useFSharpJsonSerialization
            addMemoryStorage "Default"
        }

    test <@ config.FSharpSerialization = Some FSharpSerialization.Json @>
    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>

[<Fact>]
let ``clientConfig CE selects FSharp JSON as the primary generalized serializer`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            useFSharpJsonSerialization
        }

    test <@ config.FSharpSerialization = Some FSharpSerialization.Json @>
    test <@ config.ClusteringMode.IsSome @>

[<Fact>]
let ``binary and JSON compose only as an explicit unsupported type policy`` () =
    let policy =
        FSharpSerialization.Binary
        |> FSharpSerialization.forUnsupportedTypes FSharpSerialization.Json

    let silo = siloConfig { useFSharpSerialization policy }
    let client = clientConfig { useFSharpSerialization policy }

    test <@ policy = FSharpSerialization.BinaryWithJsonForUnsupportedTypes @>
    test <@ silo.FSharpSerialization = Some policy @>
    test <@ client.FSharpSerialization = Some policy @>

[<Fact>]
let ``repeating the same serialization policy is idempotent`` () =
    let silo =
        siloConfig {
            useFSharpJsonSerialization
            useFSharpJsonSerialization
        }

    let client =
        clientConfig {
            useFSharpBinarySerialization
            useFSharpBinarySerialization
        }

    test <@ silo.FSharpSerialization = Some FSharpSerialization.Json @>
    test <@ client.FSharpSerialization = Some FSharpSerialization.Binary @>

[<Fact>]
let ``conflicting serialization policies fail instead of depending on registration order`` () =
    Assert.Throws<InvalidOperationException>(fun () ->
        siloConfig {
            useFSharpBinarySerialization
            useFSharpJsonSerialization
        }
        |> ignore)
    |> ignore

    Assert.Throws<InvalidOperationException>(fun () ->
        clientConfig {
            useFSharpJsonSerialization
            useFSharpBinarySerialization
        }
        |> ignore)
    |> ignore

[<Fact>]
let ``unsupported type composition rejects unreachable policies`` () =
    Assert.Throws<ArgumentException>(fun () ->
        FSharpSerialization.Json
        |> FSharpSerialization.forUnsupportedTypes FSharpSerialization.Binary
        |> ignore)
    |> ignore

    Assert.Throws<ArgumentException>(fun () ->
        FSharpSerialization.Binary
        |> FSharpSerialization.forUnsupportedTypes FSharpSerialization.Binary
        |> ignore)
    |> ignore

// ---------------------------------------------------------------------------
// Mode 2: Auto ([GenerateSerializer] only) — verify attribute presence
// ---------------------------------------------------------------------------

[<Fact>]
let ``GenerateSerializer attribute exists in Orleans namespace`` () =
    let attr = typeof<Orleans.GenerateSerializerAttribute>
    test <@ attr <> null @>

[<Fact>]
let ``Id attribute exists in Orleans namespace`` () =
    let attr = typeof<Orleans.IdAttribute>
    test <@ attr <> null @>

// ---------------------------------------------------------------------------
// Mode 3: Explicit ([GenerateSerializer] + [Id]) — sample types compile
// ---------------------------------------------------------------------------

/// <summary>
/// Sample DU using Mode 3 (Explicit): [GenerateSerializer] + [Id] on each case.
/// This type is used to verify the attribute combination compiles.
/// </summary>
[<Orleans.GenerateSerializer>]
type ExplicitCommand =
    | [<Orleans.Id(0u)>] DoThis
    | [<Orleans.Id(1u)>] DoThat of value: int
    | [<Orleans.Id(2u)>] DoOther of name: string * count: int

[<Fact>]
let ``Explicit mode DU has GenerateSerializer attribute`` () =
    let hasAttr =
        typeof<ExplicitCommand>.GetCustomAttributes(typeof<Orleans.GenerateSerializerAttribute>, false)
        |> Array.isEmpty
        |> not

    test <@ hasAttr @>

[<Fact>]
let ``Explicit mode DU cases compile with Id attributes`` () =
    // Verify all three cases construct without error
    let cmd1 = DoThis
    let cmd2 = DoThat 42
    let cmd3 = DoOther("test", 5)
    test <@ cmd1 = DoThis @>
    test <@ cmd2 = DoThat 42 @>
    test <@ cmd3 = DoOther("test", 5) @>

// ---------------------------------------------------------------------------
// Mode 2: Auto — verify [GenerateSerializer] without [Id] compiles
// ---------------------------------------------------------------------------

/// <summary>
/// Sample DU using Mode 2 (Auto): only [GenerateSerializer], no [Id] attributes.
/// Orleans auto-assigns ordinal IDs based on member order.
/// </summary>
[<Orleans.GenerateSerializer>]
type AutoCommand =
    | Start
    | Stop
    | Pause of duration: int

[<Fact>]
let ``Auto mode DU has GenerateSerializer attribute`` () =
    let hasAttr =
        typeof<AutoCommand>.GetCustomAttributes(typeof<Orleans.GenerateSerializerAttribute>, false)
        |> Array.isEmpty
        |> not

    test <@ hasAttr @>

[<Fact>]
let ``Auto mode DU cases construct without Id attributes`` () =
    let cmd1 = Start
    let cmd2 = Stop
    let cmd3 = Pause 30
    test <@ cmd1 = Start @>
    test <@ cmd2 = Stop @>
    test <@ cmd3 = Pause 30 @>

// ---------------------------------------------------------------------------
// Mode 1: Clean — plain DU (no attributes) compiles
// ---------------------------------------------------------------------------

/// <summary>
/// Sample DU using Mode 1 (Clean): no Orleans attributes at all.
/// Can use the explicit F# JSON generalized policy for grain boundary crossing.
/// </summary>
type CleanCommand =
    | Activate
    | Deactivate
    | SetLevel of level: int

[<Fact>]
let ``Clean mode DU has no GenerateSerializer attribute`` () =
    let hasAttr =
        typeof<CleanCommand>.GetCustomAttributes(typeof<Orleans.GenerateSerializerAttribute>, false)
        |> Array.isEmpty

    test <@ hasAttr @>

[<Fact>]
let ``Clean mode DU cases construct without any attributes`` () =
    let cmd1 = Activate
    let cmd2 = Deactivate
    let cmd3 = SetLevel 5
    test <@ cmd1 = Activate @>
    test <@ cmd2 = Deactivate @>
    test <@ cmd3 = SetLevel 5 @>

// ---------------------------------------------------------------------------
// F# JSON roundtrip (in-process, no Orleans silo)
// ---------------------------------------------------------------------------

[<Fact>]
let ``Clean mode DU roundtrips through FSharpJson serializer`` () =
    let options = Orleans.FSharp.FSharpJson.serializerOptions
    let original = SetLevel 42
    let json = System.Text.Json.JsonSerializer.Serialize(original, options)
    let deserialized = System.Text.Json.JsonSerializer.Deserialize<CleanCommand>(json, options)
    test <@ deserialized = original @>

[<Fact>]
let ``Clean mode DU fieldless case roundtrips through FSharpJson serializer`` () =
    let options = Orleans.FSharp.FSharpJson.serializerOptions
    let original = Activate
    let json = System.Text.Json.JsonSerializer.Serialize(original, options)
    let deserialized = System.Text.Json.JsonSerializer.Deserialize<CleanCommand>(json, options)
    test <@ deserialized = original @>

/// <summary>
/// Sample record using Mode 1 (Clean): no Orleans attributes.
/// </summary>
type CleanRecord =
    { Name: string
      Value: int option
      Tags: string list }

let private buildSerializationServices policy =
    let services = ServiceCollection()
    FSharpSerializationRegistration.addToServices policy services |> ignore
    services.BuildServiceProvider()

[<Fact>]
let ``binary policy registers only the binary generalized codec`` () =
    use services = buildSerializationServices FSharpSerialization.Binary

    let codecs =
        services.GetServices<IGeneralizedCodec>()
        |> Seq.map _.GetType()
        |> Seq.toList

    test <@ codecs |> List.contains typeof<FSharpBinaryCodec> @>
    test <@ codecs |> List.contains typeof<JsonCodec> |> not @>

[<Fact>]
let ``JSON policy registers only the JSON generalized codec`` () =
    use services = buildSerializationServices FSharpSerialization.Json

    let codecs = services.GetServices<IGeneralizedCodec>() |> Seq.toList
    let provider = services.GetRequiredService<ICodecProvider>()

    test <@ codecs |> List.exists (fun codec -> codec.GetType() = typeof<JsonCodec>) @>
    test <@ codecs |> List.exists (fun codec -> codec.GetType() = typeof<FSharpBinaryCodec>) |> not @>
    test <@ provider.GetCodec(typeof<CleanRecord>).GetType() = typeof<JsonCodec> @>
    test <@ provider.GetCodec(typeof<InvalidOperationException>).GetType() = typeof<ExceptionCodec> @>

[<Fact>]
let ``JSON policy wins when a binary codec was registered first`` () =
    let serviceCollection = ServiceCollection()
    FSharpSerializationRegistration.addToServices FSharpSerialization.Binary serviceCollection |> ignore
    FSharpSerializationRegistration.addToServices FSharpSerialization.Json serviceCollection |> ignore
    use services = serviceCollection.BuildServiceProvider()

    let codecs = services.GetServices<IGeneralizedCodec>() |> Seq.toList
    let copiers = services.GetServices<IGeneralizedCopier>() |> Seq.toList
    let provider = services.GetRequiredService<ICodecProvider>()

    let codecTypes = codecs |> List.map _.GetType()
    let copierTypes = copiers |> List.map _.GetType()
    let jsonIndex = codecTypes |> List.findIndex ((=) typeof<JsonCodec>)
    let binaryIndex = codecTypes |> List.findIndex ((=) typeof<FSharpBinaryCodec>)
    let jsonCopierIndex = copierTypes |> List.findIndex ((=) typeof<JsonCodec>)
    let binaryCopierIndex = copierTypes |> List.findIndex ((=) typeof<FSharpBinaryCodec>)

    test <@ jsonIndex < binaryIndex @>
    test <@ jsonCopierIndex < binaryCopierIndex @>
    test <@ provider.GetCodec(typeof<CleanRecord>).GetType() = typeof<JsonCodec> @>
    test <@ provider.GetCodec(typeof<InvalidOperationException>).GetType() = typeof<ExceptionCodec> @>

[<Fact>]
let ``JSON policy keeps priority when a binary codec is registered afterward`` () =
    let serviceCollection = ServiceCollection()
    FSharpSerializationRegistration.addToServices FSharpSerialization.Json serviceCollection |> ignore
    FSharpSerializationRegistration.addToServices FSharpSerialization.Binary serviceCollection |> ignore
    use services = serviceCollection.BuildServiceProvider()

    let provider = services.GetRequiredService<ICodecProvider>()

    test <@ provider.GetCodec(typeof<CleanRecord>).GetType() = typeof<JsonCodec> @>

[<Fact>]
let ``binary then JSON policy selects by supported CLR type`` () =
    use services =
        buildSerializationServices FSharpSerialization.BinaryWithJsonForUnsupportedTypes

    let provider = services.GetRequiredService<ICodecProvider>()

    test <@ FSharpBinaryFormat.isSupportedType typeof<CleanRecord> @>
    test <@ not (FSharpBinaryFormat.isSupportedType typeof<JsonElement>) @>
    test <@ provider.GetCodec(typeof<CleanRecord>).GetType() = typeof<FSharpBinaryCodec> @>
    test <@ provider.GetCodec(typeof<JsonElement>).GetType() = typeof<JsonCodec> @>

[<Fact>]
let ``Clean mode record roundtrips through FSharpJson serializer`` () =
    let options = Orleans.FSharp.FSharpJson.serializerOptions
    let original = { Name = "test"; Value = Some 42; Tags = [ "a"; "b" ] }
    let json = System.Text.Json.JsonSerializer.Serialize(original, options)
    let deserialized = System.Text.Json.JsonSerializer.Deserialize<CleanRecord>(json, options)
    test <@ deserialized = original @>

[<Fact>]
let ``Clean mode record with None roundtrips through FSharpJson serializer`` () =
    let options = Orleans.FSharp.FSharpJson.serializerOptions
    let original = { Name = "empty"; Value = None; Tags = [] }
    let json = System.Text.Json.JsonSerializer.Serialize(original, options)
    let deserialized = System.Text.Json.JsonSerializer.Deserialize<CleanRecord>(json, options)
    test <@ deserialized = original @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``Clean mode DU SetLevel roundtrips for any int level`` (level: int) =
    let options = Orleans.FSharp.FSharpJson.serializerOptions
    let original = SetLevel level
    let json = System.Text.Json.JsonSerializer.Serialize(original, options)
    let result = System.Text.Json.JsonSerializer.Deserialize<CleanCommand>(json, options)
    result = original

[<Property>]
let ``CleanRecord roundtrips for any int value option`` (value: int option) =
    let options = Orleans.FSharp.FSharpJson.serializerOptions
    let original = { Name = "test"; Value = value; Tags = [] }
    let json = System.Text.Json.JsonSerializer.Serialize(original, options)
    let result = System.Text.Json.JsonSerializer.Deserialize<CleanRecord>(json, options)
    result = original
