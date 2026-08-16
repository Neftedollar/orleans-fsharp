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

(*
    Classic grain { } model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so a bare `factory.GetGrain<IDashboardGrain>(...)` fails with
    "Could not find an implementation for interface IDashboardGrain" the moment it runs. See
    docs/functional-grains.md, "Running a silo from a standalone F# process" for the exact
    mechanism, and "Migrating from the grain { } CE" for the rewrite this file demonstrates.

    let factory = app.Services.GetRequiredService<IGrainFactory>()
    let dashboard = factory.GetGrain<IDashboardGrain>("default")
    // Activate the grain by sending a command. The declarative timer will start automatically.
    let! _ = dashboard.HandleMessage(GetSequenceNumber)
*)

// The dashboard grain's timer starts automatically on activation via the declarative onTimer.
// Just activate the grain so the timer begins firing -- the same "default" key the hub
// (DashboardHub.fs) talks to, so the hub's first connection sees a grain that has already been
// ticking for a moment.
let startDashboard () =
    task {
        let factory = app.Services.GetRequiredService<IGrainFactory>()
        let dashboardFn = DashboardApi.ref factory "default"
        let! seeded = dashboardFn.tick ()
        printfn "--- SignalR Realtime: Dashboard grain activated with timer (Functional Grain Runtime) ---"
        printfn "Sequence number after activation tick: %d" seeded
        printfn "Open http://localhost:5000 in your browser to see live metrics."
        printfn "Press Ctrl+C to stop."
    }

app.Lifetime.ApplicationStarted.Register(fun () ->
    startDashboard().GetAwaiter().GetResult())
|> ignore

app.Run("http://localhost:5000")
