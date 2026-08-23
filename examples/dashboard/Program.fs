module Orleans.FSharp.Examples.Dashboard

open System
open System.Net.Http
open System.Text.Json
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

[<NoEquality; NoComparison>]
type DashboardActivation =
    { grainType: string
      activationCount: int }

type DashboardVerification =
    { counter: DashboardActivation
      cart: DashboardActivation }

module DashboardCounters =
    let private tryProperty name (element: JsonElement) =
        element.EnumerateObject()
        |> Seq.tryFind (fun property -> property.Name = name)
        |> Option.map _.Value

    let private parseRow index (row: JsonElement) =
        if row.ValueKind <> JsonValueKind.Object then
            Error $"simpleGrainStats[{index}] is not a JSON object"
        else
            match tryProperty "grainType" row, tryProperty "activationCount" row with
            | Some grainType, Some activationCount
                when grainType.ValueKind = JsonValueKind.String
                     && activationCount.ValueKind = JsonValueKind.Number ->
                match grainType.GetString(), activationCount.TryGetInt32() with
                | grainType, (true, activationCount) when not (String.IsNullOrWhiteSpace grainType) ->
                    Ok
                        { grainType = grainType
                          activationCount = activationCount }
                | _ -> Error $"simpleGrainStats[{index}] has invalid grainType or activationCount values"
            | _ -> Error $"simpleGrainStats[{index}] is missing grainType or activationCount"

    let private collectRows rows =
        rows
        |> Seq.mapi parseRow
        |> Seq.fold
            (fun collected row ->
                match collected, row with
                | Ok collectedRows, Ok parsedRow -> Ok(parsedRow :: collectedRows)
                | Error error, _
                | _, Error error -> Error error)
            (Ok [])
        |> Result.map List.rev

    let parse (payload: string) =
        try
            use document = JsonDocument.Parse payload

            if document.RootElement.ValueKind <> JsonValueKind.Object then
                Error "DashboardCounters root is not a JSON object"
            else
                match tryProperty "simpleGrainStats" document.RootElement with
                | Some rows when rows.ValueKind = JsonValueKind.Array -> rows.EnumerateArray() |> collectRows
                | Some _ -> Error "DashboardCounters.simpleGrainStats is not a JSON array"
                | None -> Error "DashboardCounters is missing simpleGrainStats"
        with :? JsonException as exception' ->
            Error $"DashboardCounters returned invalid JSON: {exception'.Message}"

    let private requireActive expectedGrainType rows =
        match rows |> List.tryFind (fun row -> row.grainType = expectedGrainType) with
        | Some row when row.activationCount > 0 -> Ok row
        | Some row -> Error $"{expectedGrainType} has ActivationCount={row.activationCount}; expected > 0"
        | None -> Error $"simpleGrainStats does not contain {expectedGrainType}"

    let verify (payload: string) =
        let counterType =
            "Orleans.FSharp.FunctionalGrainMarker<Orleans.FSharp.Examples.Dashboard+CounterActor>"

        let cartType =
            "Orleans.FSharp.FunctionalGrainMarker<Orleans.FSharp.Examples.Dashboard+CartActor>"

        parse payload
        |> Result.bind (fun rows ->
            match requireActive counterType rows, requireActive cartType rows with
            | Ok counter, Ok cart -> Ok { counter = counter; cart = cart }
            | Error error, _
            | _, Error error -> Error error)

module DashboardApi =
    let rec waitForActivations (http: HttpClient) remainingAttempts =
        task {
            let! payload = http.GetStringAsync("/dashboard/DashboardCounters")

            match DashboardCounters.verify payload with
            | Ok verification -> return Ok verification
            | Error _ when remainingAttempts > 1 ->
                do! Task.Delay 250
                return! waitForActivations http (remainingAttempts - 1)
            | Error error ->
                return Error $"Dashboard API did not report both activations within 10 seconds: {error}"
        }

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

            let! verification =
                task {
                    try
                        use http = new HttpClient(BaseAddress = Uri("http://127.0.0.1:5080"))
                        let! exercise = http.GetStringAsync("/exercise")
                        let! dashboardVerification = DashboardApi.waitForActivations http 40
                        return dashboardVerification |> Result.map (fun result -> exercise, result)
                    with exception' ->
                        return Error $"Dashboard API verification failed: {exception'.Message}"
                }

            do! host.StopAsync()

            match verification with
            | Ok(exercise, verified) ->
                printfn $"EXERCISE_OK {exercise}"
                printfn "DASHBOARD_API_OK endpoint=/dashboard/DashboardCounters collection=simpleGrainStats"

                printfn
                    $"DASHBOARD_API_ACTIVATION actor=CounterActor ActivationCount={verified.counter.activationCount} grainType={verified.counter.grainType}"

                printfn
                    $"DASHBOARD_API_ACTIVATION actor=CartActor ActivationCount={verified.cart.activationCount} grainType={verified.cart.grainType}"
            | Error error ->
                return raise (InvalidOperationException error)
        }
        |> _.GetAwaiter().GetResult()
    else
        hostBuilder.Build().Run()

    0
