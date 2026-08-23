# Developer Guide

This guide is for contributors to the current Orleans.FSharp functional runtime. Application-facing
usage belongs in `docs/`; this file describes the architecture, change workflow, and invariants
that maintainers need.

## Architecture

The functional runtime separates four concerns:

1. A `GrainContract<'Actor,'Key,'Api>` declares durable identity, key encoding, protocol version,
   operation IDs, and delivery policy.
2. A `FunctionalGrainDefinition<...>` or `FunctionalJournaledGrainDefinition<...>` binds that
   contract to state and handlers.
3. Runtime hosting registers a definition and the fixed functional transport with Orleans.
4. `FunctionalGrain.ref` binds the same contract to an `IGrainFactory` and returns the typed API
   record used by callers.

The actor-brand type keeps unrelated contracts distinct at compile time. The API record describes
the callable surface. The contract owns wire compatibility; the definition owns behavior. Callers
cannot tell whether the hosted definition uses ordinary state or an event journal.

### Project roles

| Project | Responsibility |
|---|---|
| `Orleans.FSharp` | Contracts, definition builders, typed references, observers, state descriptors, serialization, and shared helpers |
| `Orleans.FSharp.Runtime` | Functional activation/dispatch, silo registration, and `siloConfig` / `clientConfig` |
| `Orleans.FSharp.Abstractions` | Fixed C# transport interfaces and generated Orleans proxies |
| `Orleans.FSharp.Testing` | TestingHost, web-host, FsCheck, mock-factory, and log-capture helpers |
| `Orleans.FSharp.Analyzers` | F# analyzer rules |
| `Orleans.FSharp.EventSourcing` | Legacy compatibility package; current journals are in `Orleans.FSharp` |
| `Orleans.FSharp.CodeGen` | Legacy per-grain CodeGen compatibility |

### Functional request path

```text
typed API field
  -> contract operation metadata
  -> fixed Orleans proxy
  -> functional request envelope
  -> hosted definition registry
  -> typed handler
  -> encoded reply
```

The wire operation ID is the record-field name unless `operationId` overrides it. Contract
version admission happens before handler dispatch. Key codecs must round-trip and are part of the
grain's durable identity.

## Computation-expression design

The public builders are staged:

- `grainContract` starts with an empty contract draft and seals a `GrainContract`.
- `grainFor contract` requires `defaultState` or `initialState` before state-dependent
  operations become available.
- `journaledGrainFor contract` requires `initialEventState` first and `apply` second so the
  state and event types are established before handlers are accepted.

This staging is deliberate API validation, not just implementation detail. A new custom operation
must preserve type inference and should reject invalid combinations while the definition is sealed,
before a silo accepts traffic.

### Adding a contract operation

When adding a keyword:

1. Decide whether it changes the wire contract, delivery policy, or only hosted behavior.
2. Add it to the narrowest builder stage that has all required type information.
3. Validate nulls, ranges, duplicate declarations, and incompatible combinations at sealing.
4. Add reflection-based surface coverage so the public keyword set cannot drift silently.
5. Add semantic tests for the behavior and a startup/integration test when Orleans configuration is
   involved.
6. Update `docs/api-reference.md`, the relevant guide, its website mirror, and
   `QUICK-REFERENCE.md`.

For a selector-based contract operation, retain the one-argument API-field invariant:

```fsharp
[<NoEquality; NoComparison>]
type ExampleApi =
    { update: (string * int) -> Task<unit>
      read: unit -> Task<int> }
```

Multiple logical inputs are one tuple argument. Curried fields are not a supported wire shape.

### Adding a definition operation

State-changing handlers return replacement state explicitly:

```fsharp
let increment _context count () =
    task {
        let next = count + 1
        return next, next
    }
```

Reply-only `handleQuery` callbacks do not return state and require a contract operation declared
`readOnly`. Streaming handlers return `IAsyncEnumerable<'Item>` directly. Journaled handlers
return `'Event list * 'Reply`; the runtime confirms the batch before releasing the reply.

## State and storage invariants

### Persistent state

`PersistentState.create stateName providerName` produces an immutable descriptor. `stateFrom`
attaches the primary state; `usePersistentState` attaches additional named facets. Descriptor
identity includes state name, provider name, and stored type. Two provider writes are not an atomic
transaction.

### Transactional state

`TransactionalState.create stateName storageName` plus `transactionalStateFrom` attaches an
Orleans transactional facet. The contract's `transactional` operation takes
`Orleans.TransactionOption`. The invocation-bound `FunctionalTransactionalState` exposes
`read`, `readWith`, `update`, and `updateWith`.

### Journals and snapshots

`IFunctionalJournalStorage<'Key,'State,'Event>` is the application-owned custom-storage seam:

- `Read` returns an optional snapshot and the ordered retained tail after it.
- `Append` compares `ExpectedVersion`, writes the entire event batch atomically, and may persist
  the resulting snapshot in the same operation.
- `Clear` deletes the complete journal for one storage identity.

Ordinary storage exceptions raised through Orleans' CustomStorage adaptor remain transient to its
retry protocol. A custom store throws `FunctionalJournalPermanentStorageException` only for a
failure that retry cannot repair; the functional runtime fails the operation and requests
activation deactivation. A zero-event manual snapshot calls `Append` directly: an ordinary
exception fails that call without implicit retry or deactivation, while the permanent marker keeps
the same fail-and-deactivate meaning.

Snapshot precedence is fixed:

1. `context.snapshotNow()` for a successful callback;
2. the definition's `snapshotPolicy`;
3. the silo-wide `FunctionalJournalSnapshotOptions.Policy` for `Inherit` or no local policy;
4. `Disabled`.

`FunctionalJournalSnapshotOptions.ManualSnapshotMaxConflictRetries` is non-negative and defaults
to `3`. It counts retries after the first CAS attempt for a zero-event manual snapshot, so the
default allows four total attempts; every retry refreshes and recomputes from confirmed state.

Snapshots are available only with `customStorage`. LogStorage keeps and replays the event log;
StateStorage writes the latest folded view and retains no event history.

## Hosting

`AddFunctionalGrain` and `AddFunctionalJournaledGrain` register definitions by value and install
the client transport. Client-only processes call `AddFunctionalGrainClient`.

A standalone F# host must make definition and payload assemblies visible before Orleans snapshots
its application manifest. Use `SiloConfig.applyToHost` / `applyToSiloBuilder` and the functional
registration methods in the order shown by `docs/getting-started.md`.

Dashboard is optional. `addDashboard` uses package defaults;
`addDashboardWithOptions counterUpdateIntervalMs historyLength hideTrace` configures the three
runtime options. The host must reference `Microsoft.Orleans.Dashboard` and map the ASP.NET Core
endpoint.

## Testing

### Fast tests

Keep pure folds and context-free handlers as named functions and test them directly. Test contract
and definition sealing for invalid combinations and exact diagnostics.

### Integration tests

Use Orleans `TestCluster` for activation, persistence, serializer, reminder, stream, transaction,
and journal behavior. Register the same definition value as production and call it through
`FunctionalGrain.ref`. Do not fabricate `FunctionalGrainContext`; its constructor is
runtime-owned.

### Required checks

```bash
dotnet build
dotnet test
python3 scripts/check-deprecation-signal.py
python3 scripts/check-docs-mirror.py
python3 scripts/check-current-docs-api.py
python3 scripts/generate-llms-full.py --check
cd website
npm run build
cd ..
python3 scripts/check-docs-links.py
```

Use focused project or test filters while iterating, then run the checks proportional to the
changed surface.

## Documentation workflow

`docs/**/*.md` is the repository rendering; `website/src/content/docs/**/*.md` is the published
mirror with Starlight frontmatter and site-form links. Content changes must land in both. Run the
mirror check before handing off.

Current pages teach only `grainContract`, `grainFor`, `journaledGrainFor`, typed functional
references, and helpers that compose with them. Compatibility examples belong under `docs/legacy`.
`website/public/llms-full.txt` is generated as a full concatenation of current docs followed by a
clearly marked Legacy section; run `python3 scripts/generate-llms-full.py` after changing a source
page. `llms.txt` remains the short navigation index.

Examples should either compile on their own or say explicitly which immediately preceding
definition they continue. Prefer immutable record updates, lowercase record fields for API
operations, `task { }`, explicit `ignore` on fluent builders, and typed selectors such as
`(_.increment)`.

## Serialization

The functional transport registers `FSharpBinaryCodec` for F# records, unions, tuples, options,
lists, maps, and sets. Stored and wire types still need deterministic, evolvable shapes. Do not
mutate a state object behind the runtime's replacement-state checks. Security-sensitive deployments
must treat the binary codec as a trusted-data format; see `docs/security.md`.

## Release process

MinVer derives package versions from `v*` Git tags; there is no version field to edit in
`Directory.Build.props`.

1. Move release notes from Unreleased to a dated version section.
2. Build and test Release configuration on `main`.
3. Tag the intended semantic version.
4. CI publishes packages through the configured trusted publisher.

Do not put a major-version wildcard from an old release line in package README examples. Use the
ordinary `dotnet add package Package.Name` command, or `Version="*"` when an XML example needs a
placeholder.

## Troubleshooting

### Functional grain not found

- Confirm the exact definition value was passed to `AddFunctionalGrain` or
  `AddFunctionalJournaledGrain`.
- Confirm client-only hosts called `AddFunctionalGrainClient`.
- Confirm the contract key codec matches the supplied domain key.
- Confirm definition and payload assemblies were loaded before the Orleans manifest snapshot.

### Serialization failure

- Confirm the argument, reply, state, and event types have supported serializers.
- Confirm a stored type is constructible by Orleans' activation path.
- Confirm client and silo installed the same functional transport/codec configuration.

### Journal startup failure

- Confirm `logProvider` names a registered log-consistency adaptor.
- Confirm `journalStorage` names an `IGrainStorage`, or register the default storage.
- For `customStorage`, register Orleans' CustomStorage provider with the same name and make the
  typed storage implementation resolvable from DI.
- Do not combine `journalStorage` with `customStorage`.

## Legacy maintenance

The obsolete universal grain, per-grain CodeGen, and original event-sourcing implementations are
still maintained for compatibility. Their architecture and examples live under
[docs/legacy](docs/legacy/index.md). Changes to those subsystems must not reintroduce their APIs into
current guides or package quick starts.

## Resources

- [Functional Grain Runtime](docs/functional-grains.md)
- [API Reference](docs/api-reference.md)
- [Event Sourcing](docs/event-sourcing.md)
- [Testing](docs/testing.md)
- [Microsoft Orleans documentation](https://learn.microsoft.com/dotnet/orleans/)
- [F# language reference](https://learn.microsoft.com/dotnet/fsharp/language-reference/)
