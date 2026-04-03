# Public API Contracts: Orleans.FSharp

**Date**: 2026-04-02
**Branch**: `001-fsharp-orleans-api`

This document defines the public API surface of the `Orleans.FSharp` library.
All types and functions listed here are the contract — changes require a
constitution-level review.

---

## Module: Orleans.FSharp.GrainBuilder

The `grain { }` computation expression for defining grain behavior.

```fsharp
/// Define a grain with DU state and message handling.
///
/// Example:
///   let counterGrain = grain {
///       defaultState (Counter.Zero)
///       handle (fun state msg ->
///           match msg with
///           | Increment -> task { return state + 1, () }
///           | GetValue  -> task { return state, state })
///       persist "Default"
///       onActivate (fun state -> task { return state })
///   }

type GrainBuilder<'State, 'Message>

// CE keywords:
val defaultState : 'State -> GrainBuilder<'State, 'Message>
val handle       : ('State -> 'Message -> Task<'State * 'Result>) -> GrainBuilder<'State, 'Message>
val persist      : storageName:string -> GrainBuilder<'State, 'Message>
val onActivate   : ('State -> Task<'State>) -> GrainBuilder<'State, 'Message>
val onDeactivate : ('State -> Task<unit>) -> GrainBuilder<'State, 'Message>
```

---

## Module: Orleans.FSharp.GrainRef

Type-safe grain references.

```fsharp
/// Get a reference to a grain by string key.
val ofString<'TInterface when 'TInterface :> IGrainWithStringKey>
    : factory:IGrainFactory -> key:string -> GrainRef<'TInterface, string>

/// Get a reference to a grain by GUID key.
val ofGuid<'TInterface when 'TInterface :> IGrainWithGuidKey>
    : factory:IGrainFactory -> key:Guid -> GrainRef<'TInterface, Guid>

/// Get a reference to a grain by int64 key.
val ofInt64<'TInterface when 'TInterface :> IGrainWithIntegerKey>
    : factory:IGrainFactory -> key:int64 -> GrainRef<'TInterface, int64>

/// Invoke a method on the referenced grain.
val invoke<'TInterface, 'TKey, 'Result>
    : ref:GrainRef<'TInterface, 'TKey>
    -> call:('TInterface -> Task<'Result>)
    -> Task<'Result>
```

---

## Module: Orleans.FSharp.GrainState

Immutable state management over Orleans persistence.

```fsharp
/// Read current state from storage.
val read<'T> : state:IPersistentState<'T> -> Task<'T>

/// Write new state to storage (replaces current).
val write<'T> : state:IPersistentState<'T> -> value:'T -> Task<unit>

/// Clear persisted state.
val clear<'T> : state:IPersistentState<'T> -> Task<unit>

/// Get current in-memory state without storage read.
val current<'T> : state:IPersistentState<'T> -> 'T
```

---

## Module: Orleans.FSharp.Streaming

Orleans stream wrapping with TaskSeq.

```fsharp
/// Get a typed stream reference.
val getStream<'T>
    : provider:IStreamProvider
    -> ns:string
    -> key:string
    -> StreamRef<'T>

/// Publish an event to a stream.
val publish<'T> : stream:StreamRef<'T> -> event:'T -> Task<unit>

/// Subscribe to a stream with a callback handler.
val subscribe<'T>
    : stream:StreamRef<'T>
    -> handler:('T -> Task<unit>)
    -> Task<StreamSubscription>

/// Consume a stream as a TaskSeq (pull-based).
val asTaskSeq<'T> : stream:StreamRef<'T> -> TaskSeq<'T>

/// Unsubscribe from a stream.
val unsubscribe : sub:StreamSubscription -> Task<unit>
```

---

## Module: Orleans.FSharp.Configuration

Silo and client configuration DSL.

```fsharp
/// Build a silo configuration.
///
/// Example:
///   let config = siloConfig {
///       useLocalhostClustering()
///       addMemoryStorage "Default"
///       addMemoryStreams "MemoryStreams"
///       useSerilog()
///   }

type SiloConfigBuilder

val siloConfig : SiloConfigBuilder

// CE keywords:
val useLocalhostClustering : unit -> SiloConfigBuilder
val useAzureClustering     : connStr:string -> SiloConfigBuilder
val addMemoryStorage       : name:string -> SiloConfigBuilder
val addAzureBlobStorage    : name:string -> connStr:string -> SiloConfigBuilder
val addAdoNetStorage       : name:string -> connStr:string -> invariant:string -> SiloConfigBuilder
val addMemoryStreams        : name:string -> SiloConfigBuilder
val useSerilog             : unit -> SiloConfigBuilder
val configureServices      : (IServiceCollection -> unit) -> SiloConfigBuilder

/// Apply the configuration to a HostApplicationBuilder.
val applyToHost : config:SiloConfig -> builder:HostApplicationBuilder -> unit
```

---

## Module: Orleans.FSharp.Logging

Structured logging integration.

```fsharp
/// Log a structured message within grain context.
/// Automatically attaches GrainType, GrainId, CorrelationId.
val logInfo    : logger:ILogger -> template:string -> [<ParamArray>] args:obj[] -> unit
val logWarning : logger:ILogger -> template:string -> [<ParamArray>] args:obj[] -> unit
val logError   : logger:ILogger -> exn:exn -> template:string -> [<ParamArray>] args:obj[] -> unit
val logDebug   : logger:ILogger -> template:string -> [<ParamArray>] args:obj[] -> unit

/// Create a correlation scope. All logs within the scope share a correlation ID.
val withCorrelation : correlationId:string -> (unit -> Task<'T>) -> Task<'T>

/// Get the current correlation ID (from ambient context).
val currentCorrelationId : unit -> string option
```

---

## Module: Orleans.FSharp.Testing

Test harness and FsCheck integration.

```fsharp
/// Create a test cluster with default in-memory configuration.
val createTestCluster : unit -> Task<TestHarness>

/// Create a test cluster with custom configuration.
val createTestClusterWith : config:SiloConfig -> Task<TestHarness>

/// Get a grain reference from the test cluster.
val getGrain<'TInterface, 'TKey>
    : harness:TestHarness
    -> key:'TKey
    -> GrainRef<'TInterface, 'TKey>

/// Capture all log entries emitted during a test.
val captureLogs : harness:TestHarness -> CapturedLogEntry list

/// Reset all grain state and logs in the test cluster.
val reset : harness:TestHarness -> Task<unit>

/// Dispose the test cluster.
val dispose : harness:TestHarness -> Task<unit>

/// FsCheck Arbitrary for generating random command sequences.
val commandSequenceArb<'Command> : Arbitrary<'Command list>

/// FsCheck property helper: verify state invariant holds after all commands.
val stateMachineProperty<'State, 'Command>
    : initial:'State
    -> apply:('State -> 'Command -> 'State)
    -> invariant:('State -> bool)
    -> commands:'Command list
    -> Property
```
