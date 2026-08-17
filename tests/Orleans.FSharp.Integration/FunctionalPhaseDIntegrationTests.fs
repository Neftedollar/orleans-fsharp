/// <summary>
/// Spec 004 item 2 on a live two-silo cluster with Orleans transactions enabled: atomic commit
/// across two functional grains, abort rolling every participant back, the re-execution question
/// answered by counting handler entries, transactional and persistent facets on one definition,
/// and the cross-silo case.
/// </summary>
module Orleans.FSharp.Integration.FunctionalPhaseDIntegrationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalPhaseDFixture

[<Collection("FunctionalPhaseD")>]
type FunctionalPhaseDIntegrationTests(fixture: FunctionalPhaseDFixture) =

    let account key = accountRef fixture.Client key
    let atm key = atmRef fixture.Client key
    let mixed key = mixedRef fixture.Client key

    // ──────────────────────────────────────────────────────────────────────────
    // Step 0 — the seam probe: one transaction, two functional grains
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The commit half of the probe. One transaction created by the orchestrator moves funds
    /// between two functional participants; both states change together.
    /// </summary>
    [<Fact>]
    member _.``an orchestrated transfer commits on both participants``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let source = $"src-{suffix}"
            let target = $"dst-{suffix}"

            do! (account source).deposit 100m
            do! (atm suffix).transfer (source, target, 40m)

            let! sourceBalance = (account source).balance ()
            let! targetBalance = (account target).balance ()

            test <@ sourceBalance = 60m @>
            test <@ targetBalance = 40m @>
        }

    /// <summary>
    /// The abort half of the probe. A failing second participant rolls the first one back:
    /// neither state changes, and the caller sees an Orleans transaction exception rather than a
    /// half-applied transfer.
    /// </summary>
    [<Fact>]
    member _.``a failing participant rolls the whole transaction back``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let source = $"src-{suffix}"
            let target = $"dst-{suffix}"

            do! (account source).deposit 10m
            do! (account target).deposit 5m

            // 40 > 10, so the withdraw participant throws after nothing has been committed.
            let! error =
                Assert.ThrowsAnyAsync<exn>(fun () -> (atm suffix).transfer (source, target, 40m) :> Task)

            let! sourceBalance = (account source).balance ()
            let! targetBalance = (account target).balance ()

            test <@ sourceBalance = 10m @>
            test <@ targetBalance = 5m @>
            test <@ error <> null @>
        }

    /// <summary>
    /// The stronger abort case: both participants really wrote, and then the orchestrator itself
    /// failed. Rolling back a participant that only read proves nothing; this one proves the
    /// commit protocol undoes writes.
    /// </summary>
    [<Fact>]
    member _.``an orchestrator failure after both participants wrote rolls both back``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let left = $"l-{suffix}"
            let right = $"r-{suffix}"

            do! (account left).deposit 7m
            do! (account right).deposit 9m

            let! _ =
                Assert.ThrowsAnyAsync<exn>(fun () ->
                    (atm suffix).failAfterDeposits (left, right, 100m) :> Task)

            let! leftBalance = (account left).balance ()
            let! rightBalance = (account right).balance ()

            test <@ leftBalance = 7m @>
            test <@ rightBalance = 9m @>
        }

    /// <summary>
    /// Both participants are read inside ONE transaction, so the pair is a consistent snapshot
    /// rather than two independent reads.
    /// </summary>
    [<Fact>]
    member _.``one transaction reads both participants``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let left = $"l-{suffix}"
            let right = $"r-{suffix}"

            do! (account left).deposit 3m
            do! (account right).deposit 4m

            let! totals = (atm suffix).totals (left, right)

            test <@ totals = (3m, 4m) @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Re-execution: what Orleans actually does on an abort and under contention
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The normative re-execution claim, measured rather than assumed: an aborted transaction
    /// does NOT re-run the handlers that took part in it. Both participants' handler bodies are
    /// entered exactly once, the transaction aborts, and neither is entered a second time.
    /// </summary>
    /// <remarks>
    /// The counters are read out of band. Silos of a <c>TestCluster</c> share one process, so a
    /// grain call is not a usable way to learn how often a handler ran — the call that would
    /// report it is the one that aborted.
    /// </remarks>
    [<Fact>]
    member _.``an aborted transaction runs each participant exactly once``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let left = $"l-{suffix}"
            let right = $"r-{suffix}"

            PhaseDCounters.reset $"deposit:{left}"
            PhaseDCounters.reset $"deposit:{right}"

            let! _ =
                Assert.ThrowsAnyAsync<exn>(fun () ->
                    (atm suffix).failAfterDeposits (left, right, 25m) :> Task)

            // Give any hypothetical retry a window to appear before the counts are read.
            do! Task.Delay 500

            test <@ PhaseDCounters.count $"deposit:{left}" = 1 @>
            test <@ PhaseDCounters.count $"deposit:{right}" = 1 @>

            let! leftBalance = (account left).balance ()
            let! rightBalance = (account right).balance ()

            test <@ leftBalance = 0m @>
            test <@ rightBalance = 0m @>
        }

    /// <summary>
    /// The control for the counter itself: a retry is the APPLICATION's action, and when the
    /// application performs one the counter moves. A counter that never moved would make the
    /// test above prove nothing.
    /// </summary>
    [<Fact>]
    member _.``an application retry is what runs a participant a second time``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let left = $"l-{suffix}"
            let right = $"r-{suffix}"

            PhaseDCounters.reset $"deposit:{left}"

            let attempt () =
                Assert.ThrowsAnyAsync<exn>(fun () ->
                    (atm suffix).failAfterDeposits (left, right, 25m) :> Task)

            let! _ = attempt ()
            test <@ PhaseDCounters.count $"deposit:{left}" = 1 @>

            let! _ = attempt ()
            test <@ PhaseDCounters.count $"deposit:{left}" = 2 @>

            // Two attempts, both aborted: still nothing committed.
            let! leftBalance = (account left).balance ()
            test <@ leftBalance = 0m @>
        }

    /// <summary>
    /// Two transactions that really contend for one transactional state. Whatever Orleans decides
    /// — serialize them, or abort the one that cannot take the lock in time — the invariant is the
    /// same and is asserted as a computed verdict: every handler body ran exactly once, and the
    /// committed balance is exactly the sum of the calls that returned successfully.
    /// </summary>
    [<Fact>]
    member _.``concurrent transactions on one state each run once and apply once``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let key = $"c-{suffix}"
            let gate = $"gate-{suffix}"

            PhaseDCounters.reset $"slowDeposit:{key}"
            PhaseDCounters.reset $"deposit:{key}"
            PhaseDGates.reset gate

            // The first transaction takes the write lock and parks on the gate.
            let slow = (account key).slowDeposit (10m, gate)

            // Wait until it has really entered the handler, then start the contender.
            let mutable spins = 0

            while PhaseDCounters.count $"slowDeposit:{key}" = 0 && spins < 200 do
                do! Task.Delay 25
                spins <- spins + 1

            test <@ PhaseDCounters.count $"slowDeposit:{key}" = 1 @>

            let fast = (account key).deposit 20m

            // Hold the lock past the configured 2-second lock-acquire timeout, then release.
            do! Task.Delay 3000
            PhaseDGates.release gate

            let outcomeOf (work: Task<unit>) =
                task {
                    try
                        do! work
                        return true
                    with _ ->
                        return false
                }

            let! slowCommitted = outcomeOf slow
            let! fastCommitted = outcomeOf fast

            // Neither handler body ran more than once, whichever of them the runtime aborted.
            test <@ PhaseDCounters.count $"slowDeposit:{key}" = 1 @>
            test <@ PhaseDCounters.count $"deposit:{key}" = 1 @>

            let expected =
                (if slowCommitted then 10m else 0m) + (if fastCommitted then 20m else 0m)

            let! balance = (account key).balance ()
            test <@ balance = expected @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Transactional and persistent facets on one definition
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A definition may carry both kinds of facet. The transactional one is written from a
    /// transaction-scoped operation; the persistent one is written from an ordinary one; neither
    /// disturbs the other.
    /// </summary>
    [<Fact>]
    member _.``transactional and persistent facets coexist on one definition``() =
        task {
            let key = Guid.NewGuid().ToString "N"

            do! (mixed key).bump 3
            do! (mixed key).bump 4
            do! (mixed key).note "first"
            do! (mixed key).note "second"

            let! total = (mixed key).total ()
            let! notes = (mixed key).notes ()

            test <@ total = 7 @>
            test <@ notes = [ "first"; "second" ] @>
        }

    /// <summary>
    /// The rule that keeps an aborted transaction from leaving an activation half-updated: inside
    /// a transaction-scoped operation the persistent-state facade refuses every mutation, naming
    /// the reason.
    /// </summary>
    [<Fact>]
    member _.``a transaction-scoped operation cannot write persistent state``() =
        task {
            let key = Guid.NewGuid().ToString "N"

            let! outcome = (mixed key).bumpPersistent 5

            test <@ outcome <> "written" @>
            test <@ outcome.Contains "state-neutral" @>

            // The transactional half still committed: the refusal is scoped to the persistent facet.
            let! total = (mixed key).total ()
            test <@ total = 5 @>

            let! notes = (mixed key).notes ()
            test <@ notes = ([]: string list) @>
        }

    /// <summary>
    /// The same rule for the primary state: a transaction-scoped handler's replacement state is
    /// discarded, exactly as a <c>readOnly</c> handler's is.
    /// </summary>
    [<Fact>]
    member _.``a transaction-scoped operation does not publish primary state``() =
        task {
            let key = Guid.NewGuid().ToString "N"

            do! (mixed key).bumpAndPublish 9

            let! published = (mixed key).published ()
            let! total = (mixed key).total ()

            test <@ published = 0 @>
            test <@ total = 9 @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Negative controls on the facade
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An operation with no <c>transactional</c> declaration cannot touch a transactional facet.
    /// The runtime says so by name instead of letting Orleans throw "did you forget a
    /// [Transaction] attribute?", which names an attribute this API does not have.
    /// </summary>
    [<Fact>]
    member _.``a non-transactional operation cannot read transactional state``() =
        task {
            let key = Guid.NewGuid().ToString "N"

            let! error =
                Assert.ThrowsAnyAsync<exn>(fun () -> (account key).unsafeBalance () :> Task)

            test <@ error.Message.Contains "never runs inside an Orleans transaction" @>
        }

    /// <summary>
    /// A <c>readOnly</c> transactional operation can read but not update.
    /// </summary>
    [<Fact>]
    member _.``a read-only transactional operation cannot update``() =
        task {
            let key = Guid.NewGuid().ToString "N"

            do! (account key).deposit 12m

            let! seen = (account key).peek ()
            test <@ seen = 12m @>

            let! outcome = (account key).peekAndWrite ()
            test <@ outcome <> "written" @>
            test <@ outcome.Contains "readOnly" @>

            let! after = (account key).peek ()
            test <@ after = 12m @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Cross-silo
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A transaction whose participants live on two different silos. The keys are chosen by
    /// asking the activations where they are, so the test is a real cross-silo case rather than a
    /// hopeful one; if two silos never separate, the test says so instead of passing quietly.
    /// </summary>
    [<Fact>]
    member _.``a transaction commits across two silos``() =
        task {
            let suffix = Guid.NewGuid().ToString "N"
            let mutable found = None
            let mutable index = 0

            while found.IsNone && index < 40 do
                let left = $"x{index}-{suffix}"
                let right = $"y{index}-{suffix}"
                let! leftSilo = (account left).whereAmI ()
                let! rightSilo = (account right).whereAmI ()

                if leftSilo <> rightSilo then
                    found <- Some(left, right)

                index <- index + 1

            test <@ found.IsSome @>

            let left, right = found.Value
            do! (account left).deposit 50m
            do! (atm suffix).transfer (left, right, 30m)

            let! leftBalance = (account left).balance ()
            let! rightBalance = (account right).balance ()

            test <@ leftBalance = 20m @>
            test <@ rightBalance = 30m @>
        }
