module Orleans.FSharp.Generator.Program

open System
open System.IO
open System.Reflection
open System.Runtime.Loader

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

[<EntryPoint>]
let main argv =
    // Parse: --assembly <path> --output <dir> [--namespace <ns>]
    let args = argv |> Array.toList

    let rec parse remaining (assembly: string option) (output: string option) (ns: string option) =
        match remaining with
        | "--assembly" :: v :: rest -> parse rest (Some v) output ns
        | "--output"   :: v :: rest -> parse rest assembly (Some v) ns
        | "--namespace":: v :: rest -> parse rest assembly output (Some v)
        | [] -> assembly, output, ns
        | unknown :: _ ->
            eprintfn "Unknown argument: %s" unknown
            None, None, None

    let assemblyPath, outputDir, ns = parse args None None None

    match assemblyPath, outputDir with
    | None, _ | _, None ->
        eprintfn "Usage: Orleans.FSharp.Generator --assembly <path.dll> --output <dir> [--namespace <ns>]"
        1
    | Some asmPath, Some outDir ->

    let ns = ns |> Option.defaultValue "Orleans.FSharp.CodeGen"

    if not (File.Exists asmPath) then
        eprintfn "Assembly not found: %s" asmPath
        1
    else

    try
        printfn "Orleans.FSharp.Generator: loading %s" (Path.GetFileName asmPath)

        // Load into the default context so Orleans.FSharp.EventSourcing types
        // (already referenced by this tool) resolve correctly.
        let assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(asmPath)

        let stubs = Discovery.discoverEventSourcedGrains assembly

        if stubs.IsEmpty then
            printfn "Orleans.FSharp.Generator: no [FSharpEventSourcedGrain] definitions found."
        else
            for s in stubs do
                printfn "  found: %s.%s → %s" s.SourceModule s.DefinitionName s.InterfaceType.Name

            CodeGen.generateAll stubs outDir ns

        0
    with ex ->
        eprintfn "Orleans.FSharp.Generator ERROR: %s" ex.Message
        eprintfn "%s" ex.StackTrace
        1
