# Quick Reference: Current Functional API

This is the compact keyword and entry-point reference for new Orleans.FSharp applications. The
original `grain { }`, `FSharpGrain.*`, and `eventSourcedGrain { }` APIs are documented only
under [Legacy](docs/legacy/index.md).

## Contract: `grainContract<'Actor,'Key,'Api> { }`

| Keyword | Argument | Purpose |
|---|---|---|
| `grainType` | `string` | Stable routing and storage identity; optional when safe to derive |
| `version` | `int` | Contract version; default `1` |
| `acceptsVersions` | `Exact \| BackwardCompatible of int` | Admitted caller versions |
| `stringKey`, `guidKey`, `int64Key` | — | Native Orleans key codec |
| `stringKeyMapped`, `guidKeyMapped`, `int64KeyMapped` | encode + decode | Domain-key codec over a native key |
| `guidCompoundKey`, `int64CompoundKey` | — | Native key plus string extension |
| `guidCompoundKeyMapped`, `int64CompoundKeyMapped` | encode + decode | Domain-key codec over a compound key |
| `operationId` | stable ID + selector | Override an operation or stream field's wire ID |
| `sinceVersion` | version + selector | Mark when an operation or stream field was introduced |
| `readOnly` | operation selector | State-neutral query; may interleave with read-only work |
| `oneWay` | unit-reply selector | Complete the caller after local send acknowledgement |
| `alwaysInterleave` | operation selector | Interleave one state-neutral operation |
| `reentrant` | — | Whole-activation reentrancy |
| `mayInterleave` | `IFunctionalRequestMetadata -> bool` | Protocol-metadata admission predicate |
| `transactional` | `Orleans.TransactionOption` + selector | Orleans transaction policy for one operation |

Every API-record field takes one argument. Use a tuple for multiple logical inputs.

## Definition: `grainFor contract { }`

| Keyword | Argument |
|---|---|
| `defaultState` | `unit -> 'State` |
| `initialState` | `'Key -> 'State` |
| `handle` | selector + `context -> state -> argument -> Task<state * reply>` |
| `handleQuery` | selector + `context -> state -> argument -> Task<reply>` |
| `handleStream` | stream selector + `context -> state -> argument -> IAsyncEnumerable<item>` |
| `stateFrom` | `PersistentStateRef<'State>` |
| `usePersistentState` | descriptor + `'Key -> storedState` |
| `transactionalStateFrom` | descriptor + `'Key -> storedState` |
| `collectionAge` | `TimeSpan` |
| `placement` | `Random \| PreferLocal \| ActivationCountBased \| ResourceOptimized` |
| `statelessWorker` | maximum local activations |
| `onActivate`, `onDeactivate`, `onLifecycle` | lifecycle hook |
| `onReminder` | name + due + period + hook |
| `onTimer` | name + `GrainTimerCreationOptions` + hook |
| `onStream` | provider + namespace + hook |
| `onBroadcast` | provider + namespace + hook |

`handleQuery` requires the selected operation to be `readOnly`.

## Journaled definition: `journaledGrainFor contract { }`

| Keyword | Argument |
|---|---|
| `initialEventState` | `'Key -> 'State` — required first |
| `apply` | `'State -> 'Event -> 'State` — required second and pure |
| `logProvider` | registered `ILogViewAdaptorFactory` name |
| `journalStorage` | named `IGrainStorage` for LogStorage/StateStorage |
| `customStorage` | `IServiceProvider -> IFunctionalJournalStorage<'Key,'State,'Event>` |
| `snapshotPolicy` | `Inherit \| Disabled \| Every of int \| When of (int -> state -> bool)` |
| `handle` | selector + callback returning `Task<event list * reply>` |
| `handleQuery`, `handleStream` | state-neutral query or stream |
| `onActivate`, `onDeactivate` | journal-aware lifecycle hook |
| `onReminder`, `onTimer`, `onStream`, `onBroadcast` | callback returning events |
| `onTentativeStateChanged`, `onStateChanged` | synchronous state notification |
| `onConnectionIssue`, `onConnectionIssueResolved` | synchronous connection notification |
| `collectionAge`, `placement` | same as `grainFor` |

`journalStorage` and `customStorage` are mutually exclusive. Snapshot precedence is:

1. `context.snapshotNow()` for the current successful callback;
2. the definition's `snapshotPolicy`;
3. the global `FunctionalJournalSnapshotOptions.Policy` when the definition says `Inherit` or
   declares no policy;
4. `Disabled`.

Automatic and manual snapshots require `customStorage`.

`ManualSnapshotMaxConflictRetries` defaults to `3`, must be `>= 0`, and counts retries after the
first CAS attempt for a zero-event manual snapshot (`3` means at most four attempts). Ordinary
custom-storage exceptions raised while Orleans' adaptor reads or appends events are transient. A
zero-event manual snapshot and `Clear` call the typed store directly, so an ordinary exception
fails that call once without implicit retry or deactivation. Throw
`FunctionalJournalPermanentStorageException(message[, innerException])` to stop retry, fail the
operation, and request grain deactivation for a genuinely permanent failure.

## Functional references

| API | Signature |
|---|---|
| `FunctionalGrain.ref` | contract → `IGrainFactory -> 'Key -> 'Api` |
| `FunctionalGrain.rawRef` | contract → `IGrainFactory -> 'Key -> FunctionalGrainRef<...>` |
| `FunctionalGrain.streamId` | contract → namespace → key → `StreamId` |
| `FunctionalGrain.channelId` | contract → namespace → key → `ChannelId` |
| `rawRef.call` | selector → argument → `Task<reply>` |
| `rawRef.callCancellable` | selector → argument → token → `Task<reply>` |
| `rawRef.stream` | selector → argument → `IAsyncEnumerable<item>` |
| `rawRef.streamCancellable` | selector → argument → token → `IAsyncEnumerable<item>` |

## Hosting functional definitions

| API | Purpose |
|---|---|
| `ISiloBuilder.AddFunctionalGrain` | Register a `grainFor` definition |
| `ISiloBuilder.AddFunctionalJournaledGrain` | Register a `journaledGrainFor` definition |
| `IClientBuilder.AddFunctionalGrainClient` | Install the client-only functional transport |
| `UseFunctionalJournalSnapshots every` | Set a positive global fixed snapshot interval |
| `ConfigureFunctionalJournalSnapshots` | Set global `Policy` and manual conflict retries |
| `FunctionalGrainRegistration.of'` | Erase one ordinary definition for heterogeneous scripting lists |
| `FunctionalScripting.startOnPorts` | Start a localhost silo from ordinary registrations |

## `siloConfig { }`

| Area | Operations |
|---|---|
| Clustering | `useLocalhostClustering`, `addRedisClustering`, `addAzureTableClustering`, `addAdoNetClustering` |
| Storage | `addMemoryStorage`, `addRedisStorage`, `addAzureBlobStorage`, `addAzureTableStorage`, `addAdoNetStorage`, `addCosmosStorage`, `addDynamoDbStorage`, `addCustomStorage` |
| Streams | `addMemoryStreams`, `addPersistentStreams`, `addBroadcastChannel` |
| Reminders | `addMemoryReminderService`, `addRedisReminderService`, `addCustomReminderService` |
| Security | `useTls`, `useTlsWithCertificate`, `useMutualTls`, `useMutualTlsWithCertificate` |
| Services | `configureServices`, `addIncomingFilter`, `addOutgoingFilter`, `addGrainService`, `addStartupTask` |
| Operations | `useSerilog`, `enableHealthChecks`, `addDashboard`, `addDashboardWithOptions`, `useGrainVersioning` |
| Identity/network | `clusterId`, `serviceId`, `siloName`, `siloPort`, `gatewayPort`, `advertisedIpAddress`, `grainCollectionAge` |

`addDashboardWithOptions counterUpdateIntervalMs historyLength hideTrace` has exactly those three
arguments. Reference `Microsoft.Orleans.Dashboard`, then map
`endpoints.MapOrleansDashboard("/dashboard")` in the ASP.NET Core host.

## `clientConfig { }`

| Area | Operations |
|---|---|
| Connection | `useLocalhostClustering`, `useStaticClustering` |
| Identity | `clusterId`, `serviceId` |
| Gateway | `gatewayListRefreshPeriod`, `preferredGatewayIndex` |
| Streams | `addMemoryStreams` |
| Security | `useTls`, `useTlsWithCertificate`, `useMutualTls` |
| Services/serialization | `configureServices`, `useFSharpBinarySerialization`, `useJsonFallbackSerialization` |

## Typed custom journal storage

| Member | Signature |
|---|---|
| `Read` | identity → `Task<FunctionalJournalRead<'State,'Event>>` |
| `Append` | identity × write → `Task<bool>` |
| `Clear` | identity → `Task` |

`Append` compares `write.ExpectedVersion`, appends `write.Events` atomically, and persists
`write.Snapshot` in the same operation when present. A conflict returns `false` without changing
storage.

## More detail

- [API Reference](docs/api-reference.md)
- [Functional Grain Runtime](docs/functional-grains.md)
- [Event Sourcing](docs/event-sourcing.md)
- [Silo Configuration](docs/silo-configuration.md)
- [Dashboard](docs/dashboard.md)
- [Legacy API](docs/legacy/index.md)
