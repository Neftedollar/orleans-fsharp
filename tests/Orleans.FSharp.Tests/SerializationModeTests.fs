module Orleans.FSharp.Tests.SerializationModeTests

open Xunit
open Swensen.Unquote
open Orleans.FSharp.Runtime

// ---------------------------------------------------------------------------
// Mode 1: Clean (JSON fallback) — CE keyword tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE default has UseJsonFallbackSerialization false`` () =
    let config = siloConfig { () }
    test <@ config.UseJsonFallbackSerialization = false @>

[<Fact>]
let ``siloConfig CE sets useJsonFallbackSerialization`` () =
    let config = siloConfig { useJsonFallbackSerialization }
    test <@ config.UseJsonFallbackSerialization = true @>

[<Fact>]
let ``siloConfig CE combines useJsonFallbackSerialization with other settings`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            useJsonFallbackSerialization
            addMemoryStorage "Default"
        }

    test <@ config.UseJsonFallbackSerialization = true @>
    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>

[<Fact>]
let ``clientConfig CE default has UseJsonFallbackSerialization false`` () =
    let config = clientConfig { () }
    test <@ config.UseJsonFallbackSerialization = false @>

[<Fact>]
let ``clientConfig CE sets useJsonFallbackSerialization`` () =
    let config = clientConfig { useJsonFallbackSerialization }
    test <@ config.UseJsonFallbackSerialization = true @>

[<Fact>]
let ``clientConfig CE combines useJsonFallbackSerialization with other settings`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            useJsonFallbackSerialization
        }

    test <@ config.UseJsonFallbackSerialization = true @>
    test <@ config.ClusteringMode.IsSome @>

// ---------------------------------------------------------------------------
// Mode 1: SiloConfig.Default has correct default
// ---------------------------------------------------------------------------

[<Fact>]
let ``SiloConfig Default has UseJsonFallbackSerialization false`` () =
    test <@ SiloConfig.Default.UseJsonFallbackSerialization = false @>

[<Fact>]
let ``ClientConfig Default has UseJsonFallbackSerialization false`` () =
    test <@ ClientConfig.Default.UseJsonFallbackSerialization = false @>

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
/// Relies on JSON fallback serialization for grain boundary crossing.
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
// JSON fallback roundtrip (in-process, no Orleans silo)
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
