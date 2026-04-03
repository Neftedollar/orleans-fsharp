// Orleans.FSharp.CodeGen — bridge project that enables Orleans source generators for F# grains.
//
// Design:
//   - Orleans.FSharp.Abstractions (C#) hosts IFSharpGrain interfaces → its source generators
//     produce Proxy_IFSharpGrain etc. in that same assembly (no cross-assembly access issue).
//   - This project (CodeGen) references the user's grain project (e.g. Sample) AND uses
//     [GenerateCodeForDeclaringAssembly] to trigger proxy generation for every grain interface
//     declared in those referenced assemblies (IEchoGrain, ICounterGrain, etc.).

[assembly: GenerateCodeForDeclaringAssembly(typeof(Orleans.FSharp.Sample.IEchoGrain))]

namespace Orleans.FSharp.CodeGen
{
    /// <summary>
    /// Marker type for referencing the CodeGen assembly at runtime.
    /// Used to ensure assembly loading in scenarios where Orleans needs to discover
    /// grain proxies across application parts.
    /// </summary>
    public static class CodeGenAssemblyMarker { }
}
