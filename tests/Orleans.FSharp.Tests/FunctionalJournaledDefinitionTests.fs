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
let private totalHandler _ (state: LedgerState) () = task { return ([]: LedgerEvent list), state.total }

let private fold (state: LedgerState) event =
    match event with
    | Credited amount -> { total = state.total + amount }
    | Reset -> { total = 0m }

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
