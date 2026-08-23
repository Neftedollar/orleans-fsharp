---
title: "Client Configuration"
description: "Complete clientConfig CE reference for configuring an Orleans client."
---

# Client Configuration

**Guide to the `clientConfig { }` computation expression.**

## What you'll learn

- How to configure an Orleans client to connect to a silo
- Clustering modes: localhost, static gateways
- TLS for secure client connections
- Custom DI services on the client

## Overview

The `clientConfig { }` CE builds a `ClientConfig` record. Apply it to a host or build it directly:

```fsharp
open Orleans.FSharp.Runtime

let config = clientConfig {
    useLocalhostClustering
}

// Option 1: Apply to a HostApplicationBuilder
let builder = HostApplicationBuilder()
ClientConfig.applyToHost config builder

// Option 2: Build directly (creates a host and returns the client)
let host, client = ClientConfig.build config
host.StartAsync().GetAwaiter().GetResult()
```

---

## Clustering

### Localhost

Connect to a single local silo for development:

```fsharp
clientConfig { useLocalhostClustering }
```

### Static gateways

Connect to known silo endpoints:

```fsharp
clientConfig {
    useStaticClustering [ "10.0.0.1:30000"; "10.0.0.2:30000" ]
}
```

Each endpoint must be in `"host:port"` format.

---

## Cluster Identity

```fsharp
clientConfig {
    useLocalhostClustering
    clusterId "my-cluster"
    serviceId "my-service"
}
```

The `clusterId` and `serviceId` must match the silo configuration.

---

## Gateway Options

### Refresh period

How often the client refreshes its list of available gateways:

```fsharp
clientConfig {
    useLocalhostClustering
    gatewayListRefreshPeriod (TimeSpan.FromSeconds 30.)
}
```

### Preferred gateway

Set the preferred gateway index for client connections:

```fsharp
clientConfig {
    useLocalhostClustering
    preferredGatewayIndex 0
}
```

---

## Streaming

### In-memory streams

```fsharp
clientConfig {
    useLocalhostClustering
    addMemoryStreams "StreamProvider"
}
```

---

## TLS

### TLS by subject name

```fsharp
clientConfig {
    useLocalhostClustering
    useTls "CN=my-client-cert"
}
```

### TLS with certificate

```fsharp
let cert = new X509Certificate2("path/to/cert.pfx", "password")

clientConfig {
    useLocalhostClustering
    useTlsWithCertificate cert
}
```

### Mutual TLS

```fsharp
clientConfig {
    useLocalhostClustering
    useMutualTls "CN=my-client-cert"
}
```

---

## Serialization

Opt the client into the F# codecs. Both are already registered for you by
`AddFunctionalGrainClient`; declare them explicitly only when other client code needs those
serializers before the functional transport is installed:

```fsharp
clientConfig {
    useLocalhostClustering
    useFSharpBinarySerialization   // F# records, unions, lists, maps, options
    useJsonFallbackSerialization   // System.Text.Json for the rest
}
```

The client's serialization must match the silo's -- see [Serialization](/orleans-fsharp/serialization/).

---

## Custom DI Services

Register services on the client's DI container:

```fsharp
clientConfig {
    useLocalhostClustering
    configureServices (fun services ->
        services.AddSingleton<IMyService, MyService>() |> ignore)
}
```

---

## Calling a functional grain from a client-only process

`clientConfig { }` configures the connection; the functional transport is installed separately, on
the `IClientBuilder`, with `AddFunctionalGrainClient()`. A process that also hosts the definition
needs nothing extra -- `AddFunctionalGrain` on the silo builder installs the client transport too.

```fsharp
open Orleans.FSharp

builder.UseOrleansClient(fun clientBuilder ->
    clientBuilder.AddFunctionalGrainClient() |> ignore)
|> ignore

// Then bind the typed API record against the connected client.
let api = CounterApi.ref client "my-counter"
```

The call is idempotent, and it fails at configuration time with a diagnostic naming
`IGrainReferenceActivatorProvider` if it runs before Orleans has installed its own reference
activators. See [Functional Grain Runtime](/orleans-fsharp/functional-grains/).

---

## Complete Example

```fsharp
open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi = { increment: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, string, CounterApi> {
            grainType "counter"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

let config = clientConfig {
    useStaticClustering [ "10.0.0.1:30000"; "10.0.0.2:30000" ]
    clusterId "production"
    serviceId "my-app"
    useMutualTls "CN=orleans-client"
    gatewayListRefreshPeriod (TimeSpan.FromSeconds 30.)
    addMemoryStreams "Events"
}

let builder = HostApplicationBuilder()
ClientConfig.applyToHost config builder
builder.UseOrleansClient(fun clientBuilder ->
    clientBuilder.AddFunctionalGrainClient() |> ignore)
|> ignore

let host = builder.Build()
host.Start()
let client = host.Services.GetRequiredService<IClusterClient>()

let counter = CounterApi.ref client "my-counter"
let result = counter.increment().GetAwaiter().GetResult()
```

## Next steps

- [Silo Configuration](/orleans-fsharp/silo-configuration/) -- configure the silo that this client connects to
- [Streaming](/orleans-fsharp/streaming/) -- publish and subscribe to streams from the client
- [Security](/orleans-fsharp/security/) -- TLS and mTLS in depth
