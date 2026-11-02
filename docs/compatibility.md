# Orleans Compatibility

**Supported version policy, verified runtimes, and relevant changes in new Orleans releases.**

This page describes the upcoming Orleans.FSharp 5.0.2 compatibility update. The current published
stable release is still 5.0.1; the 10.4.2 source-compatibility changes below belong to the update,
not retroactively to that release. See [Release and Production Status](release-status.md).

## Version policy

Orleans.FSharp declares Orleans **10.1.0 as its minimum**, not as an exact pin. NuGet can resolve
any compatible newer version selected by the application. The minimum remains 10.1.0 because the
current event-sourcing surface uses `JournaledGrain.ClearLogAsync`, which is absent from Orleans
10.0.x.

| Orleans version | Status | Meaning |
|---|---|---|
| 10.0.x | Not supported | Missing an API required by the current Orleans.FSharp assemblies |
| 10.1.0 | Supported floor | The version used to compile packages and one end of the CI matrix |
| 10.3.1 | Previous compatibility target | Covered by the 5.0.1 release checks |
| 10.4.2 | Latest stable Orleans release as of 1 November 2026 | The newest-version compatibility target for 5.0.2 |

The package floor should move only when Orleans.FSharp starts using a newer Orleans API. A new
Orleans release alone is not a reason to force every consumer to upgrade.

## What changed in Orleans 10.4

Target **10.4.2**, including the servicing fixes after 10.4.0. The official notes for
[10.4.0](https://github.com/dotnet/orleans/releases/tag/v10.4.0),
[10.4.1](https://github.com/dotnet/orleans/releases/tag/v10.4.1), and
[10.4.2](https://github.com/dotnet/orleans/releases/tag/v10.4.2) describe several changes relevant
to functional grains:

- Read-only request isolation, timer scheduling, and queued-request handling during activation
  migration/retirement were corrected. Applications inherit these fixes from their Orleans runtime.
- Serializer type-authorization checks were hardened. ADO.NET streaming redelivery and Azure
  membership interoperability also received fixes; the latter providers still need their own
  application-level upgrade checks.
- `IDeepCopier<T>` now describes nullable input explicitly. Seven hand-written Orleans.FSharp
  transport/observer copiers are aligned with that contract in 5.0.2, including direct null-input
  regression tests. This changes nullable annotations, not transport bytes or public F# APIs.
- Explicit-token resumption of an existing memory-stream subscription now replays the checkpoint
  event in our 10.4.2 tests, where 10.1.0 skipped it. The F# helpers preserve native Orleans behavior;
  consumers must not depend on exclusive resumption. This corrects start-token handling in
  Orleans 10.4.0's [PR #10936](https://github.com/dotnet/orleans/pull/10936), not a new F# delivery
  defect. See [checkpoint replay](streaming.md#resuming-subscriptions-after-reactivation).
- The Dashboard example needs OpenTelemetry.Api 1.18.0 with Dashboard 10.4.2. The central build
  dependency is updated without raising the Orleans package floor.
- RPC parameter `[Id]` values are now honored and `CancellationToken` no longer consumes a
  serialized parameter ordinal. Audit application-owned C# grain interfaces before a rolling
  upgrade, particularly tokens placed before other arguments. Our fixed transport places its
  token last and writes the envelope with its existing explicit field ID; source inspection found
  no corresponding ordinal change. This is not a blanket proof for arbitrary application interfaces.
- `SiloAddress.GetUniformHashCodes` changes its return type from an array to an immutable array.
  Applications using that low-level API need a rebuild. Orleans.FSharp does not expose it as part
  of its functional API.
- Request latency metrics move to `orleans-app-requests-latency`, a fractional-millisecond
  histogram. Review dashboards/alerts, and apply the updated Orleans SQL scripts when using
  existing SQLite storage databases.

### Useful additions, separate from this compatibility update

| Addition | How it fits Orleans.FSharp |
|---|---|
| Explicit stream subscription start: `Latest` / `EarliestAvailable` | A useful next F# helper, not added in 5.0.2. `EarliestAvailable` means the oldest retained **local queue-cache** item, not all provider history. Native 10.4 APIs are usable when the application targets that version; upgrade pulling-agent silos before enabling the feature. |
| Cancellation-aware framework operations | Evaluate helpers individually. Public stream subscribe/resume/unsubscribe still have no cancellation-token overload; cancelling only the wait can leak a subscription created later. Existing functional-call cancellation is separate. |
| Management `DeactivateGrain` | Usable through the native management grain on 10.4.1+; a dedicated F# convenience wrapper is optional. |
| New provider/cache configuration | Configure through native Orleans hosting APIs; no replacement F# configuration layer is required. |
| `Microsoft.Orleans.Journaling` / Durable Jobs | Still prerelease (`10.4.2-alpha.1`), deliberately deferred. They are not the log-consistency model wrapped by `journaledGrainFor`. |

Adding first-class start-position helpers while retaining the 10.1 floor needs an explicit
unsupported-runtime error and tests for provider defaults, retained/empty caches, batch delivery,
and subscription cleanup. Falling back silently to ordinary live delivery would hide a semantic
change. The [upstream enum contract](https://github.com/dotnet/orleans/blob/v10.4.2/src/Orleans.Streaming/Core/StreamSubscriptionStartPosition.cs)
defines the cache boundary.

## What changed in Orleans 10.3

Orleans 10.3.0 was released on 27 August 2026. Its
[official release notes](https://github.com/dotnet/orleans/releases/tag/v10.3.0) include runtime
hardening and several new integrations. The changes most likely to affect an Orleans.FSharp
application are:

- Orleans' Newtonsoft.Json storage serializer now applies a type allow-list by default. Existing
  persisted JSON containing unapproved `$type` metadata may require an explicit allow-list or a
  migration. This provider-wide setting does not silently replace the Orleans.FSharp durable-state
  or journal payload codec selected by a functional definition.
- OpenTelemetry RPC semantic attributes use the newer semantic-convention keys. Existing
  dashboards or alerts which query the old attribute names may need updating.
- Custom implementations of `IGrainContextActivator` must use its revised `CreateContext`
  contract. Normal functional-grain applications do not implement this extension point.
- Orleans' serializer and grain-reference APIs now carry more precise nullable metadata. The
  current Orleans.FSharp source aligns its hand-written transport codecs with that contract
  without changing their wire format.

Orleans 10.3.1 followed on 28 August as a stable servicing release, primarily updating
version-contract analyzer metadata plus tests and documentation. See its
[release notes](https://github.com/dotnet/orleans/releases/tag/v10.3.1) and
[NuGet package](https://www.nuget.org/packages/Microsoft.Orleans.Server/10.3.1).

## Orleans 10.3 additions already assessed

The release also highlights:

- Kinesis streaming, SQS FIFO adapters, Event Hubs Aspire integration, grain-backed stream
  checkpointing, and stateless-worker stream consumers;
- Azure Table and Redis journal providers, DynamoDB transactional storage, file grain storage,
  and a System.Text.Json grain-storage serializer;
- continued hardening of Orleans Journaling and Durable Jobs;
- incremental source generation and new Orleans application templates;
- directory, membership, and rolling-upgrade reliability improvements.

Their compatibility boundaries are:

| Category | Orleans.FSharp position |
|---|---|
| Core runtime reliability and routing changes | Inherited when the application resolves Orleans 10.3.x |
| Storage and stream providers configured through Orleans hosting APIs | Usable alongside functional grains through the native `ISiloBuilder`/host configuration; dedicated F# helpers are optional convenience work |
| Stateless-worker implicit stream consumers | Integrated in 5.0.0: `statelessWorker` + `onStream` seals only with loaded Orleans.Streaming 10.3.0+ and has a live competing-consumer integration test; `onBroadcast` remains rejected |
| `Microsoft.Orleans.Journaling`, `DurableGrain`, and Durable Jobs | Deliberately deferred and not represented as supported Orleans.FSharp APIs yet |

The last category is highlighted here for discovery only. It is not a promise that the current
`journaledGrainFor` API wraps the newer Orleans Journaling model: that API is built on Orleans
log-consistency providers.

## What compatibility checks prove

The CI matrix keeps the package floor and newest stable Orleans separate. Its newest leg builds the
solution and runs unit tests, functional integration tests, separate-process rolling-update tests,
stream tests, the functional sample, and the Dashboard smoke test against the selected release.

The N/N+1 rolling tests change **application** contracts and durable schemas with one Orleans
version per test run. They do not, by themselves, prove a cluster mixing Orleans 10.1 and 10.4
binaries. Rebuilding against both versions is also different from running a floor-built NuGet
package on the newer runtime; package-consumer smoke checks cover that additional boundary.

This verifies the Orleans.FSharp surface exercised by those suites. It does **not** certify every
third-party storage, clustering, or stream provider, nor does it prove that application-specific
persisted data can be rolled back without an explicit schema migration strategy. For that, follow
[Testing](testing.md), [Serialization](serialization.md), and
[State and Lifecycle](functional-grains/state-and-lifecycle.md).
