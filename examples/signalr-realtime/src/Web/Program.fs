open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime
open SignalRRealtime.Grains
open SignalRRealtime.CodeGen

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

builder.Services.AddFSharpGrain<DashboardState, DashboardCommand>(DashboardGrainDef.dashboard) |> ignore
builder.Services.AddSignalR() |> ignore

let app = builder.Build()

app.UseDefaultFiles() |> ignore
app.UseStaticFiles() |> ignore
app.MapHub<DashboardHub>("/dashboard") |> ignore

// Start the dashboard grain timer on startup
let startDashboard () =
    task {
        let factory = app.Services.GetRequiredService<IGrainFactory>()
        let dashboard = factory.GetGrain<IDashboardGrain>("default")
        do! dashboard.StartTimer()
        printfn "--- SignalR Realtime: Dashboard grain timer started ---"
        printfn "Open http://localhost:5000 in your browser to see live metrics."
        printfn "Press Ctrl+C to stop."
    }

app.Lifetime.ApplicationStarted.Register(fun () ->
    startDashboard().GetAwaiter().GetResult())
|> ignore

app.Run("http://localhost:5000")
