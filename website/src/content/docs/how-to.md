---
title: "Recipes"
description: "Task-oriented paths through the current Orleans.FSharp functional API."
---

# Recipes

**Task-oriented paths through the current functional API.**

> **Looking for the first tutorial?** Start with [Getting Started](/orleans-fsharp/getting-started/). This page
> assumes you already know how `grainContract`, `grainFor`, and `FunctionalGrain.ref` fit
> together and sends you directly to the task you need.

## Start a new current-API project

Until a template package containing the current functional scaffold is published, install the
template from a source checkout:

```bash
git clone https://github.com/Neftedollar/orleans-fsharp.git
dotnet new install ./orleans-fsharp/templates
dotnet new orleans-fsharp -n MyApp
```

The source template tracks the 5.0 preview. The published 4.1.0 template belongs to the unsupported
Legacy archive; do not use its package-only install command for a new functional-runtime
application. See [Release and Production Status](/orleans-fsharp/release-status/) for the package/doc split.

## Choose the grain shape

| Need | Start here |
|---|---|
| Ephemeral state and typed operations | [Contracts and handlers](/orleans-fsharp/functional-grains/#one-operation-one-argument) |
| Durable state | [Persistence model](/orleans-fsharp/functional-grains/#persistence-model) |
| Events as the source of truth | [Event Sourcing](/orleans-fsharp/event-sourcing/) |
| Read-only operation | [`handleQuery`](/orleans-fsharp/functional-grains/#reply-only-handlers-handlequery) |
| Push to a client | [Functional observers](/orleans-fsharp/functional-grains/#push-to-clients-functional-observers) |
| A sequence returned by one call | [Server-Streaming Replies](/orleans-fsharp/streaming-replies/) |
| Pub/sub between producers and consumers | [Streaming](/orleans-fsharp/streaming/) |
| Cross-grain ACID work | [Transactions](/orleans-fsharp/functional-grains/#distributed-acid-transactions) |
| Call the actor from C# | [Calling from C#](/orleans-fsharp/calling-from-csharp/) |

## Configure the host

| Task | Guide |
|---|---|
| Local silo | [Getting Started](/orleans-fsharp/getting-started/#step-4-configure-the-silo) |
| Storage, stream, reminder, or clustering provider | [Silo Configuration](/orleans-fsharp/silo-configuration/) |
| Standalone client | [Client Configuration](/orleans-fsharp/client-configuration/) |
| Orleans Dashboard | [Dashboard](/orleans-fsharp/dashboard/) |
| TLS, call filters, and secret handling | [Security](/orleans-fsharp/security/) |
| Retry, timeout, and circuit breaker | [Resilience](/orleans-fsharp/resilience/) |

## Evolve a deployed application

Treat these as separate compatibility gates:

1. Route old and new callers with [contract versioning](/orleans-fsharp/functional-grains/#operation-rename-and-contract-version).
2. Keep stored state readable with [versioned state and upcasters](/orleans-fsharp/serialization/#versioned-state-and-upcasters).
3. Keep journal entries and snapshots readable with
   [versioned events and snapshots](/orleans-fsharp/event-sourcing/#versioned-events-and-snapshots).
4. Prove N/N+1 and rollback with separate processes; see
   [Testing rolling updates](/orleans-fsharp/testing/#testing-rolling-updates-and-durable-schemas).

The [Orleans compatibility](/orleans-fsharp/compatibility/) page records the framework versions exercised by CI
and calls out new Orleans capabilities which are not yet wrapped.

## Verify the application

- Unit-test pure transition functions directly.
- Use a real `TestingHost` activation for persistence, serialization, reminders, streams,
  transactions, and lifecycle behavior.
- Keep bytes written by released serializers as fixtures when durable schemas evolve.
- Run the closest [repository example](/orleans-fsharp/examples/) before copying a provider-specific setup.

See [Testing](/orleans-fsharp/testing/) for complete patterns and [Examples](/orleans-fsharp/examples/) for runnable projects.

Migration-only material for earlier authoring models is isolated in the unsupported
[Legacy archive](/orleans-fsharp/legacy/).
