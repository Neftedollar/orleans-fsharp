# Feature Proposal 003: Functional Grain Runtime with User-Authored API Records

**Status:** implementation proposal

**Target:** .NET 10, F# 10, Orleans 10.1.0 minimum; Orleans 10.2.2 must also pass

**Scope:** contracted functional grains with ordinary `IPersistentState`
persistence

## Objective

Implement a functional-grain runtime in which an application contract consists
of:

1. a phantom actor type;
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
    let contract : GrainContract<RoomActor, RoomId, RoomApi> =
        grainContract<RoomActor, RoomId, RoomApi>() {
            grainType "chat.room"
            version 1
            stringKey RoomId.value RoomId.create

            readOnly (_.history)
            oneWay (_.typing)
            alwaysInterleave (_.typing)
        }

    let ref : IGrainFactory -> RoomId -> RoomApi =
        FunctionalGrain.ref contract

    let rawRef :
        IGrainFactory ->
        RoomId ->
        FunctionalGrainRef<RoomActor, RoomId, RoomApi> =
        FunctionalGrain.rawRef contract
```

`[<NoEquality; NoComparison>]` is recommended for records of functions, but is
not required by the runtime. Contract policies and handlers identify operations
with ordinary record-field projections such as `_.history` and `_.join`.

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

let roomDefinition =
    grainFor RoomApi.contract {
        defaultState (fun () ->
            { nextMessageId = 1L
              members = Set.empty
              messages = [] })

        persist "Default"
        collectionAge (TimeSpan.FromMinutes 30)

        handle (_.join) (fun _context state userId ->
            task {
                return
                    { state with
                        members = Set.add userId state.members },
                    ()
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

                    return
                        { state with
                            nextMessageId = id + 1L
                            messages = message :: state.messages },
                        Ok id
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

[<RequireQualifiedAccess>]
module FunctionalGrain =
    val ref :
        contract: GrainContract<'Actor, 'Key, 'Api> ->
        factory: IGrainFactory ->
        key: 'Key ->
        'Api

    val rawRef :
        contract: GrainContract<'Actor, 'Key, 'Api> ->
        factory: IGrainFactory ->
        key: 'Key ->
        FunctionalGrainRef<'Actor, 'Key, 'Api>

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
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> string) *
        decode: (string -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("guidKey")>]
    member GuidKey :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> Guid) *
        decode: (Guid -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("int64Key")>]
    member Int64Key :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> int64) *
        decode: (int64 -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("guidCompoundKey")>]
    member GuidCompoundKey :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        encode: ('Key -> Guid * string) *
        decode: (Guid -> string -> 'Key) ->
        GrainContractDraft<'Actor, 'Key, 'Api>

    [<CustomOperation("int64CompoundKey")>]
    member Int64CompoundKey :
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
    member OneWay<'Argument, 'Reply> :
        state: GrainContractDraft<'Actor, 'Key, 'Api> *
        selector: OperationSelector<'Api, 'Argument, 'Reply> ->
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

    [<CustomOperation("persist")>]
    member Persist<'State> :
        state: FunctionalGrainDefinitionDraft<'Actor, 'Key, 'Api, 'State> *
        providerName: string ->
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

Compound key encoders return ordinary F# reference tuples. Compound key decoders
are curried. Definition construction copies `DueTime`, `Period`, `Interleave`,
and `KeepAlive` from `GrainTimerCreationOptions` into immutable metadata.
`onReminder` stores explicit due time and period.

`FunctionalGrain.ref contract factory key` returns
`(FunctionalGrain.rawRef contract factory key).api`. Bound record calls use
`CancellationToken.None`; `callCancellable` supplies remote cooperative
cancellation for advanced calls.

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
5. Create one unique function object for each exact field type with
   `FSharpValue.MakeFunction`; its body throws if invoked.
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
| `grainType` | Required once; non-blank and NUL-free. |
| `version` | Defaults to `1`; positive `int`. |
| operation ID | Record-field name by default; ordinal and case-sensitive. |
| key | Exactly one explicit key codec. |
| invocation | Acknowledged, sequential, and state-replacing by default. |

`operationId` preserves a stable operation identity across a source-field
rename:

```fsharp
operationId "join" (_.enter)
```

The first parameter becomes the wire ID and the selector identifies the current
field. Final IDs are unique, non-blank, NUL-free ordinal strings. A policy or ID
override occurs at most once per field.

Contract version is application protocol metadata in every request. Requests
must match the hosted version exactly. Version is independent of `GrainId`,
storage identity, and the internal Orleans interface version, which is fixed at
`1` for this transport family.

### Key codecs and identity

Support string, `Guid`, `int64`, and Orleans compound forms. Wrapper codecs use
the custom operations from the public API:

```fsharp
stringKey RoomId.value RoomId.create
guidKey CustomerId.value CustomerId.create
int64Key OrderId.value OrderId.create
```

For every accepted domain and native key, a codec satisfies:

- deterministic, injective encoding in its selected Orleans key space;
- `decode (encode key) = key` under domain equality;
- `encode (decode native) = native` in canonical Orleans representation;
- rejection of malformed or non-canonical native values.

Compound extensions follow Orleans key validity rules. Shipped and sample
codecs require property tests for these laws.

A key codec produces and reads the same canonical `IdSpan` representation as the
stock Orleans string, Guid, int64, and compound-key helpers. Native-key codecs
use identity domain conversion. Wrapper codecs compose their domain conversion
with that stock representation. Null, empty, malformed, and non-canonical keys
follow the corresponding Orleans validation rules.

The actor identity is:

```text
GrainId(GrainType(contract.grainType), keyCodec.encode(domainKey))
```

Changing `grainType` or key encoding changes routing and storage identity.
Changing F# module, record, or actor-brand CLR names leaves identity unchanged
when the explicit grain type and encoded key remain unchanged. Contract version
and operation ID are not storage-key components.

## Construction and invocation stages

| Stage | Required work |
|---|---|
| Contract construction | Reflect and cache the API shape, build probe sentinels, resolve policy selectors, and seal operation descriptors. |
| Definition sealing | Resolve handler selectors, verify complete handler coverage, and freeze state/lifecycle configuration. |
| Reference binding | Encode the key, obtain the exact custom reference, validate serializers, and create one typed closure per record field. |
| Client invocation | Serialize the exact argument, construct and send the fixed request, then validate and deserialize the exact reply. |
| Target dispatch | Validate metadata, resolve the descriptor, deserialize the exact argument type, invoke the typed handler, publish state, and serialize the exact reply. |
| Activation lifecycle | For a durable definition, create the persistent facet; then load or initialize state, run activation hooks, reconcile reminders, and create timers. |

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
- at most one `persist`, `collectionAge`, `onActivate`, and `onDeactivate`
  operation;
- a strictly positive `collectionAge` when configured;
- unique non-blank reminder names;
- unique non-blank timer names; and
- valid policy and timer combinations.

A repeated singleton operation is a definition error rather than a replacement
of the earlier value.

Initialization is normalized to `'Key -> 'State`. `defaultState` accepts a fresh
`unit -> 'State` factory; `initialState` accepts `'Key -> 'State`. The factory is
called once for each ephemeral activation, or once when durable state reports
`RecordExists = false`.

### Functional context

Create a new immutable `FunctionalGrainContext<'Actor,'Key>` for every request,
activation hook, deactivation hook, reminder, and timer callback. It contains:

- the domain key decoded once from the supplied `GrainId`;
- `GrainId`, `IGrainFactory`, activation services, and a scoped logger;
- the registered `TimeProvider` and `utcNow = timeProvider.GetUtcNow()`;
- the current target-local cancellation token;
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
| `readOnly` | Orleans read-only scheduling; returned replacement is discarded; automatic primary-state write is skipped. |
| `oneWay` | Reply type is `unit`; the task acknowledges local send; target execution and persistence continue without a caller response. |
| `alwaysInterleave` | Combined with `readOnly` or `oneWay`; returned replacement is discarded and automatic primary-state write is skipped. |
| Timer hook | Whole-state replacement under `Interleave = false`. |
| Reminder hook | Whole-state replacement under ordinary Orleans scheduling. |

The contract rejects `oneWay + readOnly`, a non-`unit` one-way reply, and
`alwaysInterleave` without `readOnly` or `oneWay`. A one-way operation without
`alwaysInterleave` may publish and persist state sequentially.

State-neutral handlers treat the state graph as immutable. Discarding the
returned record cannot undo in-place mutation of an object reachable from state.
Examples use immutable F# state.

### State publication

For an acknowledged mutating operation:

1. Await the handler.
2. Preserve authoritative state when the handler fails.
3. For ephemeral state, publish the returned state and then return the reply.
4. For durable state, retain the previous value, assign the returned value to
   `IPersistentState.State`, and await `WriteStateAsync` within the Orleans turn.
5. Restore the previous in-memory value after a failed write when the activation
   remains alive, then propagate the storage failure.
6. Return the reply after a successful write.

`readOnly` and state-neutral interleaved operations discard the returned state
and skip the automatic write. Handler failure cannot undo in-place mutations or
external effects, so application guidance uses immutable state and explicit
idempotency where required.

One-way completion means the message entered the local Orleans send path. Target
failures are recorded through logging and tracing and are not returned to that
caller.

### Lifecycle hooks, timers, and reminders

The hook type aliases in the public API are normative. `onActivate` runs after
persistent-state setup and its returned state follows the durable publication
rule. `onDeactivate` performs cleanup and returns no replacement. Timer and
reminder replacements follow the mutating publication rule.

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
exact typed payload bytes. Durable storage serializes only `'State`. Contracts,
API facades, selectors, reflection metadata, services, references, targets, and
cancellation resources stay process-local.

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

`persist providerName` enables one `IPersistentState<'State>` with state name
`"state"` and the supplied non-blank Orleans provider name.

For a definition configured with `persist`, the custom activator synchronously
creates the persistent facet through `IPersistentStateFactory.Create<'State>`
before returning the target. This lets the facet subscribe at
`GrainLifecycleStage.SetupState`. The `IPersistentState<'State>.State` property
is the authoritative durable-state holder.

Activation ordering is:

1. The activator creates persistent facets, resolves `IGrainRuntime`, constructs
   the target, and registers its custom lifecycle observers.
2. Orleans lifecycle SetupState loads durable state.
3. `IGrainBase.OnActivateAsync` initializes missing or ephemeral state.
4. The functional `onActivate` hook runs.
5. Required durable initialization or activate-hook state is written.
6. Declared reminders are reconciled in declaration order.
7. Declared timers are created.
8. Activation completes and ordinary turns are admitted.

Deactivation invokes the functional hook before lifecycle `OnStop`, disposes
activation-local timers, and finally reaches `IGrainActivator.DisposeInstance`.

State initialization rules are:

- ephemeral definitions invoke the state factory once per activation;
- durable definitions with `RecordExists = false` invoke the factory, run the
  optional activate hook, and await one initial `WriteStateAsync` before
  activation succeeds;
- durable definitions with `RecordExists = true` use loaded state; an activate
  hook replacement is written before activation succeeds, while an activation
  without that hook performs no extra write;
- factory, hook, or initial-write failure fails activation.

Collection age is frozen into manifest properties. Storage providers, lifecycle,
ETags, reminder registrations, timer registry, activation collection, and
failure propagation remain Orleans services.

## Validation, failures, and observability

Validation occurs at the earliest stage with enough information:

| Stage | Validates |
|---|---|
| Contract construction | API shape, grain type, version, key codec, selectors, IDs, policies. |
| Definition sealing | Initializer, complete handler coverage, persistence configuration, hook/timer/reminder names and values. |
| Silo startup | Registry uniqueness, serializer and storage availability, manifest consistency, marker/interface/provider/activator agreement. |
| Reference binding | Key encoding, custom reference type, concrete client argument/reply serializers. |
| Dispatch | Envelope shape, version, operation, token, flags, payload limit, typed deserialization. |

Diagnostics identify the stage plus grain type, API field or operation ID, and
the relevant expected/actual value. Payload-size errors include direction and
limit. Version errors include expected and received versions. Logs and activities
contain grain type, operation ID, version, grain ID, and outcome; payload bytes
and deserialized application values are excluded by default.

Protocol validation errors are distinguishable from application handler and
storage exceptions. A successful acknowledged mutating call means handler
completion and successful automatic primary-state write. It does not imply
exactly-once external effects. Timeout, retry, cancellation, or failure after a
commit can leave the caller uncertain whether execution occurred. One-way has no
target acknowledgement, and cancellation has no rollback semantics.

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
9. persistent-facet creation early enough for SetupState load;
10. `ICodecProvider.GetCodec(Type)` preflight on both supported versions without
    trial serialization; and
11. isolation of equal keys under different grain types plus heterogeneous silo
    manifests.

These proofs are architecture gates. A failed seam is reported before later
runtime layers are implemented.

### Phase 1: public types and contracts

- Add the normative public signatures and computation-expression builders.
- Implement API-shape caching, sentinel resolution, operation descriptors,
  policy validation, and key codecs.
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
- Implement ephemeral and durable state publication.
- Create persistent facets in the activator and wire activation/deactivation
  ordering.
- Add real deactivation, restart, and cross-silo recovery tests.

**Exit:** write-before-reply, activation ordering, ETag behavior, and durable
recovery tests pass.

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
- `RoomApi.ref` has type `IGrainFactory -> RoomId -> RoomApi`, and a bound value
  infers `RoomApi` without annotation.
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

- Default/overridden operation IDs, duplicate IDs/policies, required grain type,
  positive version, and policy combinations behave as specified.
- String, Guid, int64, and compound codecs pass round-trip, canonicalization,
  injectivity, and malformed-input tests.
- Explicit grain/interface IDs survive CLR/module renames; a changed grain type
  or key codec changes `GrainId`.
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
  selectors, reflection metadata, and services enter neither request bytes nor
  durable storage.

### Activation, state, and lifecycle tests

- Custom target and marker are distinct; the target receives the exact supplied
  context/runtime and is disposed once.
- Deactivation wrappers, default/custom `TimeProvider`, and fresh invocation
  contexts exhibit stock Orleans behavior.
- `onActivate` observes state after durable loading.
- `onDeactivate`, lifecycle `OnStop`, timer disposal, and `DisposeInstance`
  execute once in the specified order.
- Handler coverage, duplicate hooks, and invalid reminder/timer configuration
  fail before the first call.
- Primary persistent state uses exact name `"state"` and the configured provider.
- Missing durable state is initialized and written; existing state is loaded
  without an unnecessary activation write.
- Activate-hook and mutating-handler state is written before success; a failed
  write restores the prior in-memory replacement when the activation survives.
- Read-only and state-neutral interleaved calls discard replacement state and
  skip automatic writes.
- State survives deactivation, full silo restart with retained Redis, and
  activation on another silo.
- Collection age produces stock collection and reactivation behavior.
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
