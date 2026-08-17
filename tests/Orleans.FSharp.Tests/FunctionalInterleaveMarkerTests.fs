/// <summary>
/// Spec 004 item 5: the exactness tests behind <c>mayInterleave</c> and <c>reentrant</c>.
/// </summary>
/// <remarks>
/// <para>
/// The behavioural integration tests prove that interleaving happens. What they cannot say is
/// <b>why</b> Orleans agreed to wire a predicate at all, and that "why" is a reflection contract
/// hidden inside <c>MayInterleaveConfiguratorProvider.GetMayInterleavePredicate</c>: it demands a
/// public method of the name the attribute carries, found with
/// <c>Public | Static | Instance | FlattenHierarchy</c>, returning <c>bool</c> and taking exactly
/// one <c>IInvokable</c>. If any of that drifts, Orleans throws at silo startup for a rejected
/// signature — or, worse, <b>silently binds the instanced predicate</b>, which would hand our
/// callback a <c>null</c> receiver because the functional activation instance is not the grain
/// class. These tests restate that contract against the marker so a drift is a unit-test failure
/// rather than a startup failure in someone's cluster.
/// </para>
/// <para>
/// They also pin the two property publications against a live attribute-decorated reference type,
/// rather than against string literals of what those attributes are believed to write.
/// </para>
/// </remarks>
module Orleans.FSharp.Tests.FunctionalInterleaveMarkerTests

open System
open System.Collections.Generic
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Orleans.Concurrency
open Orleans.Metadata
open Orleans.Runtime
open Orleans.Serialization.Invocation
open Orleans.FSharp
open Xunit
open Swensen.Unquote

type MarkerProbeActor = private MarkerProbeActor of unit

let private interleavingMarker = typeof<FunctionalInterleavingGrainMarker<MarkerProbeActor>>
let private plainMarker = typeof<FunctionalGrainMarker<MarkerProbeActor>>

// ──────────────────────────────────────────────────────────────────────────────
// Orleans' own reflection contract for a [MayInterleave] callback
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the interleaving marker carries MayInterleave and the plain marker does not`` () =
    let attribute = interleavingMarker.GetCustomAttribute<MayInterleaveAttribute>()
    test <@ not (isNull (box attribute)) @>
    test <@ isNull (box (plainMarker.GetCustomAttribute<MayInterleaveAttribute>())) @>

[<Fact>]
let ``the attribute names the callback the marker actually exposes`` () =
    // The attribute's CallbackMethodName is internal to Orleans, so the name is read back the
    // only way an outside observer can: through the property its own Populate writes.
    let properties = Dictionary<string, string>()

    MayInterleaveAttribute(FunctionalInterleave.CallbackName)
        .Populate(null, interleavingMarker, GrainType.Create "marker.probe", properties)

    test <@ properties.[WellKnownGrainTypeProperties.MayInterleavePredicate] = FunctionalInterleave.CallbackName @>

/// <remarks>
/// The exact <c>BindingFlags</c> Orleans uses, so this finds the method if and only if Orleans
/// would find it.
/// </remarks>
[<Fact>]
let ``the callback matches the signature Orleans demands`` () =
    let method' =
        interleavingMarker.GetMethod(
            FunctionalInterleave.CallbackName,
            BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.FlattenHierarchy
        )

    test <@ not (isNull method') @>
    test <@ method'.ReturnType = typeof<bool> @>
    test <@ method'.GetParameters().Length = 1 @>
    test <@ method'.GetParameters().[0].ParameterType = typeof<IInvokable> @>

/// <remarks>
/// Static, not instance. Orleans branches on exactly this: a static method becomes
/// <c>MayInterleaveStaticPredicate</c>, while an instance method becomes
/// <c>MayInterleaveInstancedPredicate&lt;TGrainClass&gt;</c>, which invokes
/// <c>instance as TGrainClass</c> — always <c>null</c> here, because the functional activation
/// instance is <c>FunctionalGrainTarget&lt;'Actor&gt;</c> and the grain class is this marker.
/// </remarks>
[<Fact>]
let ``the callback is static, so Orleans takes the static-predicate branch`` () =
    let method' =
        interleavingMarker.GetMethod(
            FunctionalInterleave.CallbackName,
            BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.FlattenHierarchy
        )

    test <@ method'.IsStatic @>

    // And the marker really is the grain class Orleans would reflect against: it is not
    // assignable from the activation target type, which is the whole reason the callback cannot
    // be an instance method.
    test <@ not (interleavingMarker.IsAssignableFrom typeof<FunctionalGrainTargetBase>) @>

/// <remarks>
/// A single method of that name: <c>Type.GetMethod(name, flags)</c> throws
/// <c>AmbiguousMatchException</c> on an overload set, which would make silo startup fail for every
/// definition declaring <c>mayInterleave</c>.
/// </remarks>
[<Fact>]
let ``the callback name is not overloaded`` () =
    let candidates =
        interleavingMarker.GetMethods(
            BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance ||| BindingFlags.FlattenHierarchy
        )
        |> Array.filter (fun method' -> method'.Name = FunctionalInterleave.CallbackName)

    test <@ candidates.Length = 1 @>

[<Fact>]
let ``the interleaving marker is still a functional grain marker`` () =
    // It inherits the plain marker, so everything the registry, the activator, and the manifest
    // do with a marker keeps working; the added attribute and callback are the only difference.
    test <@ plainMarker.IsAssignableFrom interleavingMarker @>
    test <@ typeof<Orleans.IGrainBase>.IsAssignableFrom interleavingMarker @>

// ──────────────────────────────────────────────────────────────────────────────
// The published property values, taken from the live attributes
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// Reference classes decorated with the real Orleans attributes. Their published properties are
/// what the registry's own provider must reproduce, read from the attributes rather than written
/// down as literals.
/// </remarks>
[<Reentrant>]
type private ReentrantReference() =
    class
    end

[<MayInterleave("MayInterleave")>]
type private MayInterleaveReference() =
    static member MayInterleave(_request: IInvokable) = false

let private propertiesOf (grainClass: Type) =
    let properties = Dictionary<string, string>()

    for attribute in grainClass.GetCustomAttributes(true) do
        match attribute with
        | :? IGrainPropertiesProviderAttribute as provider ->
            provider.Populate(null, grainClass, GrainType.Create "reference.probe", properties)
        | _ -> ()

    properties

[<Fact>]
let ``the reentrant property matches what a live decorated class publishes`` () =
    let reference = propertiesOf typeof<ReentrantReference>
    let published = Dictionary<string, string>()

    ReentrantAttribute().Populate(null, typeof<obj>, GrainType.Create "reference.probe", published)

    test <@ reference.Count = 1 @>
    test <@ reference.[WellKnownGrainTypeProperties.Reentrant] = "true" @>
    test <@ published.[WellKnownGrainTypeProperties.Reentrant] = reference.[WellKnownGrainTypeProperties.Reentrant] @>

[<Fact>]
let ``the may-interleave property matches what a live decorated class publishes`` () =
    let reference = propertiesOf typeof<MayInterleaveReference>
    let marker = propertiesOf interleavingMarker

    test <@ reference.[WellKnownGrainTypeProperties.MayInterleavePredicate] = "MayInterleave" @>

    // The marker publishes the same key and value THROUGH ITS OWN ATTRIBUTE, with no help from
    // the registry's properties provider. That is why the provider's own write of this key is
    // defence in depth rather than the load-bearing path -- recorded here so the redundancy is a
    // stated fact and not an accident.
    test
        <@
            marker.[WellKnownGrainTypeProperties.MayInterleavePredicate] = reference.[WellKnownGrainTypeProperties.MayInterleavePredicate]
        @>

[<Fact>]
let ``the plain marker publishes neither interleaving property`` () =
    let plain = propertiesOf plainMarker

    test <@ not (plain.ContainsKey WellKnownGrainTypeProperties.Reentrant) @>
    test <@ not (plain.ContainsKey WellKnownGrainTypeProperties.MayInterleavePredicate) @>

// ──────────────────────────────────────────────────────────────────────────────
// What the callback does with an invokable that is not a functional request
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// Orleans calls this callback for EVERY message queued to a busy activation, not only for
/// functional requests: a grain extension's method, a stream-consumer extension's method, a
/// reminder tick. Each of those is an <c>IInvokable</c> of some other shape, and the callback has
/// to answer "do not interleave" for all of them rather than fail.
/// </remarks>
[<Sealed>]
type private StubInvokable(arguments: obj[]) =
    interface IInvokable with
        member _.GetTarget() = null
        member _.SetTarget(_holder: ITargetHolder) = ()
        member _.Invoke() = ValueTask<Response>(Response.Completed)
        member _.GetArgumentCount() = arguments.Length

        member _.GetArgument(index: int) =
            // Exactly what Orleans' own RequestBase does when the generator emitted no override,
            // which it never does for a method with no parameters.
            if index >= arguments.Length then
                raise (ArgumentOutOfRangeException(message = "The request has zero arguments", innerException = null))

            arguments.[index]
        member _.SetArgument(index: int, value: obj) = arguments.[index] <- value
        member _.GetMethodName() = "Stub"
        member _.GetInterfaceName() = "IStub"
        member _.GetActivityName() = "IStub/Stub"
        member _.GetMethod() = null
        member _.GetInterfaceType() = typeof<obj>

    interface IDisposable with
        member _.Dispose() = ()

/// <remarks>
/// The one that used to throw. Orleans' code generator emits no <c>GetArgument</c> override for a
/// parameterless method, so a zero-argument invokable inherits <c>RequestBase.GetArgument</c>,
/// which throws <c>ArgumentOutOfRangeException</c>. Reading argument 0 unconditionally would turn
/// every such message into a thrown predicate — and Orleans rejects the INCOMING call when the
/// predicate throws, so a parameterless extension method arriving at a busy activation would have
/// failed an unrelated caller's request.
/// </remarks>
type private ZeroArgActor = private ZeroArgActor of unit

[<Fact>]
let ``a zero-argument invokable does not interleave and does not throw`` () =
    // A predicate MUST be registered first: without one the callback answers false before it
    // would ever read an argument, so an unregistered brand cannot exercise this at all.
    FunctionalInterleave.register
        typeof<FunctionalInterleavingGrainMarker<ZeroArgActor>>
        "marker.zeroarg"
        (fun _ -> true)

    use stub = new StubInvokable([||])
    test <@ FunctionalInterleavingGrainMarker<ZeroArgActor>.MayInterleave stub = false @>

[<Fact>]
let ``an invokable whose first argument is not a functional envelope does not interleave`` () =
    FunctionalInterleave.register
        typeof<FunctionalInterleavingGrainMarker<ZeroArgActor>>
        "marker.zeroarg"
        (fun _ -> true)

    use stub = new StubInvokable([| box "not an envelope"; box 42 |])
    test <@ FunctionalInterleavingGrainMarker<ZeroArgActor>.MayInterleave stub = false @>

[<Fact>]
let ``a null invokable does not interleave`` () =
    test <@ FunctionalInterleavingGrainMarker<MarkerProbeActor>.MayInterleave null = false @>

[<Fact>]
let ``an unregistered marker type does not interleave`` () =
    // No definition declaring mayInterleave has been registered for this brand, so there is no
    // predicate to consult and the answer is the spec-003 default.
    let envelope =
        FunctionalRequestEnvelope("marker.unregistered", 1, "op", Array.zeroCreate 32, 0uy, [||])

    use stub = new StubInvokable([| box envelope; box Unchecked.defaultof<CancellationToken> |])
    test <@ FunctionalInterleavingGrainMarker<MarkerProbeActor>.MayInterleave stub = false @>

type private RegisteredActor = private RegisteredActor of unit

[<Fact>]
let ``a registered predicate is consulted, and only for its own grain type`` () =
    let markerType = typeof<FunctionalInterleavingGrainMarker<RegisteredActor>>
    let seen = ResizeArray<string>()

    FunctionalInterleave.register markerType "marker.registered" (fun metadata ->
        seen.Add metadata.OperationId
        metadata.OperationId = "yes")

    let invokableFor (grainType: string) (operationId: string) =
        let envelope =
            FunctionalRequestEnvelope(grainType, 1, operationId, Array.zeroCreate 32, 0uy, [||])

        new StubInvokable([| box envelope; box Unchecked.defaultof<CancellationToken> |])

    use admitted = invokableFor "marker.registered" "yes"
    use refused = invokableFor "marker.registered" "no"
    use foreign = invokableFor "marker.other" "yes"

    test <@ FunctionalInterleavingGrainMarker<RegisteredActor>.MayInterleave admitted @>
    test <@ FunctionalInterleavingGrainMarker<RegisteredActor>.MayInterleave refused = false @>

    // A request addressed to a different grain type never reaches the predicate at all.
    test <@ FunctionalInterleavingGrainMarker<RegisteredActor>.MayInterleave foreign = false @>
    test <@ List.ofSeq seen = [ "yes"; "no" ] @>

[<Fact>]
let ``a throwing predicate is wrapped in an attributable transport diagnostic`` () =
    let markerType = typeof<FunctionalInterleavingGrainMarker<MarkerProbeActor>>

    FunctionalInterleave.register markerType "marker.throwing" (fun _ ->
        raise (ApplicationException "the predicate refuses to decide"))

    let envelope =
        FunctionalRequestEnvelope("marker.throwing", 1, "boom", Array.zeroCreate 32, 0uy, [||])

    use stub = new StubInvokable([| box envelope; box Unchecked.defaultof<CancellationToken> |])

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            FunctionalInterleavingGrainMarker<MarkerProbeActor>.MayInterleave stub |> ignore)

    test <@ error.Message.Contains "'mayInterleave' predicate of grain type 'marker.throwing'" @>
    test <@ error.Message.Contains "operation 'boom'" @>
    test <@ error.InnerException.Message = "the predicate refuses to decide" @>
