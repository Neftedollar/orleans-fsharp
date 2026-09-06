module Orleans.FSharp.Tests.DocumentationContractSamplesTests

open System
open System.IO
open Xunit
open Swensen.Unquote

// docs-snippet:functional-contract-versioning-v2
open System.Threading.Tasks
open Orleans.FSharp

type CatalogActor = private CatalogActor of unit

[<NoEquality; NoComparison>]
type CatalogApiV2 =
    { getItem: string -> Task<string>
      rebuildIndex: unit -> Task<unit> }

let catalogContractV2 =
    grainContract<CatalogActor, string, CatalogApiV2> {
        grainType "catalog"
        version 2
        acceptsVersions (BackwardCompatible 1)
        stringKey
        sinceVersion 2 (_.rebuildIndex)
    }
// docs-snippet-end:functional-contract-versioning-v2

let private extractLines startMarker endMarker (text: string) =
    let lines = text.Replace("\r\n", "\n").Split('\n')
    let startIndex = lines |> Array.findIndex ((=) startMarker)
    let endIndex = lines |> Array.findIndex ((=) endMarker)

    if endIndex <= startIndex then
        invalidOp $"Snippet end marker '{endMarker}' must follow '{startMarker}'."

    lines.[startIndex + 1 .. endIndex - 1]

let private extractFSharpFence snippetName text =
    let fenced =
        text
        |> extractLines
            $"<!-- docs-snippet:{snippetName} -->"
            $"<!-- docs-snippet-end:{snippetName} -->"

    if fenced.Length < 3 || fenced.[0] <> "```fsharp" || fenced.[fenced.Length - 1] <> "```" then
        invalidOp $"Documentation snippet '{snippetName}' must contain exactly one F# fence."

    fenced.[1 .. fenced.Length - 2]

[<Fact>]
let ``the documented versioning samples exactly match the compiled fixture`` () =
    let snippetName = "functional-contract-versioning-v2"

    let compiledSample =
        File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "DocumentationContractSamplesTests.fs"))
        |> extractLines $"// docs-snippet:{snippetName}" $"// docs-snippet-end:{snippetName}"

    let repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    let documentationPaths =
        [ Path.Combine(repositoryRoot, "docs", "functional-grains", "contracts.md")
          Path.Combine(repositoryRoot, "website", "src", "content", "docs", "functional-grains", "contracts.md") ]

    for documentationPath in documentationPaths do
        let documentedSample =
            File.ReadAllText documentationPath
            |> extractFSharpFence snippetName

        test <@ documentedSample = compiledSample @>

[<Fact>]
let ``the documented versioning contract compiles and records its compatibility window`` () =
    let sinceVersionOf fieldName =
        catalogContractV2.Operations
        |> Array.find (fun operation -> operation.FieldName = fieldName)
        |> fun operation -> operation.SinceVersion

    test <@ catalogContractV2.Version = 2 @>
    test <@ catalogContractV2.AcceptedVersions = BackwardCompatible 1 @>
    test <@ catalogContractV2.MinAcceptedVersion = 1 @>
    test <@ sinceVersionOf "getItem" = 1 @>
    test <@ sinceVersionOf "rebuildIndex" = 2 @>
