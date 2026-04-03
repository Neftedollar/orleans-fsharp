# Security

**Guide to securing Orleans.FSharp deployments.**

## What you'll learn

- How to configure TLS and mTLS for silo communication
- How to use call filters for authorization
- How to propagate security context with RequestCtx
- How to manage connection strings and secrets safely

---

## TLS Encryption

Orleans silo-to-silo and client-to-silo communication can be encrypted with TLS. Requires the `Microsoft.Orleans.Connections.Security` NuGet package.

### Silo TLS

```fsharp
open Orleans.FSharp.Runtime

// By certificate subject name (from certificate store)
let config = siloConfig {
    addRedisClustering redisConn
    useTls "CN=my-silo-cert"
}

// By certificate instance
let cert = new X509Certificate2("silo-cert.pfx", certPassword)

let config = siloConfig {
    addRedisClustering redisConn
    useTlsWithCertificate cert
}
```

### Client TLS

```fsharp
let config = clientConfig {
    useStaticClustering [ "10.0.0.1:30000" ]
    useTls "CN=my-client-cert"
}
```

---

## Mutual TLS (mTLS)

Mutual TLS requires both the server and client to present certificates. This provides stronger authentication than one-way TLS.

### Silo mTLS

```fsharp
let config = siloConfig {
    addRedisClustering redisConn
    useMutualTls "CN=my-silo-cert"
}

// Or with a certificate instance
let config = siloConfig {
    addRedisClustering redisConn
    useMutualTlsWithCertificate cert
}
```

### Client mTLS

```fsharp
let config = clientConfig {
    useStaticClustering [ "10.0.0.1:30000" ]
    useMutualTls "CN=my-client-cert"
}
```

### Production guidelines

- Always use valid certificates from a trusted certificate authority in production.
- Never disable certificate validation in production environments.
- Rotate certificates before they expire.
- Use separate certificates for silos and clients in mTLS deployments.
- Store certificate passwords in a secrets manager (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault).

---

## Call Filters

Call filters intercept grain calls for authorization, logging, rate limiting, and more.

### Incoming filter for authorization

```fsharp
open Orleans.FSharp

let authFilter =
    Filter.incoming (fun ctx ->
        task {
            let principal = RequestCtx.get<string> "Principal"
            match principal with
            | Some user ->
                // Check authorization
                let methodName = FilterContext.methodName ctx
                if isAuthorized user methodName then
                    do! ctx.Invoke()
                else
                    raise (UnauthorizedAccessException $"User {user} not authorized for {methodName}")
            | None ->
                raise (UnauthorizedAccessException "No principal in request context")
        })

let config = siloConfig {
    useLocalhostClustering
    addIncomingFilter authFilter
}
```

### Outgoing filter for context propagation

```fsharp
let propagateContextFilter =
    Filter.outgoing (fun ctx ->
        task {
            // Ensure the principal is propagated to downstream grain calls
            let principal = RequestCtx.get<string> "Principal"
            match principal with
            | Some _ -> do! ctx.Invoke()
            | None ->
                // Set a default principal for internal calls
                RequestCtx.set "Principal" (box "system")
                do! ctx.Invoke()
        })
```

### Before/after pattern

Use `Filter.incomingWithAround` for timing, metrics, or audit logging:

```fsharp
let auditFilter =
    Filter.incomingWithAround
        (fun ctx ->
            task {
                let method = FilterContext.methodName ctx
                let interfaceType = FilterContext.interfaceType ctx
                Log.logInfo logger "Grain call started: {Interface}.{Method}"
                    [| box interfaceType.Name; box method |]
            })
        (fun ctx ->
            task {
                let method = FilterContext.methodName ctx
                Log.logInfo logger "Grain call completed: {Method}" [| box method |]
            })
```

### FilterContext helpers

| Function | Returns | Description |
|---|---|---|
| `FilterContext.methodName ctx` | `string` | The method name being called |
| `FilterContext.interfaceType ctx` | `Type` | The grain interface type |
| `FilterContext.grainInstance ctx` | `obj option` | The grain instance if available |

---

## Request Context

`RequestCtx` propagates key-value pairs across grain calls automatically. Values set on the caller side are available on the callee side.

### Set and get values

```fsharp
open Orleans.FSharp

// On the caller side
RequestCtx.set "UserId" (box "user-123")
RequestCtx.set "TenantId" (box "tenant-abc")

// On the callee side (inside a grain handler)
let userId = RequestCtx.get<string> "UserId"       // Some "user-123"
let tenantId = RequestCtx.get<string> "TenantId"   // Some "tenant-abc"
let missing = RequestCtx.get<string> "NotSet"       // None
```

### Get with default

```fsharp
let role = RequestCtx.getOrDefault<string> "Role" "anonymous"
```

### Remove values

```fsharp
RequestCtx.remove "UserId"
```

### Scoped values

Set a value for the duration of an async operation, then clean up automatically:

```fsharp
let! result =
    RequestCtx.withValue "CorrelationId" (box correlationId) (fun () ->
        task {
            // CorrelationId is available here and in all downstream grain calls
            return! doWork()
        })
// CorrelationId is automatically removed after the function completes
```

---

## Connection String Security

Never hardcode connection strings or secrets in source code.

### Use environment variables

```fsharp
let redisConn = Environment.GetEnvironmentVariable("REDIS_CONNECTION")
let azureConn = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION")

let config = siloConfig {
    addRedisClustering redisConn
    addRedisStorage "Default" redisConn
}
```

### Use IConfiguration

```fsharp
let config = siloConfig {
    configureServices (fun services ->
        let sp = services.BuildServiceProvider()
        let configuration = sp.GetRequiredService<IConfiguration>()
        let connStr = configuration.GetConnectionString("Redis")
        // Use connStr for storage setup
        ())
}
```

### What to avoid

```fsharp
// DO NOT do this -- secrets will leak into version control
addRedisStorage "Default" "redis://user:password@host:6379"
addAzureBlobStorage "Default" "DefaultEndpointsProtocol=https;AccountName=..."
```

---

## Secure Principal Propagation Pattern

A common security pattern is to set the user principal at the entry point and validate it in grain call filters:

```fsharp
// At the API boundary (ASP.NET controller, etc.)
let handleRequest (httpContext: HttpContext) =
    task {
        let userId = httpContext.User.Identity.Name
        RequestCtx.set "Principal" (box userId)
        RequestCtx.set "Roles" (box (httpContext.User.Claims |> Seq.map ...))

        // All grain calls from here will carry the principal
        let! result = GrainRef.invoke myGrain (fun g -> g.DoWork())
        return result
    }

// In a grain call filter
let authFilter =
    Filter.incoming (fun ctx ->
        task {
            match RequestCtx.get<string> "Principal" with
            | Some principal ->
                let methodName = FilterContext.methodName ctx
                // Validate authorization...
                do! ctx.Invoke()
            | None ->
                invalidOp "Unauthenticated grain call"
        })
```

---

## Logging Security

Use structured logging (the `Log` module) with correlation IDs. Never log sensitive data like passwords, tokens, or personal information:

```fsharp
open Orleans.FSharp

// Good: structured templates with safe fields
Log.logInfo logger "User {UserId} accessed grain {GrainId}" [| box userId; box grainId |]

// Bad: logging sensitive data
// Log.logInfo logger "Auth token: {Token}" [| box authToken |]  // DO NOT DO THIS
```

Use `Log.withCorrelation` to scope a correlation ID across multiple log entries:

```fsharp
do! Log.withCorrelation requestId (fun () ->
    task {
        Log.logInfo logger "Processing request" [||]
        // ... all logs within this scope share the correlation ID
    })
```

## Next steps

- [Silo Configuration](silo-configuration.md) -- TLS and filter configuration
- [Advanced](advanced.md) -- transactions and more security patterns
- [Korat Integration](korat-integration.md) -- principal propagation with Korat
