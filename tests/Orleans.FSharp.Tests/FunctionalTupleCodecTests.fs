/// <summary>
/// Tuple payloads over the functional transport (spec 003, P0 regression suite).
/// </summary>
/// <remarks>
/// Orleans owns <c>System.Tuple</c> — an F# tuple argument or reply never reaches the F# binary
/// codec whole. Orleans' own tuple codec decomposes it and hands each ELEMENT to the F# codec as
/// its own field, so both halves of top-level payload handling have to know about elements:
/// the declaration table (an element's <c>FullName</c> is what gets embedded, and
/// <c>Type.GetType</c> cannot resolve a generic whose outer type lives in FSharp.Core) and the
/// expected-type guard (an element is legitimately not assignable to the tuple that was asked
/// for). Before the fix, every shape below failed with one of those two diagnostics.
///
/// Two suites, because one of them alone could pass for the wrong reason. The declaration table
/// is process-global, so a shape spelled over a WIDELY-USED element type (<c>string option</c>)
/// can be resolved by name thanks to some other test file having declared that element for its
/// own payload — the exact order-dependence that made the original bug report's table look
/// inconsistent. The shapes closed over this file's private <c>TupleProbe</c> cannot be declared
/// by anything else in the process and so pin the declaration half deterministically; the
/// verbatim shapes pin the expectation half, which no declaration can mask.
/// </remarks>
module Orleans.FSharp.Tests.FunctionalTupleCodecTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp
open Orleans.FSharp.Tests.FunctionalTransportHarness

type TupleActor = private TupleActor of unit
type ProbeActor = private ProbeActor of unit

/// <summary>A non-generic DU element, to separate "generic" from "in a tuple" as causes.</summary>
type Colour =
    | Red
    | Green

/// <summary>A record whose own fields are options — the shape that always worked.</summary>
type TwoOptions = { first: string option; second: string option }

/// <summary>
/// An element type private to this file. Nothing else in the process declares it or any generic
/// closed over it, so a shape built from it can only round-trip if the declaration of the TUPLE
/// declared the element too.
/// </summary>
type TupleProbe = { probe: string }

// ──────────────────────────────────────────────────────────────────────────────
// The nine shapes of the original report, argument and reply position at once
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Every operation echoes its argument, so one call exercises the shape in argument position
/// (client serialize → target deserialize) and in reply position (target serialize → client
/// deserialize). A shape that only broke one way still fails this.
/// </summary>
[<NoEquality; NoComparison>]
type ShapeApi =
    { optionAlone: string option -> Task<string option>
      duPair: (Colour * Colour) -> Task<Colour * Colour>
      stringPair: (string * string) -> Task<string * string>
      optionRecord: TwoOptions -> Task<TwoOptions>
      optionList: string option list -> Task<string option list>
      optionPair: (string option * string option) -> Task<string option * string option>
      listPair: (string list * string list) -> Task<string list * string list>
      mapPair: (Map<string, int> * Map<string, int>) -> Task<Map<string, int> * Map<string, int>>
      twoArguments: (int option * int option) -> Task<int option> }

let private shapeContract =
    grainContract<TupleActor, string, ShapeApi> () {
        grainType "tuple.shapes"
        version 1
        stringKey
    }

let private bindShapes () =
    let services = buildServices true None
    let target = InMemoryTarget(services, "tuple.shapes", 1)

    target.Handle<string option, string option>("optionAlone", id)
    target.Handle<Colour * Colour, Colour * Colour>("duPair", id)
    target.Handle<string * string, string * string>("stringPair", id)
    target.Handle<TwoOptions, TwoOptions>("optionRecord", id)
    target.Handle<string option list, string option list>("optionList", id)
    target.Handle<string option * string option, string option * string option>("optionPair", id)
    target.Handle<string list * string list, string list * string list>("listPair", id)
    target.Handle<Map<string, int> * Map<string, int>, Map<string, int> * Map<string, int>>("mapPair", id)

    // The wire shape a two-argument operation produces: the arguments ARE a reference tuple.
    target.Handle<int option * int option, int option>(
        "twoArguments",
        fun (left, right) ->
            match left, right with
            | Some l, Some r -> Some(l + r)
            | _ -> None
    )

    (FunctionalGrain.rawRef shapeContract (InMemoryTransport(services, target.Dispatch)) "shapes").api

[<Fact>]
let ``a bare option round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.optionAlone (Some "solo")
        test <@ echoed = Some "solo" @>
    }

[<Fact>]
let ``a tuple of a non-generic union round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.duPair (Red, Green)
        test <@ echoed = (Red, Green) @>
    }

[<Fact>]
let ``a tuple of strings round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.stringPair ("left", "right")
        test <@ echoed = ("left", "right") @>
    }

[<Fact>]
let ``a record of options round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.optionRecord { first = Some "a"; second = None }
        test <@ echoed = { first = Some "a"; second = None } @>
    }

[<Fact>]
let ``a list of options round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.optionList [ Some "a"; None; Some "c" ]
        test <@ echoed = [ Some "a"; None; Some "c" ] @>
    }

[<Fact>]
let ``a tuple of options round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.optionPair (Some "a", Some "b")
        test <@ echoed = (Some "a", Some "b") @>
    }

[<Fact>]
let ``a tuple of lists round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.listPair ([ "a"; "b" ], [ "c" ])
        test <@ echoed = ([ "a"; "b" ], [ "c" ]) @>
    }

[<Fact>]
let ``a tuple of maps round-trips`` () =
    task {
        let api = bindShapes ()
        let! echoed = api.mapPair (Map [ "a", 1 ], Map [ "b", 2 ])
        test <@ echoed = (Map [ "a", 1 ], Map [ "b", 2 ]) @>
    }

[<Fact>]
let ``a two-argument operation over options round-trips`` () =
    task {
        // The tupled spelling is the only spelling; its wire argument is the reference tuple
        // that used to fail on the first call for any FSharp.Core generic element.
        let api = bindShapes ()
        let! sum = api.twoArguments (Some 2, Some 40)
        test <@ sum = Some 42 @>

        let! missing = api.twoArguments (Some 2, None)
        test <@ missing = None @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Element types nothing else in the process can have declared
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The same shapes closed over a private element type. Declaring the tuple is the ONLY way any
/// of these element names can reach the declaration table, and <c>Type.GetType</c> can resolve
/// none of them — the outer generic lives in FSharp.Core and <c>TupleProbe</c> lives in a test
/// assembly, neither of which the codec's resolver probes. If the recursive declaration is lost,
/// every one of these fails with "not found" regardless of what other tests ran first.
/// </summary>
[<NoEquality; NoComparison>]
type ProbeApi =
    { probePair: (TupleProbe * TupleProbe) -> Task<TupleProbe * TupleProbe>
      probeOptionPair: (TupleProbe option * TupleProbe option) -> Task<TupleProbe option * TupleProbe option>
      probeListPair: (TupleProbe list * TupleProbe list) -> Task<TupleProbe list * TupleProbe list>
      probeMapPair: (Map<string, TupleProbe> * Map<string, TupleProbe>) -> Task<Map<string, TupleProbe> * Map<string, TupleProbe>>
      probeTriple: (TupleProbe option * TupleProbe list * Set<string>) -> Task<TupleProbe option * TupleProbe list * Set<string>>
      probeNested: ((TupleProbe option * TupleProbe option) * TupleProbe option) -> Task<(TupleProbe option * TupleProbe option) * TupleProbe option>
      probeArrayPair: (TupleProbe array * TupleProbe array) -> Task<TupleProbe array * TupleProbe array> }

let private probeContract =
    grainContract<ProbeActor, string, ProbeApi> () {
        grainType "tuple.probes"
        version 1
        stringKey
    }

let private bindProbes () =
    let services = buildServices true None
    let target = InMemoryTarget(services, "tuple.probes", 1)

    target.Handle<TupleProbe * TupleProbe, TupleProbe * TupleProbe>("probePair", id)

    target.Handle<TupleProbe option * TupleProbe option, TupleProbe option * TupleProbe option>(
        "probeOptionPair",
        id
    )

    target.Handle<TupleProbe list * TupleProbe list, TupleProbe list * TupleProbe list>("probeListPair", id)

    target.Handle<Map<string, TupleProbe> * Map<string, TupleProbe>, Map<string, TupleProbe> * Map<string, TupleProbe>>(
        "probeMapPair",
        id
    )

    target.Handle<TupleProbe option * TupleProbe list * Set<string>, TupleProbe option * TupleProbe list * Set<string>>(
        "probeTriple",
        id
    )

    target.Handle<(TupleProbe option * TupleProbe option) * TupleProbe option, (TupleProbe option * TupleProbe option) * TupleProbe option>(
        "probeNested",
        id
    )

    target.Handle<TupleProbe array * TupleProbe array, TupleProbe array * TupleProbe array>("probeArrayPair", id)

    (FunctionalGrain.rawRef probeContract (InMemoryTransport(services, target.Dispatch)) "probes").api

let private one = { probe = "one" }
let private two = { probe = "two" }

[<Fact>]
let ``a tuple of an undeclarable record round-trips`` () =
    task {
        let api = bindProbes ()
        let! echoed = api.probePair (one, two)
        test <@ echoed = (one, two) @>
    }

[<Fact>]
let ``a tuple of options over an undeclarable element round-trips`` () =
    task {
        let api = bindProbes ()
        let! echoed = api.probeOptionPair (Some one, None)
        test <@ echoed = (Some one, None) @>
    }

[<Fact>]
let ``a tuple of lists over an undeclarable element round-trips`` () =
    task {
        let api = bindProbes ()
        let! echoed = api.probeListPair ([ one; two ], [])
        test <@ echoed = ([ one; two ], []) @>
    }

[<Fact>]
let ``a tuple of maps over an undeclarable element round-trips`` () =
    task {
        let api = bindProbes ()
        let! echoed = api.probeMapPair (Map [ "k", one ], Map.empty)
        test <@ echoed = (Map [ "k", one ], Map.empty) @>
    }

[<Fact>]
let ``a three-element tuple of mixed generics round-trips`` () =
    task {
        let api = bindProbes ()
        let! echoed = api.probeTriple (Some one, [ two ], Set [ "s" ])
        test <@ echoed = (Some one, [ two ], Set [ "s" ]) @>
    }

[<Fact>]
let ``a tuple nested inside a tuple round-trips`` () =
    task {
        // The inner tuple is itself an element: the walk has to be transitive, not one level.
        let api = bindProbes ()
        let! echoed = api.probeNested ((Some one, None), Some two)
        test <@ echoed = ((Some one, None), Some two) @>
    }

[<Fact>]
let ``a tuple of arrays round-trips`` () =
    task {
        // Orleans owns arrays as well as tuples, so the element walk has to follow both.
        let api = bindProbes ()
        let! echoed = api.probeArrayPair ([| one |], [| two |])
        test <@ echoed = ([| one |], [| two |]) @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// The guard the fix widens is still a guard
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a payload naming a type outside the declared shape is still rejected`` () =
    // Widening the expected-type check to the declared tuple's constituents must not widen it to
    // anything else: a name that is neither the expected type nor one of its elements is refused.
    FSharpBinaryFormat.declareType typeof<TupleProbe>
    FSharpBinaryFormat.declareType typeof<Colour * Colour>

    let bytes = FSharpBinaryFormat.serializeWithType (box one) typeof<TupleProbe>

    let error =
        Assert.Throws<System.InvalidOperationException>(fun () ->
            FSharpBinaryFormat.ExpectedPayloadType.Scoped(
                typeof<Colour * Colour>,
                fun () -> FSharpBinaryFormat.deserializeWithType bytes null |> ignore
            ))

    test <@ error.Message.Contains "is not assignable to the expected type" @>
    test <@ error.Message.Contains "nor to any of its constituents" @>

[<Fact>]
let ``declaring a tuple declares its elements by name`` () =
    // The mechanism directly: after declaring only the tuple, an element's own bytes — which
    // carry the element's FullName and nothing else — resolve without any hint.
    FSharpBinaryFormat.declareType typeof<TupleProbe option * TupleProbe list>

    let elementBytes =
        FSharpBinaryFormat.serializeWithType (box (Some one)) typeof<TupleProbe option>

    let restored = FSharpBinaryFormat.deserializeWithType elementBytes null

    test <@ unbox<TupleProbe option> restored = Some one @>
