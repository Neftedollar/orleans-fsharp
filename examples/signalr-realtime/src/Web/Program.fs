open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime
open SignalRRealtime.Grains
open SignalRRealtime.Web.Hubs

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

// Functional-runtime equivalent of the grain above -- see DashboardGrainFunctional.fs.
builder.Host.UseOrleans(fun siloBuilder ->
    siloBuilder.AddFunctionalGrain(DashboardFunctionalDef.dashboard) |> ignore)
|> ignore

builder.Services.AddSignalR() |> ignore

let app = builder.Build()

app.UseDefaultFiles() |> ignore
app.UseStaticFiles() |> ignore
app.MapHub<DashboardHub>("/dashboard") |> ignore

// The dashboard grain's timer starts automatically on activation via the declarative onTimer.
// Just activate the grain so the timer begins firing.
let startDashboard () =
    task {
        let factory = app.Services.GetRequiredService<IGrainFactory>()
        let dashboard = factory.GetGrain<IDashboardGrain>("default")
        // Activate the grain by sending a command. The declarative timer will start automatically.
        let! _ = dashboard.HandleMessage(GetSequenceNumber)

        // Functional-runtime equivalent of the activation above (same dashboard domain).
        let dashboardFn = DashboardApi.ref factory "default-functional"
        let! _ = dashboardFn.tick ()
        printfn "--- SignalR Realtime: Dashboard grain activated with timer ---"
        printfn "Open http://localhost:5000 in your browser to see live metrics."
        printfn "Press Ctrl+C to stop."
    }

app.Lifetime.ApplicationStarted.Register(fun () ->
    startDashboard().GetAwaiter().GetResult())
|> ignore

app.Run("http://localhost:5000")
