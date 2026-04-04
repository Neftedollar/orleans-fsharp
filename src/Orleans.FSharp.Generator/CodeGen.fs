module Orleans.FSharp.Generator.CodeGen

open System
open System.IO
open System.Reflection
open Scriban
open Scriban.Runtime
open Discovery

// ---------------------------------------------------------------------------
// Template loading
// ---------------------------------------------------------------------------

let private loadTemplate (name: string) : Template =
    let resourceName = $"Orleans.FSharp.Generator.Templates.{name}"

    let stream =
        Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)

    if isNull stream then
        failwithf
            "Embedded template '%s' not found. Available resources: %A"
            resourceName
            (Assembly.GetExecutingAssembly().GetManifestResourceNames())

    use reader = new StreamReader(stream)
    let src = reader.ReadToEnd()
    stream.Dispose()
    let tmpl = Template.Parse(src)

    if tmpl.HasErrors then
        failwithf "Template '%s' has errors: %A" name tmpl.Messages

    tmpl

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/// Strip leading 'I' and append 'Impl': IBankAccountGrain → BankAccountGrainImpl
let private toClassName (interfaceType: Type) : string =
    let name = interfaceType.Name
    let stripped = if name.StartsWith("I", StringComparison.Ordinal) then name.[1..] else name
    stripped + "Impl"

/// C# fully-qualified name (nested type '+' → '.')
let private fqn (t: Type) =
    t.FullName.Replace('+', '.')

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

let renderEventSourcedStub (info: EventSourcedStubInfo) (ns: string) : string =
    let tmpl = loadTemplate "EventSourcedGrainStub.scriban"

    let so = ScriptObject()
    so.["class_name"]     <- toClassName info.InterfaceType :> obj
    so.["interface_fqn"]  <- fqn info.InterfaceType :> obj
    so.["state_fqn"]      <- fqn info.StateType :> obj
    so.["event_fqn"]      <- fqn info.EventType :> obj
    so.["command_fqn"]    <- fqn info.CommandType :> obj
    so.["namespace"]      <- ns :> obj
    so.["source_module"]  <- info.SourceModule :> obj
    so.["def_name"]       <- info.DefinitionName :> obj
    so.["assembly_name"]  <- info.AssemblyName :> obj
    so.["command_cases"]  <- (info.CommandCases |> String.concat ", ") :> obj

    let ctx = TemplateContext()
    ctx.PushGlobal(so)
    tmpl.Render(ctx) |> Option.ofObj |> Option.defaultValue ""

// ---------------------------------------------------------------------------
// File output
// ---------------------------------------------------------------------------

let generateAll (stubs: EventSourcedStubInfo list) (outputDir: string) (ns: string) =
    Directory.CreateDirectory(outputDir) |> ignore

    for stub in stubs do
        let className = toClassName stub.InterfaceType
        let filePath = Path.Combine(outputDir, $"{className}.g.cs")
        let content = renderEventSourcedStub stub ns
        File.WriteAllText(filePath, content)
        printfn "  generated: %s" filePath

    printfn "Orleans.FSharp.Generator: %d stub(s) written to %s" stubs.Length outputDir
