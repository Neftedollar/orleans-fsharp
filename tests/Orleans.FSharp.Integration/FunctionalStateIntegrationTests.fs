/// <summary>
/// Spec 003 Phase 4 in a real two-silo cluster: persistent facets created early enough for the
/// stock SetupState load, explicit-only storage traffic, primary-state publication, activation
/// and deactivation ordering, and the invocation-bound state facade.
/// </summary>
module Orleans.FSharp.Integration.FunctionalStateIntegrationTests

open System
open System.Threading.Tasks
open Orleans.FSharp.Integration.FunctionalStateFixture
open Xunit

/// <summary>Split a probe report of the form <c>a=1|b=2</c> into its fields.</summary>
let private fields (report: string) =
    report.Split '|'
    |> Array.map (fun part ->
        match part.IndexOf '=' with
        | -1 -> part, ""
        | index -> part.Substring(0, index), part.Substring(index + 1))
    |> Map.ofArray

let private field (report: string) (name: string) =
    match (fields report).TryFind name with
    | Some value -> value
    | None -> failwith $"field '{name}' is missing from report '{report}'"

/// <summary>
/// A call which must eventually succeed. An activation which failed is torn down asynchronously,
/// so the first call made after a fault is disarmed can still reach the dying one.
/// </summary>
let private eventually (call: unit -> Task<'T>) =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        let mutable result = Unchecked.defaultof<'T>
        let mutable succeeded = false
        let mutable last: exn = null

        while not succeeded && DateTime.UtcNow < deadline do
            try
                let! value = call ()
                result <- value
                succeeded <- true
            with error ->
                last <- error
                do! Task.Delay 100

        if not succeeded then
            raise (InvalidOperationException("the call never succeeded", last))

        return result
    }

[<Collection("FunctionalStateCluster")>]
type FunctionalStateTests(fixture: FunctionalStateClusterFixture) =

    let key (prefix: string) = $"{prefix}-{Guid.NewGuid():N}"

    // ──────────────────────────────────────────────────────────────────────────
    // Facet creation, initialization, and the absence of runtime-issued traffic
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// Spec "State initialization rules": a holder with <c>RecordExists = false</c> receives its
    /// initializer result "without writing it". The instrumented providers therefore have to
    /// show the two stock SetupState reads and nothing else — the strongest available proof that
    /// facet creation happened early enough AND that the runtime adds no write.
    /// </remarks>
    [<Fact>]
    member _.``missing state is initialized but never written``() =
        task {
            let name = key "fresh"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! snapshot = api.snapshot ()

            // The onActivate hook observed the initialized (empty) state and published its own
            // replacement in memory.
            Assert.Equal(Some "v0:[]", StateProbe.tryGet $"activate:{grainId}")
            Assert.Equal("v0:[activated]", snapshot)

            let calls = StorageLog.forGrain grainId
            Assert.Equal<string list>([ "read"; "read" ], calls |> List.map (fun call -> call.Operation))

            Assert.Equal<string list>(
                [ "ledger"; "audit" ] |> List.sort,
                calls |> List.map (fun call -> call.StateName) |> List.sort
            )
        }

    /// <remarks>
    /// "If no record is ever written, a later activation runs the corresponding initializer
    /// again." The second activation must therefore see an empty holder once more, and still no
    /// write may appear.
    /// </remarks>
    [<Fact>]
    member _.``an unwritten holder runs its initializer again on the next activation``() =
        task {
            let name = key "reinit"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! _ = api.append "only-in-memory"
            let! before = api.snapshot ()
            Assert.Equal("v1:[activated,only-in-memory]", before)

            do! fixture.Recycle name

            Assert.Equal(Some "v0:[]", StateProbe.tryGet $"activate:{grainId}")
            let! after = api.snapshot ()
            Assert.Equal("v0:[activated]", after)

            Assert.Equal(0, StorageLog.countFor grainId "write")
            Assert.Equal(0, StorageLog.countFor grainId "clear")
            Assert.Equal(4, StorageLog.countFor grainId "read")
        }

    /// <remarks>
    /// Spec "State publication": "A successful sequential handler return assigns its returned
    /// state to that holder. This publication never calls WriteStateAsync."
    /// </remarks>
    [<Fact>]
    member _.``a handler return publishes in memory and performs zero writes``() =
        task {
            let name = key "publish"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! first = api.append "a"
            let! second = api.append "b"
            Assert.Equal(1, first)
            Assert.Equal(2, second)

            let! snapshot = api.snapshot ()
            Assert.Equal("v2:[activated,a,b]", snapshot)
            Assert.Equal(0, StorageLog.countFor grainId "write")
        }

    /// <remarks>
    /// The spec's explicit example: a handler which enters with <c>A</c>, writes snapshot
    /// <c>X</c>, and returns <c>Y</c> "leaves X in storage and Y as the next call's primary
    /// state". Exactly one write may happen.
    /// </remarks>
    [<Fact>]
    member _.``a handler writing X and returning Y leaves X in storage and Y in memory``() =
        task {
            let name = key "axy"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! written = api.writeThenReturn "X|Y"
            Assert.Equal("v1:[activated,X]", written)

            // Y is the in-memory primary state…
            let! inMemory = api.snapshot ()
            Assert.Equal("v2:[activated,Y]", inMemory)
            Assert.Equal(1, StorageLog.countFor grainId "write")

            // …while the durable record still holds X, as the next activation proves.
            do! fixture.Recycle name
            Assert.Equal(Some "v1:[activated,X]", StateProbe.tryGet $"activate:{grainId}")

            let! reloaded = api.snapshot ()
            Assert.Equal("v1:[activated,X,activated]", reloaded)
            Assert.Equal(1, StorageLog.countFor grainId "write")
        }

    /// <remarks>
    /// "If a handler fails, its unreturned replacement is not assigned, but any explicit State
    /// setter, successful storage call, or external effect which already occurred remains."
    /// </remarks>
    [<Fact>]
    member _.``a handler failing after an explicit write leaves the write committed``() =
        task {
            let name = key "failafterwrite"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.writeThenFail "F" :> Task)
            Assert.Contains("handler failed after writing F", error.Message)

            // The explicit setter already replaced the authoritative holder; the runtime never
            // rolls back, retries, or reloads.
            let! inMemory = api.snapshot ()
            Assert.Equal("v1:[activated,F]", inMemory)
            Assert.Equal(1, StorageLog.countFor grainId "write")
            Assert.Equal(0, StorageLog.countFor grainId "clear")

            do! fixture.Recycle name
            Assert.Equal(Some "v1:[activated,F]", StateProbe.tryGet $"activate:{grainId}")
        }

    /// <remarks>
    /// "A read replaces that facet's State; for the primary facet this changes the authoritative
    /// in-memory holder immediately, although the handler's already-bound state argument remains
    /// its turn-entry value. A later successful handler return can assign the holder again."
    /// </remarks>
    [<Fact>]
    member _.``an explicit read replaces the holder while the bound state keeps its turn value``() =
        task {
            let name = key "reload"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "durable"
            let! _ = api.append "memory-only"
            let! beforeReload = api.snapshot ()
            Assert.Equal("v2:[activated,durable,memory-only]", beforeReload)

            let! report = api.reload ()
            Assert.Equal("v2:[activated,durable,memory-only]", field report "bound")
            Assert.Equal("v1:[activated,durable]", field report "holder")

            // The handler returned its turn-entry value, so publication assigned the holder
            // again, over the reloaded record.
            let! afterReload = api.snapshot ()
            Assert.Equal("v2:[activated,durable,memory-only]", afterReload)

            Assert.Equal(1, StorageLog.countFor grainId "write")

            // The reload replaced ONLY the selected holder: two SetupState reads plus exactly
            // one application-issued read on the ledger facet, and nothing extra on the audit
            // facet, whose value is untouched.
            let reads =
                StorageLog.forGrain grainId
                |> List.filter (fun call -> call.Operation = "read")
                |> List.map (fun call -> call.StateName)

            Assert.Equal<string list>([ "ledger"; "audit"; "ledger" ], reads)

            let! audit = api.auditPeek ()
            Assert.Equal(0, audit)
        }

    [<Fact>]
    member _.``an explicit clear follows the provider's state-buffer semantics``() =
        task {
            let name = key "clear"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "kept"
            do! api.clearNow ()
            Assert.Equal(1, StorageLog.countFor grainId "clear")

            // The record is gone, so the next activation initializes instead of loading.
            do! fixture.Recycle name
            Assert.Equal(Some "v0:[]", StateProbe.tryGet $"activate:{grainId}")
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Multiple providers
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// Spec: repeatable <c>usePersistentState</c> "loads independently typed holders from
    /// independently named providers before onActivate".
    /// </remarks>
    [<Fact>]
    member _.``two providers hold independent states``() =
        task {
            let name = key "twoproviders"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "primary-only"
            do! api.auditWrite 7

            let! ledger = api.snapshot ()
            let! audit = api.auditPeek ()
            Assert.Equal("v1:[activated,primary-only]", ledger)
            Assert.Equal(7, audit)

            let writes =
                StorageLog.forGrain grainId
                |> List.filter (fun call -> call.Operation = "write")

            Assert.Equal<string list>([ "ledger"; "audit" ], writes |> List.map (fun call -> call.StateName))

            // Both holders reload independently, and the additional one is already loaded when
            // the activation hook runs.
            do! fixture.Recycle name
            Assert.Equal(Some "7", StateProbe.tryGet $"activate-audit:{grainId}")

            let! reloadedAudit = api.auditPeek ()
            Assert.Equal(7, reloadedAudit)
        }

    /// <remarks>
    /// "Writes across descriptors are not atomic: if the second call fails, the first remains
    /// committed." The failure must reach the caller and nothing may repair it.
    /// </remarks>
    [<Fact>]
    member _.``ordered writes across two providers expose partial success``() =
        task {
            let name = key "partial"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.auditWrite 3
            StorageLog.failingWrites.[FunctionalStateProviders.Audit] <- true

            try
                let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.orderedWrites "committed" :> Task)
                Assert.Contains("configured to fail writes", error.Message)
            finally
                StorageLog.failingWrites.TryRemove FunctionalStateProviders.Audit |> ignore

            // The first write is committed and no rollback happened…
            do! fixture.Recycle name
            Assert.Equal(Some "v1:[activated,committed]", StateProbe.tryGet $"activate:{grainId}")

            // …while the second provider still holds its earlier value.
            let! audit = api.auditPeek ()
            Assert.Equal(3, audit)
        }

    // ──────────────────────────────────────────────────────────────────────────
    // The invocation-bound facade
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// Spec "Operation policies" and "Required tests": a read-only callback discards its
    /// replacement state and rejects the setter plus BOTH overloads of read, write, and clear
    /// through its state facade, while getters keep working.
    /// </remarks>
    [<Fact>]
    member _.``a read-only callback keeps getters and rejects the whole mutation surface``() =
        task {
            let name = key "readonly"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "durable"
            let! report = api.readOnlyProbe ()

            Assert.Equal("v1:[activated,durable]", field report "state")
            Assert.Equal("True", field report "recordExists")
            Assert.Equal("True", field report "etag")

            for member' in [ "set"; "read"; "write"; "clear"; "readToken"; "writeToken"; "clearToken" ] do
                Assert.Equal("InvalidOperationException", field report member')

            // The read-only replacement (version 999) was discarded, and nothing touched storage.
            let! snapshot = api.snapshot ()
            Assert.Equal("v1:[activated,durable]", snapshot)
            Assert.Equal(1, StorageLog.countFor grainId "write")
            Assert.Equal(0, StorageLog.countFor grainId "clear")
            Assert.Equal(2, StorageLog.countFor grainId "read")
        }

    [<Fact>]
    member _.``an always-interleaved callback rejects the same complete surface``() =
        task {
            let name = key "interleave"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! _ = api.append "in-memory"
            let! report = api.interleavedProbe ()

            for member' in [ "set"; "read"; "write"; "clear"; "readToken"; "writeToken"; "clearToken" ] do
                Assert.Equal("InvalidOperationException", field report member')

            let! snapshot = api.snapshot ()
            Assert.Equal("v1:[activated,in-memory]", snapshot)
            Assert.Equal(0, StorageLog.countFor grainId "write")
        }

    /// <remarks>
    /// Task-6 close-out 4: a dedicated <c>oneWay + alwaysInterleave</c> facade-rejection test.
    /// This is the only policy combination where <c>alwaysInterleave</c> alone (without
    /// <c>readOnly</c>) drives state-neutrality: <c>IsReadOnly</c> is false, so it is purely
    /// <c>IsAlwaysInterleave</c> that must reject the facade's mutation surface. One-way has no
    /// reply, so the report travels through <c>StateProbe</c> instead of a return value.
    /// </remarks>
    [<Fact>]
    member _.``a oneWay plus always-interleave callback rejects the same complete surface``() =
        task {
            let name = key "onewayinterleave"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! _ = api.append "in-memory"
            do! api.oneWayInterleavedProbe ()

            let deadline = DateTime.UtcNow.AddSeconds 30.0
            let mutable report = None

            while report.IsNone && DateTime.UtcNow < deadline do
                report <- StateProbe.tryGet $"onewayinterleaved:{grainId}"
                if report.IsNone then
                    do! Task.Delay 100

            let report =
                match report with
                | Some value -> value
                | None -> failwith "the oneWay + alwaysInterleave probe never recorded its report"

            for member' in [ "set"; "read"; "write"; "clear"; "readToken"; "writeToken"; "clearToken" ] do
                Assert.Equal("InvalidOperationException", field report member')

            // Its replacement (irrelevant here, since one-way discards) never reached storage.
            let! snapshot = api.snapshot ()
            Assert.Equal("v1:[activated,in-memory]", snapshot)
            Assert.Equal(0, StorageLog.countFor grainId "write")
        }

    /// <remarks>
    /// "The facade rejects use after its callback has completed." A handler which lets its
    /// facade escape must find every member closed afterwards, getters included.
    /// </remarks>
    [<Fact>]
    member _.``a facade which outlived its callback rejects every member``() =
        task {
            let name = key "escape"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.escapeFacade ()
            let! report = api.useEscapedFacade ()

            Assert.Equal("InvalidOperationException", field report "state")

            for member' in [ "set"; "read"; "write"; "clear"; "readToken"; "writeToken"; "clearToken" ] do
                Assert.Equal("InvalidOperationException", field report member')

            Assert.Equal(0, StorageLog.countFor grainId "write")
            Assert.Equal(0, StorageLog.countFor grainId "clear")
        }

    /// <remarks>
    /// Resolution is by logical <c>(stateName, providerName, storedType)</c> identity, and an
    /// unattached descriptor "fails deterministically" instead of silently resolving to another
    /// holder.
    /// </remarks>
    [<Fact>]
    member _.``an unattached descriptor fails deterministically``() =
        task {
            let! attachedGrain = (fixture.Ledger(key "detached")).unattached ()
            Assert.StartsWith("InvalidOperationException:", attachedGrain)
            Assert.Contains("no persistent state named 'detached'", attachedGrain)
            Assert.Contains(FunctionalStateProviders.Audit, attachedGrain)

            // The same lookup fails on a definition with no attachment at all.
            let! ephemeral = (fixture.Ephemeral(key "detached")).unattached ()
            Assert.StartsWith("InvalidOperationException:", ephemeral)
            Assert.Contains("no persistent state named 'ledger'", ephemeral)
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Activation and deactivation ordering
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// Phase-0 item 9's negative control, against production code: a facet can only be created
    /// before the activation lifecycle starts. Orleans rejects the same call from inside a
    /// handler, which is why the functional activator creates every attached facet
    /// synchronously in <c>CreateInstance</c>. Without that ordering the SetupState load — and
    /// with it every assertion in this file — could not happen.
    /// </remarks>
    [<Fact>]
    member _.``creating a persistent facet after activation is rejected by Orleans``() =
        task {
            let! outcome = (fixture.Ledger(key "latefacet")).createFacetNow ()

            Assert.StartsWith("rejected:InvalidOperationException", outcome)
            Assert.Contains("Lifecycle has already been started", outcome)
        }

    /// <remarks>
    /// Spec activation order, steps 2–4: SetupState loads, <c>OnActivateAsync</c> initializes,
    /// then the functional hook runs and "its returned state is published only in memory".
    /// Proven by recycling twice: the hook's own marker must never reappear from storage.
    /// </remarks>
    [<Fact>]
    member _.``onActivate observes loaded state and its replacement is never written``() =
        task {
            let name = key "onactivate"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "stored"
            let! written = api.snapshot ()
            Assert.Equal("v1:[activated,stored]", written)

            do! fixture.Recycle name
            Assert.Equal(Some "v1:[activated,stored]", StateProbe.tryGet $"activate:{grainId}")

            // A second recycle proves the first hook's replacement never reached storage: the
            // observed value is identical, not "…,activated" twice over.
            do! fixture.Recycle name
            Assert.Equal(Some "v1:[activated,stored]", StateProbe.tryGet $"activate:{grainId}")
            Assert.Equal(1, StorageLog.countFor grainId "write")
        }

    [<Fact>]
    member _.``onDeactivate runs with a DeactivationReason before the activation ends``() =
        task {
            let name = key "ondeactivate"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! _ = api.append "before-deactivation"
            StateProbe.observations.TryRemove $"deactivate:{grainId}" |> ignore

            do! fixture.Recycle name

            match StateProbe.tryGet $"deactivate:{grainId}" with
            | Some observed ->
                // The hook sees the live in-memory state and the Orleans reason code.
                Assert.Contains("v1:[activated,before-deactivation]", observed)
                Assert.False(observed.StartsWith("|", StringComparison.Ordinal))
            | None -> failwith "the deactivation hook did not run"

            // The hook ran BEFORE the remaining stop stages: the witness subscribed at the lower
            // SetupState stage, whose stop callback therefore runs after this one.
            let hookTick = int (Option.get (StateProbe.tryGet $"deactivate-tick:{grainId}"))
            let stopTick = int (Option.get (StateProbe.tryGet $"stop-tick:{grainId}"))
            Assert.True(hookTick < stopTick, $"onDeactivate tick {hookTick} must precede stop stage tick {stopTick}")

            Assert.Equal(0, StorageLog.countFor grainId "write")
        }

    /// <remarks>
    /// "Hook and storage exceptions receive no library retry. The task failure reaches the
    /// Orleans stop lifecycle, which observes and logs it while continuing remaining stop
    /// stages." The grain reactivating afterwards is the proof that the stop path completed.
    /// </remarks>
    [<Fact>]
    member _.``a failing deactivation hook is logged once and does not block the stop path``() =
        task {
            let name = key "faildeactivate"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "survives"
            do! api.armFailingDeactivation ()
            StateLogCapture.clear ()
            StateProbe.observations.TryRemove $"stop-tick:{grainId}" |> ignore

            do! fixture.Recycle name

            // The failure was observed and logged by the runtime…
            Assert.True(
                StateLogCapture.contains "onDeactivate hook of grain type",
                "the failing deactivation hook must be logged"
            )

            Assert.True(StateLogCapture.contains "failed on purpose", "the hook's own message must be logged")

            // …exactly once: no library retry.
            let attempts =
                StateLogCapture.entries
                |> Seq.filter (fun entry -> entry.Contains("onDeactivate hook of grain type", StringComparison.Ordinal))
                |> Seq.length

            Assert.Equal(1, attempts)

            // …the remaining Orleans stop stages still ran after the hook threw…
            let hookTick = int (Option.get (StateProbe.tryGet $"deactivate-tick:{grainId}"))
            let stopTick = int (Option.get (StateProbe.tryGet $"stop-tick:{grainId}"))

            Assert.True(
                hookTick < stopTick,
                $"the stop stage at tick {stopTick} must still run after the failing hook at tick {hookTick}"
            )

            // …and the activation really ended and came back with its durable state intact.
            Assert.Equal(Some "v1:[activated,survives]", StateProbe.tryGet $"activate:{grainId}")
            let! snapshot = api.snapshot ()
            Assert.Equal("v1:[activated,survives,activated]", snapshot)
        }

    /// <remarks>
    /// Task-6 close-out 3: the DEACTIVATION-HOOK EXPLICIT STORAGE-CALL failure arm. Unlike the
    /// test above, the hook itself does not raise — its explicit <c>WriteStateAsync</c> call
    /// fails inside real storage. The failure must still be observed and logged with no retry,
    /// and activation-local cleanup plus the remaining Orleans stop stages must still run.
    /// </remarks>
    [<Fact>]
    member _.``a deactivation hook's failing explicit storage call is logged once and does not block the stop path``
        ()
        =
        task {
            let name = key "faildeactivatewrite"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "durable"
            do! api.armWritingDeactivation "written-on-stop"
            StateLogCapture.clear ()
            StateProbe.observations.TryRemove $"stop-tick:{grainId}" |> ignore
            StorageLog.failingWrites.[FunctionalStateProviders.Ledger] <- true

            try
                do! fixture.Recycle name
            finally
                StorageLog.failingWrites.TryRemove FunctionalStateProviders.Ledger |> ignore

            // The failure was observed and logged as a hook failure, even though it originated
            // from the explicit storage call the hook made rather than an application `raise`…
            Assert.True(
                StateLogCapture.contains "onDeactivate hook of grain type",
                "the failing explicit storage call inside the hook must be logged as a hook failure"
            )

            Assert.True(
                StateLogCapture.contains "configured to fail writes",
                "the storage provider's own diagnostic must be logged"
            )

            // …exactly once: no library retry.
            let attempts =
                StateLogCapture.entries
                |> Seq.filter (fun entry -> entry.Contains("onDeactivate hook of grain type", StringComparison.Ordinal))
                |> Seq.length

            Assert.Equal(1, attempts)

            // …the remaining Orleans stop stages still ran after the storage call threw…
            let hookTick = int (Option.get (StateProbe.tryGet $"deactivate-tick:{grainId}"))
            let stopTick = int (Option.get (StateProbe.tryGet $"stop-tick:{grainId}"))

            Assert.True(
                hookTick < stopTick,
                $"the stop stage at tick {stopTick} must still run after the failing storage call at tick {hookTick}"
            )

            // …and nothing durable came from the failed write: the reactivation loads only the
            // previously committed "durable" record, never "written-on-stop".
            Assert.Equal(Some "v1:[activated,durable]", StateProbe.tryGet $"activate:{grainId}")
        }

    /// <remarks>
    /// "An onDeactivate hook may explicitly write" — and that is the only way a write happens
    /// during deactivation, since the runtime performs none of its own.
    /// </remarks>
    [<Fact>]
    member _.``a deactivation hook can write explicitly and nothing else writes``() =
        task {
            let name = key "deactivatewrite"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            let! _ = api.append "in-memory"
            do! api.armWritingDeactivation "written-on-stop"
            Assert.Equal(0, StorageLog.countFor grainId "write")

            do! fixture.Recycle name

            // Exactly one write, issued by the hook, and the next activation loads it.
            Assert.Equal(1, StorageLog.countFor grainId "write")
            Assert.Equal(Some "v2:[activated,in-memory,written-on-stop]", StateProbe.tryGet $"activate:{grainId}")
        }

    /// <remarks>
    /// "Storage read, initializer, or hook failure fails activation." An activation which never
    /// reached its state has no primary value, so the functional deactivation hook must not run
    /// with one: the runtime logs that it skipped the hook instead of handing application code
    /// an absent state and converting a clear activation failure into an unrelated one.
    /// </remarks>
    [<Fact>]
    member _.``a failing activation hook fails the call and skips the deactivation hook``() =
        task {
            let name = key "failactivate"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.armFailingActivation ()
            do! api.goAway ()
            StateProbe.observations.TryRemove $"deactivate:{grainId}" |> ignore
            StateLogCapture.clear ()

            // Every later activation of this grain fails, so every call fails.
            let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.snapshot () :> Task)
            Assert.Contains("failed on purpose", error.Message)

            // The deactivation hook of the failed activation never observed a state. On Orleans
            // 10.1.0 and 10.2.2 that is because a failed OnActivateAsync never reaches
            // OnDeactivateAsync at all — the runtime never even had to log its skip, which the
            // absence of that warning records here.
            Assert.Equal(None, StateProbe.tryGet $"deactivate:{grainId}")

            Assert.False(
                StateLogCapture.contains "Skipping the functional onDeactivate hook",
                "on these Orleans versions the hook is not reached at all, so nothing is skipped"
            )
        }

    /// <remarks>
    /// Spec state-initialization rule 6: "storage read, initializer, or hook failure fails
    /// activation." This is the READ arm: the stock SetupState load of the primary facet fails,
    /// so the activation never reaches step 3 or the functional hook, and the runtime neither
    /// retries the read nor writes anything while failing.
    /// </remarks>
    [<Fact>]
    member _.``a failing SetupState read fails activation``() =
        task {
            let name = key "failread"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name
            let record = StorageLog.record "ledger" grainId
            StorageLog.failingReads.[record] <- true

            try
                let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.snapshot () :> Task)
                Assert.Contains("configured to fail reads", error.ToString())
            finally
                StorageLog.failingReads.TryRemove record |> ignore

            // Activation stopped at the storage read: neither state initialization nor the
            // functional onActivate hook ran, and an activation with no state never reaches the
            // deactivation hook either.
            Assert.Equal(0, StateProbe.activationCount grainId)
            Assert.Equal(None, StateProbe.tryGet $"activate:{grainId}")
            Assert.Equal(None, StateProbe.tryGet $"deactivate:{grainId}")

            // Nothing was written or cleared in response to the failure.
            Assert.Equal(0, StorageLog.countFor grainId "write")
            Assert.Equal(0, StorageLog.countFor grainId "clear")

            // With the provider healthy again the grain activates normally.
            let! snapshot = eventually api.snapshot
            Assert.Equal("v0:[activated]", snapshot)
            Assert.Equal(1, StateProbe.activationCount grainId)

            // The same holds for an ADDITIONAL holder on the second provider: its SetupState read
            // is just as load-bearing as the primary one.
            let additional = key "failread-audit"
            let additionalId = fixture.LedgerId additional
            let additionalApi = fixture.Ledger additional
            let auditRecord = StorageLog.record "audit" additionalId
            StorageLog.failingReads.[auditRecord] <- true

            try
                let! error = Assert.ThrowsAnyAsync<exn>(fun () -> additionalApi.snapshot () :> Task)
                Assert.Contains("configured to fail reads of 'audit'", error.ToString())
            finally
                StorageLog.failingReads.TryRemove auditRecord |> ignore

            Assert.Equal(0, StateProbe.activationCount additionalId)
            Assert.Equal(0, StorageLog.countFor additionalId "write")

            let! recovered = eventually additionalApi.snapshot
            Assert.Equal("v0:[activated]", recovered)
        }

    /// <remarks>
    /// The INITIALIZER arm of the same rule, on both of its call sites: the initializer of an
    /// attached holder whose record does not exist, and the ephemeral state factory. Neither may
    /// be swallowed, and neither may write anything.
    /// </remarks>
    [<Fact>]
    member _.``a failing state initializer fails activation``() =
        task {
            let name = key "failinit"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name
            StateProbe.failingInitializers.[name] <- true

            try
                let! error = Assert.ThrowsAnyAsync<exn>(fun () -> api.snapshot () :> Task)
                Assert.Contains("the audit initializer", error.ToString())
            finally
                StateProbe.failingInitializers.TryRemove name |> ignore

            // Initialization is activation step 3, before the functional hook of step 4…
            Assert.Equal(0, StateProbe.activationCount grainId)
            Assert.Equal(None, StateProbe.tryGet $"activate:{grainId}")
            Assert.Equal(None, StateProbe.tryGet $"deactivate:{grainId}")

            // …and an initializer populates memory only, so the failed activation shows the two
            // SetupState reads and nothing else.
            Assert.Equal(0, StorageLog.countFor grainId "write")
            Assert.Equal(0, StorageLog.countFor grainId "clear")

            let! snapshot = eventually api.snapshot
            Assert.Equal("v0:[activated]", snapshot)

            // The ephemeral state factory is the other call site of step 3 and fails activation
            // in exactly the same way.
            let ephemeralName = key "failinit-ephemeral"
            let ephemeral = fixture.Ephemeral ephemeralName
            StateProbe.failingInitializers.[ephemeralName] <- true

            try
                let! error = Assert.ThrowsAnyAsync<exn>(fun () -> ephemeral.snapshot () :> Task)
                Assert.Contains("the ephemeral initializer", error.ToString())
            finally
                StateProbe.failingInitializers.TryRemove ephemeralName |> ignore

            let! after = eventually ephemeral.snapshot
            Assert.Equal($"v0:[{ephemeralName}]", after)
        }

    /// <remarks>
    /// Spec "Persistence and activation lifecycle": "the runtime performs no rollback, retry,
    /// reload, or ETag repair." A competing writer bumps the stored ETag between this
    /// activation's load and its explicit write, so real memory storage raises a real Orleans
    /// <c>InconsistentStateException</c>. Everything asserted afterwards is the functional
    /// runtime NOT reacting to it.
    /// </remarks>
    /// <remarks>
    /// Orleans itself does react: <c>StateStorageBridge</c> ends the activation after an
    /// inconsistent-state error. That is stock behaviour below this library, which is why the
    /// unrepaired facade is observed from inside the failing invocation and the durable outcome
    /// from the activation which replaces it.
    /// </remarks>
    [<Fact>]
    member _.``a stale ETag write fails the call and nothing repairs it``() =
        task {
            let name = key "etag"
            let grainId = fixture.LedgerId name
            let api = fixture.Ledger name

            do! api.writeNow "durable"
            Assert.Equal(1, StorageLog.countFor grainId "write")
            Assert.Equal(1, StateProbe.activationCount grainId)

            let! conflict = Assert.ThrowsAnyAsync<exn>(fun () -> api.conflictingWrite "conflict" :> Task)

            // The provider's own diagnostic reaches the caller, unwrapped and unrepaired.
            Assert.Contains("InconsistentStateException", conflict.ToString())

            // No retry: exactly one further write reached the provider…
            Assert.Equal(2, StorageLog.countFor grainId "write")
            // …no reload or repair read: still only the two SetupState reads…
            Assert.Equal(2, StorageLog.countFor grainId "read")
            // …and no clear.
            Assert.Equal(0, StorageLog.countFor grainId "clear")

            // What the failing invocation saw once the write had failed: the same stale ETag it
            // loaded with (no repair) and the value its own setter installed (no rollback).
            match StateProbe.tryGet $"conflict:{grainId}" with
            | Some report ->
                Assert.Equal("InconsistentStateException", field report "error")
                Assert.Equal("True", field report "etagUnchanged")
                Assert.Equal("v2:[activated,durable,conflict]", field report "holder")
            | None -> failwith "the conflicting write recorded nothing"

            // Orleans ends the activation after an inconsistent-state error; wait for the one
            // which replaces it.
            let deadline = DateTime.UtcNow.AddSeconds 30.0

            while StateProbe.activationCount grainId < 2 && DateTime.UtcNow < deadline do
                do! Task.Delay 100
                let! _ = eventually api.snapshot
                ()

            Assert.True(StateProbe.activationCount grainId >= 2, "the conflicting activation never ended")

            // Nothing was flushed on the way out — the write count did not move — so the record
            // is the competing writer's, and the diverged in-memory state simply disappeared.
            Assert.Equal(2, StorageLog.countFor grainId "write")
            Assert.Equal(0, StorageLog.countFor grainId "clear")
            Assert.Equal(Some "v101:[activated,durable,intruder]", StateProbe.tryGet $"activate:{grainId}")

            // The deactivation hook of the failing activation still saw the unrepaired state.
            match StateProbe.tryGet $"deactivate:{grainId}" with
            | Some observed -> Assert.Contains("v2:[activated,durable,conflict]", observed)
            | None -> failwith "the deactivation hook did not run"
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Durability across activations
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// Spec "Required tests": "State which the application explicitly writes survives
    /// deactivation … while unwritten in-memory changes disappear." Reaching a DIFFERENT silo is
    /// covered deterministically in <c>FunctionalStateRestartTests</c>, because placement of one
    /// grain identity is stable while the cluster membership does not change.
    /// </remarks>
    [<Fact>]
    member _.``written state survives deactivation while unwritten changes disappear``() =
        task {
            let name = key "survive"
            let api = fixture.Ledger name

            do! api.writeNow "written"
            let! _ = api.append "unwritten"
            let! before = api.snapshot ()
            Assert.Equal("v2:[activated,written,unwritten]", before)

            do! fixture.Recycle name

            let! after = api.snapshot ()
            Assert.Equal("v1:[activated,written,activated]", after)
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Ephemeral definitions
    // ──────────────────────────────────────────────────────────────────────────

    /// <remarks>
    /// "Ephemeral definitions invoke the state factory once per activation" — and touch no
    /// storage at all, which the instrumented providers confirm by silence.
    /// </remarks>
    [<Fact>]
    member _.``an ephemeral definition initializes per activation and touches no storage``() =
        task {
            let name = key "ephemeral"
            let grainId = $"{FunctionalStateGrainTypes.Ephemeral}/{name}"
            let api = fixture.Ephemeral name

            let! initial = api.snapshot ()
            Assert.Equal($"v0:[{name}]", initial)

            let! _ = api.append "volatile"
            let! changed = api.snapshot ()
            Assert.Equal($"v1:[{name},volatile]", changed)

            do! api.goAway ()

            let mutable reset = false
            let mutable attempts = 0

            while not reset && attempts < 100 do
                attempts <- attempts + 1
                do! Task.Delay 100
                let! current = api.snapshot ()
                reset <- current = $"v0:[{name}]"

            Assert.True(reset, "the ephemeral state must be initialized again on the next activation")
            Assert.Empty(StorageLog.forGrain grainId)
        }

