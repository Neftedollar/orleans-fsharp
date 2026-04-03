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

## Complete Example

```fsharp
open System
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Microsoft.Extensions.DependencyInjection

let config = clientConfig {
    useStaticClustering [ "10.0.0.1:30000"; "10.0.0.2:30000" ]
    clusterId "production"
    serviceId "my-app"
    useMutualTls "CN=orleans-client"
    gatewayListRefreshPeriod (TimeSpan.FromSeconds 30.)
    addMemoryStreams "Events"
}

let host, client = ClientConfig.build config
host.StartAsync().GetAwaiter().GetResult()

// Get a grain reference and make calls
let counterRef = GrainRef.ofString<ICounterGrain> (client :> IGrainFactory) "my-counter"
let! result = GrainRef.invoke counterRef (fun g -> g.Increment())
```

## Next steps

- [Silo Configuration](silo-configuration.md) -- configure the silo that this client connects to
- [Streaming](streaming.md) -- publish and subscribe to streams from the client
- [Security](security.md) -- TLS and mTLS in depth
