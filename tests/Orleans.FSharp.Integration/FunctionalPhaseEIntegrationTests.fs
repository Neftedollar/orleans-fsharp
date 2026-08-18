/// <summary>
/// Spec 004 item 3: the behaviour of a <c>journaledGrainFor</c> definition, proved against both
/// Orleans log-consistency providers from one test body wherever the two agree, and separately
/// wherever they do not.
/// </summary>
module Orleans.FSharp.Integration.FunctionalPhaseEIntegrationTests

open System
open System.Threading.Tasks
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalPhaseEFixture
open Swensen.Unquote
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Shared helpers
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The two hosted journaled definitions, bound through the ordinary functional client. Both
/// contracts have the same API record, so a test body written against one runs against the other
/// without change — which is the point: everything asserted from a shared body is behaviour the
/// two providers really do agree on.
/// </summary>
let private accounts (fixture: FunctionalPhaseEFixture) : (string * (string -> AccountApi)) list =
    [ PhaseEProviders.LogStorage, logAccountRef fixture.Client
      PhaseEProviders.StateStorage, stateAccountRef fixture.Client ]

let private freshKey (prefix: string) = $"{prefix}-{Guid.NewGuid():N}"

/// <summary>
/// Wait for the activation to be gone. <c>deactivateOnIdle</c> takes effect after the current turn
/// completes, and the next call re-activates; polling on the silo address is not enough because
/// placement is stable, so this simply gives Orleans time to finish the teardown.
/// </summary>
let private settleDeactivation () = Task.Delay 1500

/// <summary>Every message in an exception chain, so an assertion is not defeated by wrapping.</summary>
let rec private messages (error: exn) : string list =
    match error with
    | null -> []
    | :? AggregateException as aggregate ->
        error.Message :: (aggregate.InnerExceptions |> Seq.collect messages |> List.ofSeq)
    | _ -> error.Message :: messages error.InnerException

let private mentions (fragment: string) (error: exn) =
    messages error |> List.exists (fun message -> message.Contains fragment)

// ──────────────────────────────────────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────────────────────────────────────

[<Collection("FunctionalPhaseE")>]
type FunctionalPhaseEIntegrationTests(fixture: FunctionalPhaseEFixture) =

    /// <remarks>
    /// The core round trip: events raised by handlers, folded into state by <c>apply</c>, and
    /// replayed from the journal after the activation is gone. The state itself is never written
    /// by the application anywhere.
    /// </remarks>
    [<Fact>]
    member _.``events replay into the same state after the activation is recycled``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"replay-{provider}")

                let! afterFirst = api.deposit 100m
                let! afterSecond = api.deposit 40m
                let! withdrew = api.withdraw 30m

                test <@ afterFirst = 100m @>
                // The handler was handed the state its own previous event had already folded in.
                test <@ afterSecond = 140m @>
                test <@ withdrew @>

                let! before = api.balance ()
                let! historyBefore = api.history ()

                do! api.recycle ()
                do! settleDeactivation ()

                let! after = api.balance ()
                let! historyAfter = api.history ()
                let! version = api.version ()

                test <@ before = 110m @>
                test <@ after = 110m @>
                test <@ historyAfter = historyBefore @>
                // Three confirmed events; the version is the length of the journal, not of the
                // history the fold happens to build.
                test <@ version = 3 @>
        }

    /// <remarks>
    /// The purity pin. The state a replay produces must equal the plain F# fold of the same events
    /// over the same seed — computed here in the test process, with no Orleans involved at all.
    /// A fold that read the clock, called a service, or generated an identifier would diverge, and
    /// this is the assertion that would catch it.
    /// </remarks>
    [<Fact>]
    member _.``the replayed state equals a plain fold of the same events over the same seed``() =
        task {
            for provider, account in accounts fixture do
                let key = freshKey $"purity-{provider}"
                let api = account key

                let amounts = [ 5m; 12m; 3m; 40m ]

                for amount in amounts do
                    let! _ = api.deposit amount
                    ()

                do! api.recycle ()
                do! settleDeactivation ()

                let! replayedBalance = api.balance ()
                let! replayedHistory = api.history ()

                // The same seed the definition declares, folded with the same function, by hand.
                let expected =
                    amounts
                    |> List.fold
                        (fun (state: AccountState) amount ->
                            { state with
                                balance = state.balance + amount
                                history = state.history @ [ $"+{amount}" ] })
                        { balance = 0m
                          history = [ $"opened:{key}" ] }

                test <@ replayedBalance = expected.balance @>
                test <@ replayedHistory = expected.history @>
        }

    /// <remarks>
    /// Confirmation is per turn and happens after the handler returns: a handler observes the
    /// version it started from, and the caller's reply is only produced once the events are in the
    /// journal. The second half of that is what the recycle proves — a version the next activation
    /// reads back could not have come from memory.
    /// </remarks>
    [<Fact>]
    member _.``a handler observes the pre-turn version and the caller sees the post-turn one``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"version-{provider}")

                let! before = api.version ()
                let! observedBefore, observedAfter = api.depositAndReadVersion 10m
                let! afterCall = api.version ()

                test <@ before = 0 @>
                test <@ observedBefore = 0 @>
                // Nothing inside the turn confirms, so the two reads inside the handler agree.
                test <@ observedAfter = 0 @>
                test <@ afterCall = 1 @>

                do! api.recycle ()
                do! settleDeactivation ()

                let! afterRecycle = api.version ()
                test <@ afterRecycle = 1 @>
        }

    /// <remarks>
    /// A handler that raises nothing performs no storage write, which is what makes a query on a
    /// journaled grain as cheap as one on an ordinary grain. The version is the observable: a
    /// write would have moved it.
    /// </remarks>
    [<Fact>]
    member _.``a handler that raises no events does not move the journal``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"empty-{provider}")

                let! _ = api.deposit 10m
                let! refused = api.withdraw 500m
                let! _ = api.balance ()
                let! version = api.version ()

                test <@ not refused @>
                test <@ version = 1 @>
        }

    /// <remarks>
    /// A handler's events are appended as one atomic batch, so a later replay can never observe
    /// half of them.
    /// </remarks>
    [<Fact>]
    member _.``several events raised by one handler are appended together``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"batch-{provider}")

                let! raised = api.batch [ 1m; 2m; 3m ]
                let! version = api.version ()
                let! balance = api.balance ()

                test <@ raised = 3 @>
                test <@ version = 3 @>
                test <@ balance = 6m @>

                do! api.recycle ()
                do! settleDeactivation ()

                let! afterBalance = api.balance ()
                test <@ afterBalance = 6m @>
        }

    /// <remarks>
    /// <c>raiseConditional</c> appends at the current confirmed position. With a non-reentrant
    /// definition the activation is the sole writer of its journal and Orleans does not interleave
    /// its turns, so the position cannot move underneath the handler and the answer is always
    /// <c>true</c>. That is a property of the topology, not of the API, and the spec records it —
    /// this test pins the supported case rather than pretending to exercise a conflict that cannot
    /// happen here.
    /// </remarks>
    [<Fact>]
    member _.``a conditional append at the current position is accepted``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"conditional-{provider}")

                let! first = api.conditional 7m
                let! second = api.conditional 3m
                let! balance = api.balance ()
                let! version = api.version ()

                test <@ first @>
                test <@ second @>
                test <@ balance = 10m @>
                test <@ version = 2 @>
        }

    /// <remarks>
    /// The negative control for the state model: a <c>readOnly</c> operation may run while another
    /// turn is in flight, so its appends could not be ordered against that turn's. The runtime
    /// refuses rather than silently dropping the events — the handler believed it had changed the
    /// grain.
    /// </remarks>
    [<Fact>]
    member _.``a readOnly operation that raises events is refused``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"readonly-{provider}")

                let! failure = Assert.ThrowsAnyAsync<exn>(fun () -> api.readOnlyRaise () :> Task)
                test <@ mentions "Orleans.FSharp functional journal" failure @>
                test <@ mentions "readOnly" failure @>

                // Nothing was appended.
                let! version = api.version ()
                test <@ version = 0 @>
        }

    /// <remarks>
    /// The journal facade is bound to its invocation, like every other facade the context hands
    /// out. Two ways to leave that binding, both refused:
    /// a <c>readOnly</c> operation appending conditionally (it may run beside another turn), and a
    /// context an earlier turn stashed being used by a later one (its scope has expired).
    /// </remarks>
    [<Fact>]
    member _.``the journal facade is bound to the invocation that resolved it``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"scope-{provider}")

                let! fromReadOnly = api.readOnlyConditional ()
                test <@ fromReadOnly.Contains "state-neutral" @>
                test <@ fromReadOnly.Contains "readOnly" @>

                do! api.escape ()
                let! fromEscaped = api.useEscaped ()
                test <@ fromEscaped.Contains "after the" @>
                test <@ fromEscaped.Contains "had already completed" @>

                // Neither refusal appended anything.
                let! version = api.version ()
                test <@ version = 0 @>
        }

    /// <remarks>
    /// Orleans' adaptors catch an exception from <c>ILogViewAdaptorHost.UpdateView</c>, log it, and
    /// carry on with an unchanged view. That would leave the activation holding a state which is
    /// not the fold of its own journal, so the runtime raises instead. The pin matters because the
    /// swallowing is invisible: without it the call below would SUCCEED and the balance would
    /// silently stop matching the journal.
    /// </remarks>
    [<Fact>]
    member _.``a failing apply fold fails the call instead of being swallowed``() =
        task {
            for provider, account in accounts fixture do
                let api = account (freshKey $"poison-{provider}")

                let! _ = api.deposit 5m
                let! failure = Assert.ThrowsAnyAsync<exn>(fun () -> api.poison () :> Task)

                test <@ mentions "Orleans.FSharp functional journal" failure @>
                test <@ mentions "'apply'" failure @>

                // Nothing was appended: the fold runs over the confirmed state BEFORE the events
                // are submitted, so the journal is not poisoned and the grain still works.
                let! version = api.version ()
                let! balance = api.balance ()
                test <@ version = 1 @>
                test <@ balance = 5m @>

                do! api.recycle ()
                do! settleDeactivation ()

                let! replayed = api.balance ()
                test <@ replayed = 5m @>
        }

    /// <remarks>
    /// The C# facade is built from the CONTRACT, which a journaled definition shares with an
    /// ordinary one, so the definition kind is invisible across the interop boundary. This is the
    /// verification of that claim rather than an assumption of it.
    /// </remarks>
    [<Fact>]
    member _.``the C# facade over a journaled contract is transport-transparent``() =
        task {
            let facade =
                FunctionalGrainInterop.For<IAccountFacade>(
                    logAccountContract,
                    fixture.Client,
                    box (freshKey "facade")
                )

            let! deposited = facade.Deposit 25m
            let! balance = facade.Balance()
            let! version = facade.Version()

            test <@ deposited = 25m @>
            test <@ balance = 25m @>
            test <@ version = 1 @>
        }

    /// <remarks>
    /// The two providers store completely different things, and this is where they visibly differ.
    /// LogStorage persists the whole event log and folds all of it on every activation;
    /// StateStorage persists the folded view and replays nothing. The application-visible state is
    /// identical either way — which is the point of measuring the fold count instead.
    /// </remarks>
    [<Fact>]
    member _.``LogStorage replays the whole journal and StateStorage replays none of it``() =
        task {
            let logApi = logAccountRef fixture.Client (freshKey "folds-log")
            let stateApi = stateAccountRef fixture.Client (freshKey "folds-state")

            for api in [ logApi; stateApi ] do
                let! _ = api.deposit 1m
                let! _ = api.deposit 2m
                let! _ = api.deposit 3m
                ()

            // Everything so far: three folds each, one per raised event.
            PhaseECounters.reset PhaseEProviders.LogStorage
            PhaseECounters.reset PhaseEProviders.StateStorage

            do! logApi.recycle ()
            do! stateApi.recycle ()
            do! settleDeactivation ()

            let! logBalance = logApi.balance ()
            let! stateBalance = stateApi.balance ()

            test <@ logBalance = 6m @>
            test <@ stateBalance = 6m @>

            // LogStorage re-folded all three stored entries to rebuild the view.
            test <@ PhaseECounters.count PhaseEProviders.LogStorage = 3 @>
            // StateStorage read the view back whole; there was nothing to fold.
            test <@ PhaseECounters.count PhaseEProviders.StateStorage = 0 @>
        }

// ──────────────────────────────────────────────────────────────────────────────
// The journal outlives the silo that wrote it
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Write two events, remove the silo that hosted the activation, and read the state back on a silo
/// which never saw either write.
/// </summary>
/// <remarks>
/// <para>
/// The journal is addressed by grain identity, not by activation, so it follows the grain. The
/// hosting silo is removed rather than the activation recycled, because placement in a running
/// cluster is stable and recycling would land the grain back where it was.
/// </para>
/// <para>
/// The silo removed is always a SECONDARY, and the test picks grain keys until it finds one placed
/// there. Stopping the primary would take the development clustering table and the client's gateway
/// with it, which proves nothing about journals and everything about the test host.
/// </para>
/// <para>
/// Each provider gets a fresh cluster: stopping a silo of the shared fixture would change the world
/// for every other test in the collection, and restarting one inside a single run makes placement
/// unpredictable for the second half.
/// </para>
/// </remarks>
let private journalSurvivesSiloLoss (provider: string) (bind: IClusterClient -> string -> AccountApi) =
    let builder = TestClusterBuilder 2s
    builder.AddSiloBuilderConfigurator<PhaseESiloConfigurator>() |> ignore
    builder.AddClientBuilderConfigurator<PhaseEClientConfigurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()

    task {
        try
            let account = bind cluster.Client
            let secondary = cluster.SecondarySilos |> Seq.head
            let secondaryAddress = string secondary.SiloAddress

            let mutable placed = None
            let mutable attempt = 0

            while placed.IsNone && attempt < 60 do
                let api = account $"move-{provider}-{attempt}"
                let! host = api.whereAmI ()

                if host = secondaryAddress then
                    placed <- Some api

                attempt <- attempt + 1

            let api =
                match placed with
                | Some api -> api
                | None -> failwith $"no grain of '{provider}' was placed on the secondary silo in {attempt} attempts"

            let! _ = api.deposit 60m
            let! _ = api.deposit 15m

            // The activation and everything it held in memory go with the silo; only the journal
            // remains, in a storage provider no silo owns.
            do! cluster.StopSiloAsync secondary
            do! cluster.WaitForLivenessToStabilizeAsync()

            let! newSilo = api.whereAmI ()
            let! balance = api.balance ()
            let! version = api.version ()

            Assert.NotEqual<string>(secondaryAddress, newSilo)
            Assert.Equal(75m, balance)
            Assert.Equal(2, version)
        finally
            cluster.StopAllSilos()
            cluster.Dispose()
    }

[<Fact>]
let ``a LogStorage journal follows the grain to a silo that never saw the writes`` () =
    journalSurvivesSiloLoss PhaseEProviders.LogStorage (fun client -> logAccountRef client)

[<Fact>]
let ``a StateStorage journal follows the grain to a silo that never saw the writes`` () =
    journalSurvivesSiloLoss PhaseEProviders.StateStorage (fun client -> stateAccountRef client)
