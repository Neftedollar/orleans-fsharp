# Fable Fullstack

Full-stack F# pattern: Fable.Remoting server backed by an Orleans grain for todo list management. Shared types between server and (potential) Fable client ensure type-safe API communication across the stack.

This example includes the **server side only**. The server is fully functional and can be tested with any HTTP client. See below for instructions on adding a Fable frontend.

The live `/api/ITodoApi/*` endpoints are backed by the functional grain runtime's twin
(`TodoGrainFunctional.fs`): all three operations (add / list / toggle) plus explicit `stateFrom`
persistence, matching the original `persist "Default"`. `TodoGrain.fs` keeps the original `grain {}`
version as deprecated reference -- see `Program.fs` for why it cannot run standalone and
[docs/functional-grains.md](../../docs/functional-grains.md) for the full migration guide.

**A second, unrelated standalone-hosting gap was found and fixed here.** This is a
`WebApplication.CreateBuilder()` app, so it calls `SiloConfig.applyToSiloBuilder` *inside* its own
`builder.Host.UseOrleans(...)` delegate rather than through `SiloConfig.applyToHost` (which targets
plain `HostApplicationBuilder`, not `WebApplicationBuilder`). `applyToSiloBuilder`'s own internal
assembly pre-load then runs a step too late for Orleans' manifest-snapshot timing -- the same
"too late" `applyToHost`'s own source comment documents for exactly this shape. Without a fix, any
grain activation touching `addMemoryStorage`'s memory storage grain failed with `Could not find an
implementation for interface Orleans.Storage.IMemoryStorageGrain`. `Program.fs` now force-loads the
two assemblies `applyToHost` would have force-loaded, before `UseOrleans` runs -- the documented
pattern from docs/functional-grains.md, "Running a silo from a standalone F# process".
`examples/signalr-realtime` uses the same `WebApplicationBuilder` shape and needed the identical fix.

## How to run

```bash
dotnet run --project src/Server
```

The server seeds one todo through the functional twin at startup (`Try the functional grain
runtime`, already marked done) -- printed to the console and visible in the first `getTodos` call
below, so `getTodos`/`addTodo`/`toggleTodo` all have something to show immediately.

## Test the API with curl

```bash
# Get all todos (one pre-seeded item from startup)
curl http://localhost:5000/api/ITodoApi/getTodos -d '[]' -H "Content-Type: application/json"

# Add a todo
curl http://localhost:5000/api/ITodoApi/addTodo -d '["Buy groceries"]' -H "Content-Type: application/json"

# Get all todos (now has 2 items)
curl http://localhost:5000/api/ITodoApi/getTodos -d '[]' -H "Content-Type: application/json"

# Toggle the todo just added -- pass its "Id" from the addTodo response above
curl http://localhost:5000/api/ITodoApi/toggleTodo -d '["<id-from-addTodo-response>"]' -H "Content-Type: application/json"

# Toggling an id that does not exist returns null (Option.None), not an error
curl http://localhost:5000/api/ITodoApi/toggleTodo -d '["00000000-0000-0000-0000-000000000000"]' -H "Content-Type: application/json"
```

## Key concepts

- **`grainContract` / `grainFor`** the functional grain runtime's contract + definition pair (this
  example's live `/api/ITodoApi/*` path): `addTodo` / `getTodos` (`readOnly`) / `toggleTodo`
- **`stateFrom` + `PersistentState.create` + explicit `WriteStateAsync`** every add/toggle is
  persisted to the `"Default"` memory storage provider
- **Shared types** `Todo` and `ITodoApi` defined once, used by both server and client
- **`netstandard2.0` Shared project** Fable compiles F# to JS, which requires netstandard
- **Fable.Remoting.Server** auto-generates API endpoints from the `ITodoApi` record type
- **`grain {}`** (deprecated) the original computation expression, kept in `TodoGrain.fs` as
  reference -- needs a C#-generated proxy per grain interface and cannot resolve standalone in an
  F#-only project
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
