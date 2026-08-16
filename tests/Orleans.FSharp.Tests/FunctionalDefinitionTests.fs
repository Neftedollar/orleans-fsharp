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
    grainContract<RoomActor, string, RoomApi> () {
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

// ──────────────────────────────────────────────────────────────────────────────
// Hooks, reminders, and timers
// ──────────────────────────────────────────────────────────────────────────────

let private activateHook _ state = task { return state }
let private deactivateHook _ (_: DeactivationReason) (_: RoomState) = task { return () }
let private reminderHook _ state (_: TickStatus) = task { return state }
let private timerHook _ state = task { return state }

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
