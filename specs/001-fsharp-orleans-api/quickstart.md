# Quickstart: Orleans.FSharp

**Time to first grain**: ~15 minutes

## Prerequisites

- .NET 10 SDK or later
- An IDE with F# support (Rider, VS Code + Ionide, Visual Studio)

## 1. Create the solution

```bash
dotnet new sln -n MyOrleansApp
dotnet new console -lang F# -n MyOrleansApp.Silo
dotnet new classlib -lang F# -n MyOrleansApp.Grains
dotnet new xunit -lang F# -n MyOrleansApp.Tests
dotnet new classlib -lang C# -n MyOrleansApp.CodeGen

dotnet sln add MyOrleansApp.Silo
dotnet sln add MyOrleansApp.Grains
dotnet sln add MyOrleansApp.Tests
dotnet sln add MyOrleansApp.CodeGen
```

## 2. Add packages

```bash
# Grains project
cd MyOrleansApp.Grains
dotnet add package Orleans.FSharp

# Silo project
cd ../MyOrleansApp.Silo
dotnet add package Orleans.FSharp.Runtime
dotnet add reference ../MyOrleansApp.Grains
dotnet add reference ../MyOrleansApp.CodeGen

# CodeGen project (C# -- required for Orleans serializer generation)
cd ../MyOrleansApp.CodeGen
dotnet add package Microsoft.Orleans.Sdk
dotnet add reference ../MyOrleansApp.Grains

# Tests project
cd ../MyOrleansApp.Tests
dotnet add package Orleans.FSharp.Testing
dotnet add package FsCheck.Xunit -v 3.*
dotnet add package Unquote
dotnet add reference ../MyOrleansApp.Grains
```

## 3. Define a grain

`MyOrleansApp.Grains/CounterGrain.fs`:

```fsharp
module MyOrleansApp.Grains.Counter

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

// State -- a simple DU
[<GenerateSerializer>]
type State =
    | [<Id(0u)>] Zero
    | [<Id(1u)>] Count of int

// Messages
[<GenerateSerializer>]
type Command =
    | [<Id(0u)>] Increment
    | [<Id(1u)>] Decrement
    | [<Id(2u)>] GetValue

// Grain interface (required by Orleans for grain references)
type ICounterGrain =
    inherit IGrainWithIntegerKey
    abstract HandleMessage: Command -> Task<obj>

// Grain definition
let counter = grain {
    defaultState Zero

    handle (fun state cmd -> task {
        match state, cmd with
        | Zero, Increment       -> return Count 1, box 1
        | Zero, Decrement       -> return Zero, box 0
        | Count n, Increment    -> return Count (n + 1), box (n + 1)
        | Count n, Decrement when n > 1 -> return Count (n - 1), box (n - 1)
        | Count _, Decrement    -> return Zero, box 0
        | _, GetValue           ->
            let v = match state with Zero -> 0 | Count n -> n
            return state, box v
    })

    persist "Default"
}
```

## 4. Configure the CodeGen project

`MyOrleansApp.CodeGen/AssemblyInfo.cs`:

```csharp
using Orleans;

[assembly: GenerateCodeForDeclaringAssembly(typeof(MyOrleansApp.Grains.Counter))]
```

## 5. Configure and start the silo

`MyOrleansApp.Silo/Program.fs`:

```fsharp
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans.FSharp
open Orleans.FSharp.Runtime
open MyOrleansApp.Grains.Counter

let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}

let builder = Host.CreateApplicationBuilder()
SiloConfig.applyToHost config builder

// Register the grain definition
builder.Services.AddFSharpGrain<State, Command>(counter) |> ignore

let host = builder.Build()
host.Run()
```

## 6. Write a property test

`MyOrleansApp.Tests/CounterTests.fs`:

```fsharp
module MyOrleansApp.Tests.CounterTests

open FsCheck.Xunit
open Swensen.Unquote
open MyOrleansApp.Grains.Counter

let applyPure state cmd =
    match state, cmd with
    | Zero, Increment           -> Count 1
    | Zero, Decrement           -> Zero
    | Count n, Increment        -> Count (n + 1)
    | Count n, Decrement when n > 1 -> Count (n - 1)
    | Count _, Decrement        -> Zero
    | s, GetValue               -> s

let isValid = function
    | Zero -> true
    | Count n -> n > 0

[<Property>]
let ``counter always in valid state`` (cmds: Command list) =
    let finalState = cmds |> List.fold applyPure Zero
    test <@ isValid finalState @>

[<Property>]
let ``increment then decrement is identity`` (n: PositiveInt) =
    let state = Count n.Get
    let result = state |> applyPure Increment |> applyPure Decrement
    test <@ result = state @>
```

## 7. Build and test

```bash
dotnet build
dotnet test
```

## Verify

- `dotnet build` completes with 0 warnings
- `dotnet test` runs property tests with 100 random inputs each
- Grain definition uses `Orleans.FSharp` module for the CE and `Orleans` for serialization attributes
