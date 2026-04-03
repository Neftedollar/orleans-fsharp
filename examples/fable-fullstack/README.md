# Fable Fullstack

Full-stack F# pattern: Fable.Remoting server backed by an Orleans grain for todo list management. Shared types between server and (potential) Fable client ensure type-safe API communication across the stack.

This example includes the **server side only**. The server is fully functional and can be tested with any HTTP client. See below for instructions on adding a Fable frontend.

## How to run

```bash
dotnet run --project src/Server
```

## Test the API with curl

```bash
# Get all todos (initially empty)
curl http://localhost:5000/api/ITodoApi/getTodos -d '[]' -H "Content-Type: application/json"

# Add a todo
curl http://localhost:5000/api/ITodoApi/addTodo -d '["Buy groceries"]' -H "Content-Type: application/json"

# Add another todo
curl http://localhost:5000/api/ITodoApi/addTodo -d '["Learn Orleans.FSharp"]' -H "Content-Type: application/json"

# Get all todos (now has 2 items)
curl http://localhost:5000/api/ITodoApi/getTodos -d '[]' -H "Content-Type: application/json"
```

## Key concepts

- **Shared types** `Todo` and `ITodoApi` defined once, used by both server and client
- **`netstandard2.0` Shared project** Fable compiles F# to JS, which requires netstandard
- **Fable.Remoting.Server** auto-generates API endpoints from the `ITodoApi` record type
- **Orleans grain** manages todo state with the `grain {}` computation expression
- **`GrainRef.invoke`** type-safe grain method invocation from the API handler
- **`useJsonFallbackSerialization`** clean F# record serialization without attributes
- **Route builder** generates routes like `/api/ITodoApi/getTodos` automatically

## Adding a Fable frontend

To create a full-stack app with a Fable (F# compiled to JavaScript) frontend:

### Prerequisites

- Node.js (18+)
- npm

### Steps

1. Create a Client project targeting `netstandard2.0`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="App.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Shared\Shared.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Fable.Remoting.Client" Version="7.*" />
    <PackageReference Include="Fable.Elmish.React" Version="4.*" />
  </ItemGroup>
</Project>
```

2. In `App.fs`, create a Fable.Remoting client:

```fsharp
let todoApi =
    Remoting.createApi()
    |> Remoting.withRouteBuilder Route.builder
    |> Remoting.buildProxy<ITodoApi>
```

3. Install Fable and build: `dotnet tool install fable && dotnet fable src/Client`

4. Bundle with Vite/Webpack and serve alongside the ASP.NET Core server.

The same `Todo` type and `ITodoApi` definition are shared across server and client with full type safety.

## Documentation

See the [Orleans.FSharp README](../../README.md) for full documentation.
