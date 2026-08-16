/// <summary>
/// Spec 003 "Payload limits and serializer registration": what each F# codec registration entry
/// point contributes, that the two entry points share one codec registration, and how the
/// top-level payload type table behaves when a name is declared twice.
/// </summary>
module Orleans.FSharp.Tests.FunctionalSerializerRegistrationTests

open System
open System.Reflection
open System.Reflection.Emit
open System.Threading
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Serialization
open Orleans.Serialization.Activators
open Orleans.Serialization.Cloning
open Orleans.Serialization.Codecs
open Orleans.Serialization.Serializers
open Orleans.FSharp
open Swensen.Unquote
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// Service contribution
// ──────────────────────────────────────────────────────────────────────────────

let private countOf<'Service> (services: IServiceCollection) =
    services
    |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<'Service>)
    |> Seq.length

let private withBuilder (register: ISerializerBuilder -> unit) =
    let services = ServiceCollection()

    ServiceCollectionExtensions.AddSerializer(services, Action<ISerializerBuilder>(fun builder -> register builder))
    |> ignore

    services :> IServiceCollection

/// <summary>
/// Orleans' own <c>AddSerializer</c> already contributes generalized codecs and type filters, so
/// what a registration entry point "contributes" is measured as the delta against a baseline
/// builder which registered nothing of ours.
/// </summary>
let private baseline = lazy (withBuilder ignore)

let private contributed<'Service> (services: IServiceCollection) =
    countOf<'Service> services - countOf<'Service> baseline.Value

[<Fact>]
let ``functional registration contributes one codec, one type filter, and no generalized copier`` () =
    let services =
        withBuilder (fun builder -> FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore)

    test <@ contributed<IGeneralizedCodec> services = 1 @>
    test <@ contributed<ITypeFilter> services = 1 @>
    test <@ contributed<IGeneralizedCopier> services = 0 @>
    test <@ countOf<FSharpBinaryCodec> services = 1 @>

[<Fact>]
let ``functional registration is idempotent on its own`` () =
    let services =
        withBuilder (fun builder ->
            FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore
            FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore)

    test <@ contributed<IGeneralizedCodec> services = 1 @>
    test <@ contributed<ITypeFilter> services = 1 @>
    test <@ contributed<IGeneralizedCopier> services = 0 @>

[<Fact>]
let ``the compatibility entry point keeps its historical service set`` () =
    let services =
        withBuilder (fun builder -> FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore)

    test <@ contributed<IGeneralizedCodec> services = 1 @>
    test <@ contributed<ITypeFilter> services = 1 @>
    test <@ contributed<IGeneralizedCopier> services = 1 @>
    test <@ countOf<FSharpBinaryCodec> services = 1 @>

[<Fact>]
let ``using both entry points keeps a single codec registration and adds the copier`` () =
    for order in [ true; false ] do
        let services =
            withBuilder (fun builder ->
                if order then
                    FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore
                    FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore
                else
                    FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore
                    FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore)

        test <@ contributed<IGeneralizedCodec> services = 1 @>
        test <@ contributed<ITypeFilter> services = 1 @>
        test <@ contributed<IGeneralizedCopier> services = 1 @>
        test <@ countOf<FSharpBinaryCodec> services = 1 @>

[<Fact>]
let ``the codec, copier, and type filter resolve to one shared instance`` () =
    let services = ServiceCollection()

    ServiceCollectionExtensions.AddSerializer(
        services,
        Action<ISerializerBuilder>(fun builder -> FSharpBinaryCodecRegistration.addToSerializerBuilder builder |> ignore)
    )
    |> ignore

    use provider = services.BuildServiceProvider()
    let codec = provider.GetRequiredService<FSharpBinaryCodec>()

    let generalized =
        provider.GetServices<IGeneralizedCodec>()
        |> Seq.filter (fun candidate -> obj.ReferenceEquals(candidate, codec))
        |> Seq.length

    let copiers =
        provider.GetServices<IGeneralizedCopier>()
        |> Seq.filter (fun candidate -> obj.ReferenceEquals(candidate, codec))
        |> Seq.length

    test <@ generalized = 1 @>
    test <@ copiers = 1 @>

// ──────────────────────────────────────────────────────────────────────────────
// Top-level payload type declarations
// ──────────────────────────────────────────────────────────────────────────────

type DeclaredPayload = { marker: string }

/// <summary>
/// Build a distinct CLR type in a dynamic assembly whose <c>FullName</c> equals
/// <paramref name="fullName"/>, so a genuine same-name collision can be produced.
/// </summary>
let private emitTypeNamed (fullName: string) =
    let assemblyName = AssemblyName($"Orleans.FSharp.Tests.Emitted.{Guid.NewGuid():N}")

    let assembly =
        AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run)

    let moduleBuilder = assembly.DefineDynamicModule "main"
    let typeBuilder = moduleBuilder.DefineType(fullName, TypeAttributes.Public)
    typeBuilder.CreateType()

[<Fact>]
let ``a declared type resolves where Type.GetType cannot see it`` () =
    let declared = typeof<DeclaredPayload>

    // Precondition: the codec's own Type.GetType fallback runs inside Orleans.FSharp and can
    // only see that assembly and the framework, so a test-assembly type is invisible to it.
    test <@ isNull (Type.GetType(declared.FullName, throwOnError = false)) @>

    let bytes =
        FSharpBinaryFormat.serializeWithType (box { marker = "declared" }) declared

    // Before the declaration the elided-type path cannot resolve the name…
    let before =
        Assert.Throws<InvalidOperationException>(fun () ->
            FSharpBinaryFormat.deserializeWithType bytes null |> ignore)

    test <@ before.Message.Contains "not found" @>

    // …and after it, the same bytes round-trip.
    FSharpBinaryFormat.declareType declared
    let restored = FSharpBinaryFormat.deserializeWithType bytes null

    test <@ unbox<DeclaredPayload> restored = { marker = "declared" } @>

/// <remarks>
/// Resolution where <c>Type.GetType</c> returns null (the test above) is not PRECEDENCE. This
/// one takes a name <c>Type.GetType</c> CAN resolve — a framework type — and emits a distinct
/// CLR type carrying the same <c>FullName</c>. The identical bytes are resolved twice: before
/// the declaration they must yield the framework type, after it the declared type. That is the
/// production order in <c>deserializeWithType</c> (declaration table first, <c>Type.GetType</c>
/// second) and nothing weaker can distinguish the two.
/// </remarks>
[<Fact>]
let ``a declared type takes precedence over one Type.GetType can resolve`` () =
    // A framework name the codec's own fallback really resolves, so the two lookups genuinely
    // compete. Nothing else in the suite declares it.
    let contested = typeof<Version>.FullName
    test <@ Type.GetType(contested, throwOnError = false) = typeof<Version> @>

    let shadow = emitTypeNamed contested
    test <@ shadow.FullName = contested @>
    test <@ shadow <> typeof<Version> @>

    let bytes =
        FSharpBinaryFormat.serializeWithType (Activator.CreateInstance shadow) shadow

    // Before the declaration the Type.GetType fallback wins and yields the framework type…
    let fallback = FSharpBinaryFormat.deserializeWithType bytes null
    test <@ fallback.GetType() = typeof<Version> @>

    FSharpBinaryFormat.declareType shadow

    // …after it the declaration table wins on the very same bytes.
    let declared = FSharpBinaryFormat.deserializeWithType bytes null
    test <@ declared.GetType() = shadow @>
    test <@ declared.GetType() <> typeof<Version> @>

/// <remarks>
/// It uses its own emitted type rather than <c>DeclaredPayload</c>: declaring that one here
/// would make the "before the declaration" arm of the resolution test above depend on test
/// execution order.
/// </remarks>
[<Fact>]
let ``declaring the same type twice is idempotent`` () =
    let declared = emitTypeNamed $"Orleans.FSharp.Tests.Idempotent{Guid.NewGuid():N}"
    FSharpBinaryFormat.declareType declared
    FSharpBinaryFormat.declareType declared
    FSharpBinaryFormat.declareType declared

[<Fact>]
let ``declaring a conflicting type with the same FullName is rejected`` () =
    let name = $"Orleans.FSharp.Tests.Collision{Guid.NewGuid():N}"
    let first = emitTypeNamed name
    let second = emitTypeNamed name

    test <@ first.FullName = second.FullName @>
    test <@ first <> second @>

    FSharpBinaryFormat.declareType first

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> FSharpBinaryFormat.declareType second)

    test <@ error.Message.Contains name @>
    test <@ error.Message.Contains "already declared" @>
    test <@ error.Message.Contains (first.Assembly.GetName().Name) @>
    test <@ error.Message.Contains (second.Assembly.GetName().Name) @>

    // The first declaration is unchanged: the losing type never replaces the winner.
    FSharpBinaryFormat.declareType first

[<Fact>]
let ``declaring an open generic or nameless type is ignored`` () =
    FSharpBinaryFormat.declareType null
    FSharpBinaryFormat.declareType typedefof<option<_>>
    FSharpBinaryFormat.declareType (typeof<int>.MakeArrayType().GetElementType())

// ──────────────────────────────────────────────────────────────────────────────
// Preflight diagnostics
// ──────────────────────────────────────────────────────────────────────────────

let private codecProvider () =
    let services = ServiceCollection()

    ServiceCollectionExtensions.AddSerializer(
        services,
        Action<ISerializerBuilder>(fun builder -> FSharpBinaryCodecRegistration.addCodecToSerializerBuilder builder |> ignore)
    )
    |> ignore

    services.BuildServiceProvider().GetRequiredService<ICodecProvider>()

/// <remarks>
/// A <c>declareType</c> collision means two distinct CLR types share one <c>FullName</c>, which
/// is a name-collision fault — not a missing serializer. Wrapping it in "resolving the Orleans
/// serializer … failed" would bury the only message that says what is actually wrong, so
/// preflight has to report it under its own diagnostic with the collision text intact.
/// </remarks>
[<Fact>]
let ``preflight reports a declareType collision as a collision, not a serializer failure`` () =
    let provider = codecProvider ()
    let contested = typeof<Orleans.FSharp.Tests.Collision.ContestedPayload>

    // The type itself has a codec: this is not a missing-serializer case.
    (provider :> IFieldCodecProvider).GetCodec contested |> ignore

    // Something else already claimed the name, so the declaration must be rejected.
    let shadow = emitTypeNamed contested.FullName
    test <@ shadow.FullName = contested.FullName @>
    test <@ shadow <> contested @>
    FSharpBinaryFormat.declareType shadow

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            SerializerPreflight.checkType provider "collision.probe" "argument" "operation 'store'" contested)

    test <@ error.Message.Contains "cannot be declared as a top-level payload type" @>
    test <@ error.Message.Contains "already declared" @>
    test <@ error.Message.Contains "collision.probe" @>
    test <@ error.Message.Contains "operation 'store'" @>
    test <@ not (error.Message.Contains "resolving the Orleans serializer") @>
    test <@ not (error.Message.Contains "has no registered Orleans serializer") @>

    // The accurate cause is still reachable, unaltered.
    test <@ error.InnerException.Message.Contains contested.FullName @>

// ──────────────────────────────────────────────────────────────────────────────
// Client startup preflight of the fixed transport types
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A codec provider which delegates everything to a real one except <c>GetCodec(Type)</c> for one
/// nominated type, which it fails. That is the only way to reach the per-type failure arm of
/// <c>FunctionalTransportTypes.preflight</c>: in a real process the assembly-level
/// <c>[TypeManifestProvider]</c> of <c>Orleans.FSharp.Abstractions</c> always answers for the
/// three fixed types (proven by the bare-serializer test below).
/// </summary>
/// <summary>
/// Preflight resolves field codecs by <see cref="T:System.Type" /> and nothing else; any other
/// member being reached would be a change of behaviour the double below must not hide.
/// </summary>
let private unreached<'T> () : 'T =
    raise (NotSupportedException "the preflight double only serves GetCodec(Type)")

[<Sealed>]
type private CodecProviderFailingFor(inner: ICodecProvider, failFor: Type) =

    let fields = inner :> IFieldCodecProvider
    let copiers = inner :> IDeepCopierProvider

    interface ICodecProvider with
        member _.Services = inner.Services

    interface IFieldCodecProvider with
        member _.GetCodec<'TField>() : IFieldCodec<'TField> = unreached ()
        member _.TryGetCodec<'TField>() : IFieldCodec<'TField> = unreached ()
        member _.TryGetCodec(fieldType: Type) = fields.TryGetCodec fieldType

        member _.GetCodec(fieldType: Type) =
            if fieldType = failFor then
                raise (InvalidOperationException $"no codec is registered for '{fieldType.FullName}'")
            else
                fields.GetCodec fieldType

    interface IBaseCodecProvider with
        member _.GetBaseCodec<'TField when 'TField: not struct>() : IBaseCodec<'TField> = unreached ()

    interface IValueSerializerProvider with
        member _.GetValueSerializer<'TField
            when 'TField: struct and 'TField :> ValueType and 'TField: (new: unit -> 'TField)>
            ()
            : IValueSerializer<'TField> =
            unreached ()

    interface IActivatorProvider with
        member _.GetActivator<'T>() : IActivator<'T> = unreached ()

    interface IDeepCopierProvider with
        member _.GetDeepCopier<'T>() : IDeepCopier<'T> = unreached ()
        member _.TryGetDeepCopier<'T>() : IDeepCopier<'T> = unreached ()
        member _.GetDeepCopier(fieldType: Type) = copiers.GetDeepCopier fieldType
        member _.TryGetDeepCopier(fieldType: Type) = copiers.TryGetDeepCopier fieldType
        member _.GetBaseCopier<'T when 'T: not struct>() : IBaseCopier<'T> = unreached ()

/// <summary>A service provider exposing exactly one service (or none at all).</summary>
let private providerOf (service: obj) =
    { new IServiceProvider with
        member _.GetService(serviceType: Type) =
            match service with
            | :? ICodecProvider when serviceType = typeof<ICodecProvider> -> service
            | _ -> null }

/// <summary>The service provider a functional client really builds.</summary>
let private functionalClientProvider () =
    let services = ServiceCollection()

    services.AddSingleton<Orleans.GrainReferences.IGrainReferenceActivatorProvider>(fun _ ->
        Unchecked.defaultof<Orleans.GrainReferences.IGrainReferenceActivatorProvider>)
    |> ignore

    FunctionalClientServices.addTo services |> ignore
    services.BuildServiceProvider()

[<Fact>]
let ``the preflight list is exactly the three fixed transport types`` () =
    // Non-vacuity guard for every preflight test below: an empty or shortened list would make
    // them all pass without checking anything.
    let names =
        FunctionalTransportTypes.all |> Array.map (fun candidate -> candidate.Name) |> Array.toList

    test <@ names = [ "FunctionalRequest"; "FunctionalRequestEnvelope"; "FunctionalReply" ] @>

[<Fact>]
let ``the fixed transport types pass preflight on a functional client`` () =
    use provider = functionalClientProvider ()

    // Each type really resolves a codec — the assertion the preflight loop makes.
    let codecs = provider.GetRequiredService<ICodecProvider>() :> IFieldCodecProvider

    for transportType in FunctionalTransportTypes.all do
        test <@ not (isNull (box (codecs.GetCodec transportType))) @>

    FunctionalTransportTypes.preflight provider

/// <remarks>
/// The honest scope of the client preflight, established by test rather than by prose: because
/// <c>Orleans.FSharp.Abstractions</c> carries an assembly-level <c>[TypeManifestProvider]</c>,
/// even a serializer registration which knows nothing about the functional transport resolves all
/// three types. The check therefore cannot fail in a process that loaded the assembly at all.
/// </remarks>
[<Fact>]
let ``a bare Orleans serializer already resolves the fixed transport types`` () =
    let services = ServiceCollection()
    ServiceCollectionExtensions.AddSerializer(services, Action<ISerializerBuilder>(ignore)) |> ignore
    use provider = services.BuildServiceProvider()

    FunctionalTransportTypes.preflight provider

[<Fact>]
let ``preflight names the fixed transport type whose codec cannot be resolved`` () =
    // Once per fixed type, so the loop is proven to cover every element of the list.
    for failFor in FunctionalTransportTypes.all do
        use client = functionalClientProvider ()
        let real = client.GetRequiredService<ICodecProvider>()
        let broken = CodecProviderFailingFor(real, failFor) :> ICodecProvider

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                FunctionalTransportTypes.preflight (providerOf broken))

        test <@ error.Message.StartsWith(FunctionalDiagnostics.BindingStage, StringComparison.Ordinal) @>
        test <@ error.Message.Contains failFor.FullName @>
        test <@ error.Message.Contains "has no registered Orleans serializer in this process" @>
        test <@ error.Message.Contains "AddFunctionalGrainClient" @>

        // The accurate cause is preserved, not swallowed.
        test <@ not (isNull error.InnerException) @>
        test <@ error.InnerException.Message.Contains failFor.FullName @>

[<Fact>]
let ``preflight rejects a process with no Orleans serializer at all`` () =
    let error =
        Assert.Throws<InvalidOperationException>(fun () -> FunctionalTransportTypes.preflight (providerOf null))

    test <@ error.Message.StartsWith(FunctionalDiagnostics.BindingStage, StringComparison.Ordinal) @>
    test <@ error.Message.Contains "no ICodecProvider is registered in this process" @>
    test <@ isNull error.InnerException @>

/// <summary>Records what a lifecycle participant subscribed, so the callback can be run.</summary>
[<Sealed>]
type private RecordingClientLifecycle() =
    let subscriptions = ResizeArray<string * int * ILifecycleObserver>()

    member _.Subscriptions = List.ofSeq subscriptions

    interface IClusterClientLifecycle with
        member _.Subscribe(name: string, stage: int, observer: ILifecycleObserver) =
            subscriptions.Add((name, stage, observer))

            { new IDisposable with
                member _.Dispose() = () }

/// <remarks>
/// The descriptor test above proves the participant is registered; this one proves the
/// registration does something — that the subscribed start callback really runs the preflight.
/// </remarks>
[<Fact>]
let ``the client startup participant runs the transport preflight when the client starts`` () =
    let lifecycle = RecordingClientLifecycle()

    let participant =
        FunctionalClientStartupValidator(providerOf null) :> ILifecycleParticipant<IClusterClientLifecycle>

    participant.Participate lifecycle

    match lifecycle.Subscriptions with
    | [ (name, stage, observer) ] ->
        test <@ name = "Orleans.FSharp.FunctionalGrainClient" @>
        test <@ stage = ServiceLifecycleStage.RuntimeInitialize @>

        // The provider handed to the validator has no ICodecProvider, so starting must fail with
        // the preflight's own diagnostic rather than silently succeeding.
        let error =
            Assert.ThrowsAsync<InvalidOperationException>(fun () -> observer.OnStart CancellationToken.None)
            |> fun task -> task.GetAwaiter().GetResult()

        test <@ error.Message.Contains "no ICodecProvider is registered in this process" @>
    | other -> failwith $"expected exactly one lifecycle subscription, got {List.length other}"

/// <remarks>
/// The same participant, started against the service provider a functional client really builds:
/// the preflight passes and the start callback completes.
/// </remarks>
[<Fact>]
let ``the client startup participant succeeds against a real functional client provider`` () =
    use provider = functionalClientProvider ()
    let lifecycle = RecordingClientLifecycle()

    let participant =
        FunctionalClientStartupValidator(provider) :> ILifecycleParticipant<IClusterClientLifecycle>

    participant.Participate lifecycle

    match lifecycle.Subscriptions with
    | [ (_, _, observer) ] ->
        let start = observer.OnStart CancellationToken.None
        start.GetAwaiter().GetResult()
        test <@ start.IsCompletedSuccessfully @>
    | other -> failwith $"expected exactly one lifecycle subscription, got {List.length other}"

[<Fact>]
let ``the functional client registers a startup participant for the transport preflight`` () =
    let services = ServiceCollection()
    // The functional provider is inserted BEFORE the first existing one, so a stock provider
    // descriptor has to be present; it is never resolved by this test.
    services.AddSingleton<Orleans.GrainReferences.IGrainReferenceActivatorProvider>(fun _ ->
        Unchecked.defaultof<Orleans.GrainReferences.IGrainReferenceActivatorProvider>)
    |> ignore

    FunctionalClientServices.addTo services |> ignore

    let participants =
        services
        |> Seq.filter (fun descriptor ->
            descriptor.ServiceType = typeof<Orleans.ILifecycleParticipant<Orleans.IClusterClientLifecycle>>
            && descriptor.ImplementationType = typeof<FunctionalClientStartupValidator>)
        |> Seq.length

    test <@ participants = 1 @>

