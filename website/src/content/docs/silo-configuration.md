---
title: "Silo Configuration"
description: "Complete siloConfig CE reference."
---

# Silo Configuration

**Complete guide to the `siloConfig { }` computation expression.**

> **Current API.** `siloConfig { }` configures hosting; functional definitions bind the named providers configured here.

## What you'll learn

- How to configure clustering, storage, streaming, and reminders
- TLS/mTLS for secure silo communication
- Dashboard, health checks, and startup tasks
- Grain versioning, collection age, and endpoints
- Call filters and grain services

## Overview

The `siloConfig { }` CE builds a `SiloConfig` record. Apply it to a host with `SiloConfig.applyToHost`:

```fsharp
open Orleans.FSharp.Runtime

let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
}

let builder = HostApplicationBuilder()
SiloConfig.applyToHost config builder
let host = builder.Build()
host.Run()
```

Or apply directly to an `ISiloBuilder` with `SiloConfig.applyToSiloBuilder`.

## Startup validation

Functional definitions are checked at `ServiceLifecycleStage.First`, before Orleans reaches
`RuntimeInitialize`. The checks cover the local manifest, serializers, named providers, and
functional configuration limits. A rejected definition therefore fails before that runtime
stage starts background services; it does not leave them running after a failed startup.

Register the required providers and configure their options before building the host. Startup
callbacks are not a place to finish these registrations.

---

## Clustering

### Localhost (development)

```fsharp
siloConfig { useLocalhostClustering }
```

### Redis

Requires `Microsoft.Orleans.Clustering.Redis`.

```fsharp
let connStr = Environment.GetEnvironmentVariable("REDIS_CONNECTION")

siloConfig {
    addRedisClustering connStr
    addMemoryStorage "Default"
}
```

### Azure Table

Requires `Microsoft.Orleans.Clustering.AzureStorage`.

```fsharp
let connStr = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION")

siloConfig {
    addAzureTableClustering connStr
    addMemoryStorage "Default"
}
```

### ADO.NET (PostgreSQL, SQL Server)

Requires `Microsoft.Orleans.Clustering.AdoNet`.

```fsharp
let connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")

siloConfig {
    addAdoNetClustering connStr "Npgsql"
    addMemoryStorage "Default"
}
```

The second argument is the ADO.NET provider invariant. Common values: `"Npgsql"` (PostgreSQL), `"System.Data.SqlClient"` (SQL Server), `"MySql.Data.MySqlClient"` (MySQL).

### Kubernetes

Use the `Kubernetes` module for automatic discovery via the Kubernetes API:

```fsharp
open Orleans.FSharp.Kubernetes

siloConfig {
    // Use CustomClustering to wire Kubernetes hosting
    addCustomStorage "Default" (fun sb -> sb)
}

// Apply Kubernetes clustering separately on the ISiloBuilder
Kubernetes.useKubernetesClustering siloBuilder |> ignore
```

Requires `Microsoft.Orleans.Hosting.Kubernetes`.

---

## Storage Providers

### In-memory

Data is lost on silo restart. Good for development and testing.

```fsharp
siloConfig { addMemoryStorage "Default" }
```

### Redis

Requires `Microsoft.Orleans.Persistence.Redis`.

```fsharp
let connStr = Environment.GetEnvironmentVariable("REDIS_CONNECTION")

siloConfig { addRedisStorage "Default" connStr }
```

### Azure Blob

Requires `Microsoft.Orleans.Persistence.AzureStorage`.

```fsharp
let connStr = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION")

siloConfig { addAzureBlobStorage "Default" connStr }
```

### Azure Table

Requires `Microsoft.Orleans.Persistence.AzureStorage`.

```fsharp
siloConfig { addAzureTableStorage "Default" connStr }
```

### ADO.NET

Requires `Microsoft.Orleans.Persistence.AdoNet`.

```fsharp
siloConfig { addAdoNetStorage "Default" connStr "Npgsql" }
```

### Cosmos DB

Requires `Microsoft.Orleans.Persistence.Cosmos`.

```fsharp
let endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT")

siloConfig { addCosmosStorage "Default" endpoint "MyDatabase" }
```

### DynamoDB

Requires `Microsoft.Orleans.Persistence.DynamoDB`.

```fsharp
siloConfig { addDynamoDbStorage "Default" "us-east-1" }
```

### Custom

For any storage provider not covered above:

```fsharp
siloConfig {
    addCustomStorage "MyStore" (fun siloBuilder ->
        siloBuilder.AddMyCustomStorage("MyStore")
    )
}
```

### Multiple providers

You can register multiple storage providers with different names:

```fsharp
siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    addRedisStorage "Cache" redisConnStr
    addAzureBlobStorage "Archive" azureConnStr
}
```

Bind provider names to functional persistent-state descriptors, then attach a descriptor to a definition:

```fsharp
let cachedState = PersistentState.create<MyState> "state" "Cache"
let archivedState = PersistentState.create<MyState> "state" "Archive"

let cachedDefinition = grainFor cachedContract {
    stateFrom cachedState
    // handlers call context.persistentState cachedState when they read or write
}
```

---

## Streaming

### In-memory streams

```fsharp
siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    addMemoryStreams "StreamProvider"
}
```

A `PubSubStore` memory storage is automatically added if not already configured.

### Persistent streams

For durable streams backed by queues (Event Hubs, Redis, etc.):

```fsharp
siloConfig {
    addPersistentStreams "MyStreams" adapterFactory configurator
}
```

### Event Hubs

Use the `StreamProviders` module:

```fsharp
open Orleans.FSharp.StreamProviders

let connStr = Environment.GetEnvironmentVariable("EVENTHUB_CONNECTION")
let configureFn = StreamProviders.addEventHubStreams "EventHubProvider" connStr "my-hub"

// Apply to siloBuilder directly
configureFn siloBuilder |> ignore
```

### Azure Queue

```fsharp
let configureFn = StreamProviders.addAzureQueueStreams "QueueProvider" azureConnStr
configureFn siloBuilder |> ignore
```

### Broadcast channels

Broadcast channels deliver messages to ALL subscriber grains (fan-out):

```fsharp
siloConfig {
    addBroadcastChannel "Notifications"
}
```

---

## Reminders

### In-memory (development)

```fsharp
siloConfig { addMemoryReminderService }
```

### Redis

Requires `Microsoft.Orleans.Reminders.Redis`.

```fsharp
siloConfig { addRedisReminderService connStr }
```

### Custom

```fsharp
siloConfig {
    addCustomReminderService (fun siloBuilder ->
        siloBuilder.UseMyReminderService()
    )
}
```

---

## TLS / mTLS

### TLS by subject name

Requires `Microsoft.Orleans.Connections.Security`.

```fsharp
siloConfig {
    useLocalhostClustering
    useTls "CN=my-silo-cert"
}
```

### TLS with certificate instance

```fsharp
let cert = new X509Certificate2("path/to/cert.pfx", "password")

siloConfig {
    useLocalhostClustering
    useTlsWithCertificate cert
}
```

### Mutual TLS (mTLS)

```fsharp
siloConfig {
    useLocalhostClustering
    useMutualTls "CN=my-silo-cert"
}
```

### Mutual TLS with certificate instance

```fsharp
siloConfig {
    useLocalhostClustering
    useMutualTlsWithCertificate cert
}
```

**Warning:** Always use valid certificates from a trusted CA in production. Never disable certificate validation in production environments.

---

## Dashboard

Requires `Microsoft.Orleans.Dashboard`.

### Default options

```fsharp
siloConfig {
    useLocalhostClustering
    addDashboard
}
```

### Custom options

```fsharp
siloConfig {
    useLocalhostClustering
    addDashboardWithOptions
        5000    // counter update interval (ms)
        100     // history length
        false   // hide trace
}
```

Map the dashboard endpoints in your ASP.NET Core pipeline with `MapOrleansDashboard()`.
See [Orleans Dashboard](/orleans-fsharp/dashboard/) for the runnable functional-actor example, exact UI names, and production authorization guidance.

---

## Health Checks

```fsharp
siloConfig {
    useLocalhostClustering
    enableHealthChecks
}
```

`enableHealthChecks` registers separate Orleans liveness and readiness checks; it does not map
HTTP endpoints. Keep those probes separate in an ASP.NET Core host:

```fsharp
open System
open Microsoft.AspNetCore.Diagnostics.HealthChecks
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Diagnostics.HealthChecks
open Orleans.FSharp.Runtime

let onlyTag tag =
    let options = HealthCheckOptions()
    options.Predicate <-
        Func<HealthCheckRegistration, bool>(fun registration ->
            registration.Tags |> Seq.contains tag)
    options

app.MapHealthChecks(
    "/health/live",
    onlyTag OrleansHealthChecks.LivenessTag)
|> ignore

app.MapHealthChecks(
    "/health/ready",
    onlyTag OrleansHealthChecks.ReadinessTag)
|> ignore
```

Liveness stays healthy throughout normal silo startup and shutdown and becomes unhealthy only when
Orleans reports the local silo as `Dead`. Readiness is healthy only while the silo is `Active`.
Do not use an unfiltered endpoint as a Kubernetes liveness probe: it would also execute readiness
and could restart a healthy process while it is joining or draining. Use
`OrleansHealthChecks.addSiloChecks` when configuring services without the `siloConfig` builder.

---

## Startup Tasks

Run code when the silo starts:

```fsharp
siloConfig {
    useLocalhostClustering
    addStartupTask (fun sp ct ->
        task {
            let logger = sp.GetRequiredService<ILogger<_>>()
            logger.LogInformation("Silo started!")
        } :> Task)
}
```

Multiple startup tasks accumulate and run in registration order.

---

## Grain Versioning

Control how grains of different versions communicate during rolling upgrades:

```fsharp
open Orleans.FSharp.Versioning

siloConfig {
    useLocalhostClustering
    useGrainVersioning BackwardCompatible AllCompatibleVersions
}
```

Functional contract `version` is published as this native Orleans interface version. During a
rolling update, Orleans' compatibility/selector pair decides which silo can receive the call;
the selected functional host then applies its own `acceptsVersions` envelope policy. Configure
both layers consistently. `StrictVersion` plus the default functional `Exact` policy is exact at
both boundaries.

Compatibility strategies:

| Strategy | Description |
|---|---|
| `BackwardCompatible` | Older versions can call newer (default) |
| `StrictVersion` | Only exact version matches |
| `AllVersions` | All versions interoperate |

Version selector strategies:

| Strategy | Description |
|---|---|
| `AllCompatibleVersions` | Random among compatible (default) |
| `LatestVersion` | Always activate latest |
| `MinimumVersion` | Always activate oldest |

---

## Grain Collection Age

Set the global idle timeout before grain deactivation:

```fsharp
siloConfig {
    useLocalhostClustering
    grainCollectionAge (TimeSpan.FromMinutes 30.)
}
```

`grainCollectionAge` is the global idle-deactivation timeout. A functional definition can override it with the `collectionAge` operation in `grainFor { }`.

---

## Endpoints

### Cluster identity

```fsharp
siloConfig {
    useLocalhostClustering
    clusterId "my-cluster"
    serviceId "my-service"
    siloName "silo-1"
}
```

### Ports and IP

```fsharp
siloConfig {
    useLocalhostClustering
    siloPort 11111
    gatewayPort 30000
    advertisedIpAddress "10.0.0.1"
}
```

---

## Call Filters

### Incoming filters

Intercept calls arriving at a grain:

```fsharp
open Orleans.FSharp

let loggingFilter =
    Filter.incoming (fun ctx ->
        task {
            printfn "Incoming call: %s" (FilterContext.methodName ctx)
            do! ctx.Invoke()
            printfn "Call completed"
        })

siloConfig {
    useLocalhostClustering
    addIncomingFilter loggingFilter
}
```

### Outgoing filters

Intercept calls made from a grain to another grain:

```fsharp
let tracingFilter =
    Filter.outgoing (fun ctx ->
        task {
            // Add tracing headers
            do! ctx.Invoke()
        })

siloConfig {
    useLocalhostClustering
    addOutgoingFilter tracingFilter
}
```

### Before/after filters

```fsharp
let timingFilter =
    Filter.incomingWithAround
        (fun ctx -> task { printfn "Before %s" (FilterContext.methodName ctx) })
        (fun ctx -> task { printfn "After %s" (FilterContext.methodName ctx) })
```

---

## Grain Services

Grain services run on every silo. Use them for background processing:

```fsharp
siloConfig {
    useLocalhostClustering
    addGrainService typeof<MyBackgroundService>
}
```

---

## Custom DI Services

Register custom services with the host's DI container:

```fsharp
siloConfig {
    useLocalhostClustering
    configureServices (fun services ->
        services.AddSingleton<IMyService, MyService>() |> ignore
        services.AddHttpClient() |> ignore)
}
```

---

## Serialization

Select one generalized-serialization policy. Functional definition registration installs the
binary default; configure an explicit policy when this silo should choose JSON or use it only for
CLR types unsupported by the binary codec:

```fsharp
let serialization =
    FSharpSerialization.Binary
    |> FSharpSerialization.forUnsupportedTypes FSharpSerialization.Json

siloConfig {
    useLocalhostClustering
    useFSharpSerialization serialization
}
```

Use `useFSharpJsonSerialization` when JSON should be the primary generalized codec. See
[Serialization](/orleans-fsharp/serialization/) for the exact selection order.

---

## Serilog

Wire Serilog as the logging provider:

```fsharp
siloConfig {
    useLocalhostClustering
    useSerilog
}
```

---

## Complete Production Example

```fsharp
open System
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.Versioning

let redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION")
let azureConn = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION")

let config = siloConfig {
    // Clustering
    addRedisClustering redisConn
    clusterId "production"
    serviceId "my-app"

    // Storage
    addRedisStorage "Default" redisConn
    addAzureBlobStorage "Archive" azureConn

    // Streaming
    addMemoryStreams "Events"
    addBroadcastChannel "Notifications"

    // Reminders
    addRedisReminderService redisConn

    // Security
    useMutualTls "CN=orleans-silo"

    // Observability
    enableHealthChecks
    addDashboard
    useSerilog

    // Services
    configureServices (fun services ->
        services.AddHttpClient() |> ignore)

    // Versioning
    useGrainVersioning BackwardCompatible LatestVersion

    // Endpoints
    siloPort 11111
    gatewayPort 30000

    // Lifecycle
    grainCollectionAge (TimeSpan.FromHours 2.)
    addStartupTask (fun sp ct ->
        task { printfn "Silo started" } :> Threading.Tasks.Task)
}
```

## Next steps

- [Client Configuration](/orleans-fsharp/client-configuration/) -- configure Orleans clients
- [Legacy archive](/orleans-fsharp/legacy/) -- unsupported migration reference for earlier authoring models
- [Streaming](/orleans-fsharp/streaming/) -- publish and subscribe to events
- [Security](/orleans-fsharp/security/) -- TLS, mTLS, and call filters in depth
