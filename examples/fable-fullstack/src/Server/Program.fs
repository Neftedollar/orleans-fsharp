open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Fable.Remoting.Server
open Fable.Remoting.AspNetCore
open FableFullstack.Shared
open FableFullstack.Grains

let config =
    siloConfig {
        useLocalhostClustering
        addMemoryStorage "Default"
        useJsonFallbackSerialization
    }

// Force-load before the silo's first UseOrleans/AddSerializer pass, not inside it.
// WebApplicationBuilder has no applyToHost-equivalent wrapper (applyToHost targets
// HostApplicationBuilder specifically), so this app calls SiloConfig.applyToSiloBuilder from
// inside builder.Host.UseOrleans(...) below -- and applyToSiloBuilder's own internal
// preloadManifestAssemblies() call then runs a step too late for the same reason
// SiloConfigBuilder.fs's applyToHost comment documents: "the manifest snapshot is taken while
// UseOrleans constructs the silo builder". Without this, activating any grain that touches
// addMemoryStorage's memory storage grain fails with:
// System.ArgumentException: Could not find an implementation for interface Orleans.Storage.IMemoryStorageGrain
// See docs/functional-grains.md, "Running a silo from a standalone F# process".
typeof<Orleans.Storage.MemoryGrainStorage>.Assembly |> ignore
typeof<Orleans.FSharp.IFSharpGrain>.Assembly |> ignore

let builder = WebApplication.CreateBuilder()

builder.Host.UseOrleans(fun siloBuilder ->
    SiloConfig.applyToSiloBuilder config siloBuilder)
|> ignore

builder.Services.AddFSharpGrain<TodoState, TodoCommand>(TodoGrainDef.todos) |> ignore

// Functional-runtime equivalent of the grain above -- see TodoGrainFunctional.fs.
builder.Host.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(TodoFunctionalDef.todos) |> ignore)
|> ignore

let app = builder.Build()

(*
    Classic grain { } model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so a bare `factory.GetGrain<ITodoGrain>(...)` fails with
    "Could not find an implementation for interface ITodoGrain" the moment it runs -- every call to
    /api/ITodoApi/* would 500. This example's C# CodeGen bridge project was removed once it became
    unnecessary. See docs/functional-grains.md, "Running a silo from a standalone F# process" for
    the exact mechanism, and "Migrating from the grain { } CE" for the rewrite this file
    demonstrates.

    let todoApi (ctx: Microsoft.AspNetCore.Http.HttpContext) : ITodoApi =
        let factory = ctx.RequestServices.GetRequiredService<IGrainFactory>()
        let todoRef = GrainRef.ofString<ITodoGrain> factory "global"

        {
            getTodos =
                fun () ->
                    async {
                        let! result = GrainRef.invoke todoRef (fun g -> g.HandleMessage(GetTodos)) |> Async.AwaitTask
                        return result :?> Todo list
                    }
            addTodo =
                fun text ->
                    async {
                        let! result = GrainRef.invoke todoRef (fun g -> g.HandleMessage(AddTodo text)) |> Async.AwaitTask
                        return result :?> Todo
                    }
            toggleTodo =
                fun id ->
                    async {
                        let! result = GrainRef.invoke todoRef (fun g -> g.HandleMessage(ToggleTodo id)) |> Async.AwaitTask
                        return result :?> Todo option
                    }
        }
*)

/// <summary>
/// Creates the Fable.Remoting API implementation that delegates to the functional-runtime todo
/// twin. Each API method calls the twin's matching typed operation directly -- no boxing/unboxing,
/// unlike the old GrainRef.invoke + downcast pattern kept above as reference.
/// </summary>
let todoApi (ctx: Microsoft.AspNetCore.Http.HttpContext) : ITodoApi =
    let factory = ctx.RequestServices.GetRequiredService<IGrainFactory>()
    let todoRef = TodoApi.ref factory "global"

    { getTodos = fun () -> todoRef.getTodos () |> Async.AwaitTask
      addTodo = fun text -> todoRef.addTodo text |> Async.AwaitTask
      toggleTodo = fun id -> todoRef.toggleTodo id |> Async.AwaitTask }

let remotingApi =
    Remoting.createApi ()
    |> Remoting.fromContext todoApi
    |> Remoting.withRouteBuilder Route.builder

app.UseRouting() |> ignore
app.UseRemoting(remotingApi) |> ignore

app.MapGet(
    "/",
    Func<string>(fun () ->
        "Fable Fullstack Server is running. API available at /api/ITodoApi/*"))
|> ignore

// Seeds one todo through the same "global" grain key /api/ITodoApi/* serves, so the functional
// twin's persistence is visible immediately to the first curl call too.
let exerciseFunctionalTwin () =
    task {
        let factory = app.Services.GetRequiredService<IGrainFactory>()
        let todoRef = TodoApi.ref factory "global"
        let! seeded = todoRef.addTodo "Try the functional grain runtime"
        let! todos = todoRef.getTodos ()
        let! toggled = todoRef.toggleTodo seeded.Id
        printfn "Functional-runtime todos twin: %d todo(s) seeded, toggled %A" todos.Length toggled
    }

printfn "--- Fable Fullstack: Server-side Demo (Functional Grain Runtime) ---"
printfn "Fable.Remoting API available at http://localhost:5000/api/ITodoApi/*"
printfn "Press Ctrl+C to stop."

app.Lifetime.ApplicationStarted.Register(fun () ->
    exerciseFunctionalTwin().GetAwaiter().GetResult())
|> ignore

app.Run("http://localhost:5000")
