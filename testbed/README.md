# Orleans.FSharp Testbed - Multi-Silo Cluster

Two F# Orleans silos communicating via Redis, with FSharp.SystemTextJson fallback serialization.
No serialization attributes on F# types. Clean DUs and records crossing silo boundaries.

## Run

```bash
cd testbed
docker compose up --build -d
# Wait for silos to start (~10s)
docker compose run --rm client
# Clean up
docker compose down
```

## What it demonstrates

- 2 silos discovering each other via Redis clustering
- Grain state persisted in Redis
- FSharp.SystemTextJson fallback serialization (no [GenerateSerializer] attributes on types)
- Grain calls routed across silos
- Clean F# types (DU, record, list) crossing silo boundaries
- C# CodeGen bridge for Orleans source generators

## Architecture

```
┌──────────┐     ┌──────────┐     ┌──────────┐
│  Silo 1  │────>│  Redis   │<────│  Silo 2  │
│ :11111   │     │  :6379   │     │ :11112   │
└──────────┘     └──────────┘     └──────────┘
      ^                                ^
      └──────── Client ────────────────┘
```
