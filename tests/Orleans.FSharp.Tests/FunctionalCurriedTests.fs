/// <summary>
/// Curried API-record fields for spec 003: the canonicalization to the tuple, the wire
/// equivalence of the two spellings, the argument order the bound curried closure builds,
/// the policies and ID overrides on a curried field, and the hot-path promise.
/// </summary>
module Orleans.FSharp.Tests.FunctionalCurriedTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Tests.FunctionalTransportHarness

type CurriedActor = private CurriedActor of unit
type TupledActor = private TupledActor of unit

/// <summary>
/// The curried spelling. Every argument of <c>say</c> and <c>wide</c> has the same type on
/// purpose: a closure that assembled the tuple in the wrong order would still type-check, so
/// only a value assertion can catch it.
/// </summary>
[<NoEquality; NoComparison>]
type WireApi =
    { say: string -> string -> Task<int64>
      typing: string -> bool -> Task<unit>
      wide: string -> string -> string -> string -> string -> string -> string -> Task<string> }

/// <summary>The tupled spelling of exactly the same three operations.</summary>
[<NoEquality; NoComparison>]
type WireTupledApi =
    { say: (string * string) -> Task<int64>
      typing: (string * bool) -> Task<unit>
      wide: (string * string * string * string * string * string * string) -> Task<string> }

// Same grain type, same version, same field names: the two contracts differ only in how their
// API record spells the operations, which is what "wire-identical spellings" has to mean.
let private curriedContract =
    grainContract<CurriedActor, string, WireApi> () {
        grainType "curried.wire"
        version 2
        stringKey

        operationId "chat" (_.say)
        readOnly (_.wide)
        oneWay (_.typing)
        alwaysInterleave (_.typing)
    }

let private tupledContract =
    grainContract<TupledActor, string, WireTupledApi> () {
        grainType "curried.wire"
        version 2
        stringKey

        operationId "chat" (_.say)
        readOnly (_.wide)
        oneWay (_.typing)
        alwaysInterleave (_.typing)
    }

// ──────────────────────────────────────────────────────────────────────────────
// Fixtures
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A target which answers the canonical tupled operations of either spelling.</summary>
let private newTarget (services: System.IServiceProvider) =
    let target = InMemoryTarget(services, "curried.wire", 2)
    target.Handle<string * string, int64>("chat", fun (first, second) -> int64 (first.Length - second.Length))
    target.Handle<string * bool, unit>("typing", fun _ -> ())

    target.Handle<string * string * string * string * string * string * string, string>(
        "wide",
        fun (a1, a2, a3, a4, a5, a6, a7) -> String.concat "|" [ a1; a2; a3; a4; a5; a6; a7 ]
    )

    target

let private bindCurried () =
    let services = buildServices true None
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)
    transport, FunctionalGrain.rawRef curriedContract transport "general"

let private bindTupled () =
    let services = buildServices true None
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)
    transport, FunctionalGrain.rawRef tupledContract transport "general"

// ──────────────────────────────────────────────────────────────────────────────
// Wire equivalence
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// The canonicalization claim in full: two contracts whose API records differ only in currying
/// produce the same operation ID, the same protocol token, the same admission flags, and — for
/// the same values — byte-identical payloads. That is what makes the two spellings
/// interchangeable across versions of one application.
/// </remarks>
[<Fact>]
let ``the two spellings produce identical wire artifacts for the same values`` () =
    let curriedTransport, curried = bindCurried ()
    let tupledTransport, tupled = bindTupled ()

    task {
        let! curriedReply = curried.api.say "alpha" "be"
        let! tupledReply = tupled.api.say ("alpha", "be")
        do! curried.api.typing "alice" true
        do! tupled.api.typing ("alice", true)

        let! curriedWide = curried.api.wide "1" "2" "3" "4" "5" "6" "7"
        let! tupledWide = tupled.api.wide ("1", "2", "3", "4", "5", "6", "7")

        test <@ curriedReply = tupledReply @>
        test <@ curriedWide = tupledWide @>

        let describe (call: RecordedCall) =
            call.Envelope.GrainType,
            call.Envelope.ContractVersion,
            call.Envelope.OperationId,
            call.Envelope.AdmissionFlags,
            call.Envelope.ProtocolToken,
            call.Envelope.Payload

        test <@ (curriedTransport.Calls |> Array.map describe) = (tupledTransport.Calls |> Array.map describe) @>
        test <@ curriedTransport.Calls |> Array.map (fun call -> call.IsOneWay) = [| false; true; false |] @>
    }

[<Fact>]
let ``both spellings reach the same grain identity`` () =
    let _, curried = bindCurried ()
    let _, tupled = bindTupled ()

    test <@ curried.GrainId = tupled.GrainId @>

// ──────────────────────────────────────────────────────────────────────────────
// Argument order
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a curried bound field builds its tuple in declaration order`` () =
    let services = buildServices true None
    let target = InMemoryTarget(services, "curried.wire", 2)
    let observed = ResizeArray<string * string>()

    target.Handle<string * string, int64>(
        "chat",
        fun pair ->
            observed.Add pair
            0L
    )

    target.Handle<string * bool, unit>("typing", fun _ -> ())

    target.Handle<string * string * string * string * string * string * string, string>(
        "wide",
        fun (a1, a2, a3, a4, a5, a6, a7) -> String.concat "|" [ a1; a2; a3; a4; a5; a6; a7 ]
    )

    let transport = InMemoryTransport(services, target.Dispatch)
    let reference = FunctionalGrain.rawRef curriedContract transport "general"

    task {
        let! _ = reference.api.say "first" "second"
        test <@ List.ofSeq observed = [ "first", "second" ] @>
    }

[<Fact>]
let ``a curried seven-argument field keeps every position`` () =
    let _transport, reference = bindCurried ()

    task {
        let! reply = reference.api.wide "a" "b" "c" "d" "e" "f" "g"
        test <@ reply = "a|b|c|d|e|f|g" @>
    }

/// <remarks>
/// Partial application of a bound curried field must not send anything: the send fires at the
/// last argument, which is what makes the closure a drop-in for the field's declared type.
/// </remarks>
[<Fact>]
let ``a partially applied curried field sends nothing until the last argument`` () =
    let transport, reference = bindCurried ()

    let partial = reference.api.say "first"
    test <@ transport.Calls.Length = 0 @>

    task {
        let! _ = partial "second"
        test <@ transport.Calls.Length = 1 @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Policies on curried fields
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``policies and ID overrides apply to curried fields exactly as to tupled ones`` () =
    let transport, reference = bindCurried ()

    task {
        let! _ = reference.api.say "a" "b"
        do! reference.api.typing "alice" true
        let! _ = reference.api.wide "1" "2" "3" "4" "5" "6" "7"

        let sent =
            transport.Calls
            |> Array.map (fun call -> call.Envelope.OperationId, call.Envelope.AdmissionFlags, call.IsOneWay)

        test
            <@
                sent = [| "chat", AdmissionFlags.None, false
                          "typing", AdmissionFlags.OneWay ||| AdmissionFlags.AlwaysInterleave, true
                          "wide", AdmissionFlags.ReadOnly, false |]
            @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Hot path
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// The curried closure is preclosed while the contract is sealed, exactly like the tupled one,
/// so a curried call reflects nothing and closes no generic either.
/// </remarks>
[<Fact>]
let ``a bound curried call performs no reflection, selector evaluation, or generic closing`` () =
    let transport, reference = bindCurried ()

    task {
        // Warm the payload codecs; the once-per-type codec build is not what this test watches.
        let! _ = reference.api.say "warm" "warm"
        do! reference.api.typing "warm" false
        let! _ = reference.api.wide "1" "2" "3" "4" "5" "6" "7"

        let counters = FunctionalInstrumentation.start ()

        try
            let! _ = reference.api.say "alpha" "be"
            do! reference.api.typing "alice" true
            let! _ = reference.api.wide "a" "b" "c" "d" "e" "f" "g"

            test <@ counters.ApiShapeBuilds = 0 @>
            test <@ counters.SelectorEvaluations = 0 @>
            test <@ counters.GenericClosings = 0 @>
            test <@ counters.CodecBuilds = 0 @>
            test <@ counters.PayloadSerializations > 0 @>
        finally
            FunctionalInstrumentation.stop ()

        test <@ transport.Calls.Length = 6 @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Definitions
// ──────────────────────────────────────────────────────────────────────────────

type WireState = { seen: string }

[<Fact>]
let ``a definition binds tupled handlers to curried fields`` () =
    let definition =
        grainFor curriedContract {
            defaultState (fun () -> { seen = "" })
            handle2 (_.say) (fun _ state (first, second) -> task { return state, int64 (first.Length + second.Length) })
            handle2 (_.typing) (fun _ state (user, isTyping) -> task { return { seen = $"{user}/{isTyping}" }, () })

            handle7
                (_.wide)
                (fun _ state (a1, a2, a3, a4, a5, a6, a7) ->
                    task { return state, String.concat "-" [ a1; a2; a3; a4; a5; a6; a7 ] })
        }

    test <@ definition.GrainTypeName = "curried.wire" @>
    test <@ definition.Handlers.Count = 3 @>

/// <summary>
/// <c>unit</c> as the FIRST of several curried arguments: an ordinary tuple slot, not the
/// zero-input marker. Only a sole <c>unit</c> argument means "no domain input".
/// </summary>
type UnitFirstActor = private UnitFirstActor of unit

[<NoEquality; NoComparison>]
type UnitFirstApi = { go: unit -> bool -> Task<int> }

let private unitFirstContract =
    grainContract<UnitFirstActor, string, UnitFirstApi> () {
        grainType "curried.unitfirst"
        stringKey
    }

/// <remarks>
/// The asymmetry is deliberate and is pinned here so it cannot drift silently: a SOLE
/// <c>unit</c> argument stays a zero-input operation and is never canonicalized to a one-tuple,
/// while a <c>unit</c> that opens a curried chain is an ordinary tuple slot. Only <c>unit</c>
/// AFTER the first position is rejected, because there it would read like an absent argument.
/// The value assertion matters more than the shape one: it proves <c>unit * bool</c> actually
/// serializes and round-trips, rather than merely reflecting.
/// </remarks>
[<Fact>]
let ``unit opening a curried chain is an ordinary tuple slot`` () =
    let shape = ApiShape.of'<UnitFirstApi> ()

    test <@ shape.Operations.[0].ArgumentTypes = [| typeof<unit>; typeof<bool> |] @>
    test <@ shape.Operations.[0].ArgumentType = typeof<unit * bool> @>

    let services = buildServices true None
    let target = InMemoryTarget(services, "curried.unitfirst", 1)
    target.Handle<unit * bool, int>("go", fun ((), flag) -> if flag then 1 else 0)
    let transport = InMemoryTransport(services, target.Dispatch)
    let reference = FunctionalGrain.rawRef unitFirstContract transport "k"

    task {
        let! reply = reference.api.go () true
        test <@ reply = 1 @>
    }
