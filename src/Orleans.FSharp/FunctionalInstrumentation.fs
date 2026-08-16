namespace Orleans.FSharp

open System.Collections.Concurrent
open System.Threading

/// <summary>
/// Counters of the structural properties the functional runtime promises: that API-shape
/// reflection, selector evaluation, and generic closing happen while a contract is sealed or a
/// reference is bound and never on a bound call, and that no two concurrent calls share a
/// serializer session.
/// </summary>
[<Sealed; AllowNullLiteral>]
type internal FunctionalCounters() =

    /// <summary>Number of <c>ApiShape</c> reflections.</summary>
    [<DefaultValue>]
    val mutable ApiShapeBuilds: int

    /// <summary>Number of selector executions against a probe record.</summary>
    [<DefaultValue>]
    val mutable SelectorEvaluations: int

    /// <summary>Number of reflective generic method or type closings.</summary>
    [<DefaultValue>]
    val mutable GenericClosings: int

    /// <summary>Number of top-level payload serializations.</summary>
    [<DefaultValue>]
    val mutable PayloadSerializations: int

    /// <summary>Number of top-level payload deserializations.</summary>
    [<DefaultValue>]
    val mutable PayloadDeserializations: int

    /// <summary>Number of serializer sessions rented from the pool.</summary>
    [<DefaultValue>]
    val mutable SessionRentals: int

    /// <summary>
    /// Number of times a session was rented while another rental of the very same session
    /// object was still outstanding. Any value above zero means concurrent calls shared one.
    /// </summary>
    [<DefaultValue>]
    val mutable SessionConflicts: int

    /// <summary>Sessions currently rented, keyed by reference identity.</summary>
    member val ActiveSessions = ConcurrentDictionary<obj, int>(HashIdentity.Reference) with get

/// <summary>
/// Ambient, flow-scoped instrumentation of the functional runtime.
/// </summary>
/// <remarks>
/// The counters live in an <see cref="T:System.Threading.AsyncLocal`1" />, so one test observes
/// exactly the work its own call flow performed even while the rest of the suite runs in
/// parallel. Nothing is counted unless a scope is open, which keeps a bound call free of any
/// observation cost beyond one ambient read.
/// </remarks>
module internal FunctionalInstrumentation =

    let private current = AsyncLocal<FunctionalCounters>()

    /// <summary>Start counting in the current call flow and return the fresh counters.</summary>
    let start () =
        let counters = FunctionalCounters()
        current.Value <- counters
        counters

    /// <summary>Stop counting in the current call flow.</summary>
    let stop () = current.Value <- null

    /// <summary>One <c>ApiShape</c> was reflected and cached.</summary>
    let countApiShapeBuild () =
        match current.Value with
        | null -> ()
        | counters -> Interlocked.Increment &counters.ApiShapeBuilds |> ignore

    /// <summary>One selector was executed against a probe record.</summary>
    let countSelectorEvaluation () =
        match current.Value with
        | null -> ()
        | counters -> Interlocked.Increment &counters.SelectorEvaluations |> ignore

    /// <summary>One generic method or type was closed by reflection.</summary>
    let countGenericClosing () =
        match current.Value with
        | null -> ()
        | counters -> Interlocked.Increment &counters.GenericClosings |> ignore

    /// <summary>One top-level payload value was serialized.</summary>
    let countPayloadSerialization () =
        match current.Value with
        | null -> ()
        | counters -> Interlocked.Increment &counters.PayloadSerializations |> ignore

    /// <summary>One top-level payload value was deserialized.</summary>
    let countPayloadDeserialization () =
        match current.Value with
        | null -> ()
        | counters -> Interlocked.Increment &counters.PayloadDeserializations |> ignore

    /// <summary>Record that a serializer session was rented from the pool.</summary>
    let trackSessionRented (session: obj) =
        match current.Value with
        | null -> ()
        | counters ->
            Interlocked.Increment &counters.SessionRentals |> ignore

            if not (counters.ActiveSessions.TryAdd(session, 1)) then
                Interlocked.Increment &counters.SessionConflicts |> ignore

    /// <summary>Record that a serializer session was returned to the pool.</summary>
    let trackSessionReturned (session: obj) =
        match current.Value with
        | null -> ()
        | counters -> counters.ActiveSessions.TryRemove session |> ignore
