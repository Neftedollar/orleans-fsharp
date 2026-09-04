namespace Orleans.FSharp.Runtime

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Orleans.Runtime

/// <summary>
/// A local liveness check for an Orleans silo.
/// </summary>
/// <remarks>
/// This check reports healthy while the silo process is progressing through its lifecycle and
/// unhealthy only after Orleans reports the local silo as <see cref="SiloStatus.Dead"/>. It does
/// not probe other silos or application dependencies.
/// </remarks>
[<Sealed>]
type OrleansSiloLivenessHealthCheck(statusOracle: ISiloStatusOracle) =

    do ArgumentNullException.ThrowIfNull(statusOracle)

    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, _cancellationToken: CancellationToken) =
            let status = statusOracle.CurrentStatus

            if status = SiloStatus.Dead then
                HealthCheckResult.Unhealthy($"Orleans reports the local silo as {status}.")
                |> Task.FromResult
            else
                HealthCheckResult.Healthy($"Orleans reports the local silo as {status}.")
                |> Task.FromResult

/// <summary>
/// A local readiness check for an Orleans silo.
/// </summary>
/// <remarks>
/// This check reports healthy only while Orleans reports the local silo as
/// <see cref="SiloStatus.Active"/>. It does not verify application-level dependencies.
/// </remarks>
[<Sealed>]
type OrleansSiloReadinessHealthCheck(statusOracle: ISiloStatusOracle) =

    do ArgumentNullException.ThrowIfNull(statusOracle)

    interface IHealthCheck with
        member _.CheckHealthAsync(_context: HealthCheckContext, _cancellationToken: CancellationToken) =
            let status = statusOracle.CurrentStatus

            if status = SiloStatus.Active then
                HealthCheckResult.Healthy("The Orleans silo is active and ready to accept work.")
                |> Task.FromResult
            else
                HealthCheckResult.Unhealthy($"The Orleans silo is not ready because its status is {status}.")
                |> Task.FromResult

/// <summary>
/// Registration names and tags for the Orleans silo health checks.
/// </summary>
[<RequireQualifiedAccess>]
module OrleansHealthChecks =

    /// <summary>The registered local-silo liveness check name.</summary>
    [<Literal>]
    let LivenessName = "orleans-silo-liveness"

    /// <summary>The registered local-silo readiness check name.</summary>
    [<Literal>]
    let ReadinessName = "orleans-silo-readiness"

    /// <summary>Tag shared by both Orleans health checks.</summary>
    [<Literal>]
    let OrleansTag = "orleans"

    /// <summary>Tag identifying the liveness check.</summary>
    [<Literal>]
    let LivenessTag = "live"

    /// <summary>Tag identifying the readiness check.</summary>
    [<Literal>]
    let ReadinessTag = "ready"

    let private registration<'T when 'T :> IHealthCheck>
        (name: string)
        (probeTag: string)
        : HealthCheckRegistration =
        HealthCheckRegistration(
            name,
            Func<IServiceProvider, IHealthCheck>(fun services ->
                ActivatorUtilities.GetServiceOrCreateInstance<'T>(services) :> IHealthCheck),
            Nullable HealthStatus.Unhealthy,
            [ OrleansTag; probeTag ]
        )

    /// <summary>
    /// Registers local Orleans silo liveness and readiness checks.
    /// </summary>
    /// <remarks>
    /// The containing host must be configured as an Orleans silo so that
    /// <see cref="ISiloStatusOracle"/> is available. This method registers checks only; callers
    /// remain responsible for mapping health-check endpoints and selecting them by tag.
    /// </remarks>
    /// <param name="services">The host service collection.</param>
    /// <returns>The health-check builder for further registrations.</returns>
    let addSiloChecks (services: IServiceCollection) : IHealthChecksBuilder =
        ArgumentNullException.ThrowIfNull(services)

        let builder = services.AddHealthChecks()

        builder.Add(registration<OrleansSiloLivenessHealthCheck> LivenessName LivenessTag)
        |> ignore

        builder.Add(registration<OrleansSiloReadinessHealthCheck> ReadinessName ReadinessTag)
