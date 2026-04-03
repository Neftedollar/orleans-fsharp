using Orleans;

// Generate Orleans code for the shared types (grain interfaces, state types, command DUs)
[assembly: GenerateCodeForDeclaringAssembly(typeof(Testbed.Shared.CounterState))]
