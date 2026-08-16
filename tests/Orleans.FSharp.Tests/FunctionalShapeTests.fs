/// <summary>
/// Shape and selector tests for spec 003 Phase 1: API-record reflection, per-field probe
/// sentinels, selector resolution by physical identity, and shape caching.
/// </summary>
module Orleans.FSharp.Tests.FunctionalShapeTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ──────────────────────────────────────────────────────────────────────────────
// Valid API records
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A well-formed API record with three distinct operation shapes.</summary>
[<NoEquality; NoComparison>]
type SampleApi =
    { join: string -> Task<unit>
      say: int -> Task<string>
      history: int -> Task<string list> }

/// <summary>Two fields with identical function types; selectors must still be distinguished.</summary>
[<NoEquality; NoComparison>]
type TwinApi =
    { first: int -> Task<unit>
      second: int -> Task<unit> }

/// <summary>A closed constructed generic record is a valid API type.</summary>
[<NoEquality; NoComparison>]
type GenericApi<'T> = { get: unit -> Task<'T> }

// ──────────────────────────────────────────────────────────────────────────────
// Invalid API records
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A struct record is invalid.</summary>
[<Struct>]
type StructApi = { run: int -> Task<unit> }

/// <summary>A non-function field is invalid.</summary>
type NonFunctionApi = { value: int }

/// <summary>An <c>Async</c> range is invalid.</summary>
[<NoEquality; NoComparison>]
type AsyncApi = { go: int -> Async<int> }

/// <summary>A <c>ValueTask</c> range is invalid.</summary>
[<NoEquality; NoComparison>]
type ValueTaskApi = { go: int -> ValueTask<int> }

/// <summary>A non-generic <c>Task</c> range is invalid.</summary>
[<NoEquality; NoComparison>]
type PlainTaskApi = { go: int -> Task }

/// <summary>A curried multi-input field is valid sugar for the tupled spelling.</summary>
[<NoEquality; NoComparison>]
type CurriedApi = { go: int -> string -> Task<int> }

/// <summary>The tupled spelling of exactly the same operation as <c>CurriedApi.go</c>.</summary>
[<NoEquality; NoComparison>]
type TupledApi = { go: (int * string) -> Task<int> }

/// <summary>Curried fields at the extremes of the supported arity range.</summary>
[<NoEquality; NoComparison>]
type ArityApi =
    { five: int -> string -> bool -> int64 -> double -> Task<unit>
      six: int -> string -> bool -> int64 -> double -> char -> Task<unit>
      seven: int -> string -> bool -> int64 -> double -> char -> byte -> Task<unit> }

/// <summary>One argument past the cap.</summary>
[<NoEquality; NoComparison>]
type OverCapApi =
    { eight: int -> int -> int -> int -> int -> int -> int -> int -> Task<unit> }

/// <summary><c>unit</c> after the first curried position is ambiguous and invalid.</summary>
[<NoEquality; NoComparison>]
type TrailingUnitApi = { go: string -> unit -> Task<int> }

/// <summary><c>unit</c> in the middle of a curried chain is invalid.</summary>
[<NoEquality; NoComparison>]
type MiddleUnitApi = { go: string -> unit -> bool -> Task<int> }

/// <summary>A curried chain ending in a non-<c>Task</c> range is invalid.</summary>
[<NoEquality; NoComparison>]
type CurriedAsyncApi = { go: int -> string -> Async<int> }

/// <summary>
/// A function-typed argument: reflected fine, unserializable at binding. The greedy walk means
/// the trailing function is an argument, never a "returned function".
/// </summary>
[<NoEquality; NoComparison>]
type FunctionArgumentApi = { go: string -> (int -> int) -> Task<unit> }

/// <summary>A record with a private representation is invalid.</summary>
[<NoEquality; NoComparison>]
type PrivateApi = private { hidden: int -> Task<unit> }

/// <summary>A class is not a record.</summary>
type NotARecord(name: string) =
    member _.Name = name

// ──────────────────────────────────────────────────────────────────────────────
// Helpers
// ──────────────────────────────────────────────────────────────────────────────

let private shapeOf<'Api> () = ApiShape.of'<'Api> ()

let private failureFor (apiType: Type) =
    Assert.Throws<InvalidOperationException>(fun () -> ApiShape.ofType apiType |> ignore)

// ──────────────────────────────────────────────────────────────────────────────
// Declaration order and exact types
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``field declaration order is preserved`` () =
    let shape = shapeOf<SampleApi> ()

    test
        <@
            shape.Operations |> Array.map (fun op -> op.FieldName) = [| "join"; "say"; "history" |]
        @>

    test <@ shape.Operations |> Array.map (fun op -> op.Index) = [| 0; 1; 2 |] @>

[<Fact>]
let ``exact argument and reply types are preserved`` () =
    let shape = shapeOf<SampleApi> ()

    test <@ shape.Operations.[0].ArgumentType = typeof<string> @>
    test <@ shape.Operations.[0].ReplyType = typeof<unit> @>
    test <@ shape.Operations.[1].ArgumentType = typeof<int> @>
    test <@ shape.Operations.[1].ReplyType = typeof<string> @>
    test <@ shape.Operations.[2].ArgumentType = typeof<int> @>
    test <@ shape.Operations.[2].ReplyType = typeof<string list> @>

[<Fact>]
let ``a closed constructed generic record is a valid API type`` () =
    let shape = shapeOf<GenericApi<int>> ()

    test <@ shape.Operations.Length = 1 @>
    test <@ shape.Operations.[0].ArgumentType = typeof<unit> @>
    test <@ shape.Operations.[0].ReplyType = typeof<int> @>

// ──────────────────────────────────────────────────────────────────────────────
// Sentinels
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``every field receives a distinct sentinel even with identical function types`` () =
    let shape = shapeOf<TwinApi> ()
    let first = shape.Operations.[0].Sentinel
    let second = shape.Operations.[1].Sentinel

    test <@ shape.Operations.[0].FunctionType = shape.Operations.[1].FunctionType @>
    test <@ not (obj.ReferenceEquals(first, second)) @>

[<Fact>]
let ``the probe record carries exactly the field sentinels`` () =
    let shape = shapeOf<SampleApi> ()
    let probe = shape.Probe :?> SampleApi

    test <@ obj.ReferenceEquals(box probe.join, shape.Operations.[0].Sentinel) @>
    test <@ obj.ReferenceEquals(box probe.say, shape.Operations.[1].Sentinel) @>
    test <@ obj.ReferenceEquals(box probe.history, shape.Operations.[2].Sentinel) @>

[<Fact>]
let ``invoking a sentinel fails with the selector guidance`` () =
    let shape = shapeOf<SampleApi> ()
    let probe = shape.Probe :?> SampleApi

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> probe.say 1 |> ignore)

    test <@ error.Message.Contains "Use a direct API field selector such as _.join." @>

// ──────────────────────────────────────────────────────────────────────────────
// Selector resolution
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``underscore-dot selector resolves`` () =
    let shape = shapeOf<SampleApi> ()
    let resolved = ApiShape.resolve shape "test" (_.join)

    test <@ resolved.FieldName = "join" @>

[<Fact>]
let ``lambda selector resolves`` () =
    let shape = shapeOf<SampleApi> ()
    let resolved = ApiShape.resolve shape "test" (fun (api: SampleApi) -> api.history)

    test <@ resolved.FieldName = "history" @>

[<Fact>]
let ``fields with identical function types resolve to the right field`` () =
    let shape = shapeOf<TwinApi> ()

    test <@ (ApiShape.resolve shape "test" (_.first)).FieldName = "first" @>
    test <@ (ApiShape.resolve shape "test" (_.second)).FieldName = "second" @>

[<Fact>]
let ``a selector which does not return a field value fails with the required diagnostic`` () =
    let shape = shapeOf<SampleApi> ()

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.resolve shape "readOnly" (fun (_: SampleApi) -> (fun (_: string) -> Task.FromResult()))
            |> ignore)

    test <@ error.Message.Contains "Use a direct API field selector such as _.join." @>
    test <@ error.Message.Contains "readOnly" @>

[<Fact>]
let ``a selector which invokes the operation fails with the required diagnostic`` () =
    let shape = shapeOf<SampleApi> ()

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.resolve shape "handle" (fun (api: SampleApi) ->
                api.say 1 |> ignore
                api.say)
            |> ignore)

    test <@ error.Message.Contains "Use a direct API field selector such as _.join." @>

/// <remarks>
/// The acceptance rule is physical identity of the returned sentinel, not the syntactic shape
/// of the selector. A helper, a captured condition, or a branch therefore resolves as long as
/// it ultimately returns one original sentinel — and the selector body runs exactly once, at
/// configuration time.
/// </remarks>
[<Fact>]
let ``a helper or branch returning an original sentinel resolves and runs exactly once`` () =
    let shape = shapeOf<SampleApi> ()
    let mutable calls = 0
    let helper (api: SampleApi) = api.history

    let selector (api: SampleApi) =
        calls <- calls + 1
        if calls > 0 then helper api else api.history

    let resolved = ApiShape.resolve shape "readOnly" selector

    test <@ resolved.FieldName = "history" @>
    test <@ calls = 1 @>

[<Fact>]
let ``a selector from another API record fails resolution`` () =
    let shape = shapeOf<SampleApi> ()
    let twinShape = shapeOf<TwinApi> ()
    let foreignField = (twinShape.Probe :?> TwinApi).first

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.resolve shape "oneWay" (fun (_: SampleApi) -> foreignField) |> ignore)

    test <@ error.Message.Contains "Use a direct API field selector such as _.join." @>

// ──────────────────────────────────────────────────────────────────────────────
// Invalid shapes
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a non-record type fails construction`` () =
    let error = failureFor typeof<NotARecord>
    test <@ error.Message.Contains "not a public F# record" @>

[<Fact>]
let ``a struct record fails construction`` () =
    let error = failureFor typeof<StructApi>
    test <@ error.Message.Contains "struct record" @>

[<Fact>]
let ``an open generic record fails construction`` () =
    let error = failureFor (typedefof<GenericApi<_>>)
    test <@ error.Message.Contains "open generic" @>

[<Fact>]
let ``a non-function field fails construction`` () =
    let error = failureFor typeof<NonFunctionApi>
    test <@ error.Message.Contains "'Argument -> Task<'Reply>" @>

[<Fact>]
let ``an Async field fails construction`` () =
    let error = failureFor typeof<AsyncApi>
    test <@ error.Message.Contains "'Argument -> Task<'Reply>" @>

[<Fact>]
let ``a ValueTask field fails construction`` () =
    let error = failureFor typeof<ValueTaskApi>
    test <@ error.Message.Contains "'Argument -> Task<'Reply>" @>

[<Fact>]
let ``a plain Task field fails construction`` () =
    let error = failureFor typeof<PlainTaskApi>
    test <@ error.Message.Contains "'Argument -> Task<'Reply>" @>

[<Fact>]
let ``a curried chain which does not end in Task fails construction`` () =
    let error = failureFor typeof<CurriedAsyncApi>
    test <@ error.Message.Contains "'Argument -> Task<'Reply>" @>

// ──────────────────────────────────────────────────────────────────────────────
// Curried fields: the greedy walk and its canonical tuple
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a curried field collects its arguments in order and canonicalizes to the tuple`` () =
    let shape = shapeOf<CurriedApi> ()
    let operation = shape.Operations.[0]

    test <@ operation.ArgumentTypes = [| typeof<int>; typeof<string> |] @>
    test <@ operation.ArgumentType = typeof<int * string> @>
    test <@ operation.ReplyType = typeof<int> @>

[<Fact>]
let ``the curried and tupled spellings of one operation reflect to the same wire types`` () =
    let curried = (shapeOf<CurriedApi> ()).Operations.[0]
    let tupled = (shapeOf<TupledApi> ()).Operations.[0]

    test <@ curried.FieldName = tupled.FieldName @>
    test <@ curried.ArgumentType = tupled.ArgumentType @>
    test <@ curried.ReplyType = tupled.ReplyType @>
    // The field types differ — that is exactly what "two spellings" means.
    test <@ curried.FunctionType <> tupled.FunctionType @>
    test <@ tupled.ArgumentTypes = [| typeof<int * string> |] @>

/// <remarks>
/// Seven is the cap because <c>System.Tuple</c> starts nesting at eight: a canonical tuple
/// inside the cap is always flat, so it is exactly the type an author would get by writing the
/// tupled spelling by hand. This asserts the flatness rather than trusting it.
/// </remarks>
[<Fact>]
let ``curried arities up to seven canonicalize to a flat tuple`` () =
    let shape = shapeOf<ArityApi> ()

    test <@ shape.Operations |> Array.map (fun op -> op.ArgumentTypes.Length) = [| 5; 6; 7 |] @>
    test <@ shape.Operations.[0].ArgumentType = typeof<int * string * bool * int64 * double> @>
    test <@ shape.Operations.[1].ArgumentType = typeof<int * string * bool * int64 * double * char> @>
    test <@ shape.Operations.[2].ArgumentType = typeof<int * string * bool * int64 * double * char * byte> @>

    for operation in shape.Operations do
        // A flat System.Tuple`N: no TRest element, so the last generic argument is a real
        // argument type and not another tuple.
        test <@ operation.ArgumentType.GetGenericArguments().Length = operation.ArgumentTypes.Length @>
        test <@ operation.ArgumentType.GetGenericArguments() = operation.ArgumentTypes @>

[<Fact>]
let ``a curried field past the arity cap fails construction with the record guidance`` () =
    let error = failureFor typeof<OverCapApi>

    test <@ error.Message.Contains "declares 8 curried arguments" @>
    test <@ error.Message.Contains "at most 7 are supported" @>
    test <@ error.Message.Contains "Group the inputs in a record" @>

[<Fact>]
let ``unit stays a zero-input operation and never becomes a one-tuple`` () =
    let shape = shapeOf<GenericApi<int>> ()

    test <@ shape.Operations.[0].ArgumentTypes = [| typeof<unit> |] @>
    test <@ shape.Operations.[0].ArgumentType = typeof<unit> @>

[<Fact>]
let ``unit as a non-first curried argument fails construction`` () =
    let trailing = failureFor typeof<TrailingUnitApi>
    let middle = failureFor typeof<MiddleUnitApi>

    test <@ trailing.Message.Contains "'unit' as curried argument 2 of 2" @>
    test <@ trailing.Message.Contains "only valid as a field's sole argument" @>
    test <@ middle.Message.Contains "'unit' as curried argument 2 of 3" @>

/// <remarks>
/// The walk is greedy to the first <c>Task&lt;_&gt;</c> range, so a field can never "return a
/// function": a function-typed tail is consumed as another argument. That keeps the old rule
/// that a function-typed argument is unserializable — it just fails at the serializer preflight
/// rather than at shape reflection, exactly as it did for the tupled spelling.
/// </remarks>
[<Fact>]
let ``the greedy walk consumes a function-typed tail as an argument`` () =
    let shape = shapeOf<FunctionArgumentApi> ()
    let operation = shape.Operations.[0]

    test <@ operation.ArgumentTypes = [| typeof<string>; typeof<int -> int> |] @>
    test <@ operation.ArgumentType = typeof<string * (int -> int)> @>
    test <@ operation.ReplyType = typeof<unit> @>

[<Fact>]
let ``a record with a private representation fails construction`` () =
    let error = failureFor typeof<PrivateApi>
    test <@ error.Message.Contains "not a public F# record" @>

// ──────────────────────────────────────────────────────────────────────────────
// Caching
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the shape cache returns the same instance for the same API type`` () =
    let first = ApiShape.ofType typeof<SampleApi>
    let second = ApiShape.ofType typeof<SampleApi>

    test <@ obj.ReferenceEquals(first, second) @>
    test <@ obj.ReferenceEquals(first.Probe, second.Probe) @>
    test <@ obj.ReferenceEquals(first.Constructor, second.Constructor) @>
    test <@ obj.ReferenceEquals(first.Operations.[0].Sentinel, second.Operations.[0].Sentinel) @>

[<Fact>]
let ``different API types receive different shapes`` () =
    let sample = ApiShape.ofType typeof<SampleApi>
    let twin = ApiShape.ofType typeof<TwinApi>

    test <@ not (obj.ReferenceEquals(sample, twin)) @>
