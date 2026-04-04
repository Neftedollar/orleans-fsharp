namespace Orleans.FSharp.EventSourcing.Marten

open Orleans.Hosting

/// <summary>
/// Configuration helpers for Marten-based event store integration with Orleans.
/// Add the <c>Orleans.FSharp.EventSourcing.Marten</c> NuGet package to use Marten
/// (PostgreSQL) as your event store backend.
/// </summary>
[<RequireQualifiedAccess>]
module MartenConfig =

    /// <summary>
    /// Placeholder for Marten event store integration.
    /// When an Orleans 10-compatible Marten log consistency provider becomes available,
    /// this function will configure Marten as the event store backend.
    /// For now, it configures Orleans built-in log storage as a default.
    /// </summary>
    /// <param name="connectionString">
    /// The PostgreSQL connection string for Marten.
    /// Currently unused; reserved for future Marten integration.
    /// </param>
    /// <returns>A function that configures an ISiloBuilder with log storage-based consistency.</returns>
    let addMartenEventStore (_connectionString: string) : (ISiloBuilder -> ISiloBuilder) =
        fun (siloBuilder: ISiloBuilder) ->
            // TODO: configure Marten as event store once Orleans 10-compatible
            // Marten log consistency provider is available.
            siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault()
            |> ignore

            siloBuilder

    /// <summary>
    /// Adds the Orleans built-in log storage-based log consistency provider as the default.
    /// Suitable for development and testing.
    /// </summary>
    let addLogStorageDefault (siloBuilder: ISiloBuilder) : ISiloBuilder =
        siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault()
        |> ignore

        siloBuilder

    /// <summary>
    /// Adds the Orleans built-in log storage-based log consistency provider with a specific name.
    /// </summary>
    let addLogStorage (name: string) (siloBuilder: ISiloBuilder) : ISiloBuilder =
        siloBuilder.AddLogStorageBasedLogConsistencyProvider(name)
        |> ignore

        siloBuilder
