# Post-003 backlog

Follow-ups identified during the 003 implementation session (2026-08-16/17).
Each item is grounded in a verified finding; pointers name the evidence.

## Runtime features

1. **First-class placement operations** — `statelessWorker maxLocalWorkers` and
   a general `placement` custom operation on the contract/definition, publishing
   the placement-strategy grain properties the way `examples/feature-tour`
   §10 composes today via an application `IGrainPropertiesProvider`
   (measured there: 8 concurrent calls → 4 activations). Sealing must reject
   durable attachments (`stateFrom`/`usePersistentState`/`onReminder`) for
   stateless definitions.
2. **Implicit stream subscriptions for functional grains** — two-part wall,
   both parts located (feature-tour §11 experiment): (a) publish the
   stream-binding grain properties; (b) stream-consumer extension support in
   the functional activation (today Orleans activates the grain, then drops
   the item: "I don't have any subscriber for that stream"). Same machinery
   would unlock implicit broadcast-channel consumption.
3. **`IAsyncEnumerable<'T>` replies** — a new transport capability (chunked
   streaming protocol over the fixed envelope family), not sugar; API fields
   are exactly `'Arg -> Task<'Reply>` by design. Alternatives documented in
   the feature tour: Orleans streams (works today, explicit) or paged
   `readOnly` queries.
4. **Per-operation visibility in Orleans Dashboard** — all functional calls
   surface as one CLR method (`DispatchAsync`); operation granularity exists
   in Activity tags/logs but not in the Dashboard method table. Idea: a
   profiler `IIncomingGrainCallFilter` republishing per-operation stats.

## Library polish

5. **`GrainRef` third authoring style** — hand-written Orleans interface +
   class grains (`ICounterGrain`-style) remain neither deprecated nor
   functional; revisit once 003 adoption settles (survey: `GrainRef.*` ~133
   usage sites, mostly examples).
6. **Analyzer hint for derived grain types** — an Orleans.FSharp.Analyzers
   rule suggesting an explicit `grainType` on contracts whose definitions are
   likely to become durable (complements the sealing-time enforcement).
7. **Functional transactions** — was out of 003 scope by spec; **delivered by
   spec 004 item 2** (`transactional` contract operations + `transactionalStateFrom`
   facets over Orleans' own `TransactionRequest` invokable base). The classic
   `AddFSharpTransactionalGrain` path stays KEEP-listed;
   `examples/bank-transactions` now leads with the functional twin and keeps
   the classic definition compiled, registered, and tested beside it.
8. **samples/ code rewrite depth** — the three pattern READMEs now teach the
   functional model with compiled fences; turning them into buildable projects
   (like examples/) is future work, noted in their deprecation banners.

## Known environmental notes

9. **CS0618 blanket pragmas in `Orleans.FSharp.CodeGen/SampleGrainImpls.cs`**
   mask any obsolete-API use inside those C# stubs; enumerated as acceptable
   for the deprecated-support assembly, revisit when the classic model is
   removed entirely.
10. **Orleans Dashboard rendering** was reasoned from manifest identity +
    Dashboard's grain-type keying, not run in-session; first live Dashboard
    look should confirm the per-actor rows and the flat DispatchAsync method
    column.
11. **Two load-sensitive wall-clock tests flake under machine load** (found
    during spec-004 Phase C re-verification, 2026-08-18, proven pre-existing
    by 6 interleaved A/B full-suite runs on base vs the C1 tree — 0 failures
    on either side; failures cluster by run duration, not by tree):
    `GrainResilienceTests.withTimeout …` (a 50 ms Polly budget — **fixed
    2026-08-20** with item 12: both wall-clock timeout tests were replaced by
    a gated-call shape that asserts which task completed first under a 30 s
    net, so no budget is asserted at all) and
    `FunctionalPhase5IntegrationTests … KeepAlive=false does not extend
    collection lifetime` (an Orleans collection-age window, still open). Both assert pure
    wall-clock budgets; hardening candidates (wider budgets, virtual time, or
    quarantine-with-retry), on their own ticket. Same family (added during
    Phase D, outcome-agnostic but wall-clock-shaped): the transactional
    contention test `concurrent transactions on one state each run once and
    apply once` (a 3 s hold against a 2 s LockAcquireTimeout), and the
    pre-existing `TemplateTests.template generated tests all pass` (shells
    out to `dotnet new`; failed once under full-suite load, passes alone).
    Phase E observation (2026-08-18): one full-suite run at Orleans 10.2.2
    reported a single failure whose name was NOT captured (only the tail was
    read), and it did not reproduce in 6 subsequent full-suite runs (4 at
    10.2.2, 2 at the floor) nor in 4 runs of the Phase E suites alone. It is
    recorded unattributed rather than pinned on this family: without the name
    that would be a guess. It does raise the priority of the hardening ticket —
    and of always capturing full test output on a verification run.
12. **[FIXED 2026-08-20, branch `fix/resilience-and-stream-cursor`]**
    **`GrainResilience.withTimeout` cannot honour its own contract** (docs
    proofread pass, 2026-08-20, measured twice): the protected operation is
    `unit -> Task<'T>`, so `execute` starts the call before Polly's pipeline
    runs and the timeout token reaches nothing — a 100 ms timeout over an
    800 ms call returns the result after ~810 ms with no exception. The
    timeout only bounds Polly's inter-retry delays, and `AddTimeout` is
    outermost (spans the whole retry sequence), contradicting the
    "per-attempt" XML doc. Fix candidates: take `CancellationToken ->
    Task<'T>` and thread Polly's token, or retire the member. Docs already
    state the measured behaviour. Note: backlog item 11's flaky
    `withTimeout` test gains a new reading — it exercises a timeout that
    does not time out.
    *Resolution:* the reported mechanism was wrong in one respect — `f()` is
    invoked *inside* the Polly callback, so retry always re-invoked it (3
    calls for `MaxRetryAttempts = 2`, measured). The defect was that the
    callback handed Polly an already-started, non-cancellable task, which
    the timeout strategy can only await. The in-flight task is now awaited
    under the pipeline's token (~101 ms on the 800 ms case), abandoning the
    call rather than cancelling it; `executeCancellable` /
    `withTimeoutCancellable` were added for operations that take a token.
    The timeout stays outermost and is documented as a whole-sequence
    deadline.
13. **[FIXED 2026-08-20, branch `fix/resilience-and-stream-cursor`]**
    **The classic `Stream` module cannot produce a stream cursor** (same
    pass): `Stream.getSequenceToken` is a permanent `None` stub AND
    `subscribe`/`subscribeFrom` discard the token their Orleans callback
    receives — so the module's own `subscribeFrom` rewind entry point is
    unreachable from within the module. Fix candidates: a handler overload
    receiving the token, or documenting Orleans' own `SubscribeAsync` as
    the path (docs already point there).
    *Resolution:* `subscribeWithToken` / `subscribeFromWithToken` added
    (handler takes `StreamSequenceToken option`), `getSequenceToken`
    deprecated with `[<Obsolete>]` (warning) and unchanged behaviour, and the
    full rewind loop is covered by an integration test over the fixture's
    memory streams.
14. **`Stream.resumeAll` still discards the cursor** (found while fixing
    item 13, not fixed): the reactivation path re-attaches a
    `'T -> Task<unit>` handler, so a durable subscription that resumes after
    deactivation loses the ability to checkpoint that `subscribeWithToken`
    now gives a fresh subscription. Cost of closing it: one more public
    function (`resumeAllWithToken`, ~12 lines), an api-reference row, a doc
    paragraph, and one integration test. Left out of the 4.0.1 fix on scope
    grounds — the surface addition is the owner's call.
