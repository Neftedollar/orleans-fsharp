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
