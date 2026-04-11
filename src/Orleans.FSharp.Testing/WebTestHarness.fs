namespace Orleans.FSharp.Testing

#nowarn "44"

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp

// ── Internal: ref-cell-based configurator for passing state into TestCluster ──

type private WebHarnessSiloConfigurator() =
    static let mutable _state: (ISiloBuilder -> unit) option * CapturingLoggerFactory option = (None, None)
    static member SetState(cfg, lf) = _state <- (cfg, lf)
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.AddMemoryStreams("StreamProvider") |> ignore
            siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore
            match _state with
            | (Some cfg, Some lf) ->
                cfg siloBuilder
                siloBuilder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(lf) |> ignore
            | _ -> ()

/// <summary>
/// A test harness that combines an Orleans TestCluster with an ASP.NET Core TestServer.
/// Enables end-to-end testing of HTTP → Controller → Grain → Response flows.
/// </summary>
type WebTestHarness =
    {
        /// <summary>The underlying Orleans TestCluster.</summary>
        Cluster: TestCluster
        /// <summary>The cluster client for direct grain communication.</summary>
        Client: IClusterClient
        /// <summary>HTTP client pointing to the in-memory test server.</summary>
        HttpClient: HttpClient
        /// <summary>The log capturing factory.</summary>
        LogFactory: CapturingLoggerFactory
    }

/// <summary>
/// A lightweight web test harness for endpoint tests which do not require an Orleans silo.
/// It injects a provided <see cref="IGrainFactory"/> (for example, <see cref="MockGrainFactory"/>)
/// into ASP.NET Core DI so endpoints using <c>FSharpGrain.ref</c> can run in-process.
/// </summary>
type WebUnitTestHarness =
    {
        /// <summary>HTTP client pointing to the in-memory test server.</summary>
        HttpClient: HttpClient
        /// <summary>The log capturing factory.</summary>
        LogFactory: CapturingLoggerFactory
    }

/// <summary>
/// Functions for creating and managing web test harnesses that combine
/// Orleans grain testing with ASP.NET Core HTTP testing.
/// </summary>
[<RequireQualifiedAccess>]
module WebTestHarness =
    let private grainFactoryConflictMessage =
        "WebTestHarness: configureWeb already registered IGrainFactory. Remove that registration and pass the desired factory via WebTestHarness.createWithFactory/createWithMockFactory."

    let private ensureNoGrainFactoryRegistration (services: IServiceCollection) =
        let hasRegistration =
            services
            |> Seq.exists (fun sd -> sd.ServiceType = typeof<IGrainFactory>)

        if hasRegistration then
            raise (InvalidOperationException grainFactoryConflictMessage)

    let private buildTestServer
        (configureWeb: IWebHostBuilder -> unit)
        (configureRequiredServices: IServiceCollection -> unit)
        : TestServer =
        let webHostBuilder =
            WebHostBuilder()
                .UseEnvironment("Testing")

        configureWeb webHostBuilder

        webHostBuilder.ConfigureServices(fun services ->
            ensureNoGrainFactoryRegistration services
            configureRequiredServices services)
        |> ignore

        new TestServer(webHostBuilder)

    /// <summary>
    /// Creates a WebTestHarness with custom silo and web host configuration.
    /// </summary>
    /// <param name="configureSilo">Action to configure the Orleans silo (storage, clustering, grain registration).</param>
    /// <param name="configureWeb">Action to configure the ASP.NET Core web host (controllers, middleware, auth).</param>
    /// <returns>A Task containing the initialized WebTestHarness.</returns>
    let create
        (configureSilo: ISiloBuilder -> unit)
        (configureWeb: IWebHostBuilder -> unit)
        : Task<WebTestHarness> =
        task {
            let logFactory = new CapturingLoggerFactory()

            // Set the shared state for the silo configurator
            WebHarnessSiloConfigurator.SetState(Some configureSilo, Some logFactory)

            // ── 1. Build and deploy Orleans TestCluster ──
            let clusterBuilder = TestClusterBuilder()
            clusterBuilder.Options.InitialSilosCount <- 1s
            clusterBuilder.AddSiloBuilderConfigurator<WebHarnessSiloConfigurator>() |> ignore

            let cluster = clusterBuilder.Build()
            do! cluster.DeployAsync()

            // ── 2. Build ASP.NET Core TestServer connected to the cluster ──
            let testServer =
                buildTestServer
                    configureWeb
                    (fun services ->
                        services.AddSingleton(cluster.Client) |> ignore
                        services.AddSingleton<IGrainFactory>(cluster.Client) |> ignore
                        services.AddSingleton<ILoggerFactory>(logFactory) |> ignore)

            let httpClient = testServer.CreateClient()

            return
                {
                    Cluster = cluster
                    Client = cluster.Client
                    HttpClient = httpClient
                    LogFactory = logFactory
                }
        }

    /// <summary>
    /// Creates a WebTestHarness with default in-memory Orleans configuration
    /// and a minimal web host (no controllers registered — call configureWeb to add them).
    /// </summary>
    /// <param name="configureWeb">Action to configure the ASP.NET Core web host.</param>
    /// <returns>A Task containing the initialized WebTestHarness.</returns>
    let createDefault (configureWeb: IWebHostBuilder -> unit) : Task<WebTestHarness> =
        create (fun _ -> ()) configureWeb

    /// <summary>
    /// Creates a lightweight web test harness without starting Orleans TestCluster.
    /// Use this for endpoint unit tests where grains are mocked via <see cref="IGrainFactory"/>.
    /// </summary>
    /// <param name="grainFactory">The grain factory to inject into endpoint DI.</param>
    /// <param name="configureWeb">Action to configure the ASP.NET Core web host.</param>
    /// <returns>A Task containing the initialized lightweight web harness.</returns>
    let createWithFactory
        (grainFactory: IGrainFactory)
        (configureWeb: IWebHostBuilder -> unit)
        : Task<WebUnitTestHarness> =
        task {
            let logFactory = new CapturingLoggerFactory()

            let testServer =
                buildTestServer
                    configureWeb
                    (fun services ->
                        services.AddSingleton<IGrainFactory>(grainFactory) |> ignore
                        services.AddSingleton<ILoggerFactory>(logFactory) |> ignore)

            let httpClient = testServer.CreateClient()

            return
                {
                    HttpClient = httpClient
                    LogFactory = logFactory
                }
        }

    /// <summary>
    /// Creates a lightweight web test harness using <see cref="MockGrainFactory"/>.
    /// </summary>
    /// <param name="configureFactory">Function to configure mock grain registrations.</param>
    /// <param name="configureWeb">Action to configure the ASP.NET Core web host.</param>
    /// <returns>A Task containing the initialized lightweight web harness.</returns>
    let createWithMockFactory
        (configureFactory: MockGrainFactory -> MockGrainFactory)
        (configureWeb: IWebHostBuilder -> unit)
        : Task<WebUnitTestHarness> =
        let mockFactory = configureFactory (GrainMock.create ())
        createWithFactory (mockFactory :> IGrainFactory) configureWeb

    /// <summary>
    /// Gets all captured log entries from the web test harness.
    /// </summary>
    /// <param name="harness">The web test harness to get logs from.</param>
    /// <returns>A list of captured log entries sorted by timestamp.</returns>
    let captureLogs (harness: WebTestHarness) : CapturedLogEntry list =
        LogCapture.captureLogs harness.LogFactory

    /// <summary>
    /// Gets all captured log entries from the lightweight web test harness.
    /// </summary>
    let captureUnitLogs (harness: WebUnitTestHarness) : CapturedLogEntry list =
        LogCapture.captureLogs harness.LogFactory

    /// <summary>
    /// Resets the web test harness state by clearing all captured log entries.
    /// </summary>
    /// <param name="harness">The web test harness to reset.</param>
    let reset (harness: WebTestHarness) : Task<unit> =
        task { harness.LogFactory.Clear() }

    /// <summary>
    /// Resets captured log entries for the lightweight web test harness.
    /// </summary>
    let resetUnit (harness: WebUnitTestHarness) : Task<unit> =
        task { harness.LogFactory.Clear() }

    /// <summary>
    /// Disposes the web test harness, stopping the Orleans cluster and releasing resources.
    /// The HttpClient is also disposed.
    /// </summary>
    /// <param name="harness">The web test harness to dispose.</param>
    let dispose (harness: WebTestHarness) : Task<unit> =
        task {
            if not (isNull (box harness.HttpClient)) then
                harness.HttpClient.Dispose()
            if not (isNull (box harness.Cluster)) then
                do! harness.Cluster.StopAllSilosAsync()
                harness.Cluster.Dispose()
        }

    /// <summary>
    /// Disposes the lightweight web test harness resources.
    /// </summary>
    let disposeUnit (harness: WebUnitTestHarness) : Task<unit> =
        task {
            if not (isNull (box harness.HttpClient)) then
                harness.HttpClient.Dispose()
        }
