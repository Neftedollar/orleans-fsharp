# Orleans.FSharp.Templates

Project template for the current functional Orleans.FSharp API: typed `grainContract` contracts,
`grainFor` definitions, `FunctionalGrain.ref` calls, functional silo registration, and F# tests.
It does not create an application CodeGen bridge or use the Legacy `grain { }` API.

```bash
dotnet new install Orleans.FSharp.Templates@5.0.1
dotnet new orleans-fsharp -n MyApp
```

Install a 5.x template package to get the functional scaffold. Each published template is pinned
to the exact matching Orleans.FSharp package set, including prerelease versions, so generated
projects cannot silently restore an older stable runtime.

Projects created with the published 5.0.0 template need a
[one-line startup correction](https://neftedollar.com/orleans-fsharp/release-status/#the-500-project-template-needs-a-one-line-contract-correction).
The 5.0.1 template includes that correction. Updating packages does not rewrite files in an
application already generated with 5.0.0.

See the [Getting Started guide](https://neftedollar.com/orleans-fsharp/getting-started/) for the
generated solution structure and next steps.
