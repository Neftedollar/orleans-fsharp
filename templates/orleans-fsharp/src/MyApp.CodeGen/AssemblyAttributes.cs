using Orleans;

// Generate Orleans code for the F# grains project (grain interfaces, state types, etc.)
[assembly: GenerateCodeForDeclaringAssembly(typeof(MyApp.Grains.CounterState))]
