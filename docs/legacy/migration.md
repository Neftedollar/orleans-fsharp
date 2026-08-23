# Legacy API Migration

**Move an application from the original Orleans.FSharp authoring API to the functional runtime.**

> This page contains old API names on purpose. Current guides teach only `grainContract` / `grainFor` / `journaledGrainFor`.

## Migrating from the grain { } CE

The universal message-passing surface built on the `grain { }` CE -- the builder itself, the
`GrainDefinition`/`GrainContext` types it produces, `FSharpGrainAttribute`,
`AddFSharpGrain(sFromAssembly)`, the `FSharpGrain.*` handle module, `Timers`, and `Reminder` --
is superseded by this functional runtime and now carries `[<Obsolete>]` (warning, not error).

Where the warning fires -- the whole cluster, not just its entry points: on `grain { }` and the
`GrainBuilder` type behind it, on `GrainDefinition` and the old `GrainContext` (types and
modules), on `AdditionalStateSpec`, on `[<FSharpGrain>]`, on `AddFSharpGrain` /
`AddFSharpGrainsFromAssembly`, on the `Timers` and `Reminder` modules, on every operation of the
universal handle module (`FSharpGrain.ref`/`refGuid`/`refInt` and `send`/`post`/`ask` with their
`Guid`/`Int` variants), on the three handle types, on the `IFSharpGrain*` interface aliases, on
the runtime host class `FSharpGrain<'State,'Message>` and `NamedPersistentState`, on the C#
interop helpers for the old cluster (`GrainContext.forCSharp` and the
`Orleans.FSharp.Runtime.GrainDefinition` module behind `additionalState`), and -- from
`Orleans.FSharp.Testing` -- on `TestHarness.getFSharpGrain*` and `GrainMock.withFSharpGrain*`.
So a silo-only, client-only, test-only, or combined process all get the signal at their own
call sites.

Two members of the cluster are deliberately left unattributed, both recorded with a
`// NOT [<Obsolete>]` comment at their declaration in `GrainDiscovery.fs`:
`SimpleGrainState`, because it is `internal` (no consumer can name it, so the attribute would be
invisible where it matters), and `UniversalGrainHandlerRegistry`, the silo-side dispatcher wired
by the already-obsolete `AddFSharpGrain` — a consumer only reaches it after being warned at that
entry point. The prose that hands a reader a recipe naming it directly is
[testing.md](testing.md), "Testing the Universal Grain Pattern" (in this repository and in its
published mirror under `website/src/content/docs/`, which is what the docs site ships), plus the
"Understanding the Universal Grain Pattern" section of `DEVGUIDE.md`; each of those three now sits
under a deprecation banner. `CHANGELOG.md` also names it, in the historical release entry that
introduced it — a changelog records what shipped when and is deliberately left as written.

Inside this repository the library files that must keep naming these symbols (the definitions
themselves, the runtime host, the registries, the test harness) wrap exactly those references in
`#nowarn "44"` ... `#warnon "44"` brackets carrying a `deprecated API self-reference` comment.
That is a self-reference bracket, not
a blanket suppression: nothing outside the bracketed lines is silenced, and no library project
disables FS0044 project-wide.

Old code keeps compiling and running unchanged; every example under `examples/`, the sample
under `src/Orleans.FSharp.Sample`, `testbed/`, and the `orleans-fsharp` template carry a small
functional-runtime twin grain beside the old one so the two authoring styles can be compared
side by side in a real project.

Before/after mapping:

| Old (`grain { }` CE) | New (functional runtime) |
|---|---|
| `grain { defaultState ...; handle ...; persist "Default" }` | `grainFor contract { defaultState (fun () -> ...); handle (_.op) handler; usePersistentState ... }` |
| `GrainDefinition<'State,'Message>` / hand-written grain interface | `grainContract<'Actor,'Key,'Api> { grainType ...; version ...; <key op> }` defining an `'Api` record of functions |
| `[<FSharpGrain>]` + `AddFSharpGrainsFromAssembly` | `AddFunctionalGrain definition` (no attribute-scan step) |
| `AddFSharpGrain<'State,'Message>(definition)` | `AddFunctionalGrain definition` on the silo builder; `AddFunctionalGrainClient` on a client-only process |
| `FSharpGrain.ref<'State,'Message> factory key` + `FSharpGrain.send/post/ask` | `FunctionalGrain.ref contract factory key`, then call the typed API record's function directly |
| `onTimer "name" dueTime period handler` (in `grain { }`) | `onTimer` operation in `grainFor { }` |
| `onReminder "name" handler` (in `grain { }`) | `onReminder` operation in `grainFor { }` |
| `Timers.register` / `Timers.registerWithState` (class grain) | `Grain.RegisterGrainTimer` directly -- unchanged, this is a class-grain-native Orleans API, not something the functional runtime replaces |
| `Reminder.register` / `.unregister` / `.get` (class grain) | `Grain.RegisterOrUpdateReminder` / `.UnregisterReminder` / `.GetReminder` directly -- likewise class-grain-native |
| `persist "Default"` | `usePersistentState` with a `PersistentState.create<'State> "name" "provider"` descriptor |
| one-way `FSharpGrain.post` | `oneWay (_.op)` in the contract |
| `handleWithContext` / `GrainContext.getService` etc. | the `context` parameter passed to every `handle` callback (`context.services`, `context.grainFactory`, ...) |
| `FSharpObserverManager<'Obs>` held in `grain { }` state, `Subscribe`/`Unsubscribe`/`Notify` message cases | the same `FSharpObserverManager<'Obs>` held in `grainFor` state, with `subscribe: 'Obs -> Task<_>` / `unsubscribe` / a notifying operation on the contract -- unchanged, observers are not part of this deprecation (see below) |
| `onLifecycleStage n hook` (`grain { }`, arbitrary int, `CancellationToken -> Task<unit>`) | `onLifecycle stage hook` (`grainFor { }`, closed `First`/`SetupState`/`Last` set -- `Activate` is rejected, use `onActivate`; hook is `FunctionalGrainContext<'Actor,'Key> -> Task<unit>`) |

`grain { }`'s `onLifecycleStage` operation let a grain hook an *arbitrary* `GrainLifecycleStage`
(`First`/`SetupState`/`Activate`/`Last`/any other int) with a `CancellationToken -> Task<unit>`
callback. `grainFor { }` has `onLifecycle` for the closed set of
documented Orleans stages -- see [Lifecycle-stage hooks](#lifecycle-stage-hooks-onlifecycle)
above for why the hook carries no state at any stage, and for the verified activation ordering. A grain that genuinely needs an *undocumented* numbered stage
(outside `First`/`SetupState`/`Activate`/`Last`) still has no functional-runtime equivalent and
must stay on the `grain { }` CE, or hook the stage on a class grain directly via
`ILifecycleParticipant<IGrainLifecycle>` -- that residual gap is deliberately narrow: Orleans
itself documents only these four stages as stable, and `onLifecycle` already covers three of
them (`Activate` is redundant with `onActivate`).
`InterleaveMessage` has no separate capability gap: it dies with the builder, since the
functional runtime's `alwaysInterleave (_.op)` contract operation covers the same need per
operation rather than per message type.

Two other `grain { }`-adjacent pieces are **not** part of this deprecation and are unaffected:
`GrainRef.fs` (the hand-written-interface style used by `ICounterGrain`, `IOrderGrain`, etc. --
a third authoring style, not superseded by anything here) and `RequestCtx.set/get/getOrDefault/remove`
(same-static wrappers that work unchanged inside functional handlers; `RequestCtx.withValue` has
no functional-runtime equivalent).

### Observers, streams, and the other orthogonal surfaces

Pub/sub **observers are not a capability gap**. `Observer.createRef` / `Observer.deleteRef` /
`Observer.subscribe` and `FSharpObserverManager<'Obs>` are grain-model agnostic -- they need an
`IGrainFactory` and an `IGrainObserver`-derived interface, both of which a functional grain has
(`context.grainFactory`, and any observer interface you already use). An observer reference is
an ordinary contract operation argument: it clears the functional transport's serializer
preflight and round-trips as a live callback target. That is proven end to end, not asserted --
`tests/Orleans.FSharp.Integration/FunctionalObserverIntegrationTests.fs` runs a `grainContract` /
`grainFor` grain on a real TestingHost cluster which subscribes an observer reference, notifies
it, and unsubscribes it.

The one real constraint on that CLASSIC path is Orleans' own and predates all of this: the
**observer interface must be declared in C#**, because Orleans' proxy source generators run over
C# and not F#. That is why `ITestChatObserver` lives in `src/Orleans.FSharp.CodeGen`, and it
applies identically to the `grain { }` CE and to class grains. An example that declares its
observer interface in F# (`examples/chat-room`, `IChatObserver` in `ChatTypes.fs`) cannot use the
classic path at all under either authoring model.

**Functional observers remove that constraint** — see
[Push to clients: functional observers](#push-to-clients-functional-observers) above. The one
C#-declared interface lives inside `Orleans.FSharp.Abstractions`, every application observer of
every brand rides on it, and an observer becomes an ordinary F# handler record. `examples/chat-room`
pushes live through it. Use the classic path when you already have a C#-declared observer
interface and want to keep it; use functional observers otherwise.

### Call filters over a functional grain

A stock `IIncomingGrainCallFilter` sees every functional call, but **not** as the request type the
library uses internally. `FunctionalRequest` is `internal` to `Orleans.FSharp.Abstractions`, so an
application filter cannot write `context.Request :? FunctionalRequest` — that does not compile
outside the library. The supported test is on **argument 0**, which is the public read-only view:

```fsharp
open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

type FunctionalAuditFilter() =
    interface IIncomingGrainCallFilter with
        member _.Invoke(context: IIncomingGrainCallContext) =
            task {
                match context.Request.GetArgument 0 with
                | :? IFunctionalRequestMetadata as metadata ->
                    // grainType / contractVersion / operationId / readOnly / oneWay /
                    // alwaysInterleave / payload size — everything the envelope carries.
                    if metadata.IsOneWay && metadata.PayloadLength > 65536 then
                        raise (InvalidOperationException $"'{metadata.OperationId}' is too large")
                | _ -> ()   // not a functional call: a system grain, or a CE/class grain

                do! context.Invoke()
            }
            :> Task
```

The `| _ -> ()` arm is load-bearing rather than defensive tidiness: the same filter runs for
Orleans' own system grains, whose argument 0 is something else entirely.

The same "orthogonal, unaffected" verdict covers streaming, broadcast channels, filters,
Kubernetes hosting, logging, shutdown, transactions, event sourcing
(`FSharpEventSourcedGrain*` -- a separate interface family, `IFSharpEventSourcedGrain`, which
shares nothing with the deprecated `IFSharpGrain*` message-passing aliases), versioning,
resilience, batching, `GrainState.fs`, `FSharpBinaryCodec`, and `StateMigration`. None of them
carries `[<Obsolete>]`.
