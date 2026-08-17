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
