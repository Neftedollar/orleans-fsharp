/// <summary>
/// Spec 003 "Payload limits and serializer registration": what each F# codec registration entry
/// point contributes, that the two entry points share one codec registration, and how the
/// top-level payload type table behaves when a name is declared twice.
/// </summary>
module Orleans.FSharp.Tests.FunctionalSerializerRegistrationTests

open System
open System.Reflection
open System.Reflection.Emit
open Microsoft.Extensions.DependencyInjection
open Orleans.Serialization
open Orleans.Serialization.Cloning
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
