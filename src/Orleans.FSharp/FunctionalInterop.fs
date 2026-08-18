namespace Orleans.FSharp

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Runtime.CompilerServices
open System.Threading.Tasks
open FSharp.Reflection
open Orleans
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// Bind one facade member to a named operation instead of matching it by member name.
/// </summary>
/// <remarks>
/// <para>
/// The override is matched <b>exactly</b> (ordinal), unlike the default member-name match, which
/// is case-insensitive. That is deliberate: the attribute is the documented way to disambiguate a
/// contract whose operation IDs differ only by case, and a case-folding override could not
/// name either of them.
/// </para>
/// </remarks>
/// <param name="operationId">The stable wire operation ID this member calls.</param>
[<AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false); Sealed>]
type FunctionalOperationAttribute(operationId: string) =
    inherit Attribute()

    /// <summary>The stable wire operation ID this member calls.</summary>
    member _.OperationId = operationId

/// <summary>
/// The typed per-member invoker factory. Its generic method is closed once per facade member
/// while the binding plan is built, so a bound call never closes a generic.
/// </summary>
[<AbstractClass; Sealed>]
type internal FacadeInvokerFactory =

    /// <summary>
    /// Close over one operation's exact argument and reply types and return the factory which,
    /// given that operation's bound API-record field closure, produces the per-call invoker.
    /// </summary>
    /// <param name="pack">Turns the invocation's argument array into the operation's exact argument, boxed.</param>
    static member Create<'Argument, 'Reply>(pack: Func<obj[], obj>) : Func<obj, Func<obj[], obj>> =
        Func<obj, Func<obj[], obj>>(fun field ->
            let call = unbox<'Argument -> Task<'Reply>> field

            Func<obj[], obj>(fun args -> box (call (unbox<'Argument> (pack.Invoke args)))))

    /// <summary>
    /// The same, for a <b>streaming</b> operation. Spec 004 item 6: the facade member returns the
    /// BCL <c>IAsyncEnumerable&lt;TItem&gt;</c>, so a C# consumer writes <c>await foreach</c> over
    /// it with no wrapper and no reference to this library's own types.
    /// </summary>
    /// <param name="pack">Turns the invocation's argument array into the operation's exact argument, boxed.</param>
    static member CreateStream<'Argument, 'Item>(pack: Func<obj[], obj>) : Func<obj, Func<obj[], obj>> =
        Func<obj, Func<obj[], obj>>(fun field ->
            let call = unbox<'Argument -> IAsyncEnumerable<'Item>> field

            Func<obj[], obj>(fun args -> box (call (unbox<'Argument> (pack.Invoke args)))))

/// <summary>
/// The typed contract binder. Its generic method is closed once per contract CLR type, so
/// repeated <c>For</c> calls against the same contract close no generic at all.
/// </summary>
[<AbstractClass; Sealed>]
type internal FacadeBinder =

    /// <summary>Bind the contract to a boxed domain key and return the preclosed closures.</summary>
    /// <param name="contract">The sealed contract, expected to be a <c>GrainContract&lt;'Actor, 'Key, 'Api&gt;</c>.</param>
    /// <param name="factory">The grain factory of the calling client or activation.</param>
    /// <param name="key">The boxed domain key of the target grain.</param>
    static member Bind<'Actor, 'Key, 'Api>(contract: FunctionalContract, factory: IGrainFactory, key: obj) : BoundCall[] =
        let typed = contract :?> GrainContract<'Actor, 'Key, 'Api>
        (FunctionalBinding.bind typed factory (unbox<'Key> key)).BoundCalls

/// <summary>One facade member, bound to one operation of the contract.</summary>
[<ReferenceEquality>]
type internal FacadeMemberPlan =
    {
        /// The interface method this plan dispatches.
        Method: MethodInfo
        /// The operation the member maps to.
        Operation: FunctionalOperation
        /// Turns the invocation's argument array into the operation's exact argument, boxed.
        Pack: Func<obj[], obj>
        /// Preclosed over the operation's exact argument and reply types: given the bound
        /// API-record field closure, returns the per-call invoker.
        InvokerFactory: Func<obj, Func<obj[], obj>>
    }

/// <summary>
/// The validated binding of one facade interface to one contract: every member checked and
/// resolved, every invoker factory preclosed. Built once per interface/contract pair.
/// </summary>
[<ReferenceEquality>]
type internal FacadePlan =
    {
        /// The facade interface type.
        FacadeType: Type
        /// One entry per dispatchable member, in reflection order.
        Members: FacadeMemberPlan[]
    }

/// <summary>
/// The <see cref="T:System.Reflection.DispatchProxy"/> the facade is materialized through. Its
/// only per-call work is one dictionary lookup and the delegate call; every type decision was
/// made while the plan was built.
/// </summary>
/// <remarks>
/// Deliberately not sealed: <c>DispatchProxy.Create</c> generates a type that derives from this
/// one, and rejects a sealed base with "The base type ... cannot be sealed".
/// </remarks>
type internal FunctionalFacadeProxy() =
    inherit DispatchProxy()

    /// <summary>The preclosed invoker of every member of the facade interface.</summary>
    member val Invokers: Dictionary<MethodInfo, Func<obj[], obj>> = null with get, set

    /// <summary>Dispatch one proxy call through the preclosed invoker for the target method.</summary>
    /// <param name="targetMethod">The facade interface method being invoked.</param>
    /// <param name="args">The boxed call arguments.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="targetMethod"/> has no preclosed invoker. Unreachable through
    /// <c>FunctionalGrainInterop.For</c>: every dispatchable member is resolved while the plan is
    /// built, and a member that could not be resolved fails there instead.
    /// </exception>
    override this.Invoke(targetMethod: MethodInfo, args: obj[]) : obj =
        match this.Invokers.TryGetValue targetMethod with
        | true, invoke -> invoke.Invoke args
        | _ ->
            // Unreachable through FunctionalGrainInterop.For: every dispatchable member of the
            // interface was resolved while the plan was built, and a member that could not be
            // resolved failed there. It stays as a loud failure rather than a null dereference.
            fail
                InteropStage
                $"member '{targetMethod.Name}' of '{targetMethod.DeclaringType.FullName}' has no bound operation."

/// <summary>Preclosing of the typed per-member invoker factory and the typed contract binder.</summary>
[<RequireQualifiedAccess>]
module internal FacadeClosure =

    let private invokerMethod =
        match
            typeof<FacadeInvokerFactory>
                .GetMethod("Create", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null -> fail InteropStage "the typed facade invoker factory 'FacadeInvokerFactory.Create' was not found."
        | method -> method

    let private streamInvokerMethod =
        match
            typeof<FacadeInvokerFactory>
                .GetMethod("CreateStream", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null ->
            fail InteropStage "the typed facade invoker factory 'FacadeInvokerFactory.CreateStream' was not found."
        | method -> method

    let private binderMethod =
        match
            typeof<FacadeBinder>
                .GetMethod("Bind", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
        with
        | null -> fail InteropStage "the typed facade binder 'FacadeBinder.Bind' was not found."
        | method -> method

    let private binders =
        ConcurrentDictionary<Type, Func<FunctionalContract, IGrainFactory, obj, BoundCall[]>>()

    /// <summary>
    /// Close the invoker factory over one operation's exact argument and reply types. Called
    /// once per facade member while the plan is built.
    /// </summary>
    /// <param name="argumentType">The operation's exact argument type.</param>
    /// <param name="replyType">The operation's exact reply type.</param>
    /// <param name="pack">Turns the invocation's argument array into the operation's exact argument, boxed.</param>
    let invoker (argumentType: Type) (replyType: Type) (pack: Func<obj[], obj>) : Func<obj, Func<obj[], obj>> =
        FunctionalInstrumentation.countGenericClosing ()

        let closed = invokerMethod.MakeGenericMethod [| argumentType; replyType |]

        let factory =
            closed.CreateDelegate typeof<Func<Func<obj[], obj>, Func<obj, Func<obj[], obj>>>>
            :?> Func<Func<obj[], obj>, Func<obj, Func<obj[], obj>>>

        factory.Invoke pack

    /// <summary>
    /// Close the streaming invoker factory over one operation's exact argument and item types.
    /// Spec 004 item 6.
    /// </summary>
    /// <param name="argumentType">The operation's exact argument type.</param>
    /// <param name="itemType">The operation's exact streamed item type.</param>
    /// <param name="pack">Turns the invocation's argument array into the operation's exact argument, boxed.</param>
    let streamInvoker (argumentType: Type) (itemType: Type) (pack: Func<obj[], obj>) : Func<obj, Func<obj[], obj>> =
        FunctionalInstrumentation.countGenericClosing ()

        let closed = streamInvokerMethod.MakeGenericMethod [| argumentType; itemType |]

        let factory =
            closed.CreateDelegate typeof<Func<Func<obj[], obj>, Func<obj, Func<obj[], obj>>>>
            :?> Func<Func<obj[], obj>, Func<obj, Func<obj[], obj>>>

        factory.Invoke pack

    /// <summary>
    /// The typed binder of one contract CLR type, closed on first use and cached, so binding a
    /// second key through the same contract closes no generic.
    /// </summary>
    /// <param name="contractType">The sealed contract's exact CLR type, expected to be a closed <c>GrainContract&lt;_,_,_&gt;</c>.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="contractType"/> is not a closed <c>GrainContract&lt;_,_,_&gt;</c>.
    /// </exception>
    let binder (contractType: Type) : Func<FunctionalContract, IGrainFactory, obj, BoundCall[]> =
        match binders.TryGetValue contractType with
        | true, cached -> cached
        | _ ->
            let created =
                // Defensive: FunctionalContract's constructor is internal and GrainContract is
                // its only derived type, so this cannot fail for a contract built by this library.
                if
                    not (
                        contractType.IsGenericType
                        && contractType.GetGenericTypeDefinition() = typedefof<GrainContract<_, _, _>>
                    )
                then
                    fail
                        InteropStage
                        $"'{contractType.FullName}' is not a functional grain contract; only a 'grainContract' value can be bound to a facade."

                FunctionalInstrumentation.countGenericClosing ()

                let closed = binderMethod.MakeGenericMethod(contractType.GetGenericArguments())

                closed.CreateDelegate typeof<Func<FunctionalContract, IGrainFactory, obj, BoundCall[]>>
                :?> Func<FunctionalContract, IGrainFactory, obj, BoundCall[]>

            binders.GetOrAdd(contractType, created)

/// <summary>
/// Facade planning: the complete set of binding rules, all applied while
/// <see cref="M:Orleans.FSharp.FunctionalGrainInterop.For``1(Orleans.FSharp.FunctionalContract,Orleans.IGrainFactory,System.Object)"/>
/// runs and never on a call.
/// </summary>
[<RequireQualifiedAccess>]
module internal FacadePlanning =

    let private describe (t: Type) = if isNull t then "<null>" else t.FullName

    let private quoted (values: string seq) =
        values |> Seq.map (fun value -> $"'{value}'") |> String.concat ", "

    let private operationIds (contract: FunctionalContract) =
        quoted (contract.Operations |> Seq.map (fun operation -> operation.OperationId))

    /// <summary>The facade interface itself plus every interface it extends, transitively.</summary>
    /// <param name="facadeType">The facade interface type.</param>
    let private closure (facadeType: Type) =
        Array.append [| facadeType |] (facadeType.GetInterfaces())

    /// <summary>The declared parameter list of a member, as it reads in the diagnostic.</summary>
    /// <param name="method">The facade member to describe.</param>
    let private actualParameters (method: MethodInfo) =
        match method.GetParameters() with
        | [||] -> "no parameters"
        | parameters ->
            let text =
                parameters
                |> Seq.map (fun parameter -> $"'{describe parameter.ParameterType}'")
                |> String.concat ", "

            $"({text})"

    /// <summary>The parameter lists an operation's argument type accepts.</summary>
    /// <param name="argumentType">The operation's exact argument type.</param>
    let private expectedParameters (argumentType: Type) =
        if argumentType = typeof<unit> then
            "no parameters"
        elif FSharpType.IsTuple argumentType then
            let elements =
                FSharpType.GetTupleElements argumentType
                |> Seq.map (fun element -> $"'{describe element}'")
                |> String.concat ", "

            $"either one parameter of type '{describe argumentType}' or {FSharpType.GetTupleElements(argumentType).Length} parameters ({elements})"
        else
            $"one parameter of type '{describe argumentType}'"

    /// <summary>
    /// Rule 5: reject every member shape the facade cannot dispatch, naming the shape. A
    /// property, an event, a generic member, a by-reference parameter, a default implementation,
    /// and a static member are all rejected here rather than silently ignored or discovered on a
    /// call.
    /// </summary>
    /// <param name="facadeType">The facade interface being planned, for the diagnostic.</param>
    /// <param name="declaring">The interface that actually declares <paramref name="method"/> (the facade itself, or one it extends).</param>
    /// <param name="method">The member to check.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="method"/> is static, carries a default implementation, is
    /// generic, or declares a by-reference ('ref', 'out', or 'in') parameter.
    /// </exception>
    let private rejectUnsupported (facadeType: Type) (declaring: Type) (method: MethodInfo) =
        let where =
            if declaring = facadeType then
                $"facade interface '{describe facadeType}'"
            else
                $"interface '{describe declaring}', extended by facade interface '{describe facadeType}'"

        if method.IsStatic then
            fail InteropStage $"member '{method.Name}' of {where} is static. A facade dispatches instance members only."

        if not method.IsAbstract then
            fail
                InteropStage
                $"member '{method.Name}' of {where} carries a default implementation. A facade dispatches abstract members only, so a default implementation would be silently unreachable."

        if method.IsGenericMethodDefinition then
            fail
                InteropStage
                $"member '{method.Name}' of {where} is generic. An operation has one exact argument type and one exact reply type, so a generic member has no operation to map to."

        for parameter in method.GetParameters() do
            if parameter.ParameterType.IsByRef then
                let kind =
                    if parameter.IsOut then "an 'out'"
                    elif parameter.IsIn then "an 'in'"
                    else "a 'ref'"

                fail
                    InteropStage
                    $"parameter '{parameter.Name}' of member '{method.Name}' of {where} is {kind} parameter. A grain call carries one serialized argument, so a by-reference parameter cannot be passed or written back."

    /// <summary>Rules 1 and 2: resolve one member to exactly one operation.</summary>
    /// <param name="facadeType">The facade interface being planned, for the diagnostic.</param>
    /// <param name="contract">The sealed contract to resolve the member against.</param>
    /// <param name="method">The member to resolve.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="method"/> carries a blank <c>[FunctionalOperation]</c> operation
    /// ID, or when it names no operation of <paramref name="contract"/>; or, absent that attribute,
    /// when no operation's ID matches <paramref name="method"/>'s name case-insensitively, or more
    /// than one does.
    /// </exception>
    let private resolveOperation (facadeType: Type) (contract: FunctionalContract) (method: MethodInfo) =
        // The attribute type is F#-declared, so it carries no null literal; the reflection call
        // still returns null when the member does not carry it.
        match box (method.GetCustomAttribute<FunctionalOperationAttribute> false) with
        | null ->
            let matches =
                contract.Operations
                |> Array.filter (fun operation ->
                    String.Equals(operation.OperationId, method.Name, StringComparison.OrdinalIgnoreCase))

            match matches with
            | [| single |] -> single
            | [||] ->
                fail
                    InteropStage
                    $"member '{method.Name}' of facade interface '{describe facadeType}' matches no operation of grain type '{contract.GrainTypeName}'. Its operations are {operationIds contract}. Rename the member or name the operation with [FunctionalOperation(\"...\")]."
            | ambiguous ->
                fail
                    InteropStage
                    $"member '{method.Name}' of facade interface '{describe facadeType}' matches {ambiguous.Length} operations of grain type '{contract.GrainTypeName}' case-insensitively -- {quoted (ambiguous |> Seq.map (fun operation -> operation.OperationId))}. Name the intended one with [FunctionalOperation(\"...\")], which is matched exactly."
        | boxed ->
            let attribute = boxed :?> FunctionalOperationAttribute

            if isBlank attribute.OperationId then
                fail
                    InteropStage
                    $"[FunctionalOperation] on member '{method.Name}' of facade interface '{describe facadeType}' supplies a blank operation ID."

            match
                contract.Operations
                |> Array.tryFind (fun operation ->
                    String.Equals(operation.OperationId, attribute.OperationId, StringComparison.Ordinal))
            with
            | Some operation -> operation
            | None ->
                fail
                    InteropStage
                    $"[FunctionalOperation(\"{attribute.OperationId}\")] on member '{method.Name}' of facade interface '{describe facadeType}' names no operation of grain type '{contract.GrainTypeName}'. The override is matched exactly, and the operations are {operationIds contract}."

    /// <summary>
    /// Rule 3: the parameter list must match the operation's single argument exactly. A
    /// unit-argument operation maps to a parameterless member; one parameter is the argument
    /// itself; two or more parameters are the canonical tuple's elements in order, and the
    /// returned packer builds that tuple.
    /// </summary>
    /// <param name="facadeType">The facade interface being planned, for the diagnostic.</param>
    /// <param name="contract">The sealed contract, for the diagnostic.</param>
    /// <param name="method">The member whose parameter list to check.</param>
    /// <param name="operation">The operation <paramref name="method"/> was resolved to.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="method"/>'s parameter list does not match
    /// <paramref name="operation"/>'s single argument type exactly.
    /// </exception>
    let private packerFor (facadeType: Type) (contract: FunctionalContract) (method: MethodInfo) (operation: FunctionalOperation) =
        let parameters = method.GetParameters()
        let argumentType = operation.ArgumentType

        let mismatch () : Func<obj[], obj> =
            fail
                InteropStage
                $"member '{method.Name}' of facade interface '{describe facadeType}' declares {actualParameters method}, but operation '{operation.OperationId}' of grain type '{contract.GrainTypeName}' takes {expectedParameters argumentType}."

        match parameters.Length with
        | 0 -> if argumentType = typeof<unit> then Func<obj[], obj>(fun _ -> null) else mismatch ()
        | 1 ->
            if parameters.[0].ParameterType = argumentType then
                Func<obj[], obj>(fun args -> args.[0])
            else
                mismatch ()
        | count ->
            if not (FSharpType.IsTuple argumentType) then
                mismatch ()
            else
                let elements = FSharpType.GetTupleElements argumentType

                if
                    elements.Length <> count
                    || Array.exists2 (fun (parameter: ParameterInfo) element -> parameter.ParameterType <> element) parameters elements
                then
                    mismatch ()
                else
                    // Precomputed once, here: the returned closure builds the canonical tuple
                    // (nested beyond seven elements) with no reflection of its own.
                    let construct = FSharpValue.PreComputeTupleConstructor argumentType
                    Func<obj[], obj>(fun args -> construct args)

    /// <summary>
    /// Rule 4: the return type must be exactly <c>Task&lt;'Reply&gt;</c>. A unit-reply operation
    /// additionally accepts the non-generic <c>Task</c>, which is what a C# author writes.
    /// </summary>
    /// <param name="facadeType">The facade interface being planned, for the diagnostic.</param>
    /// <param name="contract">The sealed contract, for the diagnostic.</param>
    /// <param name="method">The member whose return type to check.</param>
    /// <param name="operation">The operation <paramref name="method"/> was resolved to.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="method"/>'s return type does not match
    /// <paramref name="operation"/>'s required shape: <c>IAsyncEnumerable&lt;TItem&gt;</c> for a
    /// streaming operation, or <c>Task&lt;'Reply&gt;</c> (or plain <c>Task</c> for a unit reply)
    /// otherwise.
    /// </exception>
    let private validateReturn (facadeType: Type) (contract: FunctionalContract) (method: MethodInfo) (operation: FunctionalOperation) =
        let replyType = operation.ReplyType

        // Spec 004 item 6: a streaming operation's member returns the BCL
        // IAsyncEnumerable<TItem> and nothing else -- no Task form exists for it, and the whole
        // point of the item type being BCL is that 'await foreach' works with no wrapper.
        if operation.IsStreaming then
            let isEnumerableOfItem =
                method.ReturnType.IsGenericType
                && method.ReturnType.GetGenericTypeDefinition() = typedefof<IAsyncEnumerable<_>>
                && method.ReturnType.GetGenericArguments().[0] = replyType

            if not isEnumerableOfItem then
                fail
                    InteropStage
                    $"member '{method.Name}' of facade interface '{describe facadeType}' returns '{describe method.ReturnType}', but operation '{operation.OperationId}' of grain type '{contract.GrainTypeName}' is a streaming operation and requires 'IAsyncEnumerable<{describe replyType}>'."
        else

        let isTaskOfReply =
            method.ReturnType.IsGenericType
            && method.ReturnType.GetGenericTypeDefinition() = typedefof<Task<_>>
            && method.ReturnType.GetGenericArguments().[0] = replyType

        let isUnitTask = replyType = typeof<unit> && method.ReturnType = typeof<Task>

        if not (isTaskOfReply || isUnitTask) then
            let expected =
                if replyType = typeof<unit> then
                    $"'System.Threading.Tasks.Task' or 'Task<{describe replyType}>'"
                else
                    $"'Task<{describe replyType}>'"

            fail
                InteropStage
                $"member '{method.Name}' of facade interface '{describe facadeType}' returns '{describe method.ReturnType}', but operation '{operation.OperationId}' of grain type '{contract.GrainTypeName}' requires {expected}."

    let private build (facadeType: Type) (contract: FunctionalContract) : FacadePlan =
        let members = ResizeArray<FacadeMemberPlan>()

        for declaring in closure facadeType do
            let declared =
                BindingFlags.Public
                ||| BindingFlags.NonPublic
                ||| BindingFlags.Instance
                ||| BindingFlags.Static
                ||| BindingFlags.DeclaredOnly

            let where =
                if declaring = facadeType then
                    $"facade interface '{describe facadeType}'"
                else
                    $"interface '{describe declaring}', extended by facade interface '{describe facadeType}'"

            // Properties and events are reported as themselves rather than as their accessor
            // methods, which is what the author actually wrote.
            for property in declaring.GetProperties declared do
                fail
                    InteropStage
                    $"{where} declares property '{property.Name}'. A facade dispatches methods only -- an operation is a call, and a property read would hide one."

            for event in declaring.GetEvents declared do
                fail
                    InteropStage
                    $"{where} declares event '{event.Name}'. A facade dispatches methods only; use a functional observer for push."

            for method in declaring.GetMethods declared do
                // Accessors were already rejected above, through their property or event.
                if not method.IsSpecialName then
                    rejectUnsupported facadeType declaring method
                    let operation = resolveOperation facadeType contract method
                    validateReturn facadeType contract method operation
                    let pack = packerFor facadeType contract method operation

                    members.Add
                        { Method = method
                          Operation = operation
                          Pack = pack
                          InvokerFactory =
                            if operation.IsStreaming then
                                FacadeClosure.streamInvoker operation.ArgumentType operation.ReplyType pack
                            else
                                FacadeClosure.invoker operation.ArgumentType operation.ReplyType pack }

        { FacadeType = facadeType
          Members = members.ToArray() }

    /// <summary>
    /// Plans by contract instance, then by facade interface. The outer table is weak on the
    /// contract on purpose: two contracts of the same CLR type can carry different operation IDs
    /// (<c>operationId</c> is a per-contract override), so the instance is the only correct key --
    /// and a strong one would keep every contract a process ever built alive. A contract is
    /// normally a module-level value that outlives the process anyway; this costs nothing when it
    /// is, and leaks nothing when it is not.
    /// </summary>
    let private plans =
        ConditionalWeakTable<FunctionalContract, ConcurrentDictionary<Type, FacadePlan>>()

    /// <summary>
    /// The validated plan for one interface/contract pair, built on first use and cached. A
    /// rejected facade is not cached: the diagnostic is raised by every attempt, from its own
    /// call site.
    /// </summary>
    /// <param name="facadeType">The facade interface to plan.</param>
    /// <param name="contract">The sealed contract to bind it against.</param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown, on the first call for this interface/contract pair, when the facade interface
    /// declares a property, an event, or a method this runtime cannot dispatch (static, a default
    /// implementation, generic, or a by-reference parameter); when a member cannot be resolved to
    /// exactly one operation by name or <c>[FunctionalOperation]</c>; or when a member's parameter
    /// list or return type does not match its resolved operation's shape.
    /// </exception>
    let planFor (facadeType: Type) (contract: FunctionalContract) : FacadePlan =
        let byFacade =
            plans.GetValue(contract, fun _ -> ConcurrentDictionary<Type, FacadePlan>())

        match byFacade.TryGetValue facadeType with
        | true, cached -> cached
        | _ -> byFacade.GetOrAdd(facadeType, build facadeType contract)

/// <summary>
/// The C#-callable view of a functional grain: an ordinary interface the consumer declares, whose
/// members are bound to the contract's operations when the facade is created.
/// </summary>
/// <remarks>
/// <para>
/// Every binding rule is checked by <c>For</c>, never on a call: name mapping, argument shape,
/// reply shape, and the member shapes a facade cannot dispatch. What survives <c>For</c> is one
/// preclosed invoker per member, so a call performs one dictionary lookup, one delegate call,
/// and then the same preclosed closure an F# caller reaches through the API record.
/// </para>
/// <para>
/// The interop path's own cost is <see cref="T:System.Reflection.DispatchProxy"/>'s per-call
/// dispatch, which is real: the runtime-generated proxy boxes each argument into an
/// <c>object[]</c> and returns the reply as <c>object</c>. That is the price of writing the
/// interface by hand instead of generating code, and it is paid once per call in addition to the
/// grain call itself. F# callers keep the direct route through the bound API record.
/// </para>
/// </remarks>
[<AbstractClass; Sealed>]
type FunctionalGrainInterop =

    /// <summary>
    /// Bind a contract to a domain key and return an implementation of the facade interface.
    /// </summary>
    /// <typeparam name="TFacade">
    /// The interface the consumer declared. Each member maps to one operation of the contract by
    /// case-insensitive name, or by an explicit
    /// <see cref="T:Orleans.FSharp.FunctionalOperationAttribute"/>. Operations no member maps to
    /// are left alone, so a partial facade over a large contract is supported.
    /// </typeparam>
    /// <param name="contract">The sealed contract, as built by <c>grainContract</c>.</param>
    /// <param name="factory">The grain factory of the calling client or activation.</param>
    /// <param name="key">
    /// The domain key of the target grain, boxed. It is checked against the contract's key type
    /// here, because C# has no partial type-argument inference: naming the facade type explicitly
    /// would otherwise force the caller to name the contract's three type parameters too.
    /// </param>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when <paramref name="contract"/> or <paramref name="factory"/> is null;
    /// when <typeparamref name="TFacade"/> is not an interface; when <paramref name="key"/> is
    /// null or not an instance of the contract's key type; when <paramref name="contract"/>'s CLR
    /// type is not a closed <c>GrainContract&lt;_,_,_&gt;</c>; or when facade planning rejects the
    /// interface (see <see cref="M:Orleans.FSharp.FacadePlanning.planFor"/>).
    /// </exception>
    static member For<'TFacade when 'TFacade: not struct>
        (contract: FunctionalContract, factory: IGrainFactory, key: obj)
        : 'TFacade =
        if obj.ReferenceEquals(contract, null) then
            fail InteropStage "a facade requires a contract, but null was supplied."

        if obj.ReferenceEquals(factory, null) then
            fail InteropStage "a facade requires a grain factory, but null was supplied."

        let facadeType = typeof<'TFacade>

        if not facadeType.IsInterface then
            fail
                InteropStage
                $"'{facadeType.FullName}' is not an interface. A facade is materialized as a runtime proxy, which can only implement an interface."

        if obj.ReferenceEquals(key, null) then
            fail
                InteropStage
                $"a facade over grain type '{contract.GrainTypeName}' requires a domain key of type '{contract.KeyType.FullName}', but null was supplied."

        if not (contract.KeyType.IsInstanceOfType key) then
            fail
                InteropStage
                $"a facade over grain type '{contract.GrainTypeName}' requires a domain key of type '{contract.KeyType.FullName}', but a '{key.GetType().FullName}' was supplied."

        // The plan first: a facade the contract cannot serve is rejected before any grain
        // reference exists, so the diagnostic is about the interface and nothing else.
        let plan = FacadePlanning.planFor facadeType contract
        let bound = (FacadeClosure.binder (contract.GetType())).Invoke(contract, factory, key)

        let invokers = Dictionary<MethodInfo, Func<obj[], obj>> plan.Members.Length

        for entry in plan.Members do
            invokers.[entry.Method] <- entry.InvokerFactory.Invoke bound.[entry.Operation.Index].Field

        let proxy = DispatchProxy.Create<'TFacade, FunctionalFacadeProxy>()
        (box proxy :?> FunctionalFacadeProxy).Invokers <- invokers
        proxy
