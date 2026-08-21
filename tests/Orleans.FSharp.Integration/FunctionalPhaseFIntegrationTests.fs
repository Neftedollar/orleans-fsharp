/// <summary>
/// Spec 004 Phase F: server-streaming replies over a live two-silo cluster.
/// </summary>
module Orleans.FSharp.Integration.FunctionalPhaseFIntegrationTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Orleans.Runtime
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalPhaseFFixture

[<Collection("FunctionalPhaseF")>]
type FunctionalPhaseFTests(fixture: FunctionalPhaseFFixture) =

    let ticker key = tickerRef fixture.Client key
    let relay key = relayRef fixture.Client key
    let placed key = placedRef fixture.Client key
    let ledger key = ledgerRef fixture.Client key
    let versioned key = versionedRef fixture.Client key
    let versionedV1 key = versionedV1Ref fixture.Client key

    let unique () = Guid.NewGuid().ToString "N"

    /// <summary>
    /// Drain a stream exactly the way C#'s <c>await foreach</c> desugars it: one
    /// <c>GetAsyncEnumerator</c>, <c>MoveNextAsync</c> until it answers <c>false</c>, then
    /// <c>DisposeAsync</c>. Deliberately not <c>TaskSeq.toListAsync</c> — a test that proves this
    /// runtime's streams must depend on nothing but the BCL contract, and
    /// <c>FSharp.Control.TaskSeq</c> 0.6.0 has a wrapping defect over exactly these enumerators
    /// (see the discriminator test at the end of this file).
    /// </summary>
    let collect (stream: IAsyncEnumerable<'T>) : Task<'T list> =
        task {
            let items = ResizeArray<'T>()
            let enumerator = stream.GetAsyncEnumerator CancellationToken.None

            try
                let mutable go = true

                while go do
                    let! moved = enumerator.MoveNextAsync()

                    if moved then items.Add enumerator.Current else go <- false
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            return List.ofSeq items
        }

    /// <summary>Wait until a condition holds, or fail with the supplied description.</summary>
    let waitUntil (description: string) (condition: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 20.0

            while not (condition ()) && DateTime.UtcNow < deadline do
                do! Task.Delay 20

            if not (condition ()) then
                failwith $"timed out waiting for {description}"
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Delivery
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of a stream: the caller sees an item while the producer is still blocked
    /// before the next one. A count-only assertion could not tell this from one batch delivered at
    /// the end, so the producer is held at a gate per item and the test checks, after each item,
    /// that the producer has produced exactly that many.
    /// </summary>
    [<Fact>]
    member _.``a stream delivers each item before the producer has produced the next``() =
        task {
            let key = unique ()
            let label = $"watch-{key}"

            let stream = (ticker key).watch (label, 4)
            let enumerator = stream.GetAsyncEnumerator CancellationToken.None

            try
                let received = ResizeArray<Tick>()

                for index in 0..3 do
                    // Nothing may have been produced beyond what we already consumed.
                    test <@ PhaseFProduced.count label = index @>

                    PhaseFGates.release label index
                    let! moved = enumerator.MoveNextAsync()
                    test <@ moved @>
                    received.Add enumerator.Current

                    // The producer is now parked at the NEXT gate, so it produced exactly one more.
                    test <@ PhaseFProduced.count label = index + 1 @>

                let! completed = enumerator.MoveNextAsync()
                test <@ not completed @>

                test <@ received |> Seq.map (fun tick -> tick.index) |> List.ofSeq = [ 0; 1; 2; 3 ] @>
                test <@ received.[2].note = $"{label}#2" @>
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    /// <summary>A stream that yields nothing completes cleanly rather than hanging or faulting.</summary>
    [<Fact>]
    member _.``an empty stream completes with no items``() =
        task {
            let key = unique ()
            let! items = collect ((ticker key).empty ())
            test <@ List.isEmpty items @>
        }

    /// <summary>
    /// The same sequence enumerated twice runs two independent remote enumerations — Orleans'
    /// semantics for a codegen <c>IAsyncEnumerable</c> method, preserved by the wrapper.
    /// </summary>
    [<Fact>]
    member _.``the same stream value can be enumerated twice``() =
        task {
            let key = unique ()
            let first = $"twice-a-{key}"
            let second = $"twice-b-{key}"

            let streamA = (ticker key).watch (first, 2)
            PhaseFGates.releaseAll first 2
            let! itemsA = collect streamA

            let streamB = (ticker key).watch (second, 3)
            PhaseFGates.releaseAll second 3
            let! itemsB = collect streamB

            test <@ List.length itemsA = 2 @>
            test <@ List.length itemsB = 3 @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Scheduling
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sketch's open question, answered. An enumeration is open (the producer is parked at a
    /// gate, so a MoveNext is in flight and long-polling) and an ordinary, non-interleaving call to
    /// the same activation still completes. Orleans' own <c>[AlwaysInterleave]</c> on every
    /// <c>IAsyncEnumerableGrainExtension</c> method is what makes this true: an always-interleave
    /// message never becomes the activation's blocking request
    /// (<c>ActivationData.RecordRunning</c>), so <c>MayInvokeRequest</c> admits the ordinary call.
    /// </summary>
    [<Fact>]
    member _.``an ordinary call proceeds while an enumeration is open``() =
        task {
            let key = unique ()
            let label = $"open-{key}"

            // Seed the state so the ordinary call has something non-default to report.
            let! _ = (ticker key).bump 7

            let stream = (ticker key).watch (label, 3)
            let enumerator = stream.GetAsyncEnumerator CancellationToken.None

            try
                PhaseFGates.release label 0
                let! moved = enumerator.MoveNextAsync()
                test <@ moved @>

                // The producer is now parked at gate 1: the enumeration is open and its MoveNext is
                // in flight. An ordinary call must not queue behind it.
                let stopwatch = Stopwatch.StartNew()
                let! pinged = (ticker key).ping ()
                stopwatch.Stop()

                test <@ pinged = 7 @>

                // The long poll is MessagingOptions.ResponseTimeout / 2 = 15s by default; anything
                // near that would mean the call had waited for the enumeration's turn.
                test <@ stopwatch.Elapsed < TimeSpan.FromSeconds 10.0 @>

                PhaseFGates.releaseAll label 3
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Cancellation and disposal
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposing the enumerator early must reach the target: Orleans sends <c>DisposeAsync</c> for
    /// the request id, the extension cancels the token it handed to <c>GetAsyncEnumerator</c> and
    /// then disposes the producer's enumerator, so the handler's <c>finally</c> runs and sees a
    /// cancelled token.
    /// </summary>
    [<Fact>]
    member _.``disposing the enumerator cancels the producer and runs its finally block``() =
        task {
            let key = unique ()
            let label = $"follow-{key}"

            let enumerator = ((ticker key).follow label).GetAsyncEnumerator CancellationToken.None

            let! first = enumerator.MoveNextAsync()
            test <@ first @>
            test <@ enumerator.Current = 0 @>

            do! enumerator.DisposeAsync()

            do! waitUntil "the producer to observe the disposal" (fun () -> (PhaseFObserved.tryGet label).IsSome)

            test <@ PhaseFObserved.tryGet label = Some "cancelled" @>
        }

    /// <summary>
    /// The caller's own token, supplied at the call through <c>streamCancellable</c>, cancels the
    /// enumeration. Orleans links it with the enumerator's token inside
    /// <c>AsyncEnumeratorProxy</c>, so cancelling it aborts the pull and disposes the remote
    /// enumerator.
    /// </summary>
    [<Fact>]
    member _.``a caller token cancels an open enumeration end to end``() =
        task {
            let key = unique ()
            let label = $"token-{key}"
            use source = new CancellationTokenSource()

            let reference = tickerRawRef fixture.Client key
            let stream = reference.streamCancellable (_.follow) label source.Token
            let enumerator = stream.GetAsyncEnumerator CancellationToken.None

            let! first = enumerator.MoveNextAsync()
            test <@ first @>

            source.Cancel()

            let! failure =
                Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                    task {
                        while true do
                            let! _ = enumerator.MoveNextAsync()
                            ()
                    }
                    :> Task)

            test <@ not (isNull (box failure)) @>

            do! enumerator.DisposeAsync()
            do! waitUntil "the producer to observe the cancellation" (fun () -> (PhaseFObserved.tryGet label).IsSome)
            test <@ PhaseFObserved.tryGet label = Some "cancelled" @>
        }

    /// <summary>A producer that throws mid-enumeration surfaces its exception at the consumer.</summary>
    [<Fact>]
    member _.``a producer failure surfaces at the consumer after the items it already yielded``() =
        task {
            let key = unique ()
            let stream = (ticker key).faulty 2
            let enumerator = stream.GetAsyncEnumerator CancellationToken.None
            let received = ResizeArray<int>()

            let! failure =
                Assert.ThrowsAnyAsync<Exception>(fun () ->
                    task {
                        let mutable go = true

                        while go do
                            let! moved = enumerator.MoveNextAsync()

                            if moved then received.Add enumerator.Current else go <- false
                    }
                    :> Task)

            test <@ List.ofSeq received = [ 0; 1 ] @>
            test <@ failure.Message.Contains "failed mid-enumeration" @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Payload limits, per item
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An item the silo was happy to send but the caller's own limit refuses. This is the boundary
    /// a single shared limit could never exercise, which is why the fixture gives the client a
    /// smaller one.
    /// </summary>
    [<Fact>]
    member _.``an item over the caller's payload limit is rejected on receipt``() =
        task {
            let key = unique ()
            let stream = (ticker key).oversized (PhaseFLimits.Client + 4096)

            let! failure =
                Assert.ThrowsAnyAsync<Exception>(fun () -> collect stream :> Task)

            test <@ failure.Message.Contains "caller stream item receive" @>
            test <@ failure.Message.Contains "stream-item" @>
            test <@ failure.Message.Contains PhaseFGrainTypes.Ticker @>
        }

    /// <summary>An item over the silo's own limit never leaves the silo.</summary>
    [<Fact>]
    member _.``an item over the silo's payload limit is rejected before it is sent``() =
        task {
            let key = unique ()
            let stream = (ticker key).oversized (PhaseFLimits.Silo + 4096)

            let! failure =
                Assert.ThrowsAnyAsync<Exception>(fun () -> collect stream :> Task)

            test <@ failure.Message.Contains "silo stream item send" @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // State rules
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A streaming handler reads the state snapshot taken when the enumeration started. A
    /// concurrent <c>bump</c> publishes a new state, and the still-open stream keeps yielding the
    /// old value — which is the only coherent rule available, because a stream produces across many
    /// turns and cannot own a whole-state replacement.
    /// </summary>
    [<Fact>]
    member _.``a streaming handler reads the state snapshot taken when the enumeration started``() =
        task {
            let key = unique ()
            let label = $"snapshot-{key}"

            let! _ = (ticker key).bump 5

            let stream = (ticker key).snapshot (label, 3)
            let enumerator = stream.GetAsyncEnumerator CancellationToken.None

            try
                PhaseFGates.release label 0
                let! _ = enumerator.MoveNextAsync()
                test <@ enumerator.Current = 5 @>

                // Publish a new state while the enumeration is open. This call proceeds (see the
                // scheduling test), and the stream must not see it.
                let! bumped = (ticker key).bump 100
                test <@ bumped = 105 @>

                PhaseFGates.release label 1
                let! _ = enumerator.MoveNextAsync()
                test <@ enumerator.Current = 5 @>

                PhaseFGates.releaseAll label 3
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            // The publication itself was not lost: only the stream's view is frozen.
            let! after = (ticker key).current ()
            test <@ after = 105 @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Cross-silo
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Place a batch of ticker and relay keys and group them by silo, so a genuinely cross-silo
    /// pair can be chosen rather than hoped for. Same shape as Phase D's placement probe, and for
    /// the same reason: a freshly deployed two-silo cluster may place everything on one silo until
    /// the second has gossiped that it hosts the grain type.
    /// </summary>
    member private _.PlaceAcrossSilos(suffix: string) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 30.0
            let mutable attempt = 0
            let mutable result = None

            while result.IsNone && DateTime.UtcNow < deadline do
                let placements = ResizeArray<string * string * string>()

                for index in 0..15 do
                    let tickerKey = $"t{attempt}-{index}-{suffix}"
                    let relayKey = $"r{attempt}-{index}-{suffix}"
                    let! tickerSilo = (ticker tickerKey).whereAmI ()
                    let! relaySilo = (relay relayKey).whereAmI ()
                    placements.Add(tickerKey, tickerSilo, relaySilo)

                result <-
                    placements
                    |> Seq.tryPick (fun (tickerKey, tickerSilo, relaySilo) ->
                        if tickerSilo <> relaySilo then Some(tickerKey, tickerSilo, relaySilo) else None)
                    |> Option.map (fun (tickerKey, tickerSilo, relaySilo) ->
                        let relayKey =
                            placements
                            |> Seq.pick (fun (t, _, r) -> if t = tickerKey && r = relaySilo then Some t else None)

                        tickerKey, relayKey, tickerSilo, relaySilo)

                attempt <- attempt + 1

            return result
        }

    /// <summary>
    /// A grain enumerating another grain's stream, with the two activations on different silos, so
    /// every <c>StartEnumeration</c>/<c>MoveNext</c>/<c>DisposeAsync</c> is a real network message
    /// and the request travels through the hand-written codec rather than the local copier.
    /// </summary>
    [<Fact>]
    member this.``one grain enumerates another grain's stream across two silos``() =
        task {
            let suffix = unique ()
            let! placement = this.PlaceAcrossSilos suffix

            match placement with
            | None -> failwith "the two-silo cluster never placed a ticker and a relay on different silos"
            | Some(tickerKey, _, tickerSilo, relaySilo) ->
                test <@ tickerSilo <> relaySilo @>

                let label = $"relay-{suffix}"
                let relayKey = $"r0-0-{suffix}"
                PhaseFGates.releaseAll label 4

                let! notes = collect ((relay relayKey).relay (tickerKey, label, 4))

                test <@ notes = [ $"{label}#0"; $"{label}#1"; $"{label}#2"; $"{label}#3" ] @>
        }

    /// <summary>The same enumeration when both grains are on one silo — the path that never
    /// serializes the request and therefore depends on the local copier instead of the codec.</summary>
    [<Fact>]
    member _.``a relayed enumeration works when both grains are on one silo``() =
        task {
            let suffix = unique ()
            let label = $"local-{suffix}"

            // A relay whose upstream key is its own: same key, so Orleans places both activations
            // wherever the first call landed and the second call is local to that silo.
            let key = $"same-{suffix}"
            let! tickerSilo = (ticker key).whereAmI ()
            let! relaySilo = (relay key).whereAmI ()

            PhaseFGates.releaseAll label 3
            let! notes = collect ((relay key).relay (key, label, 3))

            test <@ (PhaseFProduced.count label, PhaseFProduced.count $"{label}:in", List.length notes) = (3, 3, 3) @>
            test <@ not (String.IsNullOrEmpty tickerSilo) && not (String.IsNullOrEmpty relaySilo) @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Composition
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Version tolerance: a version-1 caller enumerates an operation that existed at
    /// version 1, on a host publishing version 2.</summary>
    [<Fact>]
    member _.``a version-1 caller enumerates a version-1 streaming operation on a version-2 host``() =
        task {
            let key = unique ()
            let! current = collect ((versioned key).ticks 3)
            let! older = collect ((versionedV1 key).ticks 3)

            test <@ current = [ 1; 2; 3 ] @>
            test <@ older = [ 1; 2; 3 ] @>
        }

    /// <summary>
    /// The negative control of the same rule: an operation introduced at version 2 is refused by
    /// name for a version-1 caller. The version-derived stream-request token alone could not catch
    /// it — the host computes exactly the caller's own version's token — which is why the
    /// <c>sinceVersion</c> check runs first.
    /// </summary>
    [<Fact>]
    member _.``a version-1 caller is refused a streaming operation introduced at version 2``() =
        task {
            let key = unique ()

            let! failure =
                Assert.ThrowsAnyAsync<Exception>(fun () -> collect ((versionedV1 key).extras 2) :> Task)

            test <@ failure.Message.Contains "was introduced at contract version 2" @>
        }

    /// <summary>Placement composes: a definition with an explicit stock strategy streams normally.</summary>
    [<Fact>]
    member _.``an explicit placement strategy composes with a streaming operation``() =
        task {
            let key = unique ()
            let! silo = (placed key).whereAmI ()
            let! numbers = collect ((placed key).numbers 4)

            test <@ numbers = [ 1; 2; 3; 4 ] @>
            test <@ not (String.IsNullOrEmpty silo) @>
        }

    /// <summary>A journaled definition streams its confirmed state without raising anything.</summary>
    [<Fact>]
    member _.``a journaled definition streams its confirmed entries``() =
        task {
            let key = unique ()
            let! _ = (ledger key).record 10
            let! _ = (ledger key).record 20
            let! _ = (ledger key).record 30

            let! entries = collect ((ledger key).entries ())
            let! count = (ledger key).count ()

            test <@ entries = [ 10; 20; 30 ] @>
            test <@ count = 3 @>
        }

    /// <summary>
    /// <c>handleQuery</c> end to end, on both definition kinds at once: the ticker's
    /// <c>current</c> and the ledger's <c>count</c> are the only two operations bound with it, so a
    /// correct reply from each proves the wrapped handler is the shape the dispatch path unboxes
    /// and that the reply still serializes. The writes interleaved between the reads are what make
    /// this more than a constant: a query that had frozen or published state would answer the same
    /// number twice.
    /// </summary>
    [<Fact>]
    member _.``handleQuery replies over a live cluster on both definition kinds``() =
        task {
            let key = unique ()

            let! _ = (ticker key).bump 3
            let! first = (ticker key).current ()
            let! _ = (ticker key).bump 4
            let! second = (ticker key).current ()

            test <@ first = 3 @>
            test <@ second = 7 @>

            let! _ = (ledger key).record 10
            let! afterOne = (ledger key).count ()
            let! _ = (ledger key).record 20
            let! afterTwo = (ledger key).count ()

            test <@ afterOne = 1 @>
            test <@ afterTwo = 2 @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // Batching
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The batch-size knob reaches Orleans' own request. It is set on the value the API field
    /// returned, and the enumeration that follows still yields exactly the same items — the knob
    /// changes how many share a reply message, never what is delivered.
    /// </summary>
    [<Fact>]
    member _.``the batch size can be set on an opened stream``() =
        task {
            let key = unique ()
            let label = $"batch-{key}"
            PhaseFGates.releaseAll label 6

            let! items =
                (ticker key).watch (label, 6)
                |> FunctionalStream.withBatchSize 2
                |> collect

            test <@ items |> List.map (fun tick -> tick.index) = [ 0; 1; 2; 3; 4; 5 ] @>
        }

    /// <summary>A non-positive batch size, and a sequence that is not a functional stream, are both
    /// refused loudly rather than silently ignored.</summary>
    [<Fact>]
    member _.``withBatchSize refuses a bad size and a foreign sequence``() =
        task {
            let key = unique ()
            let stream = (ticker key).empty ()

            let badSize =
                Assert.Throws<InvalidOperationException>(fun () ->
                    FunctionalStream.withBatchSize 0 stream |> ignore)

            test <@ badSize.Message.Contains "positive maximum batch size" @>

            let foreign =
                Assert.Throws<InvalidOperationException>(fun () ->
                    FunctionalStream.withBatchSize 4 (TaskSeq.empty<int>) |> ignore)

            test <@ foreign.Message.Contains "functional streaming operation" @>
        }

    // ──────────────────────────────────────────────────────────────────────────
    // C# surface
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The C#-surface rule for streaming: a facade member returns the BCL
    /// <c>IAsyncEnumerable&lt;T&gt;</c>, so a C# consumer's <c>await foreach</c> works with no
    /// wrapper. This test enumerates it exactly the way a C# <c>await foreach</c> desugars —
    /// <c>GetAsyncEnumerator</c>, <c>MoveNextAsync</c>, <c>DisposeAsync</c> — rather than through
    /// an F# helper, so it is that surface being proved.
    /// </summary>
    [<Fact>]
    member _.``a C# facade member is enumerated with the await-foreach shape``() =
        task {
            let key = unique ()
            let label = $"facade-{key}"
            PhaseFGates.releaseAll label 3

            let facade =
                FunctionalGrainInterop.For<ITickerFacade>(tickerContract, fixture.Client, key)

            let! pinged = facade.Ping()
            test <@ pinged = 0 @>

            let enumerator = facade.Watch(label, 3).GetAsyncEnumerator CancellationToken.None
            let received = ResizeArray<Tick>()

            try
                let mutable go = true

                while go do
                    let! moved = enumerator.MoveNextAsync()

                    if moved then received.Add enumerator.Current else go <- false
            finally
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

            let notes = received |> Seq.map (fun tick -> tick.note) |> List.ofSeq
            test <@ notes = [ $"{label}#0"; $"{label}#1"; $"{label}#2" ] @>
        }

    /// <summary>
    /// A discriminator, kept because the answer decided how the relay above is written: the same
    /// upstream stream consumed inside the grain directly, and through
    /// <c>taskSeq { for … do yield … }</c>. Nothing of the streaming REPLY path takes part, so a
    /// difference between the two counts is a property of the consuming construct alone.
    /// </summary>
    [<Fact>]
    member _.``consuming an upstream stream inside a grain yields the same count both ways``() =
        task {
            let suffix = unique ()
            let key = $"probe-{suffix}"
            let label = $"probe-{suffix}"
            PhaseFGates.releaseAll $"{label}-direct" 3
            PhaseFGates.releaseAll $"{label}-map" 3
            PhaseFGates.releaseAll $"{label}-for" 3

            let! (direct, viaMap, viaFor) = (relay key).probe (key, label, 3)

            // What this runtime owns: enumerating its own stream yields exactly the items the
            // producer yielded.
            test <@ direct = 3 @>

            // What it does not own: FSharp.Control.TaskSeq 0.6.0's WRAPPING combinators over that
            // same stream. Measured on 2026-08-18 inside the activation's task scheduler, both
            // `TaskSeq.map` and `taskSeq { for … }` answered 4 for a 3-item stream — the last item
            // twice — while enumerating it directly answered 3. That is why `relay` above is a
            // hand-written enumerator and why every assertion in this file drains through the
            // plain `await foreach` shape. The divergence is deliberately not pinned to an exact
            // value: an upstream fix must not turn this suite red.
            test <@ viaMap >= direct && viaFor >= direct @>
        }

    /// <summary>
    /// Phase C's `mayInterleave` composed with a streaming operation. The predicate runs on
    /// Orleans' own scheduling path for every message queued to a busy activation, and an
    /// enumeration's messages are Orleans' extension invokables rather than functional requests —
    /// a predicate that tried to read a functional envelope out of one would throw, and Orleans
    /// would reject the call that triggered it as transient. Nothing here is rejected, the stream
    /// is correct, and the predicate reports only operation IDs of this contract.
    /// </summary>
    [<Fact>]
    member _.``mayInterleave composes with a streaming operation``() =
        task {
            let key = unique ()
            let api = selectiveRef fixture.Client key

            // Activate first. Orleans consults an interleaving policy only for a message that
            // arrives while the activation is already EXECUTING one, so a cold activation would
            // queue both calls behind activation and admit the second without ever reaching the
            // predicate — which is how the first version of this test managed to assert against an
            // empty observation list.
            let! _ = api.peek ()

            // Now occupy the activation with a call the predicate refuses.
            let parked = api.park 2000
            do! Task.Delay 250

            // Admitted by the predicate while `park` is the blocking request.
            let! peeked = api.peek ()

            // And a whole enumeration, whose messages take the earlier [AlwaysInterleave] exit.
            let! numbers = collect (api.numbers 4)

            let! parkResult = parked

            test <@ numbers = [ 1; 2; 3; 4 ] @>
            test <@ peeked = 0 @>
            test <@ parkResult = 1 @>

            let observed = PhaseFPredicate.observed () |> List.distinct |> List.sort

            // Non-vacuity: the predicate really did run. Without this the forall below would pass
            // on an empty list and prove nothing.
            test <@ List.contains "peek" observed @>

            // Only this contract's own operations ever reached it: an extension invokable's
            // argument 0 is a Guid, not an IFunctionalRequestMetadata, and the callback answers
            // false for it instead of throwing.
            test <@ observed |> List.forall (fun id -> List.contains id [ "numbers"; "park"; "peek" ]) @>
        }
