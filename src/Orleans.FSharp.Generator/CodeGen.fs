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

/// Load and parse an embedded Scriban template by resource-relative name.
/// <param name="name">The template file name, relative to the <c>Templates</c> embedded-resource folder.</param>
/// <exception cref="System.Exception">
/// Thrown when no embedded resource matches <paramref name="name"/>, or when the loaded template fails to parse.
/// </exception>
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
/// <param name="interfaceType">The grain interface type to derive the stub class name from.</param>
let private toClassName (interfaceType: Type) : string =
    let name = interfaceType.Name
    let stripped = if name.StartsWith("I", StringComparison.Ordinal) then name.[1..] else name
    stripped + "Impl"

/// C# fully-qualified name (nested type '+' → '.')
/// <param name="t">The type to render a C#-syntax fully-qualified name for.</param>
let private fqn (t: Type) =
    t.FullName.Replace('+', '.')

// ---------------------------------------------------------------------------
// Rendering
// ---------------------------------------------------------------------------

/// <summary>
/// Renders the generated C# event-sourced grain stub source for one discovered interface, by
/// filling the thin or full Scriban template (chosen from <c>info.UseThinStub</c>) with the
/// stub's class name, fully-qualified type names, and definition metadata.
/// </summary>
/// <param name="info">The discovered event-sourced grain stub metadata to render.</param>
/// <param name="ns">The namespace to emit the generated class into.</param>
/// <returns>The rendered C# source text for the stub.</returns>
let renderEventSourcedStub (info: EventSourcedStubInfo) (ns: string) : string =
    // Choose thin template when interface inherits IFSharpEventSourcedGrain.
    let templateName =
        if info.UseThinStub then "EventSourcedGrainStubThin.scriban"
        else "EventSourcedGrainStub.scriban"

    let tmpl = loadTemplate templateName

    let so = ScriptObject()
    so.["class_name"]            <- toClassName info.InterfaceType :> obj
    so.["interface_fqn"]         <- fqn info.InterfaceType :> obj
    so.["state_fqn"]             <- fqn info.StateType :> obj
    so.["event_fqn"]             <- fqn info.EventType :> obj
    so.["command_fqn"]           <- fqn info.CommandType :> obj
    so.["namespace"]             <- ns :> obj
    so.["source_module"]         <- info.SourceModule :> obj
    so.["source_module_fqn"]     <- info.SourceModuleFqn :> obj
    so.["def_name"]              <- info.DefinitionName :> obj
    so.["assembly_name"]         <- info.AssemblyName :> obj
    so.["command_cases"]         <- (info.CommandCases |> String.concat ", ") :> obj
    so.["has_custom_storage"]    <- info.HasCustomStorage :> obj
    so.["consistency_provider"]  <- (info.ConsistencyProvider |> Option.defaultValue "") :> obj

    let ctx = TemplateContext()
    ctx.PushGlobal(so)
    tmpl.Render(ctx) |> Option.ofObj |> Option.defaultValue ""

// ---------------------------------------------------------------------------
// File output
// ---------------------------------------------------------------------------

/// <summary>
/// Renders every discovered event-sourced grain stub and writes each as a
/// <c>{ClassName}.g.cs</c> file into the output directory, creating it if needed.
/// </summary>
/// <param name="stubs">The discovered event-sourced grain stubs to render.</param>
/// <param name="outputDir">The directory to write the generated <c>.g.cs</c> files into.</param>
/// <param name="ns">The namespace to emit each generated class into.</param>
let generateAll (stubs: EventSourcedStubInfo list) (outputDir: string) (ns: string) =
    Directory.CreateDirectory(outputDir) |> ignore

    for stub in stubs do
        let className = toClassName stub.InterfaceType
        let filePath = Path.Combine(outputDir, $"{className}.g.cs")
        let content = renderEventSourcedStub stub ns
        File.WriteAllText(filePath, content)
        printfn "  generated: %s" filePath

    printfn "Orleans.FSharp.Generator: %d stub(s) written to %s" stubs.Length outputDir
