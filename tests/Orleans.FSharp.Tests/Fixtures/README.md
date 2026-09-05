# Durable compatibility fixtures

`functional-journal-view-v4.1.0.base64` and
`functional-journal-entry-v4.1.0.base64` were generated from the released `v4.1.0` source at
commit `809b714c42b99d42ee23aa2ff2b11199ccc13242`. The generator used that release's actual
`FunctionalJournalView` / `FunctionalJournalEntry` types and exact F# binary payload codec. The
payload is the historical test type
`Orleans.FSharp.Tests.FunctionalPersistenceSerializationTests+EvolutionV0` with
`Amount = 42`; the generator assembly deliberately used `AssemblyName=Orleans.FSharp.Tests`, so
the current test assembly can resolve and decode the original type identity.

Reproduction procedure:

1. Export tag `v4.1.0` with `git archive`.
2. In the exported tree, build a small `net10.0` F# executable named
   `Orleans.FSharp.Tests` with a project reference to `src/Orleans.FSharp`.
3. Register the release's `FunctionalTransportSerialization` and
   `FSharpBinaryCodecRegistration`, serialize `{ Amount = 42 }` as `EvolutionV0`, assign those
   bytes to the release journal view/entry, then serialize each outer object with Orleans
   `Serializer.SerializeToArray` and Base64-encode it.

The three `*-v0.base64` files were generated from commit
`19cab0d7836972200b2a8fdc157d97e3ff7eb2c9`, after codec envelopes were introduced but before
their `SchemaVersion` field existed. They prove omitted schema fields read as version `0`; they
are pre-schema revision fixtures, not artifacts from the `v4.1.0` package.

`fsharp-binary-property-poco-body-v5-preview.base64` and
`fsharp-binary-property-poco-envelope-v5-preview.base64` freeze the binary bytes emitted by
commit `a6cfb488d019c6d54f09344505270d4b28c965f3` for an ordinary CLR property POCO with
`Count = 42` and `Name = "baseline"`. They were captured before the issue #33 member-mapping
fix. The body fixture pins the inner codec, while the envelope fixture also pins its embedded
`Type.FullName`. Existing property-based POCOs must continue reading both fixtures and emitting
the same bytes after the fix.
