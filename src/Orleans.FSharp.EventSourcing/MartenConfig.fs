namespace Orleans.FSharp.EventSourcing

open Orleans.Hosting

/// <summary>
/// Configuration helpers for Marten-based event store integration with Orleans.
/// Currently provides a placeholder for future Marten integration when an Orleans 10-compatible
/// Marten log consistency provider becomes available. In the meantime, use the built-in
/// Orleans <c>AddLogStorageBasedLogConsistencyProviderAsDefault</c> for development and testing.
/// </summary>
[<RequireQualifiedAccess>]
module MartenConfig =

    /// <summary>
    /// Placeholder for future Marten event store integration.
    /// When an Orleans 10-compatible Marten log consistency provider is available,
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
            // Future: configure Marten as event store here.
            // For now, use Orleans built-in log storage as a stand-in.
            siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault()
            |> ignore

            siloBuilder

    /// <summary>
    /// Adds the Orleans built-in log storage-based log consistency provider as the default.
    /// This is suitable for development and testing with in-memory or simple storage backends.
    /// </summary>
    /// <param name="siloBuilder">The ISiloBuilder to configure.</param>
    /// <returns>The configured ISiloBuilder.</returns>
    let addLogStorageDefault (siloBuilder: ISiloBuilder) : ISiloBuilder =
        siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault()
        |> ignore

        siloBuilder

    /// <summary>
    /// Adds the Orleans built-in log storage-based log consistency provider with a specific name.
    /// </summary>
    /// <param name="name">The provider name (e.g., "LogStorage").</param>
    /// <param name="siloBuilder">The ISiloBuilder to configure.</param>
    /// <returns>The configured ISiloBuilder.</returns>
    let addLogStorage (name: string) (siloBuilder: ISiloBuilder) : ISiloBuilder =
        siloBuilder.AddLogStorageBasedLogConsistencyProvider(name)
        |> ignore

        siloBuilder
