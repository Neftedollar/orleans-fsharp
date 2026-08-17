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
7. **Functional transactions** — out of 003 scope by spec; the classic
   `AddFSharpTransactionalGrain` path is KEEP-listed and demonstrated in
   `examples/bank-transactions`. A functional-first design would need its own
   spec (transactional state facets, `ITransactionalState` shaping).
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
    `GrainResilienceTests.withTimeout …` (a 50 ms Polly budget) and
    `FunctionalPhase5IntegrationTests … KeepAlive=false does not extend
    collection lifetime` (an Orleans collection-age window). Both assert pure
    wall-clock budgets; hardening candidates (wider budgets, virtual time, or
    quarantine-with-retry), on their own ticket.
