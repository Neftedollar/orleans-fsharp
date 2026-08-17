namespace Orleans.FSharp

open System.Collections.Generic

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
    (definition: FunctionalHostedDefinition, facets: FunctionalActivationFacet[]) =

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

    let mutable ephemeral: obj = null
    let mutable initialized = false

    /// <summary>Every attached facet of this activation, primary first.</summary>
    member _.Facets = facets

    /// <summary>
    /// True once activation step 3 has run. An activation whose storage read, initializer, or
    /// activation hook failed never reaches it, and then no primary state value exists.
    /// </summary>
    member _.IsInitialized = initialized

    /// <summary>The current authoritative primary state, boxed.</summary>
    member _.Current: obj =
        match primary with
        | Some facet -> facet.Blueprint.GetState facet.Instance
        | None -> ephemeral

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
    member _.Publish(value: obj) =
        match primary with
        | Some facet -> facet.Blueprint.SetState facet.Instance value
        | None -> ephemeral <- value

    /// <summary>Resolve an attached facet by its logical descriptor.</summary>
    member _.TryResolve(descriptor: PersistentStateDescriptor) =
        match byDescriptor.TryGetValue descriptor with
        | true, facet -> Some facet
        | _ -> None

    /// <summary>
    /// Step 3 of the activation order: initialize the ephemeral primary state and every attached
    /// holder which reports no durable record. Initializers populate memory only; nothing here
    /// writes storage.
    /// </summary>
    member _.Initialize(key: obj) =
        for facet in facets do
            if not (facet.Blueprint.RecordExists facet.Instance) then
                facet.Blueprint.SetState facet.Instance (facet.Blueprint.Initialize key)

        if primary.IsNone then
            ephemeral <- definition.CreateState key

        initialized <- true
