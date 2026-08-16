namespace Orleans.FSharp

open System.Collections.Concurrent
open System.Threading

/// <summary>
/// Process-wide counters for the functional grain runtime, used by the test suite to prove
/// structural properties that a wall-clock measurement cannot establish: that reflection,
/// selector evaluation, and generic closing happen while caching a shape or binding a
/// reference and never on a bound call, and that no two concurrent calls share a serializer
/// session.
/// </summary>
/// <remarks>
/// The counters are unconditional interlocked increments on construction-time paths only, so
/// a bound call never touches them. Session tracking is the one hot-path observation, and it
/// is off unless a test switches it on.
/// </remarks>
module internal FunctionalInstrumentation =

    let mutable private apiShapeBuilds = 0
    let mutable private selectorEvaluations = 0
    let mutable private genericClosings = 0
    let mutable private payloadSerializations = 0
    let mutable private payloadDeserializations = 0
    let mutable private sessionRentals = 0
    let mutable private sessionConflicts = 0
    let mutable private sessionTracking = 0

    /// <summary>Sessions currently rented, keyed by reference identity.</summary>
    let private activeSessions = ConcurrentDictionary<obj, int>(HashIdentity.Reference)

    /// <summary>One <c>ApiShape</c> was reflected and cached.</summary>
    let countApiShapeBuild () = Interlocked.Increment &apiShapeBuilds |> ignore

    /// <summary>One selector was executed against a probe record.</summary>
    let countSelectorEvaluation () = Interlocked.Increment &selectorEvaluations |> ignore

    /// <summary>One generic method or type was closed by reflection.</summary>
    let countGenericClosing () = Interlocked.Increment &genericClosings |> ignore

    /// <summary>One top-level payload value was serialized.</summary>
    let countPayloadSerialization () = Interlocked.Increment &payloadSerializations |> ignore

    /// <summary>One top-level payload value was deserialized.</summary>
    let countPayloadDeserialization () = Interlocked.Increment &payloadDeserializations |> ignore

    /// <summary>Number of <c>ApiShape</c> builds so far.</summary>
    let apiShapeBuildCount () = Volatile.Read &apiShapeBuilds

    /// <summary>Number of selector evaluations so far.</summary>
    let selectorEvaluationCount () = Volatile.Read &selectorEvaluations

    /// <summary>Number of reflective generic closings so far.</summary>
    let genericClosingCount () = Volatile.Read &genericClosings

    /// <summary>Number of top-level payload serializations so far.</summary>
    let payloadSerializationCount () = Volatile.Read &payloadSerializations

    /// <summary>Number of top-level payload deserializations so far.</summary>
    let payloadDeserializationCount () = Volatile.Read &payloadDeserializations

    /// <summary>Number of serializer sessions rented while tracking was enabled.</summary>
    let sessionRentalCount () = Volatile.Read &sessionRentals

    /// <summary>
    /// Number of times a session was rented while another rental of the very same session
    /// object was still outstanding. Any value above zero means concurrent calls shared a
    /// session.
    /// </summary>
    let sessionConflictCount () = Volatile.Read &sessionConflicts

    /// <summary>Start observing serializer-session rentals.</summary>
    let beginSessionTracking () =
        activeSessions.Clear()
        Volatile.Write(&sessionRentals, 0)
        Volatile.Write(&sessionConflicts, 0)
        Volatile.Write(&sessionTracking, 1)

    /// <summary>Stop observing serializer-session rentals.</summary>
    let endSessionTracking () = Volatile.Write(&sessionTracking, 0)

    /// <summary>Record that a session was rented from the pool.</summary>
    let trackSessionRented (session: obj) =
        if Volatile.Read &sessionTracking <> 0 then
            Interlocked.Increment &sessionRentals |> ignore

            if not (activeSessions.TryAdd(session, 1)) then
                Interlocked.Increment &sessionConflicts |> ignore

    /// <summary>Record that a session was returned to the pool.</summary>
    let trackSessionReturned (session: obj) =
        if Volatile.Read &sessionTracking <> 0 then
            activeSessions.TryRemove session |> ignore
