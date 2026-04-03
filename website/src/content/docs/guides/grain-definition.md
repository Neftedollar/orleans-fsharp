---
title: Grain Definition
description: Complete guide to the grain {} computation expression — all 31 keywords with examples
---

**Complete guide to the `grain { }` computation expression.**

## What you'll learn

- Every keyword in the `grain { }` CE
- How to define handlers, lifecycle hooks, reminders, and timers
- Placement strategies, reentrancy, and stateless workers
- Multiple named states, implicit stream subscriptions, and more

## Overview

The `grain { }` CE builds a `GrainDefinition<'State, 'Message>` -- an immutable record that fully describes a grain's behavior. The Orleans.FSharp.CodeGen package reads this definition at build time and generates the corresponding C# grain class with all the correct Orleans attributes.

```fsharp
open Orleans.FSharp

let myGrain = grain {
    defaultState initialValue
    handle handlerFunction
    persist "StorageProviderName"
}
```

Every grain definition requires at minimum:
1. A `defaultState` -- the initial state value
2. At least one handler (`handle`, `handleWithContext`, `handleCancellable`, etc.)

---

## State and Handlers

### `defaultState`

Sets the initial state of the grain when first activated. Required for every grain.

```fsharp
let myGrain = grain {
    defaultState 0
    handle (fun state msg -> task { return state + 1, box(state + 1) })
}
```

For DU state:

```fsharp
let myGrain = grain {
    defaultState Zero
    handle (fun state msg -> task { return Count 1, box 1 })
}
```

### `handle`

Registers the message handler. Takes the current state and a message, returns a `Task<'State * obj>` (new state and a boxed result).

```fsharp
grain {
    defaultState 0
    handle (fun state msg ->
        task {
            match msg with
            | Add n -> return state + n, box(state + n)
            | Get -> return state, box state
        })
}
```

### `handleWithContext`

Like `handle`, but the handler receives a `GrainContext` as the first argument. Use this when you need to call other grains or resolve DI services from within the handler.

```fsharp
grain {
    defaultState Map.empty
    handleWithContext (fun ctx state msg ->
        task {
            match msg with
            | Aggregate key ->
                let otherGrain =
                    GrainContext.getGrainByString<IOtherGrain> ctx key
                let! value = GrainRef.invoke otherGrain (fun g -> g.GetValue())
                return state |> Map.add key value, box value
        })
}
```

The `GrainContext` provides:

| Member | Description |
|---|---|
| `GrainContext.getService<'T>` | Resolve a DI service |
| `GrainContext.getState<'T> name` | Get a named additional persistent state |
| `GrainContext.getGrainByString<'T> key` | Get a grain ref by string key |
| `GrainContext.getGrainByGuid<'T> key` | Get a grain ref by GUID key |
| `GrainContext.getGrainByInt64<'T> key` | Get a grain ref by int64 key |
| `GrainContext.getGrainByGuidCompound<'T> guid ext` | Compound GUID+string key |
| `GrainContext.getGrainByIntCompound<'T> key ext` | Compound int64+string key |
| `GrainContext.deactivateOnIdle` | Request deactivation when idle |
| `GrainContext.delayDeactivation span` | Delay deactivation |
| `GrainContext.grainId` | Get the GrainId |
| `GrainContext.primaryKeyString` | Get the string primary key |
| `GrainContext.primaryKeyGuid` | Get the Guid primary key |
| `GrainContext.primaryKeyInt64` | Get the int64 primary key |

### `handleWithServices`

Alias for `handleWithContext` that emphasizes DI access. Identical behavior.

```fsharp
grain {
    defaultState []
    handleWithServices (fun ctx state msg ->
        task {
            let logger = GrainContext.getService<ILogger<_>> ctx
            Log.logInfo logger "Processing {Command}" [| box msg |]
            return state, box ()
        })
}
```

### `handleCancellable`

Like `handle`, but the handler receives a `CancellationToken` for cooperative cancellation of long-running operations.

```fsharp
grain {
    defaultState ""
    handleCancellable (fun state msg ct ->
        task {
            let! result = longRunningOperation ct
            return result, box result
        })
}
```

### `handleWithContextCancellable`

Combines `GrainContext` and `CancellationToken`.

```fsharp
grain {
    defaultState 0
    handleWithContextCancellable (fun ctx state msg ct ->
        task {
            let httpClient = GrainContext.getService<HttpClient> ctx
            let! response = httpClient.GetAsync("https://api.example.com", ct)
            return state + 1, box response.StatusCode
        })
}
```

### `handleWithServicesCancellable`

Alias for `handleWithContextCancellable`.

---

## Persistence

### `persist`

Names the Orleans storage provider for state persistence. The provider must be registered in the silo configuration.

```fsharp
grain {
    defaultState Zero
    handle (fun state msg -> task { return Count 1, box 1 })
    persist "Default"
}
```

Without `persist`, the grain state is in-memory only and is lost on deactivation.

### `additionalState`

Declares a named secondary persistent state that can be accessed via `GrainContext.getState`. Use this when a grain needs multiple independently persisted state values.

```fsharp
grain {
    defaultState { Balance = 0m }

    additionalState "AuditLog" "AuditStore" ([] : AuditEntry list)

    handleWithContext (fun ctx state msg ->
        task {
            let auditState =
                GrainContext.getState<AuditEntry list> ctx "AuditLog"
            let currentAudit = auditState.State
            // ... process and update both states
            return newState, box result
        })

    persist "Default"
}
```

Parameters: `additionalState name storageName defaultValue`

---

## Lifecycle Hooks

### `onActivate`

Runs when the grain is activated. Receives the current state and returns a potentially modified state.

```fsharp
grain {
    defaultState { Cache = Map.empty; LastRefresh = DateTime.MinValue }
    handle myHandler
    persist "Default"

    onActivate (fun state ->
        task {
            printfn "Grain activated with state: %A" state
            return { state with LastRefresh = DateTime.UtcNow }
        })
}
```

### `onDeactivate`

Runs when the grain is being deactivated. Receives the current state for cleanup.

```fsharp
grain {
    defaultState { ConnectionId = None }
    handle myHandler
    persist "Default"

    onDeactivate (fun state ->
        task {
            match state.ConnectionId with
            | Some id -> printfn "Closing connection %s" id
            | None -> ()
        })
}
```

### `onLifecycleStage`

Hooks into Orleans grain lifecycle stages for fine-grained control. Standard stages:

| Stage | Value | Description |
|---|---|---|
| `GrainLifecycleStage.First` | 2000 | First stage after creation |
| `GrainLifecycleStage.SetupState` | 4000 | State setup |
| `GrainLifecycleStage.Activate` | 6000 | Activation |
| `GrainLifecycleStage.Last` | int.MaxValue | Final stage |

```fsharp
grain {
    defaultState 0
    handle myHandler

    onLifecycleStage 2000 (fun ct ->
        task {
            printfn "Early lifecycle hook firing"
        })

    onLifecycleStage 6000 (fun ct ->
        task {
            printfn "Activation-stage hook firing"
        })
}
```

Multiple hooks at the same stage are executed in registration order.

---

## Reminders and Timers

### `onReminder`

Registers a named reminder handler. Reminders are persistent periodic triggers that survive grain deactivation and silo restarts. The silo must have a reminder service configured.

```fsharp
grain {
    defaultState { CheckCount = 0 }
    handle myHandler
    persist "Default"

    onReminder "HealthCheck" (fun state reminderName tickStatus ->
        task {
            printfn "Reminder %s fired at %A" reminderName tickStatus.CurrentTickTime
            return { state with CheckCount = state.CheckCount + 1 }
        })
}
```

To register the reminder at runtime, use the `Reminder` module:

```fsharp
Reminder.register grain "HealthCheck" (TimeSpan.FromMinutes 1.) (TimeSpan.FromMinutes 5.)
```

### `onTimer`

Registers a declarative timer that is automatically started on grain activation and stopped on deactivation. Timers are in-memory only -- they do not survive deactivation.

```fsharp
grain {
    defaultState { HeartbeatCount = 0 }
    handle myHandler

    onTimer
        "Heartbeat"
        (TimeSpan.FromSeconds 10.)    // dueTime: first fire after 10s
        (TimeSpan.FromSeconds 30.)    // period: then every 30s
        (fun state ->
            task {
                return { state with HeartbeatCount = state.HeartbeatCount + 1 }
            })
}
```

---

## Reentrancy and Concurrency

### `reentrant`

Marks the grain as reentrant, allowing concurrent message processing. By default, Orleans grains process one message at a time.

```fsharp
grain {
    defaultState Map.empty
    handle myReadHeavyHandler
    reentrant
}
```

### `interleave`

Marks a specific method as always interleaved, even on non-reentrant grains. Use for methods that are safe to run concurrently.

```fsharp
grain {
    defaultState { Data = Map.empty }
    handle myHandler
    interleave "GetStatus"
    interleave "GetVersion"
}
```

### `readOnly`

Marks a method as read-only. Read-only methods are interleaved for concurrent reads on non-reentrant grains.

```fsharp
grain {
    defaultState { Items = [] }
    handle myHandler
    readOnly "GetItems"
    readOnly "GetCount"
}
```

### `mayInterleave`

Sets a custom predicate method for reentrancy decisions. The named static method receives an `InvokeMethodRequest` and returns `bool`.

```fsharp
grain {
    defaultState Map.empty
    handle myHandler
    mayInterleave "ShouldInterleave"
}
```

---

## Stateless Workers

### `statelessWorker`

Marks the grain as a stateless worker, allowing multiple activations per silo for load balancing. Stateless workers cannot use persistent state.

```fsharp
grain {
    defaultState ()
    handle (fun _ msg ->
        task {
            let result = processMessage msg
            return (), box result
        })
    statelessWorker
}
```

### `maxActivations`

Sets the maximum number of local worker activations per silo. Defaults to the number of CPU cores.

```fsharp
grain {
    defaultState ()
    handle myHandler
    statelessWorker
    maxActivations 4
}
```

---

## Placement Strategies

Control which silo activates a grain:

```fsharp
// Prefer the silo where the call originated
grain { ... ; preferLocalPlacement }

// Random silo
grain { ... ; randomPlacement }

// Consistent hash of grain ID
grain { ... ; hashBasedPlacement }

// Silo with fewest activations
grain { ... ; activationCountPlacement }

// Resource-aware (CPU, memory)
grain { ... ; resourceOptimizedPlacement }

// Target silos with a specific role
grain { ... ; siloRolePlacement "worker" }

// Custom strategy type
grain { ... ; customPlacement typeof<MyPlacementStrategy> }
```

---

## Streaming

### `implicitStreamSubscription`

Auto-subscribes the grain to a stream namespace. The handler receives the current state and a stream event (boxed), and returns the new state.

```fsharp
grain {
    defaultState { Events = [] }
    handle myHandler
    persist "Default"

    implicitStreamSubscription "OrderEvents" (fun state event ->
        task {
            let orderEvent = event :?> OrderEvent
            return { state with Events = orderEvent :: state.Events }
        })
}
```

---

## Method Annotations

### `oneWay`

Marks a method as fire-and-forget. The caller does not wait for the grain to finish processing.

```fsharp
grain {
    defaultState ()
    handle myHandler
    oneWay "LogEvent"
}
```

### `grainType`

Sets a custom grain type name (maps to `[GrainType("name")]` in CodeGen).

```fsharp
grain {
    defaultState 0
    handle myHandler
    grainType "my-counter-v2"
}
```

### `deactivationTimeout`

Sets the per-grain idle timeout before deactivation.

```fsharp
grain {
    defaultState Map.empty
    handle myHandler
    deactivationTimeout (TimeSpan.FromMinutes 30.)
}
```

---

## Complete Example

Here is a grain that uses many features together:

```fsharp
open System
open System.Threading.Tasks
open Orleans.FSharp

[<GenerateSerializer>]
type ChatState =
    | [<Id(0u)>] Empty
    | [<Id(1u)>] Active of messages: string list * participants: Set<string>

[<GenerateSerializer>]
type ChatCommand =
    | [<Id(0u)>] Join of user: string
    | [<Id(1u)>] Leave of user: string
    | [<Id(2u)>] Send of user: string * text: string
    | [<Id(3u)>] GetHistory
    | [<Id(4u)>] GetParticipants

let chatRoom =
    grain {
        defaultState Empty

        handleWithContext (fun ctx state cmd ->
            task {
                match state, cmd with
                | Empty, Join user ->
                    let newState = Active([], Set.singleton user)
                    return newState, box true
                | Active(msgs, users), Join user ->
                    return Active(msgs, users |> Set.add user), box true
                | Active(msgs, users), Leave user ->
                    let remaining = users |> Set.remove user
                    if Set.isEmpty remaining then
                        return Empty, box true
                    else
                        return Active(msgs, remaining), box true
                | Active(msgs, users), Send(user, text) ->
                    let entry = $"[{DateTime.UtcNow:HH:mm}] {user}: {text}"
                    return Active(entry :: msgs, users), box entry
                | _, GetHistory ->
                    let msgs = match state with Active(m, _) -> m | _ -> []
                    return state, box msgs
                | _, GetParticipants ->
                    let users = match state with Active(_, u) -> u | _ -> Set.empty
                    return state, box users
                | _ -> return state, box false
            })

        persist "Default"
        reentrant
        readOnly "GetHistory"
        readOnly "GetParticipants"
        deactivationTimeout (TimeSpan.FromHours 1.)

        onTimer "Cleanup" (TimeSpan.FromMinutes 5.) (TimeSpan.FromMinutes 5.)
            (fun state ->
                task {
                    match state with
                    | Active(msgs, users) when msgs.Length > 1000 ->
                        return Active(msgs |> List.take 500, users)
                    | _ -> return state
                })
    }
```

## Next steps

- [Silo Configuration](/orleans-fsharp/guides/silo-configuration/) -- configure storage, clustering, and streaming for your grains
- [Streaming](/orleans-fsharp/guides/streaming/) -- publish and subscribe to events
- [Testing](/orleans-fsharp/guides/testing/) -- test your grain definitions with FsCheck and TestHarness
