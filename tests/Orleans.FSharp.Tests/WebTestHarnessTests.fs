module Orleans.FSharp.Tests.WebTestHarnessTests

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Testing
open Swensen.Unquote
open Xunit

type CompanyState = { Count: int }
type CompanyCommand =
    | Increment
    | GetCount

let private companyDefinition =
    grain {
        defaultState { Count = 0 }
        handle (fun state (cmd: CompanyCommand) ->
            task {
                match cmd with
                | Increment ->
                    let next = { Count = state.Count + 1 }
                    return next, box next
                | GetCount ->
                    return state, box state
            })
    }

let private configureCompanyEndpoint (builder: IWebHostBuilder) =
    builder.Configure(fun app ->
        app.Run(fun ctx ->
            task {
                let factory = ctx.RequestServices.GetRequiredService<IGrainFactory>()
                let id = ctx.Request.Query.["id"].ToString()
                let key = if String.IsNullOrWhiteSpace id then "company-1" else id
                let handle = FSharpGrain.ref<CompanyState, CompanyCommand> factory key
                let! state = handle |> FSharpGrain.send Increment
                return! ctx.Response.WriteAsync(string state.Count)
            } :> Task))
    |> ignore

[<Fact>]
let ``createWithFactory wires mocked IGrainFactory into endpoint without silo`` () =
    task {
        let factory =
            GrainMock.create ()
            |> GrainMock.withFSharpGrain "company-1" companyDefinition
            :> IGrainFactory

        let! harness = WebTestHarness.createWithFactory factory configureCompanyEndpoint

        try
            let! first = harness.HttpClient.GetStringAsync("/?id=company-1")
            let! second = harness.HttpClient.GetStringAsync("/?id=company-1")
            test <@ first = "1" @>
            test <@ second = "2" @>
        finally
            harness.HttpClient.Dispose()
    }

[<Fact>]
let ``createWithMockFactory supports fluent GrainMock registration`` () =
    task {
        let! harness =
            WebTestHarness.createWithMockFactory
                (fun factory -> factory |> GrainMock.withFSharpGrain "company-2" companyDefinition)
                configureCompanyEndpoint

        try
            let! result = harness.HttpClient.GetStringAsync("/?id=company-2")
            test <@ result = "1" @>
        finally
            harness.HttpClient.Dispose()
    }

[<Fact>]
let ``createWithFactory fails fast when configureWeb already registers IGrainFactory`` () =
    task {
        let providedFactory = GrainMock.create () :> IGrainFactory

        let configureWebWithConflict (builder: IWebHostBuilder) =
            let conflictingFactory = GrainMock.create () :> IGrainFactory

            builder.ConfigureServices(fun services ->
                services.AddSingleton<IGrainFactory>(conflictingFactory) |> ignore)
            |> ignore

            builder.Configure(fun app ->
                app.Run(fun _ -> Task.CompletedTask))
            |> ignore

        let! ex =
            Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                WebTestHarness.createWithFactory providedFactory configureWebWithConflict :> Task)

        test <@ ex.Message.Contains("IGrainFactory") @>
    }
