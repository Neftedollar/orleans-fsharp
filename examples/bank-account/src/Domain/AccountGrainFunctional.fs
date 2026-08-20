/// <summary>
/// Functional-runtime equivalent of <c>AccountGrainDef.account</c> in <c>AccountGrain.fs</c> (the
/// <c>eventSourcedGrain { }</c> CE original). Full-depth twin over the SAME domain: the same
/// <c>AccountEvent</c> journal, the same <c>AccountState</c> view, and — literally, by delegation
/// rather than by copy — the same <c>applyEvent</c> fold and the same <c>handleCommand</c> decision
/// function. Nothing about the business rules is restated here; if the classic rules change, this
/// twin changes with them, which is what makes the parity reviewable in-file.
///
/// What DOES change is only the three things a journaled functional definition changes
/// (docs/event-sourcing.md, "Overview"):
///
///   | | <c>eventSourcedGrain { }</c> | <c>journaledGrainFor { }</c> |
///   |---|---|---|
///   | initial state | <c>defaultState (AccountState())</c> | <c>initialEventState</c> (key-aware) |
///   | a handler returns | events only, via <c>handle</c> | <c>events, reply</c> |
///   | the provider name | <c>logConsistencyProvider "LogStorage"</c> | <c>logProvider "LogStorage"</c> |
///
/// Command -> operation mapping, where the shapes differ:
///
///   | classic <c>AccountCommand</c> | functional operation | reply |
///   |---|---|---|
///   | <c>Deposit amount</c> | <c>deposit: decimal -&gt; ...</c> | <c>Ok newBalance</c> / <c>Error refusal</c> |
///   | <c>Withdraw amount</c> | <c>withdraw: decimal -&gt; ...</c> | <c>Ok newBalance</c> / <c>Error refusal</c> |
///   | <c>GetBalance</c> | <c>balance: unit -&gt; ...</c> (<c>readOnly</c>) | the balance |
///   | — | <c>journalVersion</c> (<c>readOnly</c>) | events confirmed so far |
///   | — | <c>recycle</c> | ends the activation, so the next call replays |
///
/// Two shape differences are worth naming, because they are the point of the twin:
///
/// 1. <b>Refusals are typed instead of silent.</b> The classic <c>handleCommand</c> answers every
///    refused command with an empty event list — an overdraft, a zero deposit and a negative
///    withdrawal are indistinguishable to the caller, which then reads back an unchanged balance
///    and has to guess why. The twin calls the very same function and turns its empty list into a
///    named <c>AccountRefusal</c>. The journal is identical either way: a refused command still
///    raises nothing and still performs no storage write at all.
/// 2. <b>Replies are typed instead of boxed.</b> <c>IBankAccountGrain.HandleCommand</c> is
///    <c>AccountCommand -&gt; Task&lt;obj&gt;</c> and returns the boxed <c>AccountState</c>; each
///    operation here has its own argument and its own reply type, checked at the call site.
///
/// <c>journalVersion</c> and <c>recycle</c> have no classic counterpart and are marked as such:
/// together they demonstrate the one claim an event-sourced example exists to make — that the
/// balance is not held in memory but folded back out of the journal after the activation is gone.
/// </summary>
namespace BankAccount.Domain

open System.Threading.Tasks
open Orleans.FSharp

/// <summary>The actor brand of the functional twin. Private constructor: a brand is an identity,
/// never a value anyone constructs.</summary>
type AccountActor = private AccountActor of unit

/// <summary>
/// Why a command was refused. The classic <c>handleCommand</c> collapses all of these into one
/// empty event list; naming them costs nothing and is the difference between a caller that can
/// report "insufficient funds" and one that can only say "nothing happened".
/// </summary>
type AccountRefusal =
    /// <summary>The amount was zero or negative — the classic <c>| Deposit _ -> []</c> and the
    /// <c>amount &gt; 0m</c> half of the withdrawal guard.</summary>
    | NonPositiveAmount of amount: decimal
    /// <summary>The withdrawal exceeded the balance — the <c>state.Balance &gt;= amount</c> half of
    /// the classic withdrawal guard.</summary>
    | InsufficientFunds of balance: decimal * requested: decimal

[<NoEquality; NoComparison>]
type AccountApi =
    { /// <summary>Deposits funds. Raises <c>Deposited</c>, or refuses and raises nothing.</summary>
      deposit: decimal -> Task<Result<decimal, AccountRefusal>>
      /// <summary>Withdraws funds. Raises <c>Withdrawn</c>, or refuses and raises nothing.</summary>
      withdraw: decimal -> Task<Result<decimal, AccountRefusal>>
      /// <summary>The current balance: the fold of the journal, raising no events.</summary>
      balance: unit -> Task<decimal>
      /// <summary>How many events this account's journal has confirmed. No classic counterpart —
      /// it is the journal's own version, which the classic boxed reply never exposed.</summary>
      journalVersion: unit -> Task<int>
      /// <summary>Ends this activation, so the next call has to replay the journal. No classic
      /// counterpart; it exists so the demo can show the balance surviving the activation.</summary>
      recycle: unit -> Task<unit> }

[<RequireQualifiedAccess>]
module AccountApi =

    /// <summary>The log-consistency provider this account's journal lives in. The same name the
    /// classic definition passes to <c>logConsistencyProvider</c>, and the same one
    /// <c>Program.fs</c> registers with <c>AddLogStorageBasedLogConsistencyProvider</c>.</summary>
    [<Literal>]
    let LogProvider = "LogStorage"

    let contract =
        grainContract<AccountActor, string, AccountApi> {
            grainType "bank-account.account.functional"
            version 1
            stringKey

            // Neither query raises an event, so neither needs the write path. On a journaled
            // definition a readOnly handler that DID return an event is refused when it is
            // called -- the runtime throws a diagnostic naming 'readOnly' and appends nothing
            // (pinned by tests/Orleans.FSharp.Integration/FunctionalPhaseEIntegrationTests.fs,
            // "a readOnly operation that raises events is refused"). Returning `[]` here is the
            // contract, not a formality.
            readOnly (_.balance)
            readOnly (_.journalVersion)
        }

    let ref = FunctionalGrain.ref contract

module AccountFunctionalDef =

    /// <summary>
    /// The events a command produces, and the balance those events fold to. Both halves delegate
    /// to <c>AccountGrainDef</c> — the classic decision function and the classic fold — so the
    /// twin cannot drift from the deprecated original by so much as a guard.
    /// </summary>
    let private decide (state: AccountState) (command: AccountCommand) : AccountEvent list * decimal =
        let events = AccountGrainDef.handleCommand state command
        let folded = events |> List.fold AccountGrainDef.applyEvent state
        events, folded.Balance

    /// <summary>
    /// Which named refusal an empty event list stood for. The classic handler does not say, so
    /// this re-derives it from the same two guards it matches on -- and re-derivation is the one
    /// place this twin could silently drift from the original, which is why
    /// <c>tests/Domain.Tests</c> pins it against <c>AccountGrainDef.handleCommand</c> directly
    /// rather than against a restatement of the guards.
    /// </summary>
    /// <param name="state">The account state the refused command was handled against.</param>
    /// <param name="amount">The amount the refused command carried.</param>
    /// <returns>The named reason no event was raised.</returns>
    let refusalFor (state: AccountState) (amount: decimal) : AccountRefusal =
        if amount <= 0m then
            NonPositiveAmount amount
        else
            InsufficientFunds(state.Balance, amount)

    /// <summary>One write operation: run the classic decision, and answer either the new balance
    /// or the named reason no event was raised.</summary>
    let private write
        (state: AccountState)
        (command: AccountCommand)
        (amount: decimal)
        : Task<AccountEvent list * Result<decimal, AccountRefusal>> =
        task {
            match decide state command with
            | [], _ -> return [], Error(refusalFor state amount)
            | events, newBalance -> return events, Ok newBalance
        }

    let account =
        journaledGrainFor AccountApi.contract {
            // The classic definition's `defaultState (AccountState())` is one shared value; a
            // journaled definition seeds per grain instead, so the factory is called with the
            // decoded key. This account opens at zero regardless of key, so the key is ignored.
            initialEventState (fun (_key: string) -> AccountState())

            // The classic fold, verbatim: `apply` and `eventSourcedGrain`'s `apply` are the same
            // 'State -> 'Event -> 'State shape, so the twin passes the very same function value.
            apply AccountGrainDef.applyEvent

            // `logConsistencyProvider "LogStorage"` on the classic definition. `journalStorage` is
            // deliberately omitted: the silo's default IGrainStorage is used, which is this
            // example's `addMemoryStorage "Default"`.
            logProvider AccountApi.LogProvider

            // Printed rather than logged on purpose: it is the only way the demo can SHOW that a
            // fresh activation rebuilt its balance by replaying the journal instead of reading a
            // stored view. A journaled `onActivate` raises no events and returns no state -- on
            // this definition kind the journal is the only thing that can change anything.
            onActivate (fun context _state ->
                task { printfn "  [activation] '%s' replayed to journal version %d" context.key context.journalVersion })

            handle (_.deposit) (fun _context state (amount: decimal) -> write state (Deposit amount) amount)

            handle (_.withdraw) (fun _context state (amount: decimal) -> write state (Withdraw amount) amount)

            // `GetBalance` produced no events in the classic handler either; here that is the
            // handler's whole contract — an empty list means no storage write at all.
            handle (_.balance) (fun _context state () -> task { return ([]: AccountEvent list), state.Balance })

            handle (_.journalVersion) (fun context state () ->
                task { return ([]: AccountEvent list), context.journalVersion })

            handle (_.recycle) (fun context state () ->
                task {
                    context.deactivateOnIdle ()
                    return ([]: AccountEvent list), ()
                })
        }
