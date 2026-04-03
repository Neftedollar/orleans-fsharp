module Orleans.FSharp.Tests.SerializationTests

open System.Text.Json
open System.Text.Json.Serialization
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ---------------------------------------------------------------------------
// fsharpJsonOptions tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``fsharpJsonOptions is same instance as FSharpJson.serializerOptions`` () =
    test <@ obj.ReferenceEquals(Serialization.fsharpJsonOptions, FSharpJson.serializerOptions) @>

[<Fact>]
let ``fsharpJsonOptions has FSharp.SystemTextJson converter`` () =
    let hasFSharpConverter =
        Serialization.fsharpJsonOptions.Converters
        |> Seq.exists (fun c -> c :? JsonFSharpConverter)

    test <@ hasFSharpConverter @>

// ---------------------------------------------------------------------------
// addFSharpConverters tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``addFSharpConverters adds converter to empty options`` () =
    let options = JsonSerializerOptions()
    let result = Serialization.addFSharpConverters options

    let hasFSharpConverter =
        result.Converters
        |> Seq.exists (fun c -> c :? JsonFSharpConverter)

    test <@ hasFSharpConverter @>

[<Fact>]
let ``addFSharpConverters returns same instance`` () =
    let options = JsonSerializerOptions()
    let result = Serialization.addFSharpConverters options
    test <@ obj.ReferenceEquals(result, options) @>

[<Fact>]
let ``addFSharpConverters is idempotent`` () =
    let options = JsonSerializerOptions()
    Serialization.addFSharpConverters options |> ignore
    let countBefore = options.Converters.Count
    Serialization.addFSharpConverters options |> ignore
    let countAfter = options.Converters.Count
    test <@ countBefore = countAfter @>

[<Fact>]
let ``addFSharpConverters enables DU serialization`` () =
    let options = JsonSerializerOptions() |> Serialization.addFSharpConverters
    let value = Some "hello"
    let json = JsonSerializer.Serialize(value, options)
    let result = JsonSerializer.Deserialize<string option>(json, options)
    test <@ result = value @>

// ---------------------------------------------------------------------------
// withConverters tests
// ---------------------------------------------------------------------------

type TestCustomConverter() =
    inherit JsonConverter<int>()

    override _.Read(reader, _typeToConvert, _options) =
        reader.GetInt32() * 10

    override _.Write(writer, value, _options) =
        writer.WriteNumberValue(value / 10)

[<Fact>]
let ``withConverters creates new options with F# support`` () =
    let options = Serialization.withConverters []

    let hasFSharpConverter =
        options.Converters
        |> Seq.exists (fun c -> c :? JsonFSharpConverter)

    test <@ hasFSharpConverter @>

[<Fact>]
let ``withConverters includes additional converters`` () =
    let customConverter = TestCustomConverter()
    let options = Serialization.withConverters [ customConverter ]

    let hasCustom =
        options.Converters
        |> Seq.exists (fun c -> c :? TestCustomConverter)

    test <@ hasCustom @>

[<Fact>]
let ``withConverters creates independent options instance`` () =
    let options1 = Serialization.withConverters []
    let options2 = Serialization.withConverters []
    test <@ not (obj.ReferenceEquals(options1, options2)) @>

[<Fact>]
let ``withConverters preserves DU roundtrip capability`` () =
    let options = Serialization.withConverters []
    let value: string option = Some "test"
    let json = JsonSerializer.Serialize(value, options)
    let result = JsonSerializer.Deserialize<string option>(json, options)
    test <@ result = value @>

// ---------------------------------------------------------------------------
// Serialization module exists in assembly
// ---------------------------------------------------------------------------

[<Fact>]
let ``Serialization module exists in the assembly`` () =
    let serializationModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Serialization" && t.IsAbstract && t.IsSealed)

    test <@ serializationModule.IsSome @>
