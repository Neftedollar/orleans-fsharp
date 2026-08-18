namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open FSharp.Reflection

/// <summary>
/// A record-field projection which identifies one operation of an API record.
/// The documented forms are <c>_.join</c> and <c>fun api -&gt; api.join</c>.
/// </summary>
type OperationSelector<'Api, 'Argument, 'Reply> = 'Api -> ('Argument -> Task<'Reply>)

/// <summary>
/// A record-field projection which identifies one <b>server-streaming</b> operation of an API
/// record. Spec 004 item 6. Same two documented forms as
/// <see cref="T:Orleans.FSharp.OperationSelector`3"/>.
/// </summary>
/// <remarks>
/// A separate type rather than a wider <c>OperationSelector</c>, because it is what makes the
/// admission policies structurally unavailable to a streaming field: <c>readOnly</c>,
/// <c>oneWay</c>, <c>alwaysInterleave</c> and <c>transactional</c> all take an
/// <c>OperationSelector</c>, whose range is <c>Task&lt;'Reply&gt;</c>, so
/// <c>readOnly (_.watch)</c> on a streaming field does not type-check at all. Contract sealing
/// still repeats every rejection, because a draft can also be built directly.
/// </remarks>
type StreamSelector<'Api, 'Argument, 'Item> = 'Api -> ('Argument -> IAsyncEnumerable<'Item>)

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

    /// <summary>Prefix identifying the validation stage in every transactional-state diagnostic.</summary>
    [<Literal>]
    let TransactionalStage = "Orleans.FSharp transactional state"

    /// <summary>Prefix identifying the validation stage in every reference-binding diagnostic.</summary>
    [<Literal>]
    let BindingStage = "Orleans.FSharp functional binding"

    /// <summary>Prefix identifying the stage in every C#-facade binding diagnostic.</summary>
    [<Literal>]
    let InteropStage = "Orleans.FSharp functional interop"

    /// <summary>Prefix identifying the stage in every fixed-transport diagnostic.</summary>
    [<Literal>]
    let TransportStage = "Orleans.FSharp functional transport"

    /// <summary>Prefix identifying the stage in every journaled-activation diagnostic.</summary>
    [<Literal>]
    let JournalStage = "Orleans.FSharp functional journal"

    /// <summary>Raise a construction-stage diagnostic. Never returns.</summary>
    /// <param name="stage">The diagnostic stage prefix identifying which validation layer failed.</param>
    /// <param name="message">The specific failure detail, appended after the stage prefix.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Always thrown, carrying "<paramref name="stage"/>: <paramref name="message"/>".
    /// </exception>
    let fail<'T> (stage: string) (message: string) : 'T =
        raise (InvalidOperationException(stage + ": " + message))

    /// <summary>Raise a construction-stage diagnostic which preserves an inner cause. Never returns.</summary>
    /// <param name="stage">The diagnostic stage prefix identifying which validation layer failed.</param>
    /// <param name="message">The specific failure detail, appended after the stage prefix.</param>
    /// <param name="cause">The original exception, preserved as <see cref="P:System.Exception.InnerException"/>.</param>
    /// <exception cref="System.InvalidOperationException">Always thrown, wrapping <paramref name="cause"/>.</exception>
    let failCause<'T> (stage: string) (message: string) (cause: exn) : 'T =
        raise (InvalidOperationException(stage + ": " + message, cause))

    /// <summary>True when a name is null, empty, or white-space only.</summary>
    /// <param name="value">The string to check.</param>
    let isBlank (value: string) = String.IsNullOrWhiteSpace value

    /// <summary>True when a name contains a NUL character.</summary>
    /// <param name="value">The string to check.</param>
    let containsNul (value: string) =
        not (isNull value) && value.IndexOf('\000') >= 0

    /// <summary>
    /// Validate one non-blank string against the fixed transport's own wire-text bounds: no
    /// longer than <see cref="F:Orleans.FSharp.FunctionalTransportDiagnostics.MaxWireTextLength"/>
    /// -- referenced here, not duplicated, so the two boundaries can never drift apart -- and free
    /// of every C0 control character (NUL included) that
    /// <see cref="M:Orleans.FSharp.FunctionalTransportDiagnostics.EnsureWireText"/> rejects on the
    /// wire. Every grain type, operation ID, and observer type eventually crosses that boundary,
    /// whether it is an explicit value or one derived from a CLR name -- an F# double-backtick
    /// identifier can carry the same "unusual characters" a hand-written string literal can.
    /// Calling this at contract construction reports the same fault the transport would, at the
    /// earliest stage with enough information to say which value it was.
    /// </summary>
    /// <param name="stage">The caller's own diagnostic stage.</param>
    /// <param name="what">
    /// A complete noun phrase identifying the value, already carrying its own quoting -- for
    /// example <c>"'grainType'"</c> or <c>$"the operation ID defaulted from API field '{name}'"</c>.
    /// </param>
    /// <param name="value">
    /// The value to check. Blank is not this function's concern: every call site already has its
    /// own "non-blank" diagnostic phrased for what the field actually is, raised before this runs.
    /// </param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="value"/> is longer than
    /// <see cref="F:Orleans.FSharp.FunctionalTransportDiagnostics.MaxWireTextLength"/> characters, or
    /// contains a control character (a codepoint below <c>' '</c>).
    /// </exception>
    let ensureWireText (stage: string) (what: string) (value: string) =
        if value.Length > FunctionalTransportDiagnostics.MaxWireTextLength then
            fail
                stage
                $"{what} must be at most {FunctionalTransportDiagnostics.MaxWireTextLength} characters, but {value.Length} were supplied."

        for index in 0 .. value.Length - 1 do
            if value.[index] < ' ' then
                fail stage $"{what} must not contain control characters, but one appears at index {index}."

/// <summary>Which reply shape an API-record field declares. Spec 004 item 6.</summary>
type internal ApiFieldKind =
    /// <summary><c>'Argument -&gt; Task&lt;'Reply&gt;</c> — one acknowledged reply.</summary>
    | UnaryField
    /// <summary><c>'Argument -&gt; IAsyncEnumerable&lt;'Item&gt;</c> — a server-streaming reply.</summary>
    | StreamingField

/// <summary>
/// One reflected API-record field: an operation of shape <c>'Argument -&gt; Task&lt;'Reply&gt;</c>
/// or <c>'Argument -&gt; IAsyncEnumerable&lt;'Item&gt;</c>.
/// </summary>
[<ReferenceEquality>]
type internal ApiOperationShape =
    {
        /// Zero-based declaration index of the record field.
        Index: int
        /// Source record-field name.
        FieldName: string
        /// Which reply shape the field declares.
        Kind: ApiFieldKind
        /// The field's exact CLR function type.
        FunctionType: Type
        /// The operation's exact argument type.
        ArgumentType: Type
        /// <summary>
        /// The operation's exact reply type: the <c>Task&lt;_&gt;</c> element type for a unary
        /// field, the <c>IAsyncEnumerable&lt;_&gt;</c> element type for a streaming one. Both are
        /// the type one payload is serialized as, which is why they share the field.
        /// </summary>
        ReplyType: Type
        /// The unique probe sentinel installed in this field of the probe record.
        Sentinel: obj
    }

    /// <summary>True when the field declares a server-streaming reply.</summary>
    member this.IsStreaming = this.Kind = StreamingField

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

                if not (FSharpType.IsFunction functionType) then
                    let message =
                        $"the API field '{describeType apiType}.{field.Name}' has type '{describeType functionType}'. "
                        + "Every API field must have the shape 'Argument -> Task<'Reply>."

                    fail ContractStage message

                let argumentType, rangeType = FSharpType.GetFunctionElements functionType

                // The two recognized reply shapes are told apart structurally, exactly as the
                // single Task shape was: by the open generic definition of the range type.
                let kind =
                    if not rangeType.IsGenericType then
                        Option.None
                    else
                        let definition = rangeType.GetGenericTypeDefinition()

                        if definition = typedefof<Task<_>> then Some UnaryField
                        elif definition = typedefof<IAsyncEnumerable<_>> then Some StreamingField
                        else Option.None

                match kind with
                | Option.None ->
                    let message =
                        $"the API field '{describeType apiType}.{field.Name}' returns '{describeType rangeType}'. "
                        + "Every API field must have the shape 'Argument -> Task<'Reply> or 'Argument -> IAsyncEnumerable<'Item>."

                    fail ContractStage message
                | Some fieldKind ->
                    { Index = index
                      FieldName = field.Name
                      Kind = fieldKind
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
    /// <param name="apiType">The closed API record type to reflect.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown, on the first call for this type, when <paramref name="apiType"/> is not a valid API
    /// shape: a struct record, an open generic type, not public, not a public F# record, has no
    /// operations, or has a field whose type is not <c>'Argument -&gt; Task&lt;'Reply&gt;</c> or
    /// <c>'Argument -&gt; IAsyncEnumerable&lt;'Item&gt;</c>.
    /// </exception>
    let ofType (apiType: Type) : ApiShape =
        cache.GetOrAdd(apiType, fun t -> lazy (build t)).Value

    /// <summary>Return the cached shape for <c>'Api</c>.</summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown, on the first call for <c>'Api</c>, when it is not a valid API shape; see
    /// <see cref="ofType"/>.
    /// </exception>
    let of'<'Api> () : ApiShape = ofType typeof<'Api>

    /// <summary>Look up an operation by source-field name.</summary>
    /// <param name="shape">The reflected API shape to search.</param>
    /// <param name="fieldName">The source record-field name to find.</param>
    let tryFindField (shape: ApiShape) (fieldName: string) =
        shape.Operations |> Array.tryFind (fun operation -> operation.FieldName = fieldName)

    /// <summary>
    /// Resolve one already-boxed selector application against the probe record by physical
    /// identity of the returned sentinel. Shared by the unary and the streaming selector types,
    /// which differ only in the range of the field they project.
    /// </summary>
    /// <param name="shape">The reflected API shape whose probe record the selector is applied to.</param>
    /// <param name="entry">The custom operation's own name, used to phrase the diagnostic.</param>
    /// <param name="apply">
    /// The boxed selector application: invokes the caller's selector against the boxed probe and
    /// boxes its result.
    /// </param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when invoking <paramref name="apply"/> itself throws (the original exception is
    /// preserved as the inner exception), or when its result is not physically one of
    /// <paramref name="shape"/>'s own sentinel values.
    /// </exception>
    let private resolveBoxed (shape: ApiShape) (entry: string) (apply: obj -> obj) : ApiOperationShape =
        FunctionalInstrumentation.countSelectorEvaluation ()

        let returned =
            try
                apply shape.Probe
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

    /// <summary>
    /// Resolve a selector against the probe record by physical identity of the returned sentinel.
    /// The selector runs exactly once, here, at configuration time.
    /// </summary>
    /// <param name="shape">The reflected API shape to resolve against.</param>
    /// <param name="entry">The custom operation's own name, used to phrase the diagnostic.</param>
    /// <param name="selector">The caller-supplied field projection to resolve.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, when invoking it throws, or when it does
    /// not return one of <paramref name="shape"/>'s own API field values.
    /// </exception>
    let resolve<'Api, 'Argument, 'Reply>
        (shape: ApiShape)
        (entry: string)
        (selector: OperationSelector<'Api, 'Argument, 'Reply>)
        : ApiOperationShape =
        if obj.ReferenceEquals(selector, null) then
            fail
                ContractStage
                $"the '{entry}' entry of '{describeType shape.ApiType}' supplied a null selector. {SelectorGuidance}"

        resolveBoxed shape entry (fun probe -> box (selector (unbox<'Api> probe)))

    /// <summary>
    /// Resolve a <b>streaming</b> selector against the probe record. Spec 004 item 6. Identical
    /// mechanism to <see cref="resolve"/>; only the projected field's range type differs.
    /// </summary>
    /// <param name="shape">The reflected API shape to resolve against.</param>
    /// <param name="entry">The custom operation's own name, used to phrase the diagnostic.</param>
    /// <param name="selector">The caller-supplied streaming field projection to resolve.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="selector"/> is null, when invoking it throws, or when it does
    /// not return one of <paramref name="shape"/>'s own API field values.
    /// </exception>
    let resolveStream<'Api, 'Argument, 'Item>
        (shape: ApiShape)
        (entry: string)
        (selector: StreamSelector<'Api, 'Argument, 'Item>)
        : ApiOperationShape =
        if obj.ReferenceEquals(selector, null) then
            fail
                ContractStage
                $"the '{entry}' entry of '{describeType shape.ApiType}' supplied a null selector. {SelectorGuidance}"

        resolveBoxed shape entry (fun probe -> box (selector (unbox<'Api> probe)))
