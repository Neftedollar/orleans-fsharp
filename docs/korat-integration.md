# Korat Integration

**How to use Orleans.FSharp with Korat for AI-powered distributed systems.**

## What you'll learn

- How to resolve Korat services from grain handlers via DI
- How to use multiple named states for configuration and memory
- How to use call filters for policy enforcement
- How to propagate principal context across grain calls

---

## DI Pattern with `handleWithServices`

Use `handleWithServices` (or `handleWithContext`) to resolve Korat services like `ILlmProvider` from the grain's dependency injection container:

```fsharp
open Orleans.FSharp

let aiGrain =
    grain {
        defaultState { ConversationHistory = [] }

        handleWithServices (fun ctx state msg ->
            task {
                let llmProvider = GrainContext.getService<ILlmProvider> ctx

                match msg with
                | Chat prompt ->
                    let! response = llmProvider.CompleteAsync(prompt)
                    let newHistory = response :: state.ConversationHistory
                    return { state with ConversationHistory = newHistory }, box response

                | GetHistory ->
                    return state, box state.ConversationHistory

                | ClearHistory ->
                    return { state with ConversationHistory = [] }, box ()
            })

        persist "Default"
    }
```

You can resolve any registered service via `GrainContext.getService<'T>`:

```fsharp
let embeddingService = GrainContext.getService<IEmbeddingService> ctx
let vectorStore = GrainContext.getService<IVectorStore> ctx
let policyEngine = GrainContext.getService<IPolicyEngine> ctx
```

---

## Multiple Named States

Use `additionalState` to manage independent state concerns -- for example, keeping LLM configuration separate from conversation memory:

```fsharp
[<GenerateSerializer>]
type AgentConfig =
    { Model: string
      Temperature: float
      SystemPrompt: string }

[<GenerateSerializer>]
type AgentMemory =
    { Messages: Message list
      TokensUsed: int }

let agent =
    grain {
        defaultState { Messages = []; TokensUsed = 0 }

        additionalState "Config" "ConfigStore"
            { Model = "default"; Temperature = 0.7; SystemPrompt = "" }

        handleWithContext (fun ctx state msg ->
            task {
                let configState =
                    GrainContext.getState<AgentConfig> ctx "Config"
                let config = configState.State

                match msg with
                | Configure newConfig ->
                    configState.State <- newConfig
                    do! configState.WriteStateAsync()
                    return state, box ()

                | SendMessage text ->
                    let llm = GrainContext.getService<ILlmProvider> ctx
                    let! response =
                        llm.CompleteAsync(config.Model, config.Temperature, text)
                    let newState =
                        { state with
                            Messages = { Role = "assistant"; Content = response } :: state.Messages
                            TokensUsed = state.TokensUsed + countTokens response }
                    return newState, box response

                | GetMemory ->
                    return state, box state
            })

        persist "MemoryStore"
    }
```

Each named state can use a different storage provider. The primary state (via `persist`) and each additional state are independently persisted.

---

## Call Filters for Policy Enforcement

Use incoming call filters to enforce security policies, rate limits, and content moderation before grain calls reach the handler:

```fsharp
open Orleans.FSharp

let contentPolicyFilter =
    Filter.incoming (fun ctx ->
        task {
            let methodName = FilterContext.methodName ctx

            // Only check content-related methods
            if methodName = "SendMessage" || methodName = "Chat" then
                let policyEngine =
                    ctx.TargetContext
                    |> fun tc -> (tc :?> Grain).ServiceProvider.GetService<IPolicyEngine>()

                // Check the message content against policies
                let args = ctx.Request.GetArgument<string>(0)
                let! isAllowed = policyEngine.CheckContentPolicy(args)

                if not isAllowed then
                    raise (InvalidOperationException "Content policy violation")

            do! ctx.Invoke()
        })

let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "Default"
    addIncomingFilter contentPolicyFilter
}
```

### Rate limiting filter

```fsharp
let rateLimitFilter =
    Filter.incomingWithAround
        (fun ctx ->
            task {
                let rateLimiter =
                    ctx.TargetContext
                    |> fun tc -> (tc :?> Grain).ServiceProvider.GetService<IRateLimiter>()

                let principal = RequestCtx.get<string> "Principal"
                match principal with
                | Some user ->
                    let! allowed = rateLimiter.TryAcquireAsync(user)
                    if not allowed then
                        raise (InvalidOperationException "Rate limit exceeded")
                | None -> ()
            })
        (fun _ctx -> task { () })
```

---

## Request Context for Principal Propagation

Use `RequestCtx` to propagate the user principal (and other security context) across grain calls. This is essential for multi-tenant AI systems where you need to track which user or tenant initiated a request chain.

### Setting context at the API boundary

```fsharp
// In your ASP.NET Core controller or middleware
let handleApiRequest (httpContext: HttpContext) =
    task {
        let userId = httpContext.User.FindFirst("sub").Value
        let tenantId = httpContext.User.FindFirst("tenant").Value

        RequestCtx.set "Principal" (box userId)
        RequestCtx.set "TenantId" (box tenantId)
        RequestCtx.set "RequestId" (box (Guid.NewGuid().ToString()))

        // All downstream grain calls inherit this context
        let! result = GrainRef.invoke agentGrain (fun g -> g.Chat(prompt))
        return result
    }
```

### Reading context in grain handlers

```fsharp
let multiTenantAgent =
    grain {
        defaultState Map.empty

        handleWithServices (fun ctx state msg ->
            task {
                let tenantId =
                    RequestCtx.get<string> "TenantId"
                    |> Option.defaultValue "default"

                let principal =
                    RequestCtx.get<string> "Principal"
                    |> Option.defaultValue "anonymous"

                match msg with
                | Process data ->
                    let llm = GrainContext.getService<ILlmProvider> ctx

                    // Use tenant-specific configuration
                    let! config = loadTenantConfig tenantId
                    let! result = llm.CompleteAsync(config.Model, data)

                    // Audit the call
                    Log.logInfo logger "Processed by {Principal} in tenant {TenantId}"
                        [| box principal; box tenantId |]

                    return state, box result
            })
    }
```

### Scoped context for sub-operations

```fsharp
let! result =
    RequestCtx.withValue "OperationId" (box operationId) (fun () ->
        task {
            // All grain calls within this scope carry the OperationId
            let! step1 = GrainRef.invoke grain1 (fun g -> g.DoStep1())
            let! step2 = GrainRef.invoke grain2 (fun g -> g.DoStep2(step1))
            return step2
        })
// OperationId is automatically removed after the scope
```

---

## Complete Pattern: AI Agent Grain

Here is a complete example of an AI agent grain using all the patterns above:

```fsharp
open System
open Orleans.FSharp
open Orleans.FSharp.Runtime

[<GenerateSerializer>]
type AgentState =
    { Messages: (string * string) list   // (role, content)
      TotalTokens: int
      LastActivity: DateTime }

[<GenerateSerializer>]
type AgentConfig =
    { Model: string
      Temperature: float
      MaxTokens: int
      SystemPrompt: string }

[<GenerateSerializer>]
type AgentCommand =
    | [<Id(0u)>] Chat of prompt: string
    | [<Id(1u)>] GetHistory
    | [<Id(2u)>] UpdateConfig of AgentConfig
    | [<Id(3u)>] Reset

let aiAgent =
    grain {
        defaultState
            { Messages = []
              TotalTokens = 0
              LastActivity = DateTime.MinValue }

        additionalState "Config" "ConfigStore"
            { Model = "gpt-4"; Temperature = 0.7; MaxTokens = 4096; SystemPrompt = "" }

        handleWithContext (fun ctx state cmd ->
            task {
                let configState = GrainContext.getState<AgentConfig> ctx "Config"
                let config = configState.State

                match cmd with
                | Chat prompt ->
                    let principal =
                        RequestCtx.get<string> "Principal"
                        |> Option.defaultValue "anonymous"

                    let llm = GrainContext.getService<ILlmProvider> ctx

                    let messages =
                        if String.IsNullOrEmpty config.SystemPrompt then
                            ("user", prompt) :: state.Messages
                        else
                            ("user", prompt) :: state.Messages
                            |> List.append [("system", config.SystemPrompt)]

                    let! response = llm.CompleteAsync(config.Model, messages)

                    let newState =
                        { state with
                            Messages = ("assistant", response.Content) :: ("user", prompt) :: state.Messages
                            TotalTokens = state.TotalTokens + response.TokensUsed
                            LastActivity = DateTime.UtcNow }

                    return newState, box response.Content

                | GetHistory ->
                    return state, box state.Messages

                | UpdateConfig newConfig ->
                    configState.State <- newConfig
                    do! configState.WriteStateAsync()
                    return state, box ()

                | Reset ->
                    return
                        { Messages = []; TotalTokens = 0; LastActivity = DateTime.UtcNow },
                        box ()
            })

        persist "MemoryStore"
        deactivationTimeout (TimeSpan.FromHours 1.)
        readOnly "GetHistory"
    }

// Silo configuration
let config = siloConfig {
    useLocalhostClustering
    addMemoryStorage "MemoryStore"
    addMemoryStorage "ConfigStore"

    configureServices (fun services ->
        services.AddSingleton<ILlmProvider, MyLlmProvider>() |> ignore
        services.AddSingleton<IPolicyEngine, MyPolicyEngine>() |> ignore)

    addIncomingFilter contentPolicyFilter
}
```

## Next steps

- [Grain Definition](grain-definition.md) -- all `grain { }` CE keywords
- [Security](security.md) -- TLS, mTLS, and call filters
- [Silo Configuration](silo-configuration.md) -- configure storage and DI services
