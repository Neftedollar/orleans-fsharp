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

---

## Orleans Dashboard Security

> **⚠️ Warning**: The Orleans Dashboard exposes grain state, silo metrics, and cluster
> topology **without authentication** by default. Anyone on the network who can reach
> the dashboard port can read all grain state.

### Production guidelines

1. **Do not enable the dashboard** in production unless you need it, OR
2. **Place it behind a reverse proxy** with authentication (nginx + basic auth, Cloudflare Access, etc.), OR
3. **Restrict network access** to the dashboard port using firewall rules or security groups

```fsharp
// Dashboard is opt-in — only add it when you need it
let config = siloConfig {
    addDashboard   // requires Microsoft.Orleans.Dashboard package
    // NOT recommended for production without additional access controls
}
```

---

## FSharpBinaryCodec Trust Model

`FSharpBinaryCodec` serializes F# types (DUs, records, options, lists, maps) without
requiring `[<GenerateSerializer>]` or `[<Id>]` attributes. It embeds the type's full
name in the serialized bytes so the deserializer can recover the type at runtime.

### Trust boundary

The codec assumes that **all serialized bytes come from trusted Orleans silos within
the same cluster**. It is NOT designed to deserialize untrusted input from:

- External HTTP APIs
- User-uploaded files
- Third-party message queues outside your cluster
- Any source that an attacker could control

### What happens on untrusted input

If an attacker can inject crafted bytes into the Orleans message stream, they could:
1. Embed arbitrary type names in the serialized data
2. Force the codec to instantiate unexpected types via `Type.GetType()`
3. Manipulate grain state through carefully constructed payloads

### Mitigation

**At the network layer** (recommended): Orleans already encrypts silo-to-silo
communication when TLS is configured. Ensure TLS is enabled in production:

```fsharp
let config = siloConfig {
    useTls "CN=my-silo-cert"   // encrypts all silo communication
}
```

**At the codec layer** (defense-in-depth): If you need to deserialize bytes from
an untrusted source, use `deserializeWithType` with an explicit `hintType` parameter
so the type name from the bytes is ignored:

```fsharp
// Safe: hintType is known at compile time, type name in bytes is ignored
let result = FSharpBinaryFormat.deserializeWithType bytes typeof<MyKnownType>
```

---

## RequestCtx Trust Model

`RequestCtx` propagates key-value pairs across grain calls using Orleans' built-in
`RequestContext` mechanism. Values flow automatically from caller to callee.

### Trust boundary

`RequestCtx` values are **only trustworthy if all grains in the cluster run trusted
code**. Any grain can read, modify, or forge `RequestCtx` values for downstream calls.

### What this means

- ✅ Safe for: correlation IDs, tracing, feature flags, non-security context
- ⚠️ Use with caution: user identity, tenant ID, role claims
- ❌ Never use for: authorization decisions without server-side validation

### Authorization pattern

If you use `RequestCtx` for security context (e.g., user principal), validate it
in an incoming grain call filter — don't trust the value in the handler alone:

```fsharp
let authFilter = Filter.incoming (fun ctx ->
    task {
        match RequestCtx.get<string> "Principal" with
        | Some principal ->
            // Validate: is this principal authorized for this method?
            let methodName = FilterContext.methodName ctx
            if not (isAuthorized principal methodName) then
                raise (UnauthorizedAccessException "Access denied")
            do! ctx.Invoke()
        | None ->
            invalidOp "Unauthenticated grain call — no principal in request context"
    })

let config = siloConfig {
    addIncomingFilter authFilter
}
```

## Next steps

- [Silo Configuration](silo-configuration.md) -- TLS and filter configuration
- [Advanced](advanced.md) -- transactions and more security patterns
- [API Reference](api-reference.md) -- all public security-related APIs
