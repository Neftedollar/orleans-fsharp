# Serialization

The functional runtime carries API arguments, replies, state, and journal events without requiring Orleans serializer attributes on ordinary F# records or discriminated unions.

Transport serialization and durable persistence are separate choices. The functional transport
continues to use its own payload codec; the persistence settings below affect only values written
to grain storage or a journal.

## Functional runtime default

Registering a definition with `AddFunctionalGrain` and a client with `AddFunctionalGrainClient` installs the functional transport and its binary payload codec. API records themselves are local typed facades; only operation arguments and replies cross the wire.

```fsharp
host.UseOrleans(fun siloBuilder ->
    siloBuilder.AddMemoryGrainStorage("Default") |> ignore
    siloBuilder.AddFunctionalGrain(counterDefinition) |> ignore)

clientBuilder.AddFunctionalGrainClient() |> ignore
```

Plain F# types need no `[<GenerateSerializer>]` or `[<Id>]` attributes:

```fsharp
type OrderState =
    | Created of orderId: string
    | Paid of amount: decimal
    | Shipped of trackingNumber: string

type PlaceOrder = { orderId: string; total: decimal }

type OrderReply = Result<int64, string>
```

Supported shapes include records, discriminated unions, options and value options, lists, arrays, sets, maps, tuples, enums, common collection interfaces, POCO classes, and nested combinations of those shapes.

## Durable persistence codecs

Functional persistent state and functional journals default to
`FunctionalPersistenceCodec.OrleansBinary`. This is intentionally the compatibility default.
`FunctionalPersistenceCodec.FSharpJson` selects F#-aware System.Text.Json instead, and
`FunctionalPersistenceCodec.CreateFSharpJson(options)` creates the same codec with a copied
`JsonSerializerOptions` instance. For a custom durable contract, prefer the overload which also
takes an application-owned stable codec id.

Set the two silo defaults independently:

```fsharp
open Orleans.FSharp
open Orleans.Hosting

let configurePersistence (silo: ISiloBuilder) =
    silo.ConfigureFunctionalPersistence(fun options ->
        options.DefaultStateCodec <- FunctionalPersistenceCodec.FSharpJson
        options.DefaultJournalCodec <- FunctionalPersistenceCodec.OrleansBinary)
```

Or select F# JSON for both defaults:

```fsharp
silo.UseFunctionalFSharpJsonPersistence() |> ignore
```

The state hierarchy is **element > grain > silo**:

```fsharp
let auditState =
    PersistentState.create<AuditState> "audit" "Default"
    |> PersistentState.withCodec FunctionalPersistenceCodec.OrleansBinary

let accountDefinition =
    grainFor accountContract {
        initialState (fun _ -> AccountState.empty)
        stateFrom accountState
        usePersistentState auditState (fun _ -> AuditState.empty)

        // Applies to accountState and any element without its own withCodec.
        persistenceCodec FunctionalPersistenceCodec.FSharpJson

        // handlers...
    }
```

Here `auditState` remains binary because the descriptor-level choice wins. `accountState` uses the
grain-level JSON choice; a state element without either override inherits
`FunctionalPersistenceOptions.DefaultStateCodec`.

The journal hierarchy has no element level: `journalCodec` on the journaled definition overrides
`FunctionalPersistenceOptions.DefaultJournalCodec`.

```fsharp
let accountDefinition =
    journaledGrainFor accountContract {
        initialEventState initialAccount
        apply applyAccount
        logProvider "LogStorage"
        journalCodec FunctionalPersistenceCodec.FSharpJson

        // handlers...
    }
```

### Provider-wide serializer versus a functional envelope

These are different layers:

| Configuration | Boundary it controls | Scope |
|---|---|---|
| A provider's `GrainStorageSerializer <- FSharpJsonGrainStorageSerializer(...)` | How that `IGrainStorage` provider serializes its complete Orleans state value | Every user of that provider, including ordinary non-functional grains |
| `withCodec`, `persistenceCodec`, or `DefaultStateCodec` | Whether a functional holder uses the direct binary schema or a runtime-owned JSON envelope | One functional state element, one functional grain, or functional silo defaults |
| `journalCodec` or `DefaultJournalCodec` | State/event payloads inside runtime-owned journal views and entries | One functional journaled definition or functional silo defaults |

For example, provider-wide F# JSON can be enabled for an Orleans memory provider without opting
functional state elements into the functional JSON envelope:

```fsharp
open Orleans.Configuration

silo.AddMemoryGrainStorage(
    "Default",
    fun (options: MemoryGrainStorageOptions) ->
        options.GrainStorageSerializer <- FSharpJsonGrainStorageSerializer())
|> ignore
```

The two layers may be combined. In that case the provider serializer owns the outer Orleans value,
while the functional codec owns the exact application payload inside it. Configuring one does not
implicitly configure the other.

### Stored schema and migrations

For supported direct state types, the default `OrleansBinary` path keeps the pre-feature storage
schema: the provider still sees the application state type directly. Selecting F# JSON for a
functional state element changes that provider-facing value to a codec-tagged envelope.

Consequently, changing an existing functional state from direct `OrleansBinary` storage to the
JSON envelope -- **or changing it back** -- requires an explicit data migration or a new
`stateName`. Merely changing the selected codec cannot reinterpret the other provider schema.
Changing a provider-wide `GrainStorageSerializer` is a separate provider-format migration and must
be assessed according to that provider's guarantees.

Functional journal views and entries already have a stable envelope shape and each stored value
carries its own `CodecId`. Records written before this feature have an empty `CodecId`; the runtime
reads an empty or whitespace id as `OrleansBinary`. New binary and JSON payloads are tagged with
their stable ids, so a journal can decode existing entries according to the format that wrote each
one.

### Custom JSON options are a durable contract

The one-argument `CreateFSharpJson(options)` overload retains the built-in `fsharp-json-v1` id for
compatibility. That id does not capture converter settings, union representation, naming policy, or
other option differences. New custom durable formats should use the explicit-id overload:

```fsharp
open System.Text.Json

let jsonCodec =
    let options = JsonSerializerOptions(FSharpJson.serializerOptions)
    options.PropertyNameCaseInsensitive <- true
    FunctionalPersistenceCodec.CreateFSharpJson("orders-json-v1", options)
```

Treat an incompatible options change as a new codec version and retain the historical reader:

```fsharp
let ordersJsonV1 =
    FunctionalPersistenceCodec.CreateFSharpJson("orders-json-v1", oldOptions)

let currentJournalCodec =
    FunctionalPersistenceCodec.OrleansBinary
        .WithReadCodec(ordersJsonV1)

// Equally valid when JSON v2 is the new writer:
let ordersJsonV2 =
    FunctionalPersistenceCodec.CreateFSharpJson("orders-json-v2", newOptions)
        .WithReadCodec(ordersJsonV1)
```

`WithReadCodec` returns a codec which writes exactly like its receiver and can read the registered
historical JSON ids. It refuses every reader id already registered on the receiver, including a
nested collision with the current JSON write id, because those payloads would be ambiguous. Data
already written by several incompatible option sets under the shared `fsharp-json-v1` id cannot be
distinguished after the fact; keep one reader compatible with all of it while migrating.
Provider-wide `FSharpJsonGrainStorageSerializer` has no per-record codec tag, so changing its custom
options remains a provider-format migration.

## Explicit generalized serializers

The hosting computation expressions can select one generalized-serialization policy for Orleans
payloads in the process. Orleans generated and built-in serializers keep their normal higher
priority. Configure every silo and standalone client which exchanges these payloads consistently.
This policy controls unannotated CLR values crossing Orleans boundaries, including functional
operation arguments/replies and ordinary grain method arguments. It does not select a durable
persistence codec; use the settings above for stored state and journals.

### F# binary

```fsharp
let silo = siloConfig {
    useLocalhostClustering
    useFSharpBinarySerialization
}

let client = clientConfig {
    useLocalhostClustering
    useFSharpBinarySerialization
}
```

Use this for compact F#-only payloads without source-generation attributes.

### F# JSON

```fsharp
let silo = siloConfig {
    useLocalhostClustering
    useFSharpJsonSerialization
}

let client = clientConfig {
    useLocalhostClustering
    useFSharpJsonSerialization
}
```

This makes F#-aware System.Text.Json the generalized codec. JSON is useful when readability matters
more than size and throughput. It is a different wire format from F# binary; changing an existing
client or silo requires a coordinated deployment and does not make already-written binary payloads
JSON-readable.

### Binary with JSON for unsupported types

Use an explicit type-based policy when binary should remain primary and JSON should handle only
types which the binary codec declines:

```fsharp
let policy =
    FSharpSerialization.Binary
    |> FSharpSerialization.forUnsupportedTypes FSharpSerialization.Json

let silo = siloConfig {
    useLocalhostClustering
    useFSharpSerialization policy
}

let client = clientConfig {
    useLocalhostClustering
    useFSharpSerialization policy
}
```

This does not retry JSON after a binary serialization error. Orleans selects one codec from the
declared CLR type before serialization begins.

### Orleans native serialization

Types shared directly with ordinary C# Orleans grains can use Orleans' generated serializers:

```fsharp
[<GenerateSerializer>]
type SharedMessage =
    { [<Id(0u)>]
      orderId: string
      [<Id(1u)>]
      amount: decimal }
```

The functional API does not require this. Use it only when the same CLR type must participate directly in a native Orleans contract or another component already depends on that format.

## Mixing formats

Generated serializers have priority for annotated types. The selected generalized policy handles
supported unannotated types. This allows native C#/F# shared messages and functional F# payloads in
one silo without relying on serializer registration order.

Do not configure incompatible policies on different cluster participants. A client and silo that exchange a generalized payload must understand the same format.

## Schema evolution

| Change | F# binary | F# JSON | Orleans generated |
|---|---|---|---|
| Append a DU case | Compatible while old readers never receive it | Compatible while old readers never receive it | Compatible with a new id |
| Reorder DU cases | Breaking | Name-based | Safe when ids stay fixed |
| Add or remove a record field | Breaking for stored/wire data | Treat as breaking unless covered by explicit JSON policy | Safe only with stable ids and compatible defaults |
| Rename a DU case | Ordinal representation is unchanged | Breaking | Safe when ids stay fixed |

Treat persisted grain state, journal events, and custom snapshots as durable contracts. For the functional binary codec, union cases and record fields are positional: append cases, keep old cases foldable, and migrate stored state explicitly when a record shape changes.

## Security boundary

Generalized deserialization resolves declared CLR types. Accept payloads only from trusted Orleans cluster participants, keep cluster transport authenticated, and do not expose raw serialized envelopes as a public untrusted-input endpoint.

## Legacy serialization

Configuration and CodeGen examples for the original authoring model are retained in [Legacy Serialization](legacy/serialization.md).

## Next steps

- [Functional Grain Runtime](functional-grains.md)
- [Event Sourcing](event-sourcing.md)
- [Silo Configuration](silo-configuration.md)
- [Legacy Serialization](legacy/serialization.md)
