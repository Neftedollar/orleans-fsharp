# Orleans.FSharp.Templates

Project template for the current functional Orleans.FSharp API: typed `grainContract` contracts,
`grainFor` definitions, `FunctionalGrain.ref` calls, functional silo registration, and F# tests.
It does not create an application CodeGen bridge or use the Legacy `grain { }` API.

```bash
dotnet new install Orleans.FSharp.Templates
dotnet new orleans-fsharp -n MyApp
```

Install a 5.x template package to get the functional scaffold. Each published template is pinned
to the exact matching Orleans.FSharp package set, including prerelease versions, so generated
projects cannot silently restore an older stable runtime.

See the [Getting Started guide](https://neftedollar.com/orleans-fsharp/getting-started/) for the
generated solution structure and next steps.
