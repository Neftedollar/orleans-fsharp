# Orleans.FSharp.Runtime

F# computation expressions for configuring and starting Orleans silos and clients.

## What it does

Provides `siloConfig {}` and `clientConfig {}` computation expressions that replace verbose `ISiloBuilder` / `IClientBuilder` chains with a declarative, type-safe F# API. Also includes `GrainDiscovery` (automatic grain registration) and `SerilogIntegration`.

## Quick example

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    localhost
    memoryStorage "Default"
    memoryStream "StreamProvider"
    memoryReminder
    useSerilog
    healthChecks
}

let host = config |> SiloConfig.buildHost
do! host.StartAsync()
```

## Supported providers

| Category | Options |
|----------|---------|
| **Clustering** | Localhost, Redis, Azure Table, ADO.NET, Custom |
| **Storage** | Memory, Redis, Azure Blob, Azure Table, ADO.NET, Cosmos DB, DynamoDB, Custom |
| **Streaming** | Memory, Persistent (adapter factory), Custom |
| **Reminders** | Memory, Redis, Custom |
| **Security** | TLS (subject/cert), Mutual TLS (subject/cert) |
| **Observability** | Serilog, Dashboard (default/custom options), Health Checks |
| **Other** | Broadcast channels, versioning, grain call filters, grain services, lifecycle hooks |

The `siloConfig {}` CE supports 39 custom operations; `clientConfig {}` supports 11.

## Requirements

- .NET 10+
- `Orleans.FSharp` (pulled in automatically)

## License

MIT
