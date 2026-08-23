module Orleans.FSharp.Examples.Dashboard

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Dashboard
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.Hosting
open Orleans.Runtime

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      value: unit -> Task<int> }

let counterContract =
    grainContract<CounterActor, string, CounterApi> {
        grainType "dashboard.counter"
        version 1
        stringKey
        readOnly (_.value)
    }

let counterDefinition =
    grainFor counterContract {
        defaultState (fun () -> 0)

        handle (_.increment) (fun _context state () ->
            task {
                let next = state + 1
                return next, next
            })

        handleQuery (_.value) (fun _context state () -> Task.FromResult state)
    }

type CartActor = private CartActor of unit

[<NoEquality; NoComparison>]
type CartApi =
    { add: string -> Task<int>
      count: unit -> Task<int> }

let cartContract =
    grainContract<CartActor, string, CartApi> {
        grainType "dashboard.cart"
        version 1
        stringKey
        readOnly (_.count)
    }

let cartDefinition =
    grainFor cartContract {
        defaultState (fun () -> Set.empty<string>)

        handle (_.add) (fun _context state sku ->
            task {
                let next = state.Add sku
                return next, next.Count
            })

        handleQuery (_.count) (fun _context state () -> Task.FromResult state.Count)
    }

[<EntryPoint>]
let main args =
    let config =
        siloConfig {
            useLocalhostClustering
            siloPort 31111
            gatewayPort 40000
            addMemoryStorage "Default"
            addDashboardWithOptions 1000 100 false
        }

    let orleansHostBuilder =
        HostBuilder()
            .ConfigureLogging(fun logging -> logging.AddConsole() |> ignore)
            .UseOrleans(fun siloBuilder ->
                SiloConfig.applyToSiloBuilder config siloBuilder
                siloBuilder.AddFunctionalGrain(counterDefinition) |> ignore
                siloBuilder.AddFunctionalGrain(cartDefinition) |> ignore)

    let hostBuilder =
        orleansHostBuilder.ConfigureWebHost(fun webBuilder ->
            webBuilder
                .UseKestrel()
                .UseUrls("http://127.0.0.1:5080")
                .ConfigureServices(fun services -> services.AddRouting() |> ignore)
                .Configure(fun app ->
                app.UseDeveloperExceptionPage() |> ignore
                app.UseRouting() |> ignore

                app.UseEndpoints(fun endpoints ->
                    endpoints.MapGet(
                        "/exercise",
                        Func<IGrainFactory, Task<string>>(fun grainFactory ->
                            task {
                                let counter = FunctionalGrain.ref counterContract grainFactory "visits"
                                let cart = FunctionalGrain.ref cartContract grainFactory "demo-cart"

                                let! count = counter.increment ()
                                let! _ = cart.add $"sku-{count}"
                                let! items = cart.count ()

                                return
                                    $"dashboard.counter/visits = {count}; dashboard.cart/demo-cart = {items} items"
                            }))
                    |> ignore

                    endpoints.MapGet("/", Func<string>(fun () -> "Open /exercise, then /dashboard/"))
                    |> ignore

                    endpoints.MapOrleansDashboard("/dashboard") |> ignore)
                |> ignore)
            |> ignore)

    if args |> Array.contains "--verify-silo" then
        use host = hostBuilder.Build()

        task {
            do! host.StartAsync()

            use http = new HttpClient(BaseAddress = Uri("http://127.0.0.1:5080"))
            let! exercise = http.GetStringAsync("/exercise")
            use! dashboardResponse = http.GetAsync("/dashboard/ClusterStats")
            dashboardResponse.EnsureSuccessStatusCode() |> ignore
            printfn "%s" exercise

            let grainFactory = host.Services.GetRequiredService<IGrainFactory>()
            let counter = FunctionalGrain.ref counterContract grainFactory "visits"
            let cart = FunctionalGrain.ref cartContract grainFactory "demo-cart"
            let! _ = counter.increment ()
            let! _ = cart.add "sku-smoke"

            let management = grainFactory.GetGrain<IManagementGrain>(0L)
            let! statistics = management.GetSimpleGrainStatistics()

            statistics
            |> Array.filter (fun statistic ->
                statistic.GrainType.Contains("Orleans.FSharp.FunctionalGrainMarker", StringComparison.Ordinal))
            |> Array.iter (fun statistic ->
                printfn "%s: %d activation(s)" statistic.GrainType statistic.ActivationCount)

            do! host.StopAsync()
        }
        |> _.GetAwaiter().GetResult()
    else
        hostBuilder.Build().Run()

    0
