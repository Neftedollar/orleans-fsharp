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

let builder = WebApplication.CreateBuilder()

builder.Host.UseOrleans(fun siloBuilder ->
    SiloConfig.applyToSiloBuilder config siloBuilder)
|> ignore

builder.Services.AddFSharpGrain<TodoState, TodoCommand>(TodoGrainDef.todos) |> ignore

let app = builder.Build()

/// <summary>
/// Creates the Fable.Remoting API implementation that delegates to the Orleans todo grain.
/// Each API method gets a grain reference and invokes the appropriate command.
/// </summary>
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

printfn "--- Fable Fullstack: Server-side Demo ---"
printfn "Fable.Remoting API available at http://localhost:5000/api/ITodoApi/*"
printfn "Press Ctrl+C to stop."

app.Run("http://localhost:5000")
