namespace Orleans.FSharp

open Orleans.Hosting

/// <summary>
/// Constants and functions for configuring OpenTelemetry observability with Orleans.
/// Orleans emits traces via Activity sources and metrics via Meters that can be
/// collected by any OpenTelemetry-compatible backend.
/// </summary>
[<RequireQualifiedAccess>]
module Telemetry =

    /// <summary>
    /// The Orleans activity source name for runtime tracing.
    /// Add this source to your OpenTelemetry tracing configuration via <c>AddSource</c>.
    /// </summary>
    [<Literal>]
    let runtimeActivitySourceName = "Microsoft.Orleans.Runtime"

    /// <summary>
    /// The Orleans activity source name for application-level tracing.
    /// Add this source to your OpenTelemetry tracing configuration via <c>AddSource</c>.
    /// </summary>
    [<Literal>]
    let applicationActivitySourceName = "Microsoft.Orleans.Application"

    /// <summary>
    /// The Orleans meter name for metrics collection.
    /// Add this meter to your OpenTelemetry metrics configuration via <c>AddMeter</c>.
    /// </summary>
    [<Literal>]
    let meterName = "Microsoft.Orleans"

    /// <summary>
    /// Enables activity propagation on the silo builder, which is required for
    /// distributed tracing to work correctly across Orleans grains and services.
    /// </summary>
    /// <param name="siloBuilder">The ISiloBuilder to configure.</param>
    /// <returns>The configured ISiloBuilder with activity propagation enabled.</returns>
    let enableActivityPropagation (siloBuilder: ISiloBuilder) : ISiloBuilder =
        siloBuilder.AddActivityPropagation()

    /// <summary>
    /// The list of Orleans activity source names for OpenTelemetry tracing configuration.
    /// Pass these to <c>AddSource</c> when configuring your tracing pipeline.
    /// </summary>
    let activitySourceNames: string list =
        [ runtimeActivitySourceName; applicationActivitySourceName ]
