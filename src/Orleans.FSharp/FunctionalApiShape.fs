namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.Reflection
open System.Threading.Tasks
open FSharp.Reflection

/// <summary>
/// A record-field projection which identifies one operation of an API record.
/// The documented forms are <c>_.join</c> and <c>fun api -&gt; api.join</c>.
/// </summary>
type OperationSelector<'Api, 'Argument, 'Reply> = 'Api -> ('Argument -> Task<'Reply>)

/// <summary>A projection identifying a curried two-argument operation of an API record.</summary>
/// <remarks>
/// The curried spelling is sugar: the operation's canonical wire argument is the F# reference
/// tuple <c>'A1 * 'A2</c>, exactly as if the field had been written
/// <c>('A1 * 'A2) -&gt; Task&lt;'Reply&gt;</c>.
/// </remarks>
type OperationSelector2<'Api, 'A1, 'A2, 'Reply> = 'Api -> ('A1 -> 'A2 -> Task<'Reply>)

/// <summary>A projection identifying a curried three-argument operation of an API record.</summary>
type OperationSelector3<'Api, 'A1, 'A2, 'A3, 'Reply> = 'Api -> ('A1 -> 'A2 -> 'A3 -> Task<'Reply>)

/// <summary>A projection identifying a curried four-argument operation of an API record.</summary>
type OperationSelector4<'Api, 'A1, 'A2, 'A3, 'A4, 'Reply> = 'Api -> ('A1 -> 'A2 -> 'A3 -> 'A4 -> Task<'Reply>)

/// <summary>A projection identifying a curried five-argument operation of an API record.</summary>
type OperationSelector5<'Api, 'A1, 'A2, 'A3, 'A4, 'A5, 'Reply> =
    'Api -> ('A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> Task<'Reply>)

/// <summary>A projection identifying a curried six-argument operation of an API record.</summary>
type OperationSelector6<'Api, 'A1, 'A2, 'A3, 'A4, 'A5, 'A6, 'Reply> =
    'Api -> ('A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> Task<'Reply>)

/// <summary>A projection identifying a curried seven-argument operation of an API record.</summary>
type OperationSelector7<'Api, 'A1, 'A2, 'A3, 'A4, 'A5, 'A6, 'A7, 'Reply> =
    'Api -> ('A1 -> 'A2 -> 'A3 -> 'A4 -> 'A5 -> 'A6 -> 'A7 -> Task<'Reply>)

/// <summary>Diagnostic helpers shared by the functional contract layer.</summary>
module internal FunctionalDiagnostics =

    /// <summary>The exact guidance text every failed selector resolution must contain.</summary>
    [<Literal>]
    let SelectorGuidance = "Use a direct API field selector such as _.join."

    /// <summary>Prefix identifying the validation stage in every contract diagnostic.</summary>
    [<Literal>]
    let ContractStage = "Orleans.FSharp functional contract"

    /// <summary>Prefix identifying the validation stage in every definition diagnostic.</summary>
    [<Literal>]
    let DefinitionStage = "Orleans.FSharp functional definition"

    /// <summary>Prefix identifying the validation stage in every persistent-descriptor diagnostic.</summary>
    [<Literal>]
    let PersistentStage = "Orleans.FSharp persistent state"

    /// <summary>Prefix identifying the validation stage in every reference-binding diagnostic.</summary>
    [<Literal>]
    let BindingStage = "Orleans.FSharp functional binding"

    /// <summary>Prefix identifying the stage in every fixed-transport diagnostic.</summary>
    [<Literal>]
    let TransportStage = "Orleans.FSharp functional transport"

    /// <summary>Raise a construction-stage diagnostic.</summary>
    let fail<'T> (stage: string) (message: string) : 'T =
        raise (InvalidOperationException(stage + ": " + message))

    /// <summary>Raise a construction-stage diagnostic which preserves an inner cause.</summary>
    let failCause<'T> (stage: string) (message: string) (cause: exn) : 'T =
        raise (InvalidOperationException(stage + ": " + message, cause))

    /// <summary>True when a name is null, empty, or white-space only.</summary>
    let isBlank (value: string) = String.IsNullOrWhiteSpace value

    /// <summary>True when a name contains a NUL character.</summary>
    let containsNul (value: string) =
        not (isNull value) && value.IndexOf('\000') >= 0

/// <summary>One reflected API-record field: an operation of shape <c>'Argument -&gt; Task&lt;'Reply&gt;</c>.</summary>
/// <remarks>
/// A field may be spelled curried (<c>'A1 -&gt; 'A2 -&gt; Task&lt;'Reply&gt;</c>). Shape reflection
/// walks the whole function chain and canonicalizes it: <see cref="P:ArgumentTypes"/> holds the
/// collected arguments in declaration order, and <see cref="P:ArgumentType"/> is the single
/// canonical wire type — the argument itself at arity one, the F# reference tuple of the
/// collected types above it. The curried and tupled spellings of one operation are therefore the
/// same operation, with the same wire argument type.
/// </remarks>
[<ReferenceEquality>]
type internal ApiOperationShape =
    {
        /// Zero-based declaration index of the record field.
        Index: int
        /// Source record-field name.
        FieldName: string
        /// The field's exact CLR function type.
        FunctionType: Type
        /// The curried argument types in declaration order; a single element for a tupled field.
        ArgumentTypes: Type[]
        /// The operation's canonical argument type: the sole argument, or the F# tuple of them.
        ArgumentType: Type
        /// The operation's exact reply type (the <c>Task&lt;_&gt;</c> element type).
        ReplyType: Type
        /// The unique probe sentinel installed in this field of the probe record.
        Sentinel: obj
    }

/// <summary>The cached reflected shape of one closed API record type.</summary>
[<ReferenceEquality>]
type internal ApiShape =
    {
        /// The closed API record type.
        ApiType: Type
        /// Operations in record declaration order.
        Operations: ApiOperationShape[]
        /// The probe record instance whose fields are the sentinels.
        Probe: obj
        /// Cached record constructor for building bound API records.
        Constructor: obj[] -> obj
    }

/// <summary>
/// API-record reflection: one cached <see cref="T:Orleans.FSharp.ApiShape"/> per closed API type,
/// per-field probe sentinels, and selector resolution by physical identity.
/// </summary>
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal ApiShape =

    open FunctionalDiagnostics

    /// <summary>
    /// The largest number of curried arguments one API field may declare. Seven is where
    /// <c>System.Tuple</c> stops nesting, so the canonical tuple of a field within the cap is
    /// always a flat <c>Tuple&lt;_,..,_&gt;</c> and never carries a <c>TRest</c> element.
    /// </summary>
    [<Literal>]
    let MaxCurriedArity = 7

    let private cache = ConcurrentDictionary<Type, Lazy<ApiShape>>()

    let private describeType (t: Type) =
        if isNull t then "<null>" else t.FullName

    /// <summary>The one sentence every malformed-field diagnostic ends with.</summary>
    let private shapeGuidance =
        "Every API field must have the shape 'Argument -> Task<'Reply>, "
        + "optionally spelled curried as 'A1 -> 'A2 -> Task<'Reply> (up to "
        + string MaxCurriedArity
        + " arguments, canonicalized to the tuple 'A1 * 'A2)."

    /// <summary>True when a range type is exactly <c>Task&lt;'Reply&gt;</c>.</summary>
    let private isTaskOfReply (rangeType: Type) =
        rangeType.IsGenericType
        && rangeType.GetGenericTypeDefinition() = typedefof<Task<_>>

    /// <summary>
    /// Walk one field's function type greedily, collecting curried argument types in order until
    /// the range is exactly <c>Task&lt;'Reply&gt;</c>. Because the walk consumes the whole chain,
    /// an API field can never "return a function": a trailing function type is part of the
    /// argument list, and only a <c>Task&lt;_&gt;</c> ends the field.
    /// </summary>
    let private walkFunctionChain (owner: string) (functionType: Type) : Type[] * Type =
        let collected = ResizeArray<Type>()
        let mutable current = functionType
        let mutable reply = Unchecked.defaultof<Type>

        while isNull (box reply) do
            let argumentType, rangeType = FSharpType.GetFunctionElements current
            collected.Add argumentType

            if isTaskOfReply rangeType then
                reply <- rangeType.GetGenericArguments().[0]
            elif FSharpType.IsFunction rangeType then
                current <- rangeType
            else
                fail ContractStage $"the API field '{owner}' returns '{describeType rangeType}'. {shapeGuidance}"

        if collected.Count > MaxCurriedArity then
            fail
                ContractStage
                $"the API field '{owner}' declares {collected.Count} curried arguments, but at most {MaxCurriedArity} are supported. Group the inputs in a record and pass it as a single argument."

        // 'unit' is the "no domain input" marker, which only means that when it is the whole
        // argument. Inside a curried chain it would silently become an ordinary tuple slot that
        // reads like an absent argument, so every later position rejects it outright.
        for position in 1 .. collected.Count - 1 do
            if collected.[position] = typeof<unit> then
                fail
                    ContractStage
                    $"the API field '{owner}' declares 'unit' as curried argument {position + 1} of {collected.Count}. 'unit' means \"no domain input\" and is only valid as a field's sole argument ('unit -> Task<'Reply>')."

        collected.ToArray(), reply

    let private sentinelFor (apiType: Type) (fieldName: string) (functionType: Type) =
        FSharpValue.MakeFunction(
            functionType,
            fun _ ->
                let message =
                    $"the API probe sentinel for field '{fieldName}' of '{describeType apiType}' was invoked. "
                    + $"Selectors are configuration-time projections and must not call the operation. {SelectorGuidance}"

                fail ContractStage message
        )

    let private build (apiType: Type) : ApiShape =
        FunctionalInstrumentation.countApiShapeBuild ()

        if apiType.IsValueType then
            fail
                ContractStage
                $"the API type '{describeType apiType}' is a struct record. A reference F# record is required."

        if apiType.ContainsGenericParameters then
            fail
                ContractStage
                $"the API type '{describeType apiType}' is an open generic type. A closed constructed type is required."

        if not apiType.IsVisible then
            fail ContractStage $"the API type '{describeType apiType}' is not public."

        if not (FSharpType.IsRecord(apiType, BindingFlags.Public)) then
            fail
                ContractStage
                $"the API type '{describeType apiType}' is not a public F# record with a public representation."

        let constructorInfo = FSharpValue.PreComputeRecordConstructorInfo(apiType, BindingFlags.Public)

        if not constructorInfo.IsPublic then
            fail ContractStage $"the API record '{describeType apiType}' has no public constructor."

        let fields = FSharpType.GetRecordFields(apiType, BindingFlags.Public)

        if fields.Length = 0 then
            fail ContractStage $"the API record '{describeType apiType}' declares no operations."

        let operations =
            fields
            |> Array.mapi (fun index (field: PropertyInfo) ->
                let getter = field.GetGetMethod(false)

                if isNull getter || not getter.IsPublic then
                    fail
                        ContractStage
                        $"the API field '{describeType apiType}.{field.Name}' has no public getter."

                let functionType = field.PropertyType
                let owner = $"{describeType apiType}.{field.Name}"

                if not (FSharpType.IsFunction functionType) then
                    fail
                        ContractStage
                        $"the API field '{owner}' has type '{describeType functionType}'. {shapeGuidance}"

                let argumentTypes, replyType = walkFunctionChain owner functionType

                { Index = index
                  FieldName = field.Name
                  FunctionType = functionType
                  ArgumentTypes = argumentTypes
                  ArgumentType =
                    if argumentTypes.Length = 1 then
                        argumentTypes.[0]
                    else
                        FSharpType.MakeTupleType argumentTypes
                  ReplyType = replyType
                  Sentinel = sentinelFor apiType field.Name functionType })

        // Defensive: the per-field sentinel rule requires physically distinct objects even
        // when two fields share the same function type.
        let distinct =
            operations
            |> Array.forall (fun operation ->
                operations
                |> Array.filter (fun other -> Object.ReferenceEquals(other.Sentinel, operation.Sentinel))
                |> Array.length = 1)

        if not distinct then
            fail ContractStage $"the API record '{describeType apiType}' produced duplicate probe sentinels."

        let recordConstructor = FSharpValue.PreComputeRecordConstructor(apiType, BindingFlags.Public)
        let probe = recordConstructor (operations |> Array.map (fun operation -> operation.Sentinel))

        { ApiType = apiType
          Operations = operations
          Probe = probe
          Constructor = recordConstructor }

    /// <summary>Return the cached shape for a closed API record type, building it once.</summary>
    let ofType (apiType: Type) : ApiShape =
        cache.GetOrAdd(apiType, fun t -> lazy (build t)).Value

    /// <summary>Return the cached shape for <c>'Api</c>.</summary>
    let of'<'Api> () : ApiShape = ofType typeof<'Api>

    /// <summary>Look up an operation by source-field name.</summary>
    let tryFindField (shape: ApiShape) (fieldName: string) =
        shape.Operations |> Array.tryFind (fun operation -> operation.FieldName = fieldName)

    /// <summary>
    /// Resolve a selector against the probe record by physical identity of the returned sentinel.
    /// The selector runs exactly once, here, at configuration time.
    /// </summary>
    /// <remarks>
    /// The projected field type is a free type parameter so that one implementation serves both
    /// the tupled spelling and every curried arity; the caller's selector type is what pins the
    /// field's shape at compile time.
    /// </remarks>
    let resolveField<'Api, 'Field>
        (shape: ApiShape)
        (entry: string)
        (selector: 'Api -> 'Field)
        : ApiOperationShape =
        if obj.ReferenceEquals(selector, null) then
            fail
                ContractStage
                $"the '{entry}' entry of '{describeType shape.ApiType}' supplied a null selector. {SelectorGuidance}"

        FunctionalInstrumentation.countSelectorEvaluation ()

        let returned =
            try
                box (selector (unbox<'Api> shape.Probe))
            with
            | :? NullReferenceException as cause ->
                failCause
                    ContractStage
                    $"the '{entry}' selector of '{describeType shape.ApiType}' failed. {SelectorGuidance}"
                    cause
            | cause ->
                failCause
                    ContractStage
                    $"the '{entry}' selector of '{describeType shape.ApiType}' failed. {SelectorGuidance}"
                    cause

        let matches =
            shape.Operations
            |> Array.filter (fun operation -> Object.ReferenceEquals(operation.Sentinel, returned))

        match matches with
        | [| operation |] -> operation
        | _ ->
            fail
                ContractStage
                $"the '{entry}' selector of '{describeType shape.ApiType}' did not return an API field value. {SelectorGuidance}"

    /// <summary>Resolve a selector of the tupled spelling; see <see cref="M:resolveField"/>.</summary>
    let resolve<'Api, 'Argument, 'Reply>
        (shape: ApiShape)
        (entry: string)
        (selector: OperationSelector<'Api, 'Argument, 'Reply>)
        : ApiOperationShape =
        resolveField<'Api, 'Argument -> Task<'Reply>> shape entry selector
