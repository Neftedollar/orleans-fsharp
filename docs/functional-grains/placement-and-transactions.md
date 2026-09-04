# Placement and Transactions

**Placement decides where an activation runs; transactions decide which durable writes commit.**

## Placement choices

A definition can select Orleans' stock placement strategies:

- `Random`
- `PreferLocal`
- `ActivationCountBased`
- `ResourceOptimized`
- `HashBased`
- `SiloRoleBased`

`statelessWorker` permits several local activations for throughput. It is intentionally
incompatible with durable state and reminders. Its one-argument form proactively removes idle
workers; use `statelessWorker maxLocalWorkers false` when `collectionAge` should govern idle
deactivation instead.

```fsharp
grainFor RouterApi.contract {
    defaultState (fun () -> ())
    placement ResourceOptimized
    handleQuery (_.route) route
}
```

Placement is advisory over the currently compatible silos. Test the topology you deploy; a
single-silo test cannot prove distribution.

## Live activation migration

`context.migrateOnIdle()` lets Orleans choose a destination. The overload taking a
`SiloAddress` supplies an explicit placement hint. Migration participants carry
activation-local values only for a successful live move; they are not durable storage and do not
survive a source-process crash.

Persistent state uses Orleans persistence, independently of migration payloads.

## Distributed transactions

Mark the participating operations `transactional`, configure Orleans transactions on the silo,
and acquire facets with `transactionalStateFrom`. A handler may be re-executed, so keep external
side effects outside the transactional retry region or make them idempotent.

A transaction covers Orleans transactional state. It does not automatically include an arbitrary
database, stream publication, HTTP call, or journal provider.

## Details

- [Placement and stateless workers](../functional-grains.md#placement-statelessworker-and-placement)
- [Live activation migration](../functional-grains.md#live-activation-migration)
- [Distributed ACID transactions](../functional-grains.md#distributed-acid-transactions)
- [Testing](../testing.md)
