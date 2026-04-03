using Orleans;

// Generate Orleans code for the F# domain project
[assembly: GenerateCodeForDeclaringAssembly(typeof(BankAccount.Domain.AccountState))]
