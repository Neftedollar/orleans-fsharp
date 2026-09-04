# Orleans Compatibility

**Supported version policy, verified runtimes, and relevant changes in new Orleans releases.**

This page describes framework compatibility for `main`, the Orleans.FSharp 5.0 preview/next major.
The published stable Orleans.FSharp line is 4.1; see [Release and Production Status](release-status.md).

## Version policy

Orleans.FSharp declares Orleans **10.1.0 as its minimum**, not as an exact pin. NuGet can resolve
any compatible newer version selected by the application. The minimum remains 10.1.0 because the
current event-sourcing surface uses `JournaledGrain.ClearLogAsync`, which is absent from Orleans
10.0.x.

| Orleans version | Status | Meaning |
|---|---|---|
| 10.0.x | Not supported | Missing an API required by the current Orleans.FSharp assemblies |
| 10.1.0 | Supported floor | The version used to compile packages and one end of the CI matrix |
| 10.3.1 | Latest stable Orleans release | The newest-version compatibility target |

The package floor should move only when Orleans.FSharp starts using a newer Orleans API. A new
Orleans release alone is not a reason to force every consumer to upgrade.

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

## Newly available Orleans surface

The release also highlights:

- Kinesis streaming, SQS FIFO adapters, Event Hubs Aspire integration, grain-backed stream
  checkpointing, and stateless-worker stream consumers;
- Azure Table and Redis journal providers, DynamoDB transactional storage, file grain storage,
  and a System.Text.Json grain-storage serializer;
- continued hardening of Orleans Journaling and Durable Jobs;
- incremental source generation and new Orleans application templates;
- directory, membership, and rolling-upgrade reliability improvements.

These additions fall into three different categories:

| Category | Orleans.FSharp position |
|---|---|
| Core runtime reliability and routing changes | Inherited when the application resolves Orleans 10.3.x |
| Storage and stream providers configured through Orleans hosting APIs | Usable alongside functional grains through the native `ISiloBuilder`/host configuration; dedicated F# helpers are optional convenience work |
| Stateless-worker implicit stream consumers | Integrated on `main`: `statelessWorker` + `onStream` seals only with loaded Orleans.Streaming 10.3.0+ and has a live competing-consumer integration test; `onBroadcast` remains rejected |
| `Microsoft.Orleans.Journaling`, `DurableGrain`, and Durable Jobs | Deliberately deferred and not represented as supported Orleans.FSharp APIs yet |

The last category is highlighted here for discovery only. It is not a promise that the current
`journaledGrainFor` API wraps the newer Orleans Journaling model: that API is built on Orleans
log-consistency providers.

## What compatibility checks prove

The CI matrix keeps the package floor and newest stable Orleans separate. Its newest leg builds the
solution and runs unit tests, functional integration tests, separate-process rolling-update tests,
stream tests, the functional sample, and the Dashboard smoke test against the selected release.

This verifies the Orleans.FSharp surface exercised by those suites. It does **not** certify every
third-party storage, clustering, or stream provider, nor does it prove that application-specific
persisted data can be rolled back without an explicit schema migration strategy. For that, follow
[Testing](testing.md), [Serialization](serialization.md), and
[State and Lifecycle](functional-grains/state-and-lifecycle.md).
