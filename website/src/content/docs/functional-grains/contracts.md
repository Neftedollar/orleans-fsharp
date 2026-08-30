---
title: "Contracts and Keys"
description: "Stable actor identity, operation shapes, key codecs, and rolling contract versions."
---

# Contracts and Keys

**Define a stable Orleans identity before you define behavior.**

A functional contract binds four identities which must remain deliberate across deployments:

| Part | Purpose |
|---|---|
| `'Actor` | Compile-time brand separating actors which happen to share an API record |
| `'Key` | Domain key type visible at every call site |
| `'Api` | Record of callable operations |
| `grainType` + `version` | Orleans-visible wire identity and routing version |

```fsharp
[<Struct>]
type RoomId = private RoomId of string

[<RequireQualifiedAccess>]
module RoomId =
    let create value = RoomId value
    let value (RoomId value) = value

let roomContract =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.room"
        version 1
        stringKeyMapped RoomId.value RoomId.create
        readOnly (_.history)
        oneWay (_.typing)
    }
```

The key mapping must round-trip and stay stable. Changing it can address a different grain and
therefore a different persisted state even when the F# key type is unchanged.

## One operation, one argument

Each API field is either:

- `'Argument -> Task<'Reply>` for one reply; or
- `'Argument -> IAsyncEnumerable<'Item>` for a streaming reply.

Group several domain values in a named record:

```fsharp
type Typing =
    { user: UserId
      isTyping: bool }

type RoomApi =
    { typing: Typing -> Task<unit> }
```

A tuple such as `UserId * bool` is a different wire shape. Do not introduce it later as an
alternative spelling for `Typing`.

## Operation IDs and versions

A field name is its operation ID unless `operationId` overrides it. Renaming a field without
keeping the old ID is a protocol change.

Contract matching is exact by default. For a rolling deployment, Orleans routing and functional
payload admission must both accept the caller:

```fsharp
grainContract<RoomActor, RoomId, RoomApiV2> {
    grainType "chat.room"
    version 2
    acceptsVersions 1 2
    stringKeyMapped RoomId.value RoomId.create
    sinceVersion (_.newOperation) 2
}
```

Deploy readers before writers, keep stable operation IDs, and test N and N+1 as separate
processes. Contract compatibility does not prove stored state or event compatibility.

## Details

- [Actor brands and short form](/orleans-fsharp/functional-grains/#why-the-actor-brand)
- [Key-codec identity rules](/orleans-fsharp/functional-grains/#key-codec-identity-rules)
- [Operation rename and contract version](/orleans-fsharp/functional-grains/#operation-rename-and-contract-version)
- [Calling the same contract from C#](/orleans-fsharp/calling-from-csharp/)
