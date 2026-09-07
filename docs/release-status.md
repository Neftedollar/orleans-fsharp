# Release and Production Status

Orleans.FSharp 5.0.0 is the current published stable release. This page records its verified
production boundaries and the status of earlier release lines.

## Release channels

| Channel | Status | Use it for |
|---|---|---|
| Orleans.FSharp `5.0.0` | Published stable | New applications and production evaluation |
| Orleans.FSharp `4.1.0` | Superseded stable line | Existing applications preparing a 5.0 migration |
| Legacy authoring models | Archived and unsupported | Migration reference for existing applications only |

The current documentation describes 5.0.0. For immutable release documentation, read the repository
at the `v5.0.0` tag. Applications remaining on 4.1 should use the documentation at the `v4.1.0`
tag while planning their migration.

The Legacy archive receives no new Legacy release line, features, compatibility work, or security
fixes. New applications should use `grainContract`, `grainFor`, `journaledGrainFor`, typed API
records, and `FunctionalGrain.ref`.

## Production boundaries in 5.0.0

### TaskSeq is an upstream boundary when wrapping streaming replies

Direct enumeration of a functional streaming reply is the supported path. With
`FSharp.Control.TaskSeq` 1.1.1 on the repository's verified static resumable-code path, wrapping a
runtime-returned `IAsyncEnumerable` with `TaskSeq.map` or
`taskSeq { for item in upstream do yield item }` was measured duplicating the final item. TaskSeq
over ordinary producers such as lists, ranges, and database cursors is not affected.

When one grain relays another grain's streaming reply, return a direct delegating
`IAsyncEnumerable`/enumerator instead of using those wrapping forms. The exact pattern and the SDK
boundary are documented under [Authoring a producer](streaming-replies.md#authoring-a-producer).

`Stream.asTaskSeq` and its cursor-preserving `Stream.asTaskSeqWithToken` variant are separate
Orleans pub/sub adapters. In 5.0.0 they create the subscription on first enumeration and dispose
that subscription when enumeration ends, including early exit.

### Functional persistence payloads are bounded per codec

Orleans.FSharp 5.0.0 defaults to **16 MiB** for one encoded functional state value, journal event, or
snapshot. Enveloped payloads are checked before decoding and before writing. Direct binary state
keeps its old provider schema: its logical Orleans encoding is checked after the provider loads
the value and before each explicit write. Pre-decode protection for that direct format belongs to
the provider serializer. This is a per-payload guard, not a quota for a whole grain, storage record,
journal, provider, or process memory.

Use an immutable per-codec override only after validating the storage provider and memory budget:

```fsharp
let largeJsonCodec =
    FunctionalPersistenceCodec.FSharpJson
        .WithMaxPayloadBytes(64 * 1024 * 1024)
```

The normal resolution order is unchanged: persistent-state element, grain definition, then silo
default; journal definition, then silo default. The provider-wide
`FSharpJsonGrainStorageSerializer` has its own 16 MiB default and constructor override because it
controls the complete provider value rather than a functional envelope payload. These limits were
introduced in 5.0.0 and are not part of the 4.1 package.

### Generalized binary payloads reject unsafe graphs

The 5.0 binary codec preserves repeated generalized values in Orleans' surrounding serializer
session, including values held by a generated C# or F# object. Inside one opaque F# payload,
cycles are rejected, nesting is capped at 128 codec calls on write and read, and repeated mutable
children decode independently. This is a bounded value codec, not a native identity-preserving
graph format; use generated Orleans serialization when internal object identity is required.

The same release fixes field-only and property-based CLR class reconstruction, exact-width enum
values, and runtime cases of closed generic unions without changing historical property-POCO wire
bytes. The precise contract is in
[Serialization](serialization.md#binary-graph-and-clr-class-contract).

### Stateless-worker implicit streams require Orleans 10.3+

In 5.0.0, a `grainFor` definition can combine `statelessWorker` with `onStream` when the loaded
`Orleans.Streaming` runtime is 10.3.0 or newer. Orleans treats the local worker activations as
competing consumers, so one stream item is handled by one available worker; do not assume that all
local activations receive a copy.

The Orleans.FSharp package floor remains 10.1.0. On Orleans 10.1/10.2, or when the streaming
package version cannot be established, definition sealing rejects this combination with a version
diagnostic instead of failing later during activation. `statelessWorker` plus `onBroadcast`
remains unsupported on every supported Orleans version because broadcast channels did not gain
equivalent stateless-worker semantics.

This capability shipped in 5.0.0 and has a live Orleans 10.3 integration test. It is not part of
the 4.1 package.

Orleans.FSharp 5.0.0 also exposes Orleans' complete stateless-worker placement setting:
`statelessWorker maxLocalWorkers` preserves the stock `removeIdleWorkers = true` default, while
`statelessWorker maxLocalWorkers false` allows a functional definition to use `collectionAge`.
The generated manifest is checked against the corresponding live Orleans attribute on both ends
of the supported version matrix.

## What the repository checks do not prove

Passing unit, integration, rolling-update, website, and link checks does not certify every
third-party provider, deployment topology, persisted application schema, or workload size. Before
production, test the selected providers, payload distribution, failure recovery, observability,
and N/N+1 rollback path with representative data.
