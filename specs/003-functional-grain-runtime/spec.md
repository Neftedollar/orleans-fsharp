# Feature Proposal 003: Functional Grain Runtime with User-Authored API Records

**Status:** implementation proposal

**Target:** .NET 10, F# 10, Orleans 10.1.0 minimum; Orleans 10.2.2 must also pass

**Scope:** contracted functional grains with ordinary `IPersistentState`
persistence

## Objective

Implement a functional-grain runtime in which an application contract consists
of:

1. a phantom actor type, called the actor brand;
2. a domain key type with an explicit Orleans key codec;
3. a public F# record whose fields are remote operations;
4. immutable contract metadata; and
5. a server definition containing state initialization, handlers, persistence,
   lifecycle, timer, reminder, collection, and scheduling configuration.

Every API-record field has the shape:

```fsharp
'Argument -> Task<'Reply>
```

The runtime binds the record to an Orleans grain reference. Client code uses the
bound record directly:

```fsharp
task {
    let lobby = RoomApi.ref client (RoomId.create "general")

    do! lobby.join userId
    let! recent = lobby.history { take = 20 }
    return recent
}
```

## Public authoring model

### Shared domain and contract

```fsharp
namespace Chat.Contracts

open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

[<Struct>]
type UserId = private UserId of string

[<RequireQualifiedAccess>]
module UserId =
    let create value = UserId value
    let value (UserId value) = value

[<Struct>]
type RoomId = private RoomId of string

[<RequireQualifiedAccess>]
module RoomId =
    let create value = RoomId value
    let value (RoomId value) = value

type PostMessage =
    { author : UserId
      text : string }

type ChatMessage =
    { author : UserId
      text : string
      sentAt : DateTimeOffset }

type HistoryRequest = { take : int }

type Typing =
    { user : UserId
      isTyping : bool }

type PostError =
    | NotAMember
    | EmptyText

type RoomActor = private RoomActor of unit

[<NoEquality; NoComparison>]
type RoomApi =
    { join : UserId -> Task<unit>
      say : PostMessage -> Task<Result<int64, PostError>>
      history : HistoryRequest -> Task<ChatMessage list>
      typing : Typing -> Task<unit> }

[<RequireQualifiedAccess>]
module RoomApi =
    let contract =
        grainContract<RoomActor, RoomId, RoomApi>() {
            grainType "chat.room"
            version 1
            stringKeyMapped RoomId.value RoomId.create

            readOnly (_.history)
            oneWay (_.typing)
            alwaysInterleave (_.typing)
        }

    let ref = FunctionalGrain.ref contract
    let rawRef = FunctionalGrain.rawRef contract
```

`[<NoEquality; NoComparison>]` is recommended for records of functions, but is
not required by the runtime. Contract policies and handlers identify operations
with ordinary record-field projections such as `_.history` and `_.join`.

`stringKeyMapped` keeps `RoomId` as the public key type and supplies its two-way
mapping to Orleans' native string key. `readOnly` selects read-only scheduling;
`oneWay` requires a `Task<unit>` field and acknowledges only the local send;
`alwaysInterleave` permits that state-neutral one-way request to interleave.

The compiler infers these complete types without annotations:

```fsharp
contract : GrainContract<RoomActor, RoomId, RoomApi>
ref : IGrainFactory -> RoomId -> RoomApi
rawRef :
    IGrainFactory ->
    RoomId ->
    FunctionalGrainRef<RoomActor, RoomId, RoomApi>
```

`contract`, `ref`, and `rawRef` are ordinary values. Their module and parameter
order are application choices. For example, this binding remains fully
inferred and presents the key before the factory:

```fsharp
module RoomClient =
    let ref roomId factory = FunctionalGrain.ref RoomApi.contract factory roomId
    let rawRef roomId factory =
        FunctionalGrain.rawRef RoomApi.contract factory roomId
```

Application calls normally use `ref`. `rawRef` returns the typed wrapper which
exposes `key`, the same cached `api` record, selector-based `call`, and
`callCancellable` for cooperative remote cancellation. Both functions address
the same grain identity and use the same transport path.

### Server definition

```fsharp
module Chat.Server

open System
open Chat.Contracts
open Microsoft.Extensions.Logging
open Orleans.FSharp

type RoomState =
    { nextMessageId : int64
      members : Set<UserId>
      messages : ChatMessage list }

let roomState =
    PersistentState.create<RoomState> "state" "Default"

let roomDefinition =
    grainFor RoomApi.contract {
        defaultState (fun () ->
            { nextMessageId = 1L
              members = Set.empty
              messages = [] })

        stateFrom roomState
        collectionAge (TimeSpan.FromMinutes 30.0)

        handle (_.join) (fun context state userId ->
            task {
                let next =
                    { state with
                        members = Set.add userId state.members }

                let storage = context.persistentState roomState
                storage.State <- next
                do! storage.WriteStateAsync()
                return next, ()
            })

        handle (_.say) (fun context state post ->
            task {
                if not (Set.contains post.author state.members) then
                    return state, Error NotAMember
                elif String.IsNullOrWhiteSpace post.text then
                    return state, Error EmptyText
                else
                    let message =
                        { author = post.author
                          text = post.text
                          sentAt = context.utcNow }

                    let id = state.nextMessageId

                    let next =
                        { state with
                            nextMessageId = id + 1L
                            messages = message :: state.messages }

                    let storage = context.persistentState roomState
                    storage.State <- next
                    do! storage.WriteStateAsync()
                    return next, Ok id
            })

        handle (_.history) (fun _context state request ->
            task {
                return
                    state,
                    state.messages
                    |> List.truncate (max 0 request.take)
                    |> List.rev
            })

        handle (_.typing) (fun context state typing ->
            task {
                context.logger.LogDebug(
                    "{User} typing={IsTyping}",
                    typing.user,
                    typing.isTyping)

                return state, ()
            })
    }
```

`stateFrom` selects the loaded primary holder; it does not enable an automatic
save policy. `context.persistentState roomState` returns its typed
`IPersistentState<RoomState>`. Returning `next` publishes it for the activation,
while the two explicit `WriteStateAsync` calls above are the only storage writes
in those handlers. `collectionAge` controls when an idle in-memory activation
becomes eligible for Orleans collection; it does not control storage writes.

### Registration and calls

Register each hosted definition on the silo and install the client transport on
every process which creates functional references:

```fsharp
siloBuilder.AddFunctionalGrain(roomDefinition) |> ignore
clientBuilder.AddFunctionalGrainClient() |> ignore
```

Both external clients and grain handlers bind references through
`IGrainFactory`:

```fsharp
task {
    let lobby = RoomApi.ref client (RoomId.create "general")

    do! lobby.join (UserId.create "alice")

    let! result =
        lobby.say
            { author = UserId.create "alice"
              text = "Hello from F#" }

    let! recent = lobby.history { take = 20 }
    return result, recent
}
```

Inside a handler:

```fsharp
let otherRoom = RoomApi.ref context.grainFactory otherRoomId
do! otherRoom.join userId
```

## Normative public API

The following `.fsi` sketch fixes public names, generic relationships, and
computation-expression member shapes. Constructors and internal representations
of opaque types remain private.

```fsharp
namespace Orleans.FSharp

open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Hosting
open Orleans.Runtime

[<Sealed>]
type PersistentStateRef<'State>

[<RequireQualifiedAccess>]
module PersistentState =
    val create<'State> :
        stateName: string ->
        providerName: string ->
        PersistentStateRef<'State>

[<Sealed>]
type FunctionalGrainContext<'Actor, 'Key> =
    member key : 'Key
    member grainId : GrainId
    member grainFactory : IGrainFactory
    member services : IServiceProvider
    member logger : ILogger
    member timeProvider : TimeProvider
    member utcNow : DateTimeOffset
    member cancellationToken : CancellationToken
    member deactivateOnIdle : unit -> unit
    member delayDeactivation : TimeSpan -> unit
    member persistentState<'State> :
        state: PersistentStateRef<'State> ->
        IPersistentState<'State>
    member tryGetRequestContext<'Value> : name: string -> 'Value option
    member setRequestContext<'Value> : name: string -> value: 'Value -> unit
    member removeRequestContext : name: string -> unit

type OperationSelector<'Api, 'Argument, 'Reply> =
    'Api -> ('Argument -> Task<'Reply>)

type Handler<'Actor, 'Key, 'State, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> ->
    'State ->
    'Argument ->
    Task<'State * 'Reply>

type ActivateHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> ->
    'State ->
    Task<'State>

type DeactivateHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> ->
    DeactivationReason ->
    'State ->
    Task<unit>

type ReminderHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> ->
    'State ->
    TickStatus ->
    Task<'State>

type TimerHook<'Actor, 'Key, 'State> =
    FunctionalGrainContext<'Actor, 'Key> ->
    'State ->
    Task<'State>

type IFunctionalRequestMetadata =
    abstract GrainType : string
    abstract ContractVersion : int
    abstract OperationId : string
    abstract IsReadOnly : bool
    abstract IsOneWay : bool
    abstract IsAlwaysInterleave : bool
    abstract PayloadLength : int

[<Sealed>]
type FunctionalGrainTransportOptions =
    new : unit -> FunctionalGrainTransportOptions
    member MaxPayloadBytes : int with get, set

[<Sealed>]
type GrainContract<'Actor, 'Key, 'Api>

[<Sealed>]
type FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>

[<Sealed>]
type FunctionalGrainRef<'Actor, 'Key, 'Api> =
    member key : 'Key
    member api : 'Api

    member call :
        selector: OperationSelector<'Api, 'Argument, 'Reply> ->
        argument: 'Argument ->
        Task<'Reply>

    member callCancellable :
        selector: OperationSelector<'Api, 'Argument, 'Reply> ->
        argument: 'Argument ->
        cancellationToken: CancellationToken ->
        Task<'Reply>

[<AbstractClass; Sealed>]
type FunctionalGrain =
    static member ref :
        contract: GrainContract<'Actor, 'Key, 'Api> ->
        (IGrainFactory -> 'Key -> 'Api)

    static member rawRef :
        contract: GrainContract<'Actor, 'Key, 'Api> ->
        (IGrainFactory -> 'Key -> FunctionalGrainRef<'Actor, 'Key, 'Api>)

[<Sealed>]
type GrainContractDraft<'Actor, 'Key, 'Api>

[<Sealed>]
type GrainContractBuilder<'Actor, 'Key, 'Api> =
    member Yield :
        unit -> GrainContractDraft<'Actor, 'Key, 'Api>

    member Run :
        GrainContractDraft<'Actor, 'Key, 'Api> ->
        GrainContract<'Actor, 'Key, 'Api>

    [<CustomOperation("grainType")>]
    member GrainType :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        value: string ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("version")>]
    member Version :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        value: int ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("stringKey")>]
    member StringKey :
        state: GrainContractDraft<'Actor, string, 'Api> ->
        GrainContractDraft<'Actor, string, 'Api>

    [<CustomOperation("stringKeyMapped")>]
    member StringKeyMapped :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> string) *
        decode: (string -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("guidKey")>]
    member GuidKey :
        state: GrainContractDraft<'Actor, Guid, 'Api> ->
        GrainContractDraft<'Actor, Guid, 'Api>

    [<CustomOperation("guidKeyMapped")>]
    member GuidKeyMapped :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> Guid) *
        decode: (Guid -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("int64Key")>]
    member Int64Key :
        state: GrainContractDraft<'Actor, int64, 'Api> ->
        GrainContractDraft<'Actor, int64, 'Api>

    [<CustomOperation("int64KeyMapped")>]
    member Int64KeyMapped :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> int64) *
        decode: (int64 -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("guidCompoundKey")>]
    member GuidCompoundKey :
        state: GrainContractDraft<'Actor, Guid * string, 'Api> ->
        GrainContractDraft<'Actor, Guid * string, 'Api>

    [<CustomOperation("guidCompoundKeyMapped")>]
    member GuidCompoundKeyMapped :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> Guid * string) *
        decode: (Guid -> string -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("int64CompoundKey")>]
    member Int64CompoundKey :
        state: GrainContractDraft<'Actor, int64 * string, 'Api> ->
        GrainContractDraft<'Actor, int64 * string, 'Api>

    [<CustomOperation("int64CompoundKeyMapped")>]
    member Int64CompoundKeyMapped :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> int64 * string) *
        decode: (int64 -> string -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("readOnly")>]
    member ReadOnly<'Argument, 'Reply> :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        selector: OperationSelector<'Api, 'Argument, 'Reply> ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("oneWay")>]
    member OneWay<'Argument> :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        selector: OperationSelector<'Api, 'Argument, unit> ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("alwaysInterleave")>]
    member AlwaysInterleave<'Argument, 'Reply> :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        selector: OperationSelector<'Api, 'Argument, 'Reply> ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("operationId")>]
    member OperationId<'Argument, 'Reply> :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        stableWireId: string *
        selector: OperationSelector<'Api, 'Argument, 'Reply> ->
        GrainContractDraft<'Actor, 'Key, 'Api>

[<Sealed>]
type FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api>

[<Sealed>]
type FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

[<Sealed>]
type FunctionalGrainDefinitionBuilder<'Actor, 'Key, 'Api> =
    member Yield :
        unit -> FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api>

    member Run<'State> :
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> ->
        FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("defaultState")>]
    member DefaultState<'State> :
        state: FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api> *
        factory: (unit -> 'State) ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("initialState")>]
    member InitialState<'State> :
        state: FunctionalGrainDefinitionSeed<'Actor, 'Key, 'Api> *
        factory: ('Key -> 'State) ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("handle")>]
    member Handle<'State, 'Argument, 'Reply> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        selector: OperationSelector<'Api, 'Argument, 'Reply> *
        handler: Handler<'Actor, 'Key, 'State, 'Argument, 'Reply> ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("stateFrom")>]
    member StateFrom<'State> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        persistentState: PersistentStateRef<'State> ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("usePersistentState")>]
    member UsePersistentState<'State, 'StoredState> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        persistentState: PersistentStateRef<'StoredState> *
        initializer: ('Key -> 'StoredState) ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("collectionAge")>]
    member CollectionAge<'State> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        age: TimeSpan ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("onActivate")>]
    member OnActivate<'State> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        hook: ActivateHook<'Actor, 'Key, 'State> ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("onDeactivate")>]
    member OnDeactivate<'State> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        hook: DeactivateHook<'Actor, 'Key, 'State> ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("onReminder")>]
    member OnReminder<'State> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        name: string *
        dueTime: TimeSpan *
        period: TimeSpan *
        hook: ReminderHook<'Actor, 'Key, 'State> ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

    [<CustomOperation("onTimer")>]
    member OnTimer<'State> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        name: string *
        options: GrainTimerCreationOptions *
        hook: TimerHook<'Actor, 'Key, 'State> ->
        FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State>

[<AutoOpen>]
module FunctionalGrainBuilders =
    val grainContract<'Actor, 'Key, 'Api> :
        unit -> GrainContractBuilder<'Actor, 'Key, 'Api>

    val grainFor :
        contract: GrainContract<'Actor, 'Key, 'Api> ->
        FunctionalGrainDefinitionBuilder<'Actor, 'Key, 'Api>

[<AbstractClass; Sealed; Extension>]
type FunctionalGrainClientHostingExtensions =
    [<Extension>]
    static member AddFunctionalGrainClient :
        builder: IClientBuilder -> IClientBuilder

[<AbstractClass; Sealed; Extension>]
type FunctionalGrainSiloHostingExtensions =
    [<Extension>]
    static member AddFunctionalGrain<'Actor, 'Key, 'Api, 'State> :
        builder: ISiloBuilder *
        definition: FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State> ->
        ISiloBuilder
```

`FunctionalGrainClientHostingExtensions` is implemented by
`Orleans.FSharp`. `FunctionalGrainSiloHostingExtensions` is implemented by
`Orleans.FSharp.Runtime`. Both types use the `Orleans.FSharp` namespace, so the
call-site syntax shown above is stable. The silo registration path calls the
client registration path before adding server services.

`grainContract` is a generic factory function, so the exact spelling is
`grainContract<RoomActor, RoomId, RoomApi>() { ... }`. The first operation in a
definition expression is `defaultState` or `initialState`; it introduces
`'State`, and all later operations retain that state type. `Run` seals the value
and validates completeness.

`stateFrom` accepts only a descriptor whose stored type is the definition's
primary `'State`. `usePersistentState` is repeatable, retains each independent
stored type, and takes a key-aware initializer used only when that holder has no
record. Neither operation implies a storage write.

Native key operations take no arguments and constrain `'Key` to their exact
native type. Mapped key operations retain the domain `'Key` and take an encoder
and decoder. Mapped compound encoders return ordinary F# reference tuples and
their decoders are curried. Definition construction copies `DueTime`, `Period`,
`Interleave`, and `KeepAlive` from `GrainTimerCreationOptions` into immutable
metadata. `onReminder` stores explicit due time and period.

`FunctionalGrain.ref contract factory key` returns
`(FunctionalGrain.rawRef contract factory key).api`. Bound record calls use
`CancellationToken.None`; `callCancellable` supplies remote cooperative
cancellation for advanced calls.

`FunctionalGrain` is a static class whose members take `contract` as their only
declared parameter and return the remaining curried function. This form is
load-bearing, not stylistic: F# inserts subtype flexibility for non-sealed
types at declared parameter positions of the member being used, so declaring
`factory: IGrainFactory` as a second curried parameter makes every point-free
partial application (`let ref = FunctionalGrain.ref contract`) generic in a
flexible `'_a :> IGrainFactory` and fail the value restriction (FS0030). With
the factory in the result type the partial application stays concrete, the
example's point-free bindings infer their complete types with no annotation and
no use site, and ordinary subsumption still accepts any `IGrainFactory`
implementation (such as `IClusterClient`) at application sites.

## API records, selectors, and contracts

### API-record shape

`'Api` is a public F# reference record with a public representation, public
record constructor, public field getters, and a closed CLR type. A closed
constructed generic record is valid. A struct record and an open generic record
are invalid.

Every field is an operation and has exactly this type:

```fsharp
'Argument -> Task<'Reply>
```

Use `unit -> Task<'Reply>` for an operation without domain input and
`'Argument -> Task<unit>` for an acknowledged operation without a result.
Multiple domain inputs are grouped in one tuple or record. `Async<'Reply>`,
`ValueTask<'Reply>`, non-generic `Task`, curried multi-input functions, and
non-function fields fail contract construction. Helpers are ordinary values in
the companion module.

Contract construction validates the complete record shape before returning a
`GrainContract`.

### Selector resolution

The documented selector forms are:

```fsharp
_.join
fun api -> api.join
```

Create one cached `ApiShape` for each closed API type:

1. Verify `FSharpType.IsRecord(apiType, BindingFlags.Public)`, reference-record
   representation, public constructor, public getters, and closed type.
2. Read fields in declaration order with
   `FSharpType.GetRecordFields(apiType, BindingFlags.Public)`.
3. For each field, use `FSharpType.IsFunction` and
   `FSharpType.GetFunctionElements` to extract argument and range types.
4. Verify that the range is exactly `Task<'Reply>` and record the reply type.
5. Create one unique function object for each record field, with that field's
   exact function type, using `FSharpValue.MakeFunction`; its body throws if
   invoked. Fields with identical function types receive distinct objects.
6. Construct one probe record from those sentinels with a cached
   `FSharpValue.PreComputeRecordConstructor` delegate.
7. Invoke the selector once with the probe record.
8. Match its returned object against the field sentinels with
   `Object.ReferenceEquals`.
9. Accept exactly one match. Otherwise fail with a diagnostic containing
   `Use a direct API field selector such as _.join.`

Contract and definition construction catch a selector exception and wrap it in a
diagnostic naming the API type and configuration entry. A normal bound-field
call never invokes a selector. `FunctionalGrainRef.call` resolves its explicit
selector once for that raw call against the cached shape.

The acceptance rule is physical identity of the returned sentinel. A helper,
side effect, captured condition, or branch can resolve if it ultimately returns
one original sentinel. Application documentation requires direct field
projections and describes selector execution as construction-time configuration
code.

### Contract metadata

A contract discovers all operation descriptors from the record, then applies
its custom operations.

| Setting | Rule |
|---|---|
| `grainType` | Optional; defaults to the actor brand's CLR simple name. Non-blank and NUL-free when explicit. |
| `version` | Defaults to `1`; positive `int`. |
| operation ID | Record-field name by default; ordinal and case-sensitive. |
| key | Exactly one native or mapped key operation. |
| invocation | Acknowledged, sequential, and state-replacing by default. |

`operationId` preserves a stable operation identity across a source-field
rename:

```fsharp
operationId "join" (_.enter)
```

The first parameter becomes the wire ID and the selector identifies the current
field. Final IDs are unique, non-blank, NUL-free ordinal strings. Each kind of
policy operation and the ID override occurs at most once per field; distinct
policy kinds may combine on one field within the valid combinations defined
under "Operation policies" (the example's `oneWay` + `alwaysInterleave` pair on
`_.typing` is such a combination).

Contract version is application protocol metadata in every request. Requests
must match the hosted version exactly. Version is independent of `GrainId`,
storage identity, and the internal Orleans interface version, which is fixed at
`1` for this transport family.

### Key codecs and identity

The contract supports five native Orleans key shapes and mapped domain-key
variants of each shape:

| Custom operation | Required domain `'Key` | Arguments |
|---|---|---|
| `stringKey` | `string` | none |
| `guidKey` | `Guid` | none |
| `int64Key` | `int64` | none |
| `guidCompoundKey` | `Guid * string` | none |
| `int64CompoundKey` | `int64 * string` | none |
| `stringKeyMapped` | any closed `'Key` | `'Key -> string`, `string -> 'Key` |
| `guidKeyMapped` | any closed `'Key` | `'Key -> Guid`, `Guid -> 'Key` |
| `int64KeyMapped` | any closed `'Key` | `'Key -> int64`, `int64 -> 'Key` |
| `guidCompoundKeyMapped` | any closed `'Key` | `'Key -> Guid * string`, `Guid -> string -> 'Key` |
| `int64CompoundKeyMapped` | any closed `'Key` | `'Key -> int64 * string`, `int64 -> string -> 'Key` |

Native keys require no conversion functions:

```fsharp
grainContract<SessionActor, string, SessionApi>() {
    grainType "session"
    stringKey
}

grainContract<TenantItemActor, Guid * string, TenantItemApi>() {
    grainType "tenant.item"
    guidCompoundKey
}
```

An ordinary domain wrapper uses its unwrap and construct functions:

```fsharp
stringKeyMapped RoomId.value RoomId.create
```

When the application uses the optional `FSharp.UMX` package, an erased tagged
key has the same mapping without an allocated wrapper:

```fsharp
[<Measure>]
type roomId

type TaggedRoomId = string<roomId>

stringKeyMapped UMX.untag UMX.tag
```

For every mapped key, its conversion pair satisfies:

- deterministic, injective encoding in its selected Orleans key space;
- `decode (encode key) = key` under domain equality;
- `encode (decode native) = native` in canonical Orleans representation;
- rejection of malformed or non-canonical native values.

Compound extensions follow Orleans key validity rules. Shipped and sample
mapped codecs require property tests for these laws.

A key codec produces and reads the same canonical `IdSpan` representation as the
stock Orleans string, Guid, int64, and compound-key helpers. Native-key codecs
use identity domain conversion. Mapped codecs compose their domain conversion
with that stock representation. Null, empty, malformed, and non-canonical keys
follow the corresponding Orleans validation rules.

The actor identity is:

```text
GrainId(GrainType(contract.grainType), keyCodec.encode(domainKey))
```

Changing `grainType` or key encoding changes routing and storage identity.
When the grain type is explicit, changing F# module, record, or actor-brand
CLR names leaves identity unchanged, provided the explicit grain type and
encoded key remain unchanged. Contract version and operation ID are not
storage-key components.

When `grainType` is omitted, it defaults to the actor brand's CLR simple name,
so renaming the brand type changes the *derived* `grainType` string and
therefore changes routing and storage identity exactly as editing an explicit
`grainType` would -- the CLR-rename independence above holds only for an
explicit grain type. This is why a definition that attaches `stateFrom`,
`usePersistentState`, or declares `onReminder` requires its contract to carry
an explicit `grainType`: a brand rename would otherwise silently move routing
and storage identity, orphaning persisted state and losing durable reminders
registered under the old name. Ephemeral definitions (none of the three) have
nothing durable to orphan and may rely on the derived default. The derived
name is also unqualified by namespace: two actor brands with the same CLR
simple name in different namespaces derive the same grain type and collide
under the same grain-type-uniqueness rule that already rejects two explicit
contracts sharing a `grainType` (see "Silo registry and manifest").

## Construction and invocation stages

| Stage | Required work |
|---|---|
| Contract construction | Reflect and cache the API shape, build probe sentinels, resolve policy selectors, and seal operation descriptors. |
| Definition sealing | Resolve handler selectors, verify complete handler coverage, and freeze state/lifecycle configuration. |
| Reference binding | Encode the key, obtain the exact custom reference, validate serializers, and create one typed closure per record field. |
| Client invocation | Serialize the exact argument, construct and send the fixed request, then validate and deserialize the exact reply. |
| Target dispatch | Validate metadata, resolve the descriptor, deserialize the exact argument type, invoke the typed handler, publish state, and serialize the exact reply. |
| Activation lifecycle | Create every attached persistent facet; then load or initialize in-memory state, run activation hooks, reconcile reminders, and create timers. |

Reflection, selector evaluation, and generic-method closing occur while caching
the shape or binding a reference. A bound field invokes its cached typed closure
directly.

### Operation descriptors and bound references

Each immutable descriptor contains:

- field index and source-field name;
- stable operation ID;
- exact argument and reply `Type` values;
- policy flags;
- precomputed request and reply protocol tokens;
- a preclosed typed client-closure factory; and
- a preclosed typed server adapter.

`FunctionalGrain.rawRef` performs these steps:

1. Encode the domain key and construct the exact `GrainId`.
2. Construct the stable actor-specific `GrainInterfaceType`.
3. Call `IGrainFactory.GetGrain(grainId, grainInterfaceType)`.
4. Verify that the returned addressable is `FunctionalGrainReference`.
5. Ask the injected payload-codec service to resolve the Orleans serializer for
   every exact argument and reply type, caching success per contract shape and
   serializer-service instance.
6. Create a `FunctionalGrainRef` and one typed closure per field.
7. Build the API record with the cached record constructor and retain that exact
   instance in `.api`.

The generic closure factories and server adapters are closed once per descriptor.
A bound call performs argument serialization, fixed-request construction and
send, reply validation, and typed reply deserialization. Its public return value
is `Task<'Reply>`.

## Definition, context, and state semantics

### Handler binding and definition validation

The handler type is:

```fsharp
type Handler<'Actor, 'Key, 'State, 'Argument, 'Reply> =
    FunctionalGrainContext<'Actor, 'Key> ->
    'State ->
    'Argument ->
    Task<'State * 'Reply>
```

The single tupled `handle` custom operation links the selector and handler's
`'Argument` and `'Reply` parameters. The definition retains the contract's
`'Actor`, `'Key`, and `'Api` types plus one inferred `'State` type.

Definition sealing requires:

- exactly one `defaultState` or `initialState` operation;
- exactly one handler for every API-record field;
- at most one `stateFrom`, `collectionAge`, `onActivate`, and `onDeactivate`
  operation;
- zero or more `usePersistentState` operations;
- a unique `stateName` for every attached persistent state, with one provider
  and one stored CLR type per name;
- a strictly positive `collectionAge` when configured;
- unique non-blank reminder names;
- unique non-blank timer names;
- an explicit contract `grainType` whenever the definition attaches `stateFrom`,
  `usePersistentState`, or declares `onReminder` -- a derived grain type moves
  when the actor brand is renamed, which would silently orphan persisted state
  or lose durable reminders; and
- valid policy and timer combinations.

A repeated singleton operation is a definition error rather than a replacement
of the earlier value.

Initialization is normalized to `'Key -> 'State`. `defaultState` accepts a fresh
`unit -> 'State` factory; `initialState` accepts `'Key -> 'State`. The factory is
called once for each ephemeral activation, or when the primary `stateFrom`
holder reports `RecordExists = false`. Each additional persistent-state
initializer is also normalized to `'Key -> 'StoredState` and is called when that
holder reports no record. Initializers populate memory and never write storage.

### Functional context

Create a new immutable `FunctionalGrainContext<'Actor,'Key>` for every request,
activation hook, deactivation hook, reminder, and timer callback. It contains:

- the domain key decoded once from the supplied `GrainId`;
- `GrainId`, `IGrainFactory`, activation services, and a scoped logger;
- the registered `TimeProvider` and `utcNow = timeProvider.GetUtcNow()`;
- the current target-local cancellation token;
- typed lookup of every persistent state attached to the definition;
- wrappers for Orleans deactivation methods; and
- `RequestContext` accessors.

Cancellation and request-context values belong to that invocation context rather
than activation-wide mutable state. Contexts and service objects stay
process-local.

The context cancellation token is selected by callback kind:

| Callback | `cancellationToken` |
|---|---|
| Acknowledged request | Target-local token created by the cancellable request. |
| Delivered one-way request | `CancellationToken.None`. |
| Activate/deactivate hook | Token supplied by the corresponding Orleans lifecycle callback. |
| Timer hook | Token supplied by the Orleans timer callback. |
| Reminder hook | `CancellationToken.None`, because `IRemindable.ReceiveReminder` supplies no token. |

### Operation policies

| Policy | Required behavior |
|---|---|
| Default | Sequential, acknowledged; returned state is published. |
| `readOnly` | Orleans read-only scheduling; returned replacement is discarded; persistent-state setters and storage calls are rejected. |
| `oneWay` | Its selector is `OperationSelector<'Api,'Argument,unit>`; the bound `Task<unit>` acknowledges local send, while target execution and any explicit storage work continue without a caller response. |
| `alwaysInterleave` | Combined with `readOnly` or `oneWay`; returned replacement is discarded; persistent-state setters and storage calls are rejected. |
| Timer hook | Whole-state replacement under `Interleave = false`. |
| Reminder hook | Whole-state replacement under ordinary Orleans scheduling. |

The F# compiler rejects a `oneWay` selector whose API field does not return
`Task<unit>`. Contract construction defensively checks the same invariant. The
contract rejects `oneWay + readOnly` and `alwaysInterleave` without `readOnly`
or `oneWay`. A sequential one-way operation may perform explicit storage work,
but its caller cannot observe a target-side storage failure.

State-neutral handlers treat the state graph as immutable. Discarding the
returned record cannot undo in-place mutation of an object reachable from state.
The persistent-state facade can reject its `State` setter but cannot intercept
deep mutation through a value returned by its getter, whether the holder is
primary or additional. Examples use immutable F# state.

### State publication

The primary in-memory state is either an activation-local cell or, when
`stateFrom` is configured, the selected `IPersistentState<'State>.State` holder.
A successful sequential handler return assigns its returned state to that
holder. This publication never calls `WriteStateAsync`.

After Orleans' automatic SetupState load, every application-triggered reload,
write, and clear is explicit. Additional holders are reached only through
`context.persistentState`; the primary holder is also supplied as the handler's
`state` value:

```fsharp
let storage = context.persistentState roomState
storage.State <- next
do! storage.WriteStateAsync()
```

`ReadStateAsync`, `WriteStateAsync`, and `ClearStateAsync` retain their ordinary
Orleans semantics. A read replaces that facet's `State`; for the primary facet
this changes the authoritative in-memory holder immediately, although the
handler's already-bound `state` argument remains its turn-entry value. A later
successful handler return can assign the holder again. A clear affects the
backing record and provider metadata but does not define a replacement primary
state. The application decides what the handler returns.

Explicit effects happen in program order, and successful return publication is
last. A handler can therefore write value `A`, return value `B`, and finish with
`A` in durable storage and `B` in the primary in-memory holder. Activate, timer,
and reminder replacements follow the same ordering. If the callback fails,
there is no final return publication.

There are no automatic writes, retries, reloads, rollbacks, ETag-conflict
repairs, or deactivation flushes. If a handler fails, its unreturned replacement
is not assigned, but any explicit `State` setter, successful storage call, or
external effect which already occurred remains. Storage exceptions propagate
through the callback task. Applications use immutable state and explicit
idempotency where required.

`context.persistentState` resolves by the descriptor's logical
`(stateName, providerName, storedType)` identity and fails deterministically for
an unattached descriptor. It returns an invocation-bound `IPersistentState`
facade. The facade rejects use after its callback has completed; in `readOnly`
and `alwaysInterleave` callbacks it permits getters but rejects `State` mutation
and both the parameterless and `CancellationToken` overloads of
`ReadStateAsync`, `WriteStateAsync`, and `ClearStateAsync`.

One-way completion means the message entered the local Orleans send path. Target
execution still awaits the complete handler task inside its Orleans turn. A
successful sequential one-way handler publishes its returned primary state and
then completes the target turn; `oneWay + alwaysInterleave` discards that
replacement. Target failures are recorded through logging and tracing and are
not returned to that caller.

### Lifecycle hooks, timers, and reminders

The hook type aliases in the public API are normative. `onActivate` runs after
persistent-state setup and its returned state is published only in memory.
`onDeactivate` performs cleanup and returns no replacement. Timers, reminders,
and lifecycle hooks use the same explicit persistence capability; the runtime
adds no storage calls around them.

Definition sealing validates:

- reminder `dueTime >= TimeSpan.Zero`;
- reminder `period > TimeSpan.Zero`;
- `GrainTimerCreationOptions.Interleave = false`.

Silo startup validates every declared period against its configured
`ReminderOptions.MinimumReminderPeriod`.

Each successful activation reconciles declared reminders with
`RegisterOrUpdateReminder` in declaration order, after state initialization and
the activate hook have completed, and before activation completes. A renamed or
removed durable reminder is an explicit deployment migration which unregisters
the old name. Timers are created after successful activation and disposed during
deactivation. Orleans supplies reminder/timer retry, scheduling, and logging
behavior.

## Fixed transport and wire protocol

### Runtime transport types

The transport family consists of:

```text
FunctionalGrainMarker<'Actor>       concrete manifest grain type
IFunctionalGrainTarget<'Actor>      closed Orleans target interface
IFunctionalDispatchTarget           non-generic dispatch seam
FunctionalGrainReference            custom GrainReference
FunctionalRequest                   Request<FunctionalReply>
FunctionalRequestEnvelope           fixed request data
FunctionalReply                     fixed reply data
```

`IFunctionalGrainTarget<'Actor>` inherits `IGrain`. Both target interfaces expose
the same CLR method shape:

```csharp
ValueTask<FunctionalReply> DispatchAsync(
    FunctionalRequestEnvelope envelope,
    CancellationToken cancellationToken);
```

`FunctionalRequest` calls `IFunctionalDispatchTarget.DispatchAsync` from
`InvokeInner`. One request class carries every operation. The generic target
interface supplies the actor-specific Orleans method metadata; the non-generic
interface is the actual invocation seam.

`FunctionalGrainReference` derives from `GrainReference`. It owns the protected
Orleans send methods and an injected exact-type payload codec. Acknowledged calls
use `InvokeAsync<FunctionalReply>` and one-way calls use protected `Invoke`.
Contract descriptors remain in the upper `FunctionalGrainRef`; the lower
reference receives a complete fixed request.

### Request and reply layout

Numeric field IDs are the stable wire identity:

| Type | Field ID | Field | Wire value |
|---|---:|---|---|
| `FunctionalRequest` | 0 | `envelope` | one non-null `FunctionalRequestEnvelope` |
| `FunctionalRequestEnvelope` | 0 | `grainType` | non-empty NUL-free string |
|  | 1 | `contractVersion` | positive signed 32-bit integer |
|  | 2 | `operationId` | non-empty NUL-free ordinal string |
|  | 3 | `protocolToken` | exactly 32 bytes |
|  | 4 | `admissionFlags` | one byte |
|  | 5 | `payload` | non-null byte array |
| `FunctionalReply` | 0 | `protocolToken` | exactly 32 bytes |
|  | 1 | `payload` | non-null byte array |

`admissionFlags` uses this layout:

| Bit | Hex | Meaning |
|---:|---:|---|
| 0 | `0x01` | read-only |
| 1 | `0x02` | one-way |
| 2 | `0x04` | always-interleave |
| 3–7 | `0xF8` | reserved; a set reserved bit invalidates the request |

`FunctionalRequest` therefore has one serialized top-level field. Its
nonserialized method metadata, request options, target, and cancellation state
are reconstructed by the request lifecycle. The fixed codecs require every
listed field exactly once with its exact wire type.
Payload arrays are immutable after construction. Explicit serializers, copiers,
and activators cover the fixed request/reply types and preserve dynamic options,
caller cancellation state, and caller-side method metadata across local copies.
Target objects and target-local cancellation resources are reset on a copy and
remain absent from wire data.

Arguments and replies cross the transport boundary as fresh byte arrays produced
by the Orleans serializer for the descriptor's exact CLR type. This gives local
and remote calls the same object-graph isolation. Each top-level serialization
or deserialization rents or creates a fresh/reset `SerializerSession` and
returns it in `finally`; sessions are never shared by concurrent calls.

The serialization graph for a call consists of the fixed request/reply types and
exact typed payload bytes. Durable storage serializes the application state
value of each attached facet. Contracts, API facades, selectors, reflection
metadata, `PersistentStateRef` values, invocation facades, services, references,
targets, and cancellation resources stay process-local.

### Protocol token

`protocolToken` is the raw SHA-256 digest of the UTF-8 sequence:

```text
grainType NUL version NUL operationId NUL direction
```

`version` is invariant ASCII decimal without sign or leading zero. Direction is
the lowercase literal `request` or `reply`. The digest detects descriptor
misrouting; argument/reply compatibility is governed by the exact registered
types and contract version.

Golden vectors, rendered as lowercase hexadecimal:

| Input | SHA-256 |
|---|---|
| `chat.room NUL 1 NUL join NUL request` | `525f112d5114016be421e973fee8aa7e4b439b560f29b419fd374e48336c430e` |
| `chat.room NUL 1 NUL join NUL reply` | `2a2e7b5513cb992ef81759d0e761ef0071ec634be2d8d3b0931f961641ad61bf` |

### Payload limits and serializer registration

`FunctionalGrainTransportOptions.MaxPayloadBytes` defaults to 16 MiB and must be
positive. Enforce it at four boundaries: caller request send, silo request
receive, silo reply send, and caller reply receive. Each endpoint uses its local
configuration; Orleans' general message-size limit can be stricter. Diagnostics
include grain type, operation ID, direction, actual size, and local limit, and
exclude payload contents.

Client and silo registration add `FSharpBinaryCodec` as `IGeneralizedCodec`
together with its type filter through
`FSharpBinaryCodecRegistration.addCodecToSerializerBuilder`. Functional
registration uses the explicit byte boundary for copy isolation. The existing
`addToSerializerBuilder` entry point remains behavior-compatible, and shared
idempotence detection prevents duplicate codec registration when both entry
points are used.

Serializer preflight resolves
`Orleans.Serialization.Serializers.ICodecProvider` and calls its public
`GetCodec(System.Type)` method. Success means that method returns a codec for the
declared type; `CodecNotFoundException` or any resolution failure becomes a
configuration/binding diagnostic. Preflight uses the type itself rather than a
trial serialization of `null` or a default value. Both Orleans 10.1.0 and 10.2.2
expose this interface method.

Silo startup performs that check for every hosted argument, reply, and durable
state type. An external client validates fixed transport types at startup and
validates concrete argument/reply types while binding a contract, before
returning the API record. Validation success is cached per contract shape and
serializer-service instance. Runtime serialization still validates the concrete
value when a declared argument or reply type is abstract or an interface.

### Dispatch validation order

The target handles a request in this order:

1. Validate fixed envelope shape, grain type, contract version, payload size,
   token length, and reserved flags.
2. Resolve the immutable descriptor by ordinal operation ID.
3. Compare the exact protocol token and admission flags with the descriptor.
4. Deserialize payload bytes into the descriptor's exact argument type using a
   fresh session.
5. Create the per-invocation context and call the preclosed typed handler adapter.
6. Apply the operation's state-publication rule.
7. Serialize the exact reply type, enforce the silo reply limit, and return the
   descriptor's reply token and fresh payload.

The client validates reply shape, reply token, and its reply-size limit before
deserializing the exact reply type. Protocol validation precedes user-payload
deserialization and handler execution.

### Orleans request options and cancellation

`FunctionalRequest` restores `RequestBase.Options` from validated admission flags
before send. Orleans `MessageFactory` therefore receives `ReadOnly`, `OneWay`,
and `AlwaysInterleave` through its normal request path. The same flags are in the
envelope and are checked against the server descriptor.

The request implements Orleans cancellable-invokable behavior:

- caller-side state holds the supplied process-local `CancellationToken`;
- `GetCancellationToken` returns that token;
- acknowledged calls report `IsCancellable = true`;
- `SetTarget` resolves the dispatch target and creates target-local cancellation
  state;
- `TryCancel` cancels the target-local source; and
- `Dispose` releases request-owned cancellation resources.

`FunctionalGrainContext.cancellationToken` receives the target-local token.
Cancellation is cooperative and does not roll back state or external effects.
Normal bound-record calls use `CancellationToken.None`.

For a one-way `callCancellable`, an already-cancelled token returns
`Task.FromCanceled<unit>`. Otherwise the request uses the void send path and
returns a completed `Task<unit>`; later token cancellation has no remote effect.
The delivered one-way context uses `CancellationToken.None`.

### Call-filter metadata

On the caller, `FunctionalRequest` stores nonserialized closed target-interface
`Type` and `MethodInfo` metadata. `SetTarget` restores those values from the
actual target after deserialization. `GetInterfaceType`, `GetMethod`, interface
name, method name, and activity name are stable and non-null on both sides.

The request exposes two logical CLR arguments matching `DispatchAsync`:

- argument `0`: `FunctionalRequestEnvelope`;
- argument `1`: the current `CancellationToken`.

`GetArgumentCount()` returns `2`. `GetArgument(0)` returns the envelope and
`GetArgument(1)` returns the current token. `SetArgument(0, value)` accepts only
the exact internal envelope type. `SetArgument(1, value)` accepts only
`CancellationToken` and replaces the current token. Every other index raises
`ArgumentOutOfRangeException`.

On the target, `SetTarget` stores the non-generic dispatch target, resolves the
single closed functional target interface and its method metadata, creates the
target-local cancellation source, and replaces argument `1` with that
target-local token. Target dispatch revalidates all envelope metadata.

The envelope implements public read-only `IFunctionalRequestMetadata`, allowing
application filters to inspect grain type, version, operation, policy flags, and
payload length. Filters can log, trace, use `RequestContext`, or reject the call.
The successful reply type remains an internal transport value.

## Orleans hosting integration

### Project ownership

| Concern | Project |
|---|---|
| Fixed target interface, request/reply/reference types, transport options, metadata interface, and explicit transport serialization | `src/Orleans.FSharp.Abstractions` |
| API reflection, selectors, contracts, definitions, bound records, typed adapters, payload codec, builders, and client registration | `src/Orleans.FSharp` |
| Marker, frozen registry, manifest providers, activator, actual target, persistence, reminders, timers, and silo registration | `src/Orleans.FSharp.Runtime` |
| Unit and compile tests | `tests/Orleans.FSharp.Tests` |
| Cluster, multi-silo, heterogeneous-hosting, and restart tests | `tests/Orleans.FSharp.Integration` |

Project references are Runtime → `Orleans.FSharp` → Abstractions. Abstractions
contains only fixed transport types and primitives; API shape, contracts,
operation descriptors, and server definitions belong to the upper layers. Use
`InternalsVisibleTo` for the upper projects and tests while keeping envelopes,
requests, references, and serializer-session internals outside the application
surface.

### Builder registration

`AddFunctionalGrainClient` is an idempotent `IClientBuilder` extension. It
registers:

- the custom reference activator provider;
- fixed request/reply serializers, copiers, and activators;
- exact-type payload codec services;
- transport options with `ValidateOnStart`; and
- the F# generalized codec and type filter.

`AddFunctionalGrain` is an `ISiloBuilder` extension implemented through
`ISiloBuilder.ConfigureServices`. It first invokes the same internal idempotent
client-service registration routine, then registers the hosted definition,
registry, manifest providers, activator, persistence, reminder, timer, and silo
validation services. Repeated registration is idempotent except that conflicting
contracts and definitions are configuration errors.

Both paths call `AddOptions<FunctionalGrainTransportOptions>()`, require
`MaxPayloadBytes > 0`, and use `ValidateOnStart`. Silo registration uses
`TryAddSingleton<TimeProvider>(TimeProvider.System)` so an application-provided
clock remains authoritative.

### Custom reference selection

The functional interface ID is:

```text
orleans.fsharp.functional/<grainType>
```

The prefix provider accepts only a non-empty, NUL-free suffix which exactly
equals the supplied `GrainId.Type`. It creates `FunctionalGrainReference` with
fixed internal interface version `1`. On success it constructs
`GrainReferenceShared` with that version and passes it, together with the
injected exact-type payload codec, to `FunctionalGrainReference`. Other IDs are
declined.

Registration inserts the singleton functional
`IGrainReferenceActivatorProvider` descriptor immediately before the first
existing provider descriptor. Orleans installs its default providers before the
`IClientBuilder`/`ISiloBuilder` extension runs; absence of an existing provider
is a registration error. Microsoft DI enumeration order and Orleans first-match
selection make the reserved functional IDs choose this provider on Orleans
10.1.0 and 10.2.2.

Reference binding constructs `GrainId` and `GrainInterfaceType` explicitly and
calls `IGrainFactory.GetGrain(grainId, grainInterfaceType)`. External-client
configuration consists of the fixed transport services; the application-supplied
contract value provides the binding metadata.

### Silo registry and manifest

The singleton definition registry enforces:

- each actor-brand CLR type maps to exactly one registered contract;
- each explicit grain type maps to exactly one registered contract and hosted
  definition;
- repeated registration of the same definition value is idempotent;
- a different definition with an existing actor brand or grain type is a
  configuration error; and
- agreement among contract, definition, marker, interface, and activator.

For each definition, close `FunctionalGrainMarker<'Actor>` and
`IFunctionalGrainTarget<'Actor>` over its actor brand. A registry-backed
`IGrainTypeProvider` maps the marker to the explicit grain type. A registry-backed
`IGrainInterfaceTypeProvider` maps the interface to the stable functional
interface ID. `IGrainInterfacePropertiesProvider` publishes fixed Orleans
interface version `1` and the default grain type.

Register these enumerable services once:

```csharp
ServiceDescriptor.Singleton<IGrainPropertiesProvider,
    FunctionalGrainPropertiesProvider>()

ServiceDescriptor.Singleton<IPostConfigureOptions<GrainTypeOptions>,
    FunctionalGrainTypeOptionsPostConfigure>()
```

`FunctionalGrainTypeOptionsPostConfigure.PostConfigure` acts on
`Options.DefaultName`. It atomically freezes the registry into one immutable
snapshot, removes the open marker/interface definitions discovered by default,
and adds only the registered closed types to `GrainTypeOptions.Classes` and
`.Interfaces`. All type providers, property providers, and activators read the
frozen snapshot; a later registration fails.

`ISiloBuilder.ConfigureServices` runs after Orleans default service
registration. `TryAddEnumerable` appends `FunctionalGrainPropertiesProvider`
after Orleans' `ImplementedInterfaceProvider`. The functional provider examines
only implemented-interface property keys. It finds the single value normalized
by Orleans from the closed functional interface to the open generic interface
ID, replaces that value in the same property key with the registered closed
interface ID, and preserves `IRemindable` and other interface properties. Zero or
multiple matching normalized entries fail silo startup.

The final manifest contains the closed marker and closed actor-specific target
interface for every definition hosted by that silo. Heterogeneous silos publish
only their own definitions.

### Marker and activation target

`FunctionalGrainMarker<'Actor>` is a concrete `Grain` implementing the closed
target interface and `IRemindable`. It has a public parameterless constructor so
Orleans can construct its default activator during component configuration. Its
methods throw an internal configuration error if that marker instance receives a
call.

An `IConfigureGrainTypeComponents` implementation recognizes registered
functional grain types and installs the functional `IGrainActivator`. It leaves
other grain types unchanged and produces the same final activator regardless of
whether Orleans' default configurator runs before or after it.

The functional activator returns an F# object expression derived from:

```fsharp
type internal FunctionalGrainTargetBase
    (grainContext: IGrainContext, grainRuntime: IGrainRuntime) =
    inherit Grain(grainContext, grainRuntime)
```

It implements the exact closed `IFunctionalGrainTarget<'Actor>`,
`IFunctionalDispatchTarget`, and `IRemindable`. The activator resolves
`IGrainRuntime` from `grainContext.ActivationServices`, passes the supplied
context and runtime to the protected `Grain` constructor, and verifies
`Object.ReferenceEquals((target :> IGrainBase).GrainContext, grainContext)`
before returning the target.

`FunctionalGrainTargetBase` supplies narrow internal wrappers for protected
`DeactivateOnIdle` and `DelayDeactivation`. The Orleans-supplied `IGrainContext`
remains the activation context and owns scheduling, lifecycle, collection,
placement, activation services, and disposal.

All functional markers and targets implement `IRemindable`, since the closed
generic marker has one fixed interface set. A definition without reminder hooks
registers no reminders. An unknown reminder name logs grain and reminder
identity and fails that callback.

## Persistence and activation lifecycle

`PersistentState.create<'State> stateName providerName` creates an immutable
logical descriptor. `providerName` selects an `IGrainStorage` registration
already configured on every silo which can host the definition; it is not a
connection string or storage type. Creation immediately rejects blank or
NUL-containing names. Definition sealing checks unique state-name attachment;
silo startup checks that every named provider is available.

`stateFrom descriptor` attaches one `PersistentStateRef<'State>` as the source
and holder of the handler's primary state. `usePersistentState descriptor
initializer` attaches an additional named state of any closed type and is
repeatable. The primary descriptor is also available through
`context.persistentState` and must not be repeated with
`usePersistentState`.

For example, one complete definition can use `stateFrom postgres` for its
primary state and `usePersistentState redis (fun _ -> emptyRoom ())` for a
second provider:

```fsharp
let postgres = PersistentState.create<RoomState> "primary" "Postgres"
let redis = PersistentState.create<RoomState> "replica" "Redis"

let emptyRoom () =
    { nextMessageId = 1L
      members = Set.empty
      messages = [] }

let joinHandler
    (context: FunctionalGrainContext<RoomActor, RoomId>)
    (state: RoomState)
    (userId: UserId) =
    task {
        let next =
            { state with
                members = Set.add userId state.members }

        let primary = context.persistentState postgres
        primary.State <- next
        do! primary.WriteStateAsync()

        let replica = context.persistentState redis
        replica.State <- next
        do! replica.WriteStateAsync()

        return next, ()
    }
```

Each descriptor has its own unique state name plus its own `State`, `Etag`,
`RecordExists`, and provider. State names are unique within a definition even
when providers differ, because Orleans activation-migration keys are based on
the state name. Writes across descriptors are not atomic: if the second call
fails, the first remains committed. An application which requires coherent
mirroring, failover, or repair registers one composite `IGrainStorage` with
those semantics and addresses it through one descriptor.

The custom activator synchronously creates every attached facet through the
closed generic `IPersistentStateFactory.Create<'StoredState>` before returning
the target. This lets every facet subscribe at
`GrainLifecycleStage.SetupState`. The primary `IPersistentState<'State>.State`
is the authoritative in-memory holder when `stateFrom` is present; an ephemeral
definition uses an activation-local holder.

Activation ordering is:

1. The activator creates persistent facets, resolves `IGrainRuntime`, constructs
   the target, and registers its custom lifecycle observers.
2. Orleans lifecycle SetupState loads durable state.
3. `IGrainBase.OnActivateAsync` initializes ephemeral state and every attached
   holder whose `RecordExists` is false.
4. The functional `onActivate` hook runs.
5. Declared reminders are reconciled in declaration order.
6. Declared timers are created.
7. Activation completes and ordinary turns are admitted.

Deactivation invokes the functional hook before lifecycle `OnStop`, disposes
activation-local timers, and finally reaches `IGrainActivator.DisposeInstance`.
It performs no implicit storage write. An `onDeactivate` hook may explicitly
write, but process or silo failure cannot guarantee that hook runs. Hook and
storage exceptions receive no library retry. The task failure reaches the
Orleans stop lifecycle, which observes and logs it while continuing remaining
stop stages; activation-local cleanup runs in `finally`.

State initialization rules are:

- ephemeral definitions invoke the state factory once per activation;
- a primary holder with `RecordExists = true` supplies the loaded primary state;
- a primary holder with `RecordExists = false` receives the primary initializer
  result without writing it;
- an additional holder with `RecordExists = false` receives its declared
  initializer result without writing it;
- an `onActivate` replacement is published in memory and is not written unless
  the hook explicitly calls `WriteStateAsync`; and
- storage read, initializer, or hook failure fails activation.

If no record is ever written, a later activation runs the corresponding
initializer again. Orleans performs the normal SetupState read for each attached
facet before activation. After that load, reloads, writes, and clears occur only
when application callbacks explicitly call `ReadStateAsync`, `WriteStateAsync`,
or `ClearStateAsync`. No handler return, activation hook, deactivation, or
collection event adds a hidden write or clear.

`collectionAge age` sets the normal Orleans idle-age threshold for the in-memory
activation. Once the activation has received no activity for at least `age`, a
periodic collection scan may deactivate it and release its memory; this is an
eligibility threshold, not an exact timer. A later call creates another
activation. Durable state is loaded again, while ephemeral state is initialized
again. Incoming calls, reminders, and stream events count as activity; outgoing
calls, timers configured with `KeepAlive = false`, and arbitrary I/O do not.
An active timer configured with `KeepAlive = true` extends the activation's
lifetime according to stock Orleans timer semantics.

When `collectionAge` is absent, the definition publishes no per-grain override
and the host's stock Orleans collection policy applies. Any primary or
additional state change which the application did not explicitly write is lost
when the activation ends. Reactivation loads the last successfully written
record, or runs the initializer again when no record exists.

`collectionAge` is not a data TTL, does not delete storage, and does not select
when state is written. Explicit deactivation, shutdown, silo failure, and memory
pressure remain separate lifecycle events.

Collection age is frozen into manifest properties. Storage providers, lifecycle,
ETags, reminder registrations, timer registry, activation collection, and
failure propagation remain Orleans services.

## Validation, failures, and observability

Validation occurs at the earliest stage with enough information:

| Stage | Validates |
|---|---|
| Persistent descriptor construction | Non-blank, NUL-free state and provider names plus a closed stored type. |
| Contract construction | API shape, grain type, version, key codec, selectors, IDs, policies. |
| Definition sealing | Initializer, complete handler coverage, persistent attachment identity/types, hook/timer/reminder names and values. |
| Silo startup | Registry uniqueness, serializer and storage availability, manifest consistency, marker/interface/provider/activator agreement. |
| Reference binding | Key encoding, custom reference type, concrete client argument/reply serializers. |
| Dispatch | Envelope shape, version, operation, token, flags, payload limit, typed deserialization. |

Diagnostics identify the stage plus grain type, API field or operation ID, and
the relevant expected/actual value. Payload-size errors include direction and
limit. Version errors include expected and received versions. Logs and activities
contain grain type, operation ID, version, grain ID, and outcome; payload bytes
and deserialized application values are excluded by default.

Persistent descriptor, attachment, lookup, and provider errors include
`stateName`, `providerName`, and the stored CLR type. They never log the stored
state value.

Protocol validation errors are distinguishable from application handler and
storage exceptions. A successful acknowledged mutating call means handler
completion, including any storage tasks which that handler explicitly awaited.
It does not imply that the returned state was persisted, nor exactly-once
external effects. Timeout, retry, cancellation, or failure after a commit can
leave the caller uncertain whether execution occurred. One-way has no target
acknowledgement, and cancellation has no rollback semantics.

Orleans remains the trust boundary for pre-admission scheduling flags. Target
dispatch compares serialized flags with the hosted descriptor and rejects a
mismatch before payload deserialization.

## Implementation plan

Each phase is merged only after its exit tests pass on the versions named for
that phase. Temporary proof code from Phase 0 is either promoted into production
tests or removed before the feature merges.

### Phase 0: Orleans seam proof

Build one minimal record contract with two operations and prove on Orleans
10.1.0 and 10.2.2:

1. stable IDs for closed marker and interface types;
2. final-manifest removal of open functional marker/interface entries;
3. replacement of the normalized open-interface property with the closed ID;
4. exact-ID `GetGrain` selection of `FunctionalGrainReference` using only
   `IGrainFactory`;
5. invocation of a custom-activated target whose CLR type differs from the
   marker and whose `IGrainBase.GrainContext` is the supplied context;
6. valid global-filter interface and implementation method metadata;
7. stock `ReadOnly`, `OneWay`, and `AlwaysInterleave` scheduling behavior;
8. cross-silo target cancellation through the fixed request;
9. creation of every attached persistent facet early enough for SetupState load;
10. `ICodecProvider.GetCodec(Type)` preflight on both supported versions without
    trial serialization; and
11. isolation of equal keys under different grain types plus heterogeneous silo
    manifests.

These proofs are architecture gates. A failed seam is reported before later
runtime layers are implemented.

### Phase 1: public types and contracts

- Add the normative public signatures and computation-expression builders.
- Implement API-shape caching, sentinel resolution, operation descriptors,
  policy validation, and all five native plus five mapped key operations.
- Add compile fixtures and shape/contract unit tests.

**Exit:** the public example compiles and the compile, shape, selector, ID, and
key test groups pass.

### Phase 2: bound records and fixed serialization

- Implement `FunctionalGrainRef`, record binding, preclosed thunks, exact-type
  payload serialization, protocol tokens, and fixed envelope codecs.
- Exercise the path first with an in-memory request sender.
- Measure ordinary calls to verify that reflection and selector work remains in
  construction/binding.

**Exit:** binding, byte isolation, wire golden vectors, limits, and hot-path
tests pass.

### Phase 3: Orleans reference, manifest, and activation

- Implement fixed request/reply/reference types and client registration.
- Implement the frozen silo registry and Orleans manifest providers.
- Implement custom target activation and dispatch.
- Add two-silo and heterogeneous-hosting fixtures.

**Exit:** all Phase-0 proofs pass against production code on both Orleans
versions, including filters, policy scheduling, and cancellation.

### Phase 4: state and lifecycle

- Implement definition sealing and typed handler adapters.
- Implement ephemeral publication, `stateFrom`, repeatable
  `usePersistentState`, and invocation-bound typed state lookup.
- Create every attached persistent facet in the activator and wire
  activation/deactivation ordering without implicit writes.
- Add real deactivation, restart, and cross-silo recovery tests.

**Exit:** explicit read/write/clear, multi-provider, activation ordering, ETag,
and durable recovery tests pass with instrumentation proving that the runtime
adds no write or clear and no reload beyond stock SetupState reads.

### Phase 5: collection, reminders, timers, and context

- Publish collection age in manifest properties.
- Implement declarative reminder reconciliation and timer lifecycle.
- Complete `TimeProvider`, `RequestContext`, tracing, and deactivation wrappers.

**Exit:** the full example and every capability test pass in a real multi-silo
fixture.

### Phase 6: sample, documentation, and compatibility

- Add one runnable end-to-end sample based on the public example.
- Document key-codec identity, operation rename/version rules, delivery
  semantics, immutable-state guidance, and reminder migrations.
- Run the complete pre-existing test suite.

**Exit:** sample, documentation, new functional matrix, and compatibility suites
all pass.

## Required tests

The functional integration suite runs with exact package overrides:

```text
dotnet test -p:OrleansVersion=10.1.0
dotnet test -p:OrleansVersion=10.2.2
```

CI includes two-silo and heterogeneous-hosting fixtures plus a non-skipped Redis
durability job. The Redis job stops and recreates a silo process while retaining
Redis, then verifies that the same `GrainId` reloads committed state.

### Compile fixtures

- The complete contract, definition, registration, and client examples compile.
- Unannotated `RoomApi.contract`, `RoomApi.ref`, and `RoomApi.rawRef` infer their
  complete concrete types; reordered application-owned bindings do as well.
- `RoomApi.ref` has type `IGrainFactory -> RoomId -> RoomApi`, and a bound value
  infers `RoomApi` without annotation.
- All five native key operations compile without arguments only for their exact
  native key type; a mismatched native key fails to compile.
- All five mapped key operations preserve the domain key type; reversed or
  otherwise mismatched encoder/decoder types fail to compile.
- `oneWay (_.typing)` compiles, while selecting a field whose reply is not
  `Task<unit>` fails to compile.
- `stateFrom` accepts only `PersistentStateRef<'State>` for the definition's
  primary state; `usePersistentState` preserves each independent stored type,
  and `context.persistentState` returns the corresponding exact
  `IPersistentState<'StoredState>`.
- Wrong key, field argument, handler argument, and handler reply fail to compile.
- A selector from another API record fails to compile.

### Shape and selector tests

- Field declaration order and exact argument/reply types are preserved.
- `_.join` and `fun api -> api.join` resolve, including fields with identical
  function types.
- A selector result which is not a sentinel fails with the required diagnostic.
- A helper or branch returning an original sentinel documents the physical-
  identity limit and still executes exactly once during construction.
- Non-record, struct-record, open-generic, non-function, `Async`, `ValueTask`,
  plain `Task`, and curried shapes fail construction.
- Shape, constructor, probe, and closed-thunk caches are reused.

### Contract, identity, and manifest tests

- Default/overridden operation IDs, duplicate IDs/policies, an omitted grain
  type deriving the actor brand's CLR simple name (with a generic or nested
  brand rejected), positive version, and policy combinations behave as
  specified.
- Native string, Guid, int64, and compound operations produce the same `GrainId`
  representations as the corresponding stock Orleans key helpers.
- Mapped key operations pass domain/native round-trip, canonicalization,
  injectivity, and malformed-input tests; compound native keys are exactly
  `Guid * string` or `int64 * string`.
- Missing or repeated native/mapped key operations fail contract construction.
- Explicit grain/interface IDs survive CLR/module renames, while a derived
  grain type moves with an actor-brand rename; a changed grain type or key
  codec changes `GrainId`.
- A definition that attaches `stateFrom`, `usePersistentState`, or declares
  `onReminder` fails sealing and fails silo registration when its contract's
  grain type is derived rather than explicit; two actor brands that derive the
  same grain type from a colliding CLR simple name fail registration under the
  existing grain-type-uniqueness rule.
- Equal keys under different grain types select distinct activations.
- Different definitions with the same actor brand fail registration; different
  contracts or definitions with the same explicit grain type also fail.
- Registry mutation after freeze fails.
- Final manifests contain the exact closed functional marker/interface entries,
  fixed interface version, correct default-grain properties, and only
  definitions hosted by each silo.
- External-client manifests remain independent of application contract values.
- Component-configurator ordering produces the same final custom activator.
- The functional reference provider precedes stock providers, selects only its
  exact prefix/suffix, and returns `FunctionalGrainReference` on both Orleans
  versions.

### Binding, request, and transport tests

- Exact-ID `IGrainFactory` binding works from an external client without an
  application definition registry.
- Binding validates and caches concrete argument/reply serializer availability.
- Every bound field sends the expected operation, version, token, and flags;
  `.api` returns the cached record instance.
- Bound calls perform no shape reflection, selector evaluation, or generic
  closing.
- Fixed-codec golden vectors cover field IDs, required fields, types, tokens,
  every valid flag combination, and reserved flags.
- The local copier preserves the envelope, options, caller token, and caller
  method metadata while clearing the target and target-local cancellation state.
- `GetArgumentCount`, `GetArgument`, and `SetArgument` are verified for indices
  `0`, `1`, wrong value types, and invalid indices.
- Local and remote requests/replies use the same byte boundary and exact CLR
  payload types.
- Unknown operation, bad version/token/flags, corrupt payload, and all four
  oversized-payload boundaries fail before handler execution.
- An application handler exception follows Orleans' response-exception path.
- One-way completion/failure behavior and acknowledged/one-way cancellation
  semantics match this specification.
- Global filters receive valid method and functional metadata, can reject calls,
  and observe `RequestContext`.
- Concurrent calls never share a serializer session.
- Functional serializer registration alone contributes one codec, one type
  filter, and no generalized copier. Enabling the compatibility registration
  entry point as well preserves its service set while keeping a single codec
  registration.
- F# records, unions, options, and lists containing `byte[]`, ref cells, and
  mutable objects have equivalent local/remote isolation.
- Serializer instrumentation proves that API facades, contracts, definitions,
  selectors, persistent-state descriptors/facades, reflection metadata, and
  services enter neither request bytes nor durable storage.

### Activation, state, and lifecycle tests

- Custom target and marker are distinct; the target receives the exact supplied
  context/runtime and is disposed once.
- Deactivation wrappers, default/custom `TimeProvider`, and fresh invocation
  contexts exhibit stock Orleans behavior.
- `onActivate` observes state after durable loading.
- `onDeactivate`, lifecycle `OnStop`, timer disposal, and `DisposeInstance`
  execute once in the specified order.
- A failing deactivation hook or explicit storage call is observed and logged
  without library retry; activation-local cleanup and remaining Orleans stop
  stages still run.
- Handler coverage, duplicate hooks, and invalid reminder/timer configuration
  fail before the first call.
- Persistent descriptors validate names, providers, stored types, duplicate
  logical identities, and attachment before the first call.
- The same `stateName` attached with a different provider still fails sealing,
  preserving unique Orleans activation-migration keys.
- `stateFrom` uses the selected holder as primary state; repeatable
  `usePersistentState` loads independently typed holders from independently
  named providers before `onActivate`.
- Missing attached state is initialized but not written; existing state is
  loaded by the expected SetupState read without an activation write.
- Handler, timer, reminder, activate, and deactivate returns never write
  storage. Instrumented providers observe the expected automatic SetupState
  reads; after activation they observe only application-issued reads, writes,
  and clears, with no runtime-issued write or clear.
- A successful handler return publishes its primary replacement in memory. An
  explicit setter or storage effect remains when a later handler step fails;
  the runtime performs no rollback, retry, reload, or ETag repair.
- A handler which enters with `A`, explicitly writes snapshot `X`, and returns
  `Y` leaves `X` in storage and `Y` as the next call's primary state. Returning
  `Y` without a setter/write performs zero writes.
- Explicit reads replace only the selected holder, explicit clears follow the
  provider's state-buffer semantics, and an unattached or expired invocation
  facade fails deterministically.
- Two providers can hold independent states or receive explicitly ordered
  writes; partial success is observable and no cross-provider transaction is
  claimed.
- Read-only and state-neutral interleaved calls discard replacement state and
  reject the setter plus parameterless and cancellation-token overloads of
  read/write/clear through their state facades. Expired facades reject the same
  complete surface.
- State which the application explicitly writes survives deactivation, full
  silo restart with retained Redis, and activation on another silo.
- An omitted collection age uses the host default; an override produces stock
  collection eligibility and reactivation behavior. Unwritten in-memory changes
  disappear on deactivation, while the last explicit write reloads.
- Timers with `KeepAlive = false` do not extend collection lifetime; timers with
  `KeepAlive = true` do so according to stock Orleans behavior.
- Heterogeneous routing invokes a definition only on a silo which advertises it,
  including silo join and leave.
- Scheduling behavior is demonstrated under concurrency rather than by flags
  alone.
- Reminders reconcile before activation completion and survive reactivation;
  timers are recreated and disposed with the activation.
- Unknown reminder names fail explicitly; reminder rename/removal uses the
  documented unregister migration.
- Whole-state timers reject `Interleave = true`, and concurrent contexts never
  leak cancellation or `RequestContext` values.
- Tracing includes stable grain type, operation ID, version, and outcome.

## Functional observers (extension, landed post-Phase-6)

This section is an EXTENSION, added after the phase plan above was completed.
The phase plan is left as it was executed; nothing in it changes.

### Why observers needed the same move as grains

Orleans' proxy generators are Roslyn source generators and never run over F#.
An `IGrainObserver` declared in F# therefore has no generated proxy, and
`IGrainFactory.CreateObjectReference` fails on it — which is why push
notification was the one capability an F# application could not reach without
adding a C# project of its own. The constraint is Orleans', identical for the
`grain { }` CE and for class grains, but it is a constraint the functional
runtime can absorb: exactly as ONE fixed request carries every grain
operation, ONE C#-declared observer interface in `Orleans.FSharp.Abstractions`
carries every application observer, of every brand. Above that interface an
observer is an ordinary F# record.

### Surface

```fsharp
type RoomObserver = private RoomObserver of unit      // observer brand

type RoomObserverApi =                                 // handler record
    { onMessage: ChatMessage -> Task<unit> }           // push ops: 'Msg -> Task<unit>

let observerContract =
    observerContract<RoomObserver, RoomObserverApi> () {
        observerType "chat.room.observer"              // defaults to the brand's simple name
        version 1                                      // defaults to 1
    }

// client: wrap handlers, get a serializable typed handle
let handle =
    FunctionalObserver.create observerContract client { onMessage = fun m -> task { … } }

// the handle is an ordinary operation argument
do! room.subscribe handle

// grain side: typed push through the handle
do! FunctionalObserver.notify handle (_.onMessage) message

// or resolve once and reuse the preclosed push function, the hot-path form
let push = FunctionalObserver.notifier handle (_.onMessage)
do! push message

// or fan out with a liveness window
let observers = FunctionalObserverManager<RoomObserver, RoomObserverApi>(TimeSpan.FromMinutes 5.0)
observers.Subscribe handle
do! observers.Notify (_.onMessage) message

// release
FunctionalObserver.unsubscribe client handle
```

Observer sends follow the same hot-path rule as a bound grain call: `notifier`
resolves its selector once and returns a preclosed `'Msg -> Task<unit>`
closure, so a notifier-based push invokes no selector and closes no generic
however many times it runs, while `notify` is the convenience form that
resolves on every call and `FunctionalObserverManager.Notify` resolves once
per call across its whole subscriber set rather than once per subscriber.

A handler record IS an API record whose replies are all `unit`: shape
reflection, selector resolution by sentinel identity, and every diagnostic are
the grain rules unchanged. The one observer-specific rule is that a push
operation returning anything but `Task<unit>` fails contract construction —
an observer never returns data.

A push operation's wire ID is always its handler-record field name. There is
no `operationId` override, and deliberately: the notifying side derives the ID
from the same record type the observing side does, so there is no pair of
declarations that can drift apart.

`observerContract` has no key operation. An observer is addressed by the
object reference inside its handle, not by a domain key.

### Wire

The notification envelope mirrors `FunctionalRequestEnvelope` field for field,
with the observer type standing in for the grain type and no admission flags —
an observer has no scheduling policy to carry:

| Field | Type | Meaning |
|---|---|---|
| 0 | string | observer type |
| 1 | int32 | contract version |
| 2 | string | operation ID |
| 3 | byte[32] | notify-direction protocol token |
| 4 | byte[] | serialized message payload |

The protocol token is computed exactly as a grain token is — the SHA-256
digest of `observerType NUL version NUL operationId NUL direction` — with a
THIRD direction literal, `notify`, beside `request` and `reply`.

A notify token cannot collide with a grain-operation token even when an
observer type and a grain type share a name, a version, and an operation ID.
The direction is part of the hashed preimage, so the three preimages differ in
their final NUL-separated field and hash to different digests; a collision
would require a SHA-256 preimage collision rather than a naming coincidence.
What the token detects is what it detects for grains: a notification routed to
a descriptor other than the one it was built for.

The typed handle's own wire form is exactly three fields — observer type,
contract version, and the Orleans object reference, written by Orleans' own
reference codec. Its two type parameters are phantom: they keep an observer of
one brand from being handed to a grain expecting another, and neither appears
on the wire.

Both the envelope and the handle are published by an assembly-level
`TypeManifestProvider`, not by the client registration alone. The observer
interface is non-generic, so Orleans' startup validator closes and scans it in
every process that merely loads `Orleans.FSharp.Abstractions` — including one
that hosts no functional grain — and would otherwise refuse to start.

### Delivery semantics

Delivery is best-effort, which is Orleans' own observer semantics.
`DispatchAsync` is `[OneWay]`: `notify` completes when the notification has
entered the local send path, not when the observed object has handled it.

That is load-bearing rather than an optimisation. Under an acknowledged
dispatch, a single subscriber whose object reference has been released blocks
the notifying grain's handler until Orleans times the message out — thirty
seconds, for a subscriber the application has already forgotten. Under one-way
dispatch a dead reference costs the notifying handler nothing.

An observer whose handler throws is reported to the logger of the process
HOSTING the observer and never to the notifying grain. A protocol fault —
wrong observer type, version, operation, or token — does propagate on the
observing side, because it means the two ends disagree about the contract
rather than that one message failed.

### Lifetime

Orleans' object-reference table holds an observed object WEAKLY, so nothing in
Orleans keeps a client-hosted observer alive. The handle therefore anchors it:
keep the handle, keep receiving. `FunctionalObserver.unsubscribe` releases the
object reference (`DeleteObjectReference`) and is idempotent — releasing a
reference that is already gone is the normal outcome of a second release, of a
torn-down client, and of an object collected before the application got round
to unsubscribing, and none of those is an error in a cleanup path.

`FunctionalObserverManager<'Brand,'Api>` expires a subscription that has not
been refreshed within its window, on the next notification or the next
explicit sweep. It is a MUTABLE object held in handler state, and it carries
the same caveat this specification states for deep mutation elsewhere: state
is published by returning it from a handler, so a mutation performed in place
is visible immediately and is not part of any state write. A manager must
never be attached to a persistent state type.

### Where a handle may appear

A handle may be an operation's argument, or an element of a tuple argument:
Orleans owns both shapes and routes each element to its own codec. It may NOT
be a field of an F# record, option, list, or union argument — the F# binary
codec owns those payloads whole and has no codec for an Orleans object
reference, and refuses them.

The same refusal is what keeps a live object reference out of durable storage:
a functional grain's state crosses the F# binary codec, so a state type
carrying a handle cannot be written at all, rather than being written as
something that looks restorable and is not.

### Registration

`AddFunctionalGrainClient` (and therefore `AddFunctionalGrain`) registers the
observer transport alongside the grain transport, idempotently. There is no
separate observer entry point: a process that can call a functional grain can
also be pushed to by one.

## Completion criteria

The proposal is complete when every implementation-phase exit condition and
required test group passes at both Orleans versions, the Redis restart job is
green, and the runnable sample uses the public surface shown in this document.

All pre-existing public APIs remain source- and behavior-compatible, and their
test suites continue to pass.

## Repository map

- `src/Orleans.FSharp.Abstractions` — fixed transport and metadata types.
- `src/Orleans.FSharp` — public contract/definition/binding API and client
  registration.
- `src/Orleans.FSharp.Runtime` — silo manifest, activation, persistence, timer,
  and reminder implementation.
- `src/Orleans.FSharp/FSharpBinaryCodec.fs` — shared F# codec registration.
- `tests/Orleans.FSharp.Tests` — compile and unit fixtures.
- `tests/Orleans.FSharp.Integration` — real-cluster fixtures.
- `Directory.Packages.props` — `OrleansVersion` package override.
- `.github/workflows/ci.yml` — version matrix and Redis restart job.

The implementation uses the public Orleans 10.1/10.2 extension points named in
this document and verifies each version-specific seam in Phase 0 before the
dependent runtime layer is built.
