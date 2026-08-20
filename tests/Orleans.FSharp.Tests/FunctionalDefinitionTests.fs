/// <summary>
/// Definition tests for spec 003 Phase 1: the <c>grainFor</c> computation expression accumulates
/// immutable configuration, normalizes state initialization to <c>'Key -&gt; 'State</c>, and
/// rejects repeated singleton operations, incomplete handler coverage, and invalid
/// persistence, reminder, and timer configuration.
/// </summary>
module Orleans.FSharp.Tests.FunctionalDefinitionTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.Runtime
open Orleans.FSharp

type RoomActor = private RoomActor of unit

[<NoEquality; NoComparison>]
type RoomApi =
    { join: string -> Task<unit>
      say: string -> Task<int64> }

type RoomState = { count: int }
type AuditState = { total: int64 }

let private contract =
    grainContract<RoomActor, string, RoomApi> {
        grainType "def.room"
        stringKey
    }

let private primary = PersistentState.create<RoomState> "state" "Default"
let private audit = PersistentState.create<AuditState> "audit" "Audit"

let private throws (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

let private joinHandler _ state (_: string) = task { return state, () }
let private sayHandler _ state (_: string) = task { return state, 1L }

let private complete () =
    grainFor contract {
        defaultState (fun () -> { count = 0 })
        handle (_.join) joinHandler
        handle (_.say) sayHandler
    }

// ──────────────────────────────────────────────────────────────────────────────
// Sealing
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a complete definition seals with one handler per API field`` () =
    let definition = complete ()

    test <@ definition.GrainTypeName = "def.room" @>
    test <@ definition.Handlers.Count = 2 @>
    test <@ definition.Handlers.ContainsKey 0 && definition.Handlers.ContainsKey 1 @>
    test <@ definition.Primary.IsNone @>
    test <@ definition.CollectionAge.IsNone @>

[<Fact>]
let ``defaultState normalizes to a key-independent initializer`` () =
    let definition = complete ()

    test <@ definition.Initializer "any" = { count = 0 } @>

[<Fact>]
let ``initialState receives the domain key`` () =
    let definition =
        grainFor contract {
            initialState (fun (key: string) -> { count = key.Length })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.Initializer "abcd" = { count = 4 } @>

[<Fact>]
let ``the state factory runs once per initializer call`` () =
    let mutable calls = 0

    let definition =
        grainFor contract {
            defaultState (fun () ->
                calls <- calls + 1
                { count = calls })

            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ calls = 0 @>
    let first = definition.Initializer "a"
    let second = definition.Initializer "a"
    test <@ first = { count = 1 } && second = { count = 2 } @>

[<Fact>]
let ``a missing handler fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                handle (_.join) joinHandler
            }
            |> ignore)

    test <@ error.Message.Contains "no handler for API field(s) say" @>

[<Fact>]
let ``a duplicate handler fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                handle (_.join) joinHandler
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "already has a handler" @>

// ──────────────────────────────────────────────────────────────────────────────
// Persistence attachment
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``stateFrom selects the primary holder and usePersistentState attaches extras`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            stateFrom primary
            usePersistentState audit (fun _ -> { total = 0L })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.Primary.IsSome @>
    test <@ definition.Primary.Value.StateName = "state" @>
    test <@ definition.Additional |> List.map (fun extra -> extra.Descriptor.StateName) = [ "audit" ] @>
    test <@ definition.Additional.Head.Descriptor.StoredType = typeof<AuditState> @>
    test <@ definition.Additional.Head.Initialize "key" = box { total = 0L } @>

[<Fact>]
let ``a repeated stateFrom fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                stateFrom primary
                stateFrom primary
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'stateFrom' is declared more than once" @>

/// <remarks>
/// Task-6 close-out 1: this is the counter-case for `repeatsPrimary` — 'sameName' shares
/// 'primary's stateName but NOT its provider, so this is a genuine name collision between two
/// DIFFERENT attachments, not a repeat of the 'stateFrom' descriptor. Before the fix,
/// `repeatsPrimary` compared StateName alone (trivially true inside this branch, since the
/// dictionary key IS the shared StateName) and wrongly appended the "already attached as the
/// primary state" sentence to this message.
/// </remarks>
[<Fact>]
let ``the same state name with a different provider fails definition sealing`` () =
    let sameName = PersistentState.create<AuditState> "state" "Other"

    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                stateFrom primary
                usePersistentState sameName (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "is attached more than once" @>
    test <@ not (error.Message.Contains "already attached as the primary state") @>

/// <remarks>
/// Task-7 close-out A.4: this is the positive arm of `repeatsPrimary` — the same descriptor
/// value (matching StateName, ProviderName, AND StoredType) reattached via `usePersistentState`
/// really is the primary repeated, so the "already attached as the primary state" sentence must
/// be present. Asserting only "is attached more than once" (as this test did before) would still
/// pass with `repeatsPrimary` hardcoded to `false`, since that substring is common to both arms
/// of the message; the sentence below only appears on the true arm.
/// </remarks>
[<Fact>]
let ``the primary descriptor must not be repeated with usePersistentState`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                stateFrom primary
                usePersistentState primary (fun _ -> { count = 0 })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "is attached more than once" @>
    test <@ error.Message.Contains "already attached as the primary state" @>

[<Fact>]
let ``persistent descriptors reject blank and NUL names`` () =
    let blankState =
        throws (fun () -> PersistentState.create<RoomState> "  " "Default" |> ignore)

    let nulState =
        throws (fun () -> PersistentState.create<RoomState> "sta\000te" "Default" |> ignore)

    let blankProvider =
        throws (fun () -> PersistentState.create<RoomState> "state" "" |> ignore)

    let nulProvider =
        throws (fun () -> PersistentState.create<RoomState> "state" "De\000fault" |> ignore)

    test <@ blankState.Message.Contains "non-blank" @>
    test <@ nulState.Message.Contains "NUL" @>
    test <@ blankProvider.Message.Contains "non-blank" @>
    test <@ nulProvider.Message.Contains "NUL" @>

[<Fact>]
let ``a persistent descriptor keeps its logical identity`` () =
    let descriptor = primary.Descriptor

    test <@ descriptor.StateName = "state" @>
    test <@ descriptor.ProviderName = "Default" @>
    test <@ descriptor.StoredType = typeof<RoomState> @>

// ──────────────────────────────────────────────────────────────────────────────
// Collection age
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``collectionAge is frozen into definition metadata`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            collectionAge (TimeSpan.FromMinutes 30.0)
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.CollectionAge = Some(TimeSpan.FromMinutes 30.0) @>

[<Fact>]
let ``a non-positive collectionAge fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                collectionAge TimeSpan.Zero
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "strictly positive" @>

[<Fact>]
let ``a repeated collectionAge fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                collectionAge (TimeSpan.FromMinutes 1.0)
                collectionAge (TimeSpan.FromMinutes 2.0)
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'collectionAge' is declared more than once" @>

let private activateHook _ state = task { return state }
let private deactivateHook _ (_: DeactivationReason) (_: RoomState) = task { return () }
let private reminderHook _ state (_: TickStatus) = task { return state }
let private timerHook _ state = task { return state }

// ──────────────────────────────────────────────────────────────────────────────
// Placement (spec 004 item 4)
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``statelessWorker is frozen into definition metadata`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            statelessWorker 4
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.Placement = Some(StatelessWorker 4) @>

[<Fact>]
let ``placement is frozen into definition metadata`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            placement PreferLocal
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.Placement = Some(Strategy PreferLocal) @>

[<Fact>]
let ``a non-positive maxLocalWorkers fails definition sealing`` () =
    let zero =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 0
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let negative =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker -1
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ zero.Message.Contains "strictly positive" @>
    test <@ negative.Message.Contains "strictly positive" @>

[<Fact>]
let ``statelessWorker and placement are mutually exclusive in either order`` () =
    let statelessWorkerThenPlacement =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                placement PreferLocal
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let placementThenStatelessWorker =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                placement PreferLocal
                statelessWorker 4
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let repeatedPlacement =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                placement PreferLocal
                placement Random
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ statelessWorkerThenPlacement.Message.Contains "cannot be combined" @>
    test <@ placementThenStatelessWorker.Message.Contains "cannot be combined" @>
    test <@ repeatedPlacement.Message.Contains "cannot be combined" @>

/// <remarks>
/// Spec item 4: "statelessWorker rejects stateFrom, usePersistentState, and onReminder (durable
/// identity is meaningless for multiplexed local activations) and rejects collectionAge." All
/// four in both declaration orders (the rejected operation before or after 'statelessWorker'),
/// since the check is deferred to sealing rather than order-dependent.
/// </remarks>
[<Fact>]
let ``statelessWorker rejects stateFrom, usePersistentState, onReminder, and collectionAge`` () =
    let rejectsStateFromBefore =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                stateFrom primary
                statelessWorker 4
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let rejectsStateFromAfter =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                stateFrom primary
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let rejectsUsePersistentState =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                usePersistentState audit (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let rejectsOnReminder =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                onReminder "sweep" (TimeSpan.FromMinutes 1.0) (TimeSpan.FromMinutes 5.0) reminderHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let rejectsCollectionAge =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                collectionAge (TimeSpan.FromMinutes 10.0)
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ rejectsStateFromBefore.Message.Contains "'statelessWorker' with 'stateFrom'" @>
    test <@ rejectsStateFromAfter.Message.Contains "'statelessWorker' with 'stateFrom'" @>
    test <@ rejectsUsePersistentState.Message.Contains "'statelessWorker' with 'usePersistentState'" @>
    test <@ rejectsOnReminder.Message.Contains "'statelessWorker' with 'onReminder'" @>
    test <@ rejectsCollectionAge.Message.Contains "'statelessWorker' with 'collectionAge'" @>

// ──────────────────────────────────────────────────────────────────────────────
// Implicit subscriptions (spec 004 item 1)
// ──────────────────────────────────────────────────────────────────────────────

let private streamHook _ state (_: string) = task { return state }

[<Fact>]
let ``onStream and onBroadcast are frozen into definition metadata`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            onStream "Streams" "chat.messages" streamHook
            onStream "Streams" "chat.presence" (fun _ state (_: int) -> task { return state })
            onBroadcast "Channels" "chat.control" streamHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    let bindings = definition.StreamBindings
    test <@ bindings.Length = 3 @>

    test
        <@
            bindings
            |> List.map (fun binding -> binding.OperationName, binding.ProviderName, binding.Namespace) = [ "onStream",
                                                                                                            "Streams",
                                                                                                            "chat.messages"
                                                                                                            "onStream",
                                                                                                            "Streams",
                                                                                                            "chat.presence"
                                                                                                            "onBroadcast",
                                                                                                            "Channels",
                                                                                                            "chat.control" ]
        @>

    // The item type is captured from the hook, one per declaration.
    test <@ bindings |> List.map (fun binding -> binding.ItemType) = [ typeof<string>; typeof<int>; typeof<string> ] @>
    test <@ bindings |> List.map (fun binding -> binding.IsStream) = [ true; true; false ] @>

/// <remarks>
/// The uniqueness key is (transport, provider, namespace), not the namespace alone: the same
/// namespace on two different providers is two different streams, and the delivery path matches
/// on both, so both may be declared. Mutation-checked in both directions — the accepted case is
/// asserted as well as the rejected one, so a rule that rejected everything would fail here.
/// </remarks>
[<Fact>]
let ``onStream rejects a repeated (provider, namespace) pair but allows the same namespace on another provider`` () =
    let repeated =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onStream "Streams" "chat.messages" streamHook
                onStream "Streams" "chat.messages" streamHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let repeatedChannel =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onBroadcast "Channels" "chat.control" streamHook
                onBroadcast "Channels" "chat.control" streamHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let twoProviders =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            onStream "Streams" "chat.messages" streamHook
            onStream "OtherStreams" "chat.messages" streamHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    // A stream and a channel may share a namespace: they are different binding types, and
    // Orleans accepts both attributes on one class.
    let bothTransports =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            onStream "Streams" "chat.shared" streamHook
            onBroadcast "Channels" "chat.shared" streamHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ repeated.Message.Contains "'onStream' is declared more than once" @>
    test <@ repeated.Message.Contains "chat.messages" @>
    test <@ repeatedChannel.Message.Contains "'onBroadcast' is declared more than once" @>
    test <@ twoProviders.StreamBindings.Length = 2 @>
    test <@ bothTransports.StreamBindings.Length = 2 @>

[<Fact>]
let ``onStream and onBroadcast reject a blank provider or namespace`` () =
    let blankProvider =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onStream "  " "chat.messages" streamHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let blankNamespace =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onStream "Streams" "" streamHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let blankChannelNamespace =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onBroadcast "Channels" "   " streamHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let missingHook =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onStream "Streams" "chat.messages" Unchecked.defaultof<StreamHook<RoomActor, string, RoomState, string>>
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ blankProvider.Message.Contains "blank provider name" @>
    test <@ blankNamespace.Message.Contains "blank namespace" @>
    test <@ blankChannelNamespace.Message.Contains "blank namespace" @>
    test <@ missingHook.Message.Contains "requires a hook" @>

/// <remarks>
/// Orleans' own <c>SiloStreamProviderRuntime.BindExtension</c> throws "The extension ... cannot
/// be bound to a Stateless Worker", so a stateless worker can never host a consumer extension —
/// and implicit delivery addresses one activation identity derived from the stream key, which
/// multiplexed local activations cannot honor. Rejected at sealing in both declaration orders,
/// and mutation-checked against a non-stateless-worker placement, which combines freely.
/// </remarks>
[<Fact>]
let ``statelessWorker rejects onStream and onBroadcast in either order`` () =
    let streamAfter =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                onStream "Streams" "chat.messages" streamHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let streamBefore =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onStream "Streams" "chat.messages" streamHook
                statelessWorker 4
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let broadcastAfter =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                onBroadcast "Channels" "chat.control" streamHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let placementCombinesFreely =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            placement PreferLocal
            onStream "Streams" "chat.messages" streamHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ streamAfter.Message.Contains "'statelessWorker' with 'onStream'" @>
    test <@ streamBefore.Message.Contains "'statelessWorker' with 'onStream'" @>
    test <@ broadcastAfter.Message.Contains "'statelessWorker' with 'onBroadcast'" @>
    test <@ placementCombinesFreely.StreamBindings.Length = 1 @>

/// <remarks>
/// Mutation control for every rejection above: a definition with no implicit subscription at all
/// still seals and carries an empty binding list, so the new sealing block cannot be passing by
/// rejecting or accepting everything.
/// </remarks>
[<Fact>]
let ``a definition without onStream or onBroadcast carries no bindings`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.StreamBindings.IsEmpty @>

/// <remarks>
/// Regression control mirroring "stateFrom with an explicit grain type still seals" below: a
/// non-stateless-worker definition combines 'placement' with a durable attachment freely, so the
/// rejection above really is specific to 'statelessWorker' and not to 'placement' in general.
/// </remarks>
[<Fact>]
let ``placement (non-stateless-worker) combines freely with stateFrom, reminders, and collectionAge`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            placement PreferLocal
            stateFrom primary
            collectionAge (TimeSpan.FromMinutes 10.0)
            onReminder "sweep" (TimeSpan.FromMinutes 1.0) (TimeSpan.FromMinutes 5.0) reminderHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.Placement = Some(Strategy PreferLocal) @>
    test <@ definition.Primary.IsSome @>
    test <@ definition.CollectionAge = Some(TimeSpan.FromMinutes 10.0) @>
    test <@ definition.Reminders |> List.map (fun reminder -> reminder.Name) = [ "sweep" ] @>

// ──────────────────────────────────────────────────────────────────────────────
// Lifecycle-stage hooks (spec 004 item 8a)
// ──────────────────────────────────────────────────────────────────────────────

let private lifecycleHook _ = task { return () }

[<Fact>]
let ``onLifecycle hooks are frozen into definition metadata, keyed by stage`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            onLifecycle First lifecycleHook
            onLifecycle SetupState lifecycleHook
            onLifecycle Last lifecycleHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.LifecycleHooks.Count = 3 @>
    test <@ definition.LifecycleHooks.ContainsKey First @>
    test <@ definition.LifecycleHooks.ContainsKey SetupState @>
    test <@ definition.LifecycleHooks.ContainsKey Last @>

[<Fact>]
let ``onLifecycle Activate is rejected -- use onActivate instead`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onLifecycle Activate lifecycleHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'onLifecycle Activate' is rejected" @>
    test <@ error.Message.Contains "onActivate" @>

[<Fact>]
let ``a repeated stage fails definition sealing -- each stage accepts at most one hook`` () =
    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onLifecycle First lifecycleHook
                onLifecycle First lifecycleHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'onLifecycle First' is declared more than once" @>

// ──────────────────────────────────────────────────────────────────────────────
// Hooks, reminders, and timers
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``lifecycle hooks are retained and may be declared once`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            onActivate activateHook
            onDeactivate deactivateHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.OnActivate.IsSome && definition.OnDeactivate.IsSome @>

    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onActivate activateHook
                onActivate activateHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'onActivate' is declared more than once" @>

[<Fact>]
let ``reminders keep their explicit due time and period`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            onReminder "sweep" (TimeSpan.FromMinutes 1.0) (TimeSpan.FromMinutes 5.0) reminderHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.Reminders |> List.map (fun reminder -> reminder.Name) = [ "sweep" ] @>
    test <@ definition.Reminders.Head.DueTime = TimeSpan.FromMinutes 1.0 @>
    test <@ definition.Reminders.Head.Period = TimeSpan.FromMinutes 5.0 @>

[<Fact>]
let ``invalid reminder configuration fails definition sealing`` () =
    let duplicate =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onReminder "sweep" TimeSpan.Zero (TimeSpan.FromMinutes 5.0) reminderHook
                onReminder "sweep" TimeSpan.Zero (TimeSpan.FromMinutes 5.0) reminderHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let negativeDueTime =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onReminder "sweep" (TimeSpan.FromMinutes -1.0) (TimeSpan.FromMinutes 5.0) reminderHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let zeroPeriod =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onReminder "sweep" TimeSpan.Zero TimeSpan.Zero reminderHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let blankName =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onReminder " " TimeSpan.Zero (TimeSpan.FromMinutes 5.0) reminderHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ duplicate.Message.Contains "declared more than once" @>
    test <@ negativeDueTime.Message.Contains "dueTime >= 0" @>
    test <@ zeroPeriod.Message.Contains "period > 0" @>
    test <@ blankName.Message.Contains "blank name" @>

[<Fact>]
let ``timers copy the Orleans creation options into immutable metadata`` () =
    let options =
        GrainTimerCreationOptions(TimeSpan.FromSeconds 1.0, TimeSpan.FromSeconds 5.0, KeepAlive = true)

    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            onTimer "tick" options timerHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    let timer = definition.Timers.Head

    test <@ timer.Name = "tick" @>
    test <@ timer.DueTime = TimeSpan.FromSeconds 1.0 @>
    test <@ timer.Period = TimeSpan.FromSeconds 5.0 @>
    test <@ timer.Interleave = false @>
    test <@ timer.KeepAlive @>

[<Fact>]
let ``an interleaving timer fails definition sealing`` () =
    let options =
        GrainTimerCreationOptions(TimeSpan.FromSeconds 1.0, TimeSpan.FromSeconds 5.0, Interleave = true)

    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onTimer "tick" options timerHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "Interleave = true" @>

[<Fact>]
let ``a duplicate timer name fails definition sealing`` () =
    let options =
        GrainTimerCreationOptions(TimeSpan.FromSeconds 1.0, TimeSpan.FromSeconds 5.0)

    let error =
        throws (fun () ->
            grainFor contract {
                defaultState (fun () -> { count = 0 })
                onTimer "tick" options timerHook
                onTimer "tick" options timerHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "declared more than once" @>

// ──────────────────────────────────────────────────────────────────────────────
// Durable attachments require an explicit grain type (Task 12)
// ──────────────────────────────────────────────────────────────────────────────

type private DerivedExtraState = { total: int64 }

let private derivedContract =
    grainContract<Orleans.FSharp.Tests.GrainTypeDerivation.DerivableActor, string, RoomApi> { stringKey }

let private derivedPrimary = PersistentState.create<RoomState> "derived-state" "Default"
let private derivedExtra = PersistentState.create<DerivedExtraState> "derived-extra" "Default"

[<Fact>]
let ``an ephemeral definition may omit the contract's grain type`` () =
    let definition =
        grainFor derivedContract {
            defaultState (fun () -> { count = 0 })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.GrainTypeName = "DerivableActor" @>

/// <remarks>
/// Task 12 point 3: nothing durable outlives the activation for a timer, collectionAge, or a
/// lifecycle hook, so none of them trigger the explicit-grainType restriction -- unlike
/// stateFrom/usePersistentState/onReminder below, all four combine freely with a derived grain
/// type.
/// </remarks>
[<Fact>]
let ``onActivate, onDeactivate, onTimer, and collectionAge do not require an explicit grain type`` () =
    let options = GrainTimerCreationOptions(TimeSpan.FromSeconds 1.0, TimeSpan.FromSeconds 5.0)

    let definition =
        grainFor derivedContract {
            defaultState (fun () -> { count = 0 })
            collectionAge (TimeSpan.FromMinutes 10.0)
            onActivate activateHook
            onDeactivate deactivateHook
            onTimer "tick" options timerHook
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.GrainTypeName = "DerivableActor" @>
    test <@ definition.CollectionAge = Some(TimeSpan.FromMinutes 10.0) @>
    test <@ definition.OnActivate.IsSome && definition.OnDeactivate.IsSome @>
    test <@ definition.Timers |> List.map (fun timer -> timer.Name) = [ "tick" ] @>

[<Fact>]
let ``stateFrom on a derived grain type fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor derivedContract {
                defaultState (fun () -> { count = 0 })
                stateFrom derivedPrimary
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'DerivableActor'" @>
    test <@ error.Message.Contains "explicit 'grainType'" @>

[<Fact>]
let ``usePersistentState on a derived grain type fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor derivedContract {
                defaultState (fun () -> { count = 0 })
                usePersistentState derivedExtra (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'DerivableActor'" @>
    test <@ error.Message.Contains "explicit 'grainType'" @>

[<Fact>]
let ``onReminder on a derived grain type fails definition sealing`` () =
    let error =
        throws (fun () ->
            grainFor derivedContract {
                defaultState (fun () -> { count = 0 })
                onReminder "sweep" (TimeSpan.FromMinutes 1.0) (TimeSpan.FromMinutes 5.0) reminderHook
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'DerivableActor'" @>
    test <@ error.Message.Contains "explicit 'grainType'" @>

/// <remarks>
/// Regression control: an explicit grain type plus a durable attachment is unaffected by the new
/// rule -- unchanged from the behavior every other test in the "Persistence attachment" and
/// "Hooks, reminders, and timers" sections above already pins.
/// </remarks>
[<Fact>]
let ``stateFrom with an explicit grain type still seals`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            stateFrom primary
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.GrainTypeName = "def.room" @>
    test <@ definition.Primary.IsSome @>

// ──────────────────────────────────────────────────────────────────────────────
// The specification's own example
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the specification example contract and definition are constructed`` () =
    let contract = Chat.Contracts.RoomApi.contract
    let definition = Chat.Server.Definition.roomDefinition

    test
        <@
            contract.Operations |> Array.map (fun op -> op.OperationId) = [| "join"; "say"; "history"; "typing" |]
        @>

    test <@ contract.Operations.[2].IsReadOnly @>
    test <@ contract.Operations.[3].IsOneWay && contract.Operations.[3].IsAlwaysInterleave @>
    test <@ definition.Handlers.Count = 4 @>
    test <@ definition.Primary.IsSome @>
    test <@ definition.CollectionAge = Some(TimeSpan.FromMinutes 30.0) @>

    let grainId = contract.GrainIdOf(Chat.Contracts.RoomId.create "general")
    let grainTypeText = grainId.Type.ToString()
    let keyText = grainId.Key.ToString()

    test <@ grainTypeText = "chat.room" @>
    test <@ keyText = "general" @>

[<Fact>]
let ``binding without a grain factory fails with a binding diagnostic`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalGrain.ref contract Unchecked.defaultof<IGrainFactory> "general"
            |> ignore)

    test <@ error.Message.Contains "requires a grain factory" @>

// ──────────────────────────────────────────────────────────────────────────────
// Spec 004 item 2 — transactional state attachment
// ──────────────────────────────────────────────────────────────────────────────

type TxState = { total: int64 }

let private txContract =
    grainContract<RoomActor, string, RoomApi> {
        grainType "def.room.tx"
        stringKey
        transactional Orleans.TransactionOption.CreateOrJoin (_.join)
    }

let private plainContract =
    grainContract<RoomActor, string, RoomApi> {
        grainType "def.room.plain"
        stringKey
    }

let private supportedOnlyContract =
    grainContract<RoomActor, string, RoomApi> {
        grainType "def.room.supported"
        stringKey
        transactional Orleans.TransactionOption.Supported (_.join)
    }

let private suppressOnlyContract =
    grainContract<RoomActor, string, RoomApi> {
        grainType "def.room.suppress"
        stringKey
        transactional Orleans.TransactionOption.Suppress (_.join)
    }

let private ledger = TransactionalState.create<TxState> "ledger" "TxStore"
let private audits = TransactionalState.create<TxState> "audits" "TxStore"

[<Fact>]
let ``TransactionalState.create validates its names and stored type`` () =
    let blankName = throws (fun () -> TransactionalState.create<TxState> "  " "TxStore" |> ignore)
    let nulName = throws (fun () -> TransactionalState.create<TxState> "sta\000te" "TxStore" |> ignore)
    let blankStorage = throws (fun () -> TransactionalState.create<TxState> "state" "" |> ignore)
    let nulStorage = throws (fun () -> TransactionalState.create<TxState> "state" "Tx\000Store" |> ignore)

    test <@ blankName.Message.Contains "stateName must be a non-blank string" @>
    test <@ nulName.Message.Contains "must not contain a NUL character" @>
    test <@ blankStorage.Message.Contains "storageName for stateName 'state'" @>
    test <@ nulStorage.Message.Contains "must not contain a NUL character" @>

[<Fact>]
let ``a transactional facet seals with its declared identity`` () =
    let definition =
        grainFor txContract {
            defaultState (fun () -> { count = 0 })
            transactionalStateFrom ledger (fun _ -> { total = 0L })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.TransactionalFacets.Length = 1 @>

    let descriptor = definition.TransactionalFacets.Head.Descriptor

    test <@ descriptor.StateName = "ledger" @>
    test <@ descriptor.StorageName = "TxStore" @>
    test <@ descriptor.StoredType = typeof<TxState> @>

[<Fact>]
let ``transactionalStateFrom requires a descriptor and an initializer`` () =
    let noDescriptor =
        throws (fun () ->
            grainFor txContract {
                defaultState (fun () -> { count = 0 })
                transactionalStateFrom (Unchecked.defaultof<TransactionalStateRef<TxState>>) (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    let noInitializer =
        throws (fun () ->
            grainFor txContract {
                defaultState (fun () -> { count = 0 })
                transactionalStateFrom ledger (Unchecked.defaultof<string -> TxState>)
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ noDescriptor.Message.Contains "requires a TransactionalStateRef value" @>
    test <@ noInitializer.Message.Contains "requires an initializer" @>

[<Fact>]
let ``a repeated transactional state name is rejected`` () =
    let shadow = TransactionalState.create<RoomState> "ledger" "OtherStore"

    let error =
        throws (fun () ->
            grainFor txContract {
                defaultState (fun () -> { count = 0 })
                transactionalStateFrom ledger (fun _ -> { total = 0L })
                transactionalStateFrom shadow (fun _ -> { count = 0 })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "transactional stateName 'ledger' is attached more than once" @>

[<Fact>]
let ``two transactional facets under distinct names are accepted`` () =
    let definition =
        grainFor txContract {
            defaultState (fun () -> { count = 0 })
            transactionalStateFrom ledger (fun _ -> { total = 0L })
            transactionalStateFrom audits (fun _ -> { total = 0L })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.TransactionalFacets.Length = 2 @>

[<Fact>]
let ``a transactional facet with no operation that could reach it is rejected`` () =
    let noneAtAll =
        throws (fun () ->
            grainFor plainContract {
                defaultState (fun () -> { count = 0 })
                transactionalStateFrom ledger (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    // Suppress can never carry a transaction context either, so it does not unlock the facet.
    let suppressOnly =
        throws (fun () ->
            grainFor suppressOnlyContract {
                defaultState (fun () -> { count = 0 })
                transactionalStateFrom ledger (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ noneAtAll.Message.Contains "declares no 'transactional' operation that can carry a transaction context" @>
    test <@ suppressOnly.Message.Contains "declares no 'transactional' operation that can carry a transaction context" @>

[<Fact>]
let ``a Supported operation is enough to reach a transactional facet`` () =
    let definition =
        grainFor supportedOnlyContract {
            defaultState (fun () -> { count = 0 })
            transactionalStateFrom ledger (fun _ -> { total = 0L })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.TransactionalFacets.Length = 1 @>

[<Fact>]
let ``a transactional operation without any transactional facet is accepted`` () =
    // The orchestrator shape: a state-free participant that only drives other grains. Orleans
    // supports it, and this repository's own classic FSharpAtmGrain is the same shape.
    let definition =
        grainFor txContract {
            defaultState (fun () -> { count = 0 })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.TransactionalFacets.IsEmpty @>

[<Fact>]
let ``transactionalStateFrom on a derived grain type fails definition sealing`` () =
    // A transactional state name is part of the ParticipantId Orleans addresses during the commit
    // protocol AND of the storage key, so a grain type that moves when the brand is renamed would
    // orphan it exactly as it orphans persistent state.
    let derived =
        grainContract<Orleans.FSharp.Tests.GrainTypeDerivation.DerivableActor, string, RoomApi> {
            stringKey
            transactional Orleans.TransactionOption.CreateOrJoin (_.join)
        }

    let error =
        throws (fun () ->
            grainFor derived {
                defaultState (fun () -> { count = 0 })
                transactionalStateFrom ledger (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'DerivableActor'" @>
    test <@ error.Message.Contains "'transactionalStateFrom'" @>
    test <@ error.Message.Contains "explicit 'grainType'" @>

[<Fact>]
let ``statelessWorker rejects transactionalStateFrom`` () =
    let error =
        throws (fun () ->
            grainFor txContract {
                defaultState (fun () -> { count = 0 })
                statelessWorker 4
                transactionalStateFrom ledger (fun _ -> { total = 0L })
                handle (_.join) joinHandler
                handle (_.say) sayHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'statelessWorker' with 'transactionalStateFrom'" @>

[<Fact>]
let ``a transactional facet coexists with a persistent one`` () =
    let definition =
        grainFor txContract {
            defaultState (fun () -> { count = 0 })
            stateFrom primary
            usePersistentState audit (fun _ -> { total = 0L })
            transactionalStateFrom ledger (fun _ -> { total = 0L })
            handle (_.join) joinHandler
            handle (_.say) sayHandler
        }

    test <@ definition.Primary.IsSome @>
    test <@ definition.Additional.Length = 1 @>
    test <@ definition.TransactionalFacets.Length = 1 @>
