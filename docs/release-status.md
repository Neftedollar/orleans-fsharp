# Release and Production Status

This page separates the published package line from the documentation built from `main`.

## Release channels

| Channel | Status | Use it for |
|---|---|---|
| Orleans.FSharp 4.1 (latest `4.1.0`) | Published stable | Production evaluation and applications which need a released package |
| `main` / Orleans.FSharp 5.0 preview | Next major, not published stable | Evaluating and contributing to the next release from source |
| Legacy authoring models | Archived and unsupported | Migration reference for existing applications only |

The current README, `docs/`, website, and `[Unreleased]` changelog section track `main`, so they may
describe 5.0-preview behavior that 4.1 packages do not contain. For the exact stable surface, read
the documentation at the `v4.1.0` tag. Do not infer that an item under `[Unreleased]` has shipped.

The Legacy archive receives no new Legacy release line, features, compatibility work, or security
fixes. New applications should use `grainContract`, `grainFor`, `journaledGrainFor`, typed API
records, and `FunctionalGrain.ref`.

## Production boundaries on `main`

### TaskSeq is an upstream boundary when wrapping streaming replies

Direct enumeration of a functional streaming reply is the supported path. With
`FSharp.Control.TaskSeq` 1.1.1 on the repository's verified static resumable-code path, wrapping a
runtime-returned `IAsyncEnumerable` with `TaskSeq.map` or
`taskSeq { for item in upstream do yield item }` was measured duplicating the final item. TaskSeq
over ordinary producers such as lists, ranges, and database cursors is not affected.

When one grain relays another grain's streaming reply, return a direct delegating
`IAsyncEnumerable`/enumerator instead of using those wrapping forms. The exact pattern and the SDK
boundary are documented under [Authoring a producer](streaming-replies.md#authoring-a-producer).

`Stream.asTaskSeq` is a separate Orleans pub/sub adapter. On `main` it creates the subscription on
first enumeration and disposes that subscription when enumeration ends, including early exit.

### Functional persistence payloads are bounded per codec

The 5.0 preview rejects any one encoded functional state value, journal event, or snapshot larger
than **16 MiB**, before writing and before decoding. This is a per-payload guard, not a quota for a
whole grain, storage record, journal, or provider.

Use an immutable per-codec override only after validating the storage provider and memory budget:

```fsharp
let largeJsonCodec =
    FunctionalPersistenceCodec.FSharpJson
        .WithMaxPayloadBytes(64 * 1024 * 1024)
```

The normal resolution order is unchanged: persistent-state element, grain definition, then silo
default; journal definition, then silo default. The provider-wide
`FSharpJsonGrainStorageSerializer` has its own 16 MiB default and constructor override because it
controls the complete provider value rather than a functional envelope payload. These limits are
present on `main`; they are not part of the published 4.1 package.

### Stateless-worker implicit streams require Orleans 10.3+

On `main`, a `grainFor` definition can combine `statelessWorker` with `onStream` when the loaded
`Orleans.Streaming` runtime is 10.3.0 or newer. Orleans treats the local worker activations as
competing consumers, so one stream item is handled by one available worker; do not assume that all
local activations receive a copy.

The Orleans.FSharp package floor remains 10.1.0. On Orleans 10.1/10.2, or when the streaming
package version cannot be established, definition sealing rejects this combination with a version
diagnostic instead of failing later during activation. `statelessWorker` plus `onBroadcast`
remains unsupported on every supported Orleans version because broadcast channels did not gain
equivalent stateless-worker semantics.

This capability is part of the 5.0 preview and has a live Orleans 10.3 integration test. It is not
part of the published 4.1 package.

## What the repository checks do not prove

Passing unit, integration, rolling-update, website, and link checks does not certify every
third-party provider, deployment topology, persisted application schema, or workload size. Before
production, test the selected providers, payload distribution, failure recovery, observability,
and N/N+1 rollback path with representative data.
