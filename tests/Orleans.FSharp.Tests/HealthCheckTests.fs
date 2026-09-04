module Orleans.FSharp.Tests.HealthCheckTests

open Xunit
open Swensen.Unquote
open System
open System.Reflection
open System.Threading
open FsCheck
open FsCheck.Xunit
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Orleans.Runtime
open Orleans.FSharp.Runtime

type CurrentStatusOracleProxy() =
    inherit DispatchProxy()

    member val Status = SiloStatus.None with get, set

    override this.Invoke(targetMethod: MethodInfo, _arguments: obj array) : obj =
        match targetMethod.Name with
        | "get_CurrentStatus" -> box this.Status
        | methodName -> invalidOp $"The health-check test double does not implement {methodName}."

let private statusOracle status =
    let oracle = DispatchProxy.Create<ISiloStatusOracle, CurrentStatusOracleProxy>()
    (oracle :?> CurrentStatusOracleProxy).Status <- status
    oracle

let private runHealthCheck (healthCheck: IHealthCheck) =
    healthCheck.CheckHealthAsync(HealthCheckContext(), CancellationToken.None)

let private livenessCheck status =
    OrleansSiloLivenessHealthCheck(statusOracle status) :> IHealthCheck

let private readinessCheck status =
    OrleansSiloReadinessHealthCheck(statusOracle status) :> IHealthCheck

[<Fact>]
let ``siloConfig CE default has health checks disabled`` () =
    let config = siloConfig { () }
    test <@ config.EnableHealthChecks = false @>

[<Fact>]
let ``siloConfig CE enables health checks`` () =
    let config = siloConfig { enableHealthChecks }
    test <@ config.EnableHealthChecks = true @>

[<Fact>]
let ``siloConfig CE health checks compose with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            enableHealthChecks
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.EnableHealthChecks = true @>

[<Fact>]
let ``SiloConfig.Default has health checks disabled`` () =
    let config = SiloConfig.Default
    test <@ config.EnableHealthChecks = false @>

[<Fact>]
let ``enableHealthChecks registers tagged Orleans liveness and readiness checks`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            enableHealthChecks
        }

    let builder = Host.CreateApplicationBuilder()
    SiloConfig.applyToHost config builder
    use host = builder.Build()

    let options = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value

    let liveness =
        options.Registrations
        |> Seq.find (fun registration -> registration.Name = OrleansHealthChecks.LivenessName)

    let readiness =
        options.Registrations
        |> Seq.find (fun registration -> registration.Name = OrleansHealthChecks.ReadinessName)

    test <@ liveness.Tags.Contains OrleansHealthChecks.OrleansTag @>
    test <@ liveness.Tags.Contains OrleansHealthChecks.LivenessTag @>
    test <@ readiness.Tags.Contains OrleansHealthChecks.OrleansTag @>
    test <@ readiness.Tags.Contains OrleansHealthChecks.ReadinessTag @>

[<Fact>]
let ``first-class Orleans health registration resolves and executes both checks`` () =
    task {
        let services = ServiceCollection()
        services.AddLogging() |> ignore

        services.AddSingleton<ISiloStatusOracle>(statusOracle SiloStatus.Active)
        |> ignore

        OrleansHealthChecks.addSiloChecks services |> ignore

        use provider = services.BuildServiceProvider()
        let healthCheckService = provider.GetRequiredService<HealthCheckService>()
        let! report = healthCheckService.CheckHealthAsync(CancellationToken.None)

        Assert.Equal(HealthStatus.Healthy, report.Status)
        test <@ report.Entries.ContainsKey OrleansHealthChecks.LivenessName @>
        test <@ report.Entries.ContainsKey OrleansHealthChecks.ReadinessName @>
    }

[<Fact>]
let ``Orleans liveness fails only after the local silo is dead`` () =
    task {
        let! joining = runHealthCheck (livenessCheck SiloStatus.Joining)
        and! stopping = runHealthCheck (livenessCheck SiloStatus.Stopping)
        and! dead = runHealthCheck (livenessCheck SiloStatus.Dead)

        Assert.Equal(HealthStatus.Healthy, joining.Status)
        Assert.Equal(HealthStatus.Healthy, stopping.Status)
        Assert.Equal(HealthStatus.Unhealthy, dead.Status)
    }

[<Fact>]
let ``Orleans readiness succeeds only while the local silo is active`` () =
    task {
        let! active = runHealthCheck (readinessCheck SiloStatus.Active)
        and! joining = runHealthCheck (readinessCheck SiloStatus.Joining)
        and! shuttingDown = runHealthCheck (readinessCheck SiloStatus.ShuttingDown)

        Assert.Equal(HealthStatus.Healthy, active.Status)
        Assert.Equal(HealthStatus.Unhealthy, joining.Status)
        Assert.Equal(HealthStatus.Unhealthy, shuttingDown.Status)
    }

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``enableHealthChecks is idempotent — toggling twice still yields true`` () =
    let config =
        siloConfig {
            enableHealthChecks
            enableHealthChecks
        }

    config.EnableHealthChecks = true

[<Property>]
let ``enableHealthChecks does not disturb any storage provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config =
            siloConfig {
                addMemoryStorage name.Get
                enableHealthChecks
            }

        config.EnableHealthChecks = true
        && config.StorageProviders |> Map.containsKey name.Get)
