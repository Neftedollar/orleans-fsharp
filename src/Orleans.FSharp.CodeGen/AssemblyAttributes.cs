using Orleans;

// Generate Orleans code for the core F# library
[assembly: GenerateCodeForDeclaringAssembly(typeof(Orleans.FSharp.AssemblyMarker))]

// Generate Orleans code for the F# sample project (grain interfaces, state types, etc.)
[assembly: GenerateCodeForDeclaringAssembly(typeof(Orleans.FSharp.Sample.CounterState))]
