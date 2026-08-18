namespace Orleans.FSharp;

/// <summary>
/// The runtime-owned mutable cell that holds the log view of one functional journaled grain.
/// </summary>
/// <remarks>
/// <para>
/// Orleans' <c>ILogViewAdaptorFactory.MakeLogViewAdaptor</c> constrains the view type to
/// <c>class, new()</c> and the adaptors update it <b>in place</b>:
/// <c>ILogViewAdaptorHost.UpdateView(view, entry)</c> receives the very instance the adaptor is
/// folding into. An F# state value is an immutable record, union, or tuple — it has neither a
/// parameterless constructor nor a way to be mutated — so the runtime hands Orleans this cell
/// instead, exactly as <see cref="FunctionalTransactionalBox{TValue}"/> does for transactional
/// state.
/// </para>
/// <para>
/// The cell carries the state as <b>exact-type payload bytes</b> rather than as a live object,
/// which is what makes the journal durable and copyable at once:
/// </para>
/// <list type="bullet">
/// <item>
/// Durability: the StateStorage log-consistency provider persists the view itself. Bytes produced
/// by the definition's exact-type payload codec carry no CLR type names, so a stored view does not
/// depend on the assembly-qualified identity of the application's state type — the same byte
/// boundary the functional transport puts between a caller and a handler.
/// </item>
/// <item>
/// Copying: <c>PrimaryBasedLogViewAdaptor</c> deep-copies the view through the Orleans serializer
/// on construction and again for every tentative-state calculation. The functional runtime
/// registers the F# generalized codec <b>without</b> its generalized copier, so an ordinary F#
/// state type has no Orleans copier at all and that copy would fail with "copier not found". A
/// cell of <c>byte[]</c> plus <c>bool</c> is copied by Orleans' own generated copier, with no
/// registration of any kind.
/// </item>
/// </list>
/// </remarks>
[GenerateSerializer]
public sealed class FunctionalJournalView
{
    /// <summary>
    /// The current view, serialized as the definition's exact state type. Null until the first
    /// event has been folded in.
    /// </summary>
    [Id(0)]
    public byte[]? Payload { get; set; }

    /// <summary>
    /// False on a view Orleans materialized with <c>new()</c> rather than one this runtime wrote.
    /// </summary>
    /// <remarks>
    /// It is load-bearing rather than defensive. The StateStorage adaptor reads into a fresh
    /// <c>GrainStateWithMetaData&lt;TView&gt;</c>, whose constructor does <c>State = new TView()</c>,
    /// so the seeded initial view handed to <c>MakeLogViewAdaptor</c> is <b>discarded</b> on the
    /// first read of a grain with no stored record. The runtime therefore re-materializes the
    /// declared initial state whenever it meets a cell that was never written, instead of trusting
    /// the seed to survive. (The LogStorage adaptor folds into the seeded instance and keeps it;
    /// the two providers differ here, and only this flag makes the difference invisible upstream.)
    /// </remarks>
    [Id(1)]
    public bool HasValue { get; set; }
}

/// <summary>
/// One entry of a functional grain's journal: an application event serialized as the definition's
/// exact event type.
/// </summary>
/// <remarks>
/// Orleans constrains the log entry type to <c>class</c> only, so an F# union would be legal here.
/// It is bytes for the same two reasons the view is: the LogStorage provider persists every entry
/// forever, and the adaptors deep-copy notification payloads through the Orleans serializer.
/// </remarks>
[GenerateSerializer]
public sealed class FunctionalJournalEntry
{
    /// <summary>The event, serialized as the definition's exact event type.</summary>
    [Id(0)]
    public byte[] Payload { get; set; } = [];
}
