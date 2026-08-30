/// <summary>
/// Spec 004 item 3: what the <c>journaledGrainFor</c> computation expression accumulates, and
/// what it refuses to seal.
/// </summary>
/// <remarks>
/// Every rejection here is mutation-checked: the test that proves a rule fires is paired with the
/// definition that differs from it in exactly the one respect the rule is about and seals cleanly.
/// A rejection test on its own cannot tell "the rule fired" from "the definition was broken for
/// some other reason".
/// </remarks>
module Orleans.FSharp.Tests.FunctionalJournaledDefinitionTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.FSharp
open Orleans.Runtime

type LedgerActor = private LedgerActor of unit
type TransactionalLedgerActor = private TransactionalLedgerActor of unit

[<NoEquality; NoComparison>]
type LedgerApi =
    { credit: decimal -> Task<unit>
      total: unit -> Task<decimal> }

type LedgerState = { total: decimal }

type LedgerEvent =
    | Credited of decimal
    | Reset

let private contract =
    grainContract<LedgerActor, string, LedgerApi> {
        grainType "journal.ledger"
        stringKey
    }

let private throws (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

let private creditHandler _ (_: LedgerState) (amount: decimal) = task { return [ Credited amount ], () }

let private totalHandler _ (state: LedgerState) () =
    task { return ([]: LedgerEvent list), state.total }

let private fold (state: LedgerState) event =
    match event with
    | Credited amount -> { total = state.total + amount }
    | Reset -> { total = 0m }

let private resolveCustomStorage
    (_: IServiceProvider)
    : IFunctionalJournalStorage<string, LedgerState, LedgerEvent> =
    Unchecked.defaultof<_>

/// The reference definition every rejection below is a one-change mutation of.
let private complete () =
    journaledGrainFor contract {
        initialEventState (fun (_: string) -> { total = 0m })
        apply fold
        logProvider "LogStorage"
        handle (_.credit) creditHandler
        handle (_.total) totalHandler
    }

// ──────────────────────────────────────────────────────────────────────────────
// Sealing
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a complete journaled definition seals with one handler per API field`` () =
    let definition = complete ()

    test <@ definition.GrainTypeName = "journal.ledger" @>
    test <@ definition.Handlers.Count = 2 @>
    test <@ definition.Journal.IsSome @>
    test <@ definition.Journal.Value.ProviderName = "LogStorage" @>
    test <@ definition.Journal.Value.StorageName.IsNone @>

[<Fact>]
let ``initialEventState receives the domain key and apply is the declared fold`` () =
    let definition =
        journaledGrainFor contract {
            initialEventState (fun (key: string) -> { total = decimal key.Length })
            apply fold
            logProvider "LogStorage"
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.Initial "abcd" = { total = 4m } @>
    test <@ definition.Apply { total = 10m } (Credited 5m) = { total = 15m } @>
    test <@ definition.Apply { total = 10m } Reset = { total = 0m } @>

[<Fact>]
let ``journalStorage names the storage the provider writes through`` () =
    let definition =
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> { total = 0m })
            apply fold
            logProvider "LogStorage"
            journalStorage "Ledgers"
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.Journal.Value.StorageName = Some "Ledgers" @>

[<Fact>]
let ``customStorage and an explicit snapshot policy are retained by the sealed definition`` () =
    let definition =
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> { total = 0m })
            apply fold
            logProvider "CustomStorage"
            customStorage resolveCustomStorage
            snapshotPolicy (FunctionalJournalSnapshotPolicy.Every 100)
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.CustomStorage.IsSome @>
    test <@ definition.SnapshotPolicy.IsSome @>

    match definition.SnapshotPolicy.Value with
    | FunctionalJournalSnapshotPolicy.Every eventCount -> test <@ eventCount = 100 @>
    | policy -> failwith $"unexpected policy {policy.GetType().Name}"

[<Fact>]
let ``customStorage inherits the silo rule when snapshotPolicy is absent`` () =
    let definition =
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> { total = 0m })
            apply fold
            logProvider "CustomStorage"
            customStorage resolveCustomStorage
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.CustomStorage.IsSome @>
    test <@ definition.SnapshotPolicy.IsNone @>

[<Fact>]
let ``customStorage rejects an unused journalStorage name`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "CustomStorage"
                journalStorage "Unused"
                customStorage resolveCustomStorage
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "both 'customStorage' and 'journalStorage'" @>
    test <@ error.Message.Contains "would never be used" @>

[<Fact>]
let ``snapshotPolicy validates its interval and requires customStorage`` () =
    let invalidInterval =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "CustomStorage"
                customStorage resolveCustomStorage
                snapshotPolicy (FunctionalJournalSnapshotPolicy.Every 0)
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let missingStorage =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                snapshotPolicy FunctionalJournalSnapshotPolicy.Disabled
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ invalidInterval.Message.Contains "requires a positive event count" @>
    test <@ missingStorage.Message.Contains "but no 'customStorage'" @>

[<Fact>]
let ``customStorage and snapshotPolicy are singleton declarations`` () =
    let duplicateStorage =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "CustomStorage"
                customStorage resolveCustomStorage
                customStorage resolveCustomStorage
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let duplicatePolicy =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "CustomStorage"
                customStorage resolveCustomStorage
                snapshotPolicy FunctionalJournalSnapshotPolicy.Disabled
                snapshotPolicy FunctionalJournalSnapshotPolicy.Inherit
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ duplicateStorage.Message.Contains "'customStorage' is declared more than once" @>
    test <@ duplicatePolicy.Message.Contains "'snapshotPolicy' is declared more than once" @>

[<Fact>]
let ``journaled definitions admit event-producing lifecycle and delivery hooks`` () =
    let timerOptions =
        GrainTimerCreationOptions(
            TimeSpan.FromSeconds 1.0,
            TimeSpan.FromSeconds 5.0,
            Interleave = true,
            KeepAlive = true
        )

    let definition =
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> { total = 0m })
            apply fold
            logProvider "LogStorage"
            onActivate (fun _ _ -> task { return () })
            onDeactivate (fun _ _ _ -> task { return () })
            onReminder "close-day" TimeSpan.Zero (TimeSpan.FromHours 24.0) (fun _ _ _ -> task { return [ Reset ] })
            onTimer "interest" timerOptions (fun _ _ -> task { return [ Credited 1m ] })
            onStream "Memory" "ledger-events" (fun _ _ (_: string) -> task { return [ Credited 2m ] })
            onBroadcast "Broadcast" "ledger-events" (fun _ _ (_: int) -> task { return [ Credited 3m ] })
            onTentativeStateChanged (fun _ _ -> ())
            onStateChanged (fun _ _ -> ())
            onConnectionIssue (fun _ _ _ -> ())
            onConnectionIssueResolved (fun _ _ _ -> ())
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.Reminders.Length = 1 @>
    test <@ definition.Reminders.Head.Name = "close-day" @>
    test <@ definition.Timers.Length = 1 @>
    test <@ definition.Timers.Head.Interleave @>
    test <@ definition.Timers.Head.KeepAlive @>
    test <@ definition.StreamBindings.Length = 2 @>
    test <@ definition.StreamBindings.[0].IsStream @>
    test <@ not definition.StreamBindings.[1].IsStream @>
    test <@ definition.OnActivate.IsSome @>
    test <@ definition.OnDeactivate.IsSome @>
    test <@ definition.OnTentativeStateChanged.IsSome @>
    test <@ definition.OnStateChanged.IsSome @>
    test <@ definition.OnConnectionIssue.IsSome @>
    test <@ definition.OnConnectionIssueResolved.IsSome @>

[<Fact>]
let ``duplicate journaled delivery bindings are rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onStream "Memory" "ledger-events" (fun _ _ (_: string) -> task { return [ Reset ] })
                onStream "Memory" "ledger-events" (fun _ _ (_: string) -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'onStream' is declared more than once" @>

[<Fact>]
let ``journaled reminder declarations validate identity and schedule`` () =
    let blank =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onReminder " " TimeSpan.Zero (TimeSpan.FromMinutes 1.0) (fun _ _ _ -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let duplicate =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onReminder "close-day" TimeSpan.Zero (TimeSpan.FromMinutes 1.0) (fun _ _ _ -> task { return [ Reset ] })
                onReminder "close-day" TimeSpan.Zero (TimeSpan.FromMinutes 1.0) (fun _ _ _ -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let negativeDueTime =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onReminder "close-day" (TimeSpan.FromTicks -1L) (TimeSpan.FromMinutes 1.0) (fun _ _ _ -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let nonPositivePeriod =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onReminder "close-day" TimeSpan.Zero TimeSpan.Zero (fun _ _ _ -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ blank.Message.Contains "blank name" @>
    test <@ duplicate.Message.Contains "declared more than once" @>
    test <@ negativeDueTime.Message.Contains "dueTime >= 0" @>
    test <@ nonPositivePeriod.Message.Contains "period > 0" @>

[<Fact>]
let ``journaled timer and delivery declarations validate their identities`` () =
    let options =
        GrainTimerCreationOptions(TimeSpan.Zero, TimeSpan.FromMinutes 1.0)

    let blankTimer =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onTimer " " options (fun _ _ -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let duplicateTimer =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onTimer "close-day" options (fun _ _ -> task { return [ Reset ] })
                onTimer "close-day" options (fun _ _ -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let blankProvider =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onStream " " "ledger-events" (fun _ _ (_: string) -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let blankNamespace =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onBroadcast "Broadcast" " " (fun _ _ (_: string) -> task { return [ Reset ] })
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ blankTimer.Message.Contains "blank name" @>
    test <@ duplicateTimer.Message.Contains "declared more than once" @>
    test <@ blankProvider.Message.Contains "blank provider name" @>
    test <@ blankNamespace.Message.Contains "blank namespace" @>

[<Fact>]
let ``journal state and connection callbacks are singletons`` () =
    let duplicateTentative =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onTentativeStateChanged (fun _ _ -> ())
                onTentativeStateChanged (fun _ _ -> ())
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let duplicateConfirmed =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onStateChanged (fun _ _ -> ())
                onStateChanged (fun _ _ -> ())
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let duplicateIssue =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onConnectionIssue (fun _ _ _ -> ())
                onConnectionIssue (fun _ _ _ -> ())
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    let duplicateResolved =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                onConnectionIssueResolved (fun _ _ _ -> ())
                onConnectionIssueResolved (fun _ _ _ -> ())
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ duplicateTentative.Message.Contains "'onTentativeStateChanged' is declared more than once" @>
    test <@ duplicateConfirmed.Message.Contains "'onStateChanged' is declared more than once" @>
    test <@ duplicateIssue.Message.Contains "'onConnectionIssue' is declared more than once" @>
    test <@ duplicateResolved.Message.Contains "'onConnectionIssueResolved' is declared more than once" @>

// ──────────────────────────────────────────────────────────────────────────────
// Rejections
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// The mutation control is <c>complete</c>, which differs only by naming a provider.
/// </remarks>
[<Fact>]
let ``a journaled definition without logProvider is rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "does not declare 'logProvider'" @>
    // The control seals.
    test <@ (complete ()).Journal.IsSome @>

[<Fact>]
let ``a repeated logProvider is rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                logProvider "StateStorage"
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'logProvider' is declared more than once" @>

[<Fact>]
let ``a blank logProvider is rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "  "
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "must be a non-blank name" @>

[<Fact>]
let ``journalStorage before logProvider is rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                journalStorage "Ledgers"
                logProvider "LogStorage"
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "must follow 'logProvider'" @>

[<Fact>]
let ``a missing handler is rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                handle (_.credit) creditHandler
            }
            |> ignore)

    test <@ error.Message.Contains "has no handler for API field(s) total" @>

/// <remarks>
/// A journal's storage key contains the grain type name, so a derived one would orphan the whole
/// journal on a brand rename rather than a single record. The control is <c>complete</c>, whose
/// contract declares <c>grainType</c> explicitly and seals.
/// </remarks>
[<Fact>]
let ``a journaled definition over a contract with a derived grain type is rejected`` () =
    // A namespace-scoped brand, because a brand declared inside an F# module is CLR-nested and
    // the contract layer refuses to derive a grain type from one at all — which would make this
    // test pass for the wrong reason.
    let derived =
        grainContract<Orleans.FSharp.Tests.GrainTypeDerivation.DerivableActor, string, LedgerApi> { stringKey }

    let error =
        throws (fun () ->
            journaledGrainFor derived {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "derives 'grainType' from the actor brand" @>
    test <@ error.Message.Contains "orphan every stored event" @>

/// <remarks>
/// The mechanism, not a policy: an Orleans log-view adaptor registers nothing with the transaction
/// manager, so events confirmed inside a transaction survive its abort.
/// </remarks>
[<Fact>]
let ``a journaled definition over a transactional contract is rejected`` () =
    let transactional =
        grainContract<TransactionalLedgerActor, string, LedgerApi> {
            grainType "journal.transactional"
            stringKey
            transactional Orleans.TransactionOption.CreateOrJoin (_.credit)
        }

    let error =
        throws (fun () ->
            journaledGrainFor transactional {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "is not a transaction participant" @>
    test <@ error.Message.Contains "'credit'" @>

/// <remarks>
/// <c>statelessWorker</c> is not an operation of this builder at all, so the rejection is a
/// compile error rather than a sealing one; the sealing rule exists for a definition value that
/// reached registration another way. What is asserted here is the positive half: an ordinary
/// placement strategy IS accepted, so the journaled kind is not simply placement-free.
/// </remarks>
[<Fact>]
let ``placement is accepted on a journaled definition`` () =
    let definition =
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> { total = 0m })
            apply fold
            logProvider "LogStorage"
            placement PlacementStrategy.PreferLocal
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.Placement.IsSome @>

[<Fact>]
let ``collectionAge is accepted once and rejected twice`` () =
    let definition =
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> { total = 0m })
            apply fold
            logProvider "LogStorage"
            collectionAge (TimeSpan.FromMinutes 5.0)
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.CollectionAge = Some(TimeSpan.FromMinutes 5.0) @>

    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                collectionAge (TimeSpan.FromMinutes 5.0)
                collectionAge (TimeSpan.FromMinutes 6.0)
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "'collectionAge' is declared more than once" @>

[<Fact>]
let ``a repeated handler for one API field is rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor contract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                handle (_.credit) creditHandler
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
            }
            |> ignore)

    test <@ error.Message.Contains "already has a handler" @>

// ──────────────────────────────────────────────────────────────────────────────
// handleQuery — a reply-only handler, admitted only on a readOnly operation
// ──────────────────────────────────────────────────────────────────────────────

/// `total` is the readOnly operation; `credit` is deliberately left ordinary, so the same contract
/// carries both arms of the rule.
let private queryContract =
    grainContract<LedgerActor, string, LedgerApi> {
        grainType "journal.ledger.query"
        stringKey
        readOnly (_.total)
    }

let private totalQuery _ (state: LedgerState) () = task { return state.total }

/// The reference definition the rejections below differ from in exactly one respect.
let private queried () =
    journaledGrainFor queryContract {
        initialEventState (fun (_: string) -> { total = 0m })
        apply fold
        logProvider "LogStorage"
        handle (_.credit) creditHandler
        handleQuery (_.total) totalQuery
    }

/// <summary>
/// The stored handler is read back as an ordinary <c>JournaledHandler</c> and invoked, because that
/// -- not the map count -- is what proves the wrapper is the shape the dispatch path unboxes, and
/// that the event list it raises is empty. The context is null: the wrapper forwards it untouched
/// and `totalQuery` ignores it.
/// </summary>
[<Fact>]
let ``handleQuery on a readOnly field stores a handler that replies and raises nothing`` () =
    let definition = queried ()

    test <@ definition.Handlers.Count = 2 @>

    let handler =
        definition.Handlers.[1]
        |> unbox<JournaledHandler<LedgerActor, string, LedgerState, LedgerEvent, unit, decimal>>

    let events, reply =
        handler Unchecked.defaultof<FunctionalGrainContext<LedgerActor, string>> { total = 12m } ()
        |> _.GetAwaiter().GetResult()

    test <@ events = ([]: LedgerEvent list) @>
    test <@ reply = 12m @>

[<Fact>]
let ``handleQuery on a field that is not readOnly is rejected`` () =
    let error =
        throws (fun () ->
            journaledGrainFor queryContract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                handleQuery (_.credit) (fun _ (_: LedgerState) (_: decimal) -> task { return () })
                handleQuery (_.total) totalQuery
            }
            |> ignore)

    test <@ error.Message.Contains "'handleQuery' binds API field 'credit'" @>
    test <@ error.Message.Contains "'journal.ledger.query'" @>
    test <@ error.Message.Contains "is not declared 'readOnly'" @>
    test <@ error.Message.Contains "Declare the operation 'readOnly' in the contract" @>
    test <@ error.Message.Contains "use 'handle' and return the events explicitly" @>
    // The control seals: `total` differs from `credit` only by carrying the readOnly declaration.
    test <@ (queried ()).Handlers.Count = 2 @>

[<Fact>]
let ``handle and handleQuery on the same field collide as a duplicate handler`` () =
    let error =
        throws (fun () ->
            journaledGrainFor queryContract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                handle (_.credit) creditHandler
                handle (_.total) totalHandler
                handleQuery (_.total) totalQuery
            }
            |> ignore)

    test <@ error.Message.Contains "API field 'total'" @>
    test <@ error.Message.Contains "already has a handler" @>

/// <remarks>
/// Coverage is the seal-time rule `handleQuery` must not slip past: it stores into the same handler
/// map `handle` does, so a field bound by it is covered. The negative arm removes only that binding.
/// </remarks>
[<Fact>]
let ``handleQuery counts towards handler coverage`` () =
    test <@ (queried ()).Handlers.ContainsKey 1 @>

    let error =
        throws (fun () ->
            journaledGrainFor queryContract {
                initialEventState (fun (_: string) -> { total = 0m })
                apply fold
                logProvider "LogStorage"
                handle (_.credit) creditHandler
            }
            |> ignore)

    test <@ error.Message.Contains "has no handler for API field(s) total" @>

[<Fact>]
let ``journaled definitions register activation migration participant factories before replay`` () =
    let factory: FunctionalMigrationParticipantFactory<LedgerActor, string> =
        fun _ -> ActivationMigration.participant ignore ignore

    let definition =
        journaledGrainFor contract {
            initialEventState (fun (_: string) -> { total = 0m })
            apply fold
            logProvider "LogStorage"
            migrationParticipant factory
            handle (_.credit) creditHandler
            handle (_.total) totalHandler
        }

    test <@ definition.MigrationParticipants.Length = 1 @>
    Assert.Same(box factory, box definition.MigrationParticipants.Head)
