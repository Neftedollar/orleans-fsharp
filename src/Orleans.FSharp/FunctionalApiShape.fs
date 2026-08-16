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

    /// <summary>Raise a construction-stage diagnostic.</summary>
    let fail<'T> (stage: string) (message: string) : 'T =
        raise (InvalidOperationException(stage + ": " + message))

    /// <summary>Raise a construction-stage diagnostic which preserves an inner cause.</summary>
    let failCause<'T> (stage: string) (message: string) (cause: exn) : 'T =
        raise (InvalidOperationException(stage + ": " + message, cause))

    /// <summary>Raise a diagnostic for a surface which lands in a later implementation phase.</summary>
    let notAvailable<'T> (phase: string) (feature: string) : 'T =
        raise (
            NotSupportedException(
                $"Orleans.FSharp functional runtime: {feature} is not available until {phase} of the functional grain runtime."
            )
        )

    /// <summary>True when a name is null, empty, or white-space only.</summary>
    let isBlank (value: string) = String.IsNullOrWhiteSpace value

    /// <summary>True when a name contains a NUL character.</summary>
    let containsNul (value: string) =
        not (isNull value) && value.IndexOf('\000') >= 0

/// <summary>One reflected API-record field: an operation of shape <c>'Argument -&gt; Task&lt;'Reply&gt;</c>.</summary>
[<ReferenceEquality>]
type internal ApiOperationShape =
    {
        /// Zero-based declaration index of the record field.
        Index: int
        /// Source record-field name.
        FieldName: string
        /// The field's exact CLR function type.
        FunctionType: Type
        /// The operation's exact argument type.
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

    let private cache = ConcurrentDictionary<Type, Lazy<ApiShape>>()

    let private describeType (t: Type) =
        if isNull t then "<null>" else t.FullName

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

                if not (FSharpType.IsFunction functionType) then
                    let message =
                        $"the API field '{describeType apiType}.{field.Name}' has type '{describeType functionType}'. "
                        + "Every API field must have the shape 'Argument -> Task<'Reply>."

                    fail ContractStage message

                let argumentType, rangeType = FSharpType.GetFunctionElements functionType

                let isTaskOfReply =
                    rangeType.IsGenericType
                    && rangeType.GetGenericTypeDefinition() = typedefof<Task<_>>

                if not isTaskOfReply then
                    let message =
                        $"the API field '{describeType apiType}.{field.Name}' returns '{describeType rangeType}'. "
                        + "Every API field must have the shape 'Argument -> Task<'Reply>."

                    fail ContractStage message

                { Index = index
                  FieldName = field.Name
                  FunctionType = functionType
                  ArgumentType = argumentType
                  ReplyType = rangeType.GetGenericArguments().[0]
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
    let resolve<'Api, 'Argument, 'Reply>
        (shape: ApiShape)
        (entry: string)
        (selector: OperationSelector<'Api, 'Argument, 'Reply>)
        : ApiOperationShape =
        if obj.ReferenceEquals(selector, null) then
            fail
                ContractStage
                $"the '{entry}' entry of '{describeType shape.ApiType}' supplied a null selector. {SelectorGuidance}"

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
