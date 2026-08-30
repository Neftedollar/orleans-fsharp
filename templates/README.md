# Orleans.FSharp.Templates

Project template for the current functional Orleans.FSharp API: typed `grainContract` contracts,
`grainFor` definitions, `FunctionalGrain.ref` calls, functional silo registration, and F# tests.
It does not create an application CodeGen bridge or use the Legacy `grain { }` API.

```bash
git clone https://github.com/Neftedollar/orleans-fsharp.git
dotnet new install ./orleans-fsharp/templates
dotnet new orleans-fsharp -n MyApp
```

The source-checkout form is intentional until a package containing this functional scaffold is
published. `Orleans.FSharp.Templates` 4.1.0 still creates the Legacy CodeGen-based project.

See the [Getting Started guide](https://neftedollar.com/orleans-fsharp/getting-started/) for the
generated solution structure and next steps.
