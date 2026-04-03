# Orleans.FSharp

**Idiomatic F# API for Microsoft Orleans**

<!-- Badges -->
[![Build](https://github.com/orleans-fsharp/orleans-fsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/orleans-fsharp/orleans-fsharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Orleans.FSharp.svg)](https://www.nuget.org/packages/Orleans.FSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

---

## Overview

Orleans.FSharp provides a thin, idiomatic F# layer on top of [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/).
It is **not** a reimplementation of Orleans -- it is a set of computation expressions, helpers, and analyzers that let you write Orleans grains in natural F# style while the full Orleans runtime does the heavy lifting underneath.

## Features

- **`grain {}` computation expression** -- define grains declaratively with state, commands, and persistence
- **Discriminated union states** -- use F# DUs as grain state with automatic serialization support
- **`GrainRef<'T>`** -- strongly-typed grain references
- **Streaming** -- F#-friendly stream subscription and publishing
- **Logging** -- integrated logging helpers
- **Reminders** -- declarative reminder registration and handling
- **Timers** -- timer support within the grain CE
- **Observers** -- grain observer patterns
- **Reentrancy** -- reentrant grain support
- **Stateless workers** -- stateless worker grain definitions
- **Event sourcing** -- event-sourced grain support via `Orleans.FSharp.EventSourcing`
- **Roslyn analyzer** -- compile-time checks for common Orleans + F# mistakes
- **`dotnet new` template** -- scaffold a new project in seconds
- **Scripting** -- F# scripting support
- **`GrainArbitrary`** -- FsCheck arbitrary generators for property-based testing

## Quick Example

Define a counter grain using the `grain {}` CE:

```fsharp
open Orleans
open Orleans.FSharp

[<GenerateSerializer>]
type CounterState =
    | [<Id(0u)>] Zero
    | [<Id(1u)>] Count of int

[<GenerateSerializer>]
type CounterCommand =
    | [<Id(0u)>] Increment
    | [<Id(1u)>] Decrement
    | [<Id(2u)>] GetValue

let counter =
    grain {
        defaultState Zero

        handle (fun state cmd ->
            task {
                match state, cmd with
                | Zero, Increment -> return Count 1, box 1
                | Zero, Decrement -> return Zero, box 0
                | Count n, Increment -> return Count(n + 1), box (n + 1)
                | Count n, Decrement when n > 1 -> return Count(n - 1), box (n - 1)
                | Count _, Decrement -> return Zero, box 0
                | _, GetValue ->
                    let v = match state with Zero -> 0 | Count n -> n
                    return state, box v
            })

        persist "Default"
    }
```

## Installation

```bash
dotnet add package Orleans.FSharp
```

## Getting Started

Scaffold a new project with the included template:

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyApp
```

## Project Structure

| Package | Description |
|---|---|
| `Orleans.FSharp` | Core library: grain CE, DU state, GrainRef, streaming, logging, reminders, timers, observers |
| `Orleans.FSharp.Runtime` | Runtime hosting and silo configuration helpers |
| `Orleans.FSharp.EventSourcing` | Event-sourced grain support |
| `Orleans.FSharp.Analyzers` | Roslyn analyzer for compile-time checks |
| `Orleans.FSharp.CodeGen` | Code generation for F# grain types |
| `Orleans.FSharp.Testing` | Test utilities and GrainArbitrary for FsCheck |
| `Orleans.FSharp.Templates` | `dotnet new` project template |

## Technology Stack

- **F#** on **.NET 10**
- **Microsoft Orleans** -- virtual actor framework
- **FsCheck** -- property-based testing
- **xUnit** -- test framework

## Contributing

Contributions are welcome! Please open an issue or pull request on [GitHub](https://github.com/orleans-fsharp/orleans-fsharp).

## License

This project is licensed under the [MIT License](LICENSE).
