namespace Orleans.FSharp

open System.Collections.Generic
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>One attached persistent facet of one activation: its blueprint and the real
/// Orleans <c>IPersistentState</c> instance created for this activation, boxed.</summary>
[<ReferenceEquality>]
type internal FunctionalActivationFacet =
    {
        /// The stored-type-closed blueprint of the attached facet.
        Blueprint: FunctionalFacetBlueprint
        /// The Orleans facet created for this activation, boxed as <c>IPersistentState&lt;_&gt;</c>.
        Instance: obj
    }

/// <summary>
/// One attached transactional facet of one activation: its blueprint, the real Orleans
/// <c>ITransactionalState</c> instance created for this activation (boxed), and the boxed initial
/// value a read of a never-written state observes.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalActivationTransactionalFacet =
    {
        /// The stored-type-closed blueprint of the attached transactional facet.
        Blueprint: FunctionalTransactionalBlueprint
        /// The Orleans facet created for this activation, boxed as
        /// <c>ITransactionalState&lt;FunctionalTransactionalBox&lt;_&gt;&gt;</c>.
        Instance: obj
        /// The declared initial value for this activation's key, boxed. Computed once per
        /// activation rather than per call, exactly like a persistent facet's initializer.
        Initial: obj
    }

/// <summary>
/// The primary in-memory state of one activation, plus every attached facet.
/// </summary>
/// <remarks>
/// <para>
/// When <c>stateFrom</c> is configured, the authoritative primary holder is the selected
/// <c>IPersistentState&lt;'State&gt;.State</c> itself, so an explicit <c>ReadStateAsync</c> on
/// that facet replaces the authoritative value immediately, exactly as ordinary Orleans
/// semantics require. An ephemeral definition uses an activation-local cell instead.
/// </para>
/// <para>
/// Publication never calls <c>WriteStateAsync</c>: it only assigns the holder.
/// </para>
/// </remarks>
[<Sealed>]
type internal FunctionalActivationState
    (
        definition: FunctionalHostedDefinition,
        facets: FunctionalActivationFacet[],
        transactionalFacets: FunctionalActivationTransactionalFacet[]
    ) =

    let primary =
        match definition.PrimaryFacet with
        | Some blueprint -> facets |> Array.tryFind (fun facet -> obj.ReferenceEquals(facet.Blueprint, blueprint))
        | None -> None

    let byDescriptor =
        let map =
            Dictionary<PersistentStateDescriptor, FunctionalActivationFacet>(HashIdentity.Structural)

        for facet in facets do
            map.[facet.Blueprint.Descriptor] <- facet

        map

    let byTransactionalDescriptor =
        let map =
            Dictionary<TransactionalStateDescriptor, FunctionalActivationTransactionalFacet>(
                HashIdentity.Structural
            )

        for facet in transactionalFacets do
            map.[facet.Blueprint.Descriptor] <- facet

        map

    let mutable ephemeral: obj = null
    let mutable initialized = false
    let mutable journal: IFunctionalJournalAccess = null

    /// <summary>Every attached facet of this activation, primary first.</summary>
    member _.Facets = facets

    /// <summary>Every attached transactional facet of this activation, in declaration order.</summary>
    member _.TransactionalFacets = transactionalFacets

    /// <summary>
    /// True once activation step 3 has run. An activation whose storage read, initializer, or
    /// activation hook failed never reaches it, and then no primary state value exists.
    /// </summary>
    member _.IsInitialized = initialized

    /// <summary>
    /// The activation's journal, for a definition built with <c>journaledGrainFor</c>. Attached by
    /// the activator before the activation lifecycle starts, and <c>null</c> for every other
    /// definition.
    /// </summary>
    member _.Journal = journal

    /// <summary>Attach this activation's journal. Called once, from the grain activator.</summary>
    /// <param name="access">The journal access surface for this activation.</param>
    member _.AttachJournal(access: IFunctionalJournalAccess) = journal <- access

    /// <summary>
    /// The current authoritative primary state, boxed. For a journaled definition it is the
    /// confirmed fold of the journal: there is no in-memory cell and no persistent holder, so
    /// nothing else could be authoritative.
    /// </summary>
    member _.Current: obj =
        match journal with
        | null ->
            match primary with
            | Some facet -> facet.Blueprint.GetState facet.Instance
            | None -> ephemeral
        | access -> access.Current

    /// <summary>Publish a replacement primary state in memory. Never writes storage.</summary>
    /// <remarks>
    /// <para>
    /// This is an unsynchronized reference write — no lock, no volatile, no interlocked
    /// exchange — and it is safe because of where it runs, not because a reference write is
    /// atomic. Orleans serializes the turns of one activation: every path that reaches
    /// <c>Publish</c> is inside a dispatched request on that activation's own scheduler, and
    /// two such turns never overlap, so the write and every subsequent read of
    /// <see cref="Current" /> are ordered by the scheduler rather than by memory barriers.
    /// </para>
    /// <para>
    /// The claim survives the interleaving cases because none of them publish. A read-only or
    /// always-interleave request is the only kind Orleans admits while another request is in
    /// flight, and the dispatch rule discards its returned state rather than publishing it, so
    /// such a turn can only read. A declared timer publishes like a handler return, but sealing
    /// rejects <c>Interleave = true</c> on a whole-state timer hook, so a publishing timer tick
    /// never overlaps a request either. Reminder and activation hooks publish from turns that
    /// are not interleaving to begin with. What remains is one writer at a time, ordered against
    /// its own readers by the same scheduler — which is what makes the plain write correct.
    /// Admitting publication from an interleaving operation would invalidate this, and needs the
    /// synchronization this deliberately does without.
    /// </para>
    /// </remarks>
    /// <param name="value">The replacement primary state value, boxed.</param>
    /// <exception cref="System.InvalidOperationException">The activation is journaled, so its state is the fold of the journal and cannot be replaced directly.</exception>
    member _.Publish(value: obj) =
        match journal with
        | null ->
            match primary with
            | Some facet -> facet.Blueprint.SetState facet.Instance value
            | None -> ephemeral <- value
        | _ ->
            // Unreachable through any shipped path: every caller of Publish consults
            // FunctionalActivationState.Journal first and raises events instead, and a journaled
            // definition declares no timer, reminder, or stream hook that could reach here.
            fail
                JournalStage
                $"the state of grain type '{definition.GrainTypeName}' is the fold of its journal and cannot be replaced directly. Raise an event instead."

    /// <summary>Resolve an attached facet by its logical descriptor.</summary>
    /// <param name="descriptor">The logical descriptor of the persistent facet to resolve.</param>
    member _.TryResolve(descriptor: PersistentStateDescriptor) =
        match byDescriptor.TryGetValue descriptor with
        | true, facet -> Some facet
        | _ -> None

    /// <summary>Resolve an attached transactional facet by its logical descriptor.</summary>
    /// <param name="descriptor">The logical descriptor of the transactional facet to resolve.</param>
    member _.TryResolveTransactional(descriptor: TransactionalStateDescriptor) =
        match byTransactionalDescriptor.TryGetValue descriptor with
        | true, facet -> Some facet
        | _ -> None

    /// <summary>
    /// Step 3 of the activation order: initialize the ephemeral primary state and every attached
    /// holder which reports no durable record. Initializers populate memory only; nothing here
    /// writes storage.
    /// </summary>
    /// <param name="key">The activation's primary key, passed to each facet's and the primary state's initializer.</param>
    member _.Initialize(key: obj) =
        for facet in facets do
            if not (facet.Blueprint.RecordExists facet.Instance) then
                facet.Blueprint.SetState facet.Instance (facet.Blueprint.Initialize key)

        // A journaled definition has no in-memory cell to seed: its state is the fold of the
        // journal, which the log-view adaptor has already replayed by the time this runs.
        if primary.IsNone && isNull journal then
            ephemeral <- definition.CreateState key

        initialized <- true
