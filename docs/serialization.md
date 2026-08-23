# Serialization

The functional runtime carries API arguments, replies, state, and journal events without requiring Orleans serializer attributes on ordinary F# records or discriminated unions.

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

## Explicit generalized serializers

The hosting computation expressions can also register a generalized serializer for other Orleans payloads in the same process. Configure both silo and standalone client consistently.

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

### JSON fallback

```fsharp
let silo = siloConfig {
    useLocalhostClustering
    useJsonFallbackSerialization
}

let client = clientConfig {
    useLocalhostClustering
    useJsonFallbackSerialization
}
```

JSON is useful when a readable payload matters more than size and throughput. It is a fallback for types without an Orleans generated serializer.

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

Generated serializers have priority for annotated types. The configured generalized serializer handles supported unannotated types. This allows native C#/F# shared messages and functional F# payloads in one silo.

Do not configure incompatible fallbacks on different cluster participants. A client and silo that exchange a generalized payload must understand the same format.

## Schema evolution

| Change | F# binary | JSON fallback | Orleans generated |
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
