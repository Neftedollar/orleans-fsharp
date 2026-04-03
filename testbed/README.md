# Orleans.FSharp Testbed - Multi-Silo Cluster

Two F# Orleans silos communicating via Redis, with F# Binary serialization.
No serialization attributes. Clean F# types crossing silo boundaries.

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
- F# Binary serialization (no [GenerateSerializer] attributes)
- Grain calls routed across silos
- Clean F# types crossing silo boundaries

## Architecture

```
┌──────────┐     ┌──────────┐     ┌──────────┐
│  Silo 1  │────>│  Redis   │<────│  Silo 2  │
│ :11111   │     │  :6379   │     │ :11112   │
└──────────┘     └──────────┘     └──────────┘
      ^                                ^
      └──────── Client ────────────────┘
```
