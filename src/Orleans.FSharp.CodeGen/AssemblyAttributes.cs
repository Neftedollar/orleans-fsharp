using Orleans;

// Generate Orleans proxy code for the IFSharpGrain universal interfaces in the Orleans.FSharp assembly.
// New F# grains use IFSharpGrain — no per-grain C# interface needed.
[assembly: GenerateCodeForDeclaringAssembly(typeof(Orleans.FSharp.IFSharpGrain))]

// Generate Orleans proxy code for the F# sample project (backward-compatible per-grain interfaces).
[assembly: GenerateCodeForDeclaringAssembly(typeof(Orleans.FSharp.Sample.CounterState))]

namespace Orleans.FSharp.CodeGen
{
    /// <summary>
    /// Marker type for referencing the CodeGen assembly at runtime.
    /// Used to force assembly loading so Orleans discovers the generated proxies.
    /// </summary>
    public static class CodeGenAssemblyMarker { }
}
