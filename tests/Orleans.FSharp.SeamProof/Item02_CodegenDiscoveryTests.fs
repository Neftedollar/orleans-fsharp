/// Phase 0 item 2, ground truth for the removal seam.
///
/// The question the removal step exists to answer is: does Orleans' own codegen-driven
/// discovery admit an OPEN GENERIC grain class / grain interface into `GrainTypeOptions`,
/// in what CLR shape, and does such an entry actually reach the final grain manifest?
/// An all-F# assembly cannot answer it (F# never gets an Orleans type manifest), so the
/// referenced C# fixture assembly `Orleans.FSharp.SeamProof.CodegenFixture` supplies a
/// real, discovered open generic pair and these tests read the answer off the live silo.
module Orleans.FSharp.SeamProof.Item02_CodegenDiscoveryTests

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Orleans.Configuration
open Orleans.Metadata
open Orleans.Runtime
open Orleans.Serialization.Configuration
open Xunit

[<Collection("SeamCluster")>]
type CodegenDiscoveryTests(fixture: SeamClusterFixture) =

    let primary = fixture.SiloServices "Primary"

    let liveOptions () =
        primary.GetRequiredService<IOptions<GrainTypeOptions>>().Value

    let localManifest () =
        primary.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

    let fullTypeName (grain: GrainProperties) =
        match grain.Properties.TryGetValue WellKnownGrainTypeProperties.FullTypeName with
        | true, value -> value
        | _ -> ""

    /// Why the seed in `SeamSiloConfigurator` exists at all: the F# assembly carries no
    /// Orleans type manifest, the C# one does. This is the premise of concern #1 in the
    /// task report, asserted rather than asserted-in-prose.
    [<Fact>]
    member _.``only the C# assembly carries an Orleans codegen type manifest``() =
        let attributesOf (assembly: Reflection.Assembly) =
            assembly.GetCustomAttributes(typeof<TypeManifestProviderAttribute>, false)

        Assert.Empty(attributesOf typeof<ProbeActor>.Assembly)

        // Canary. If this fires, the C# fixture was compiled without the Orleans source
        // generator — seen once on a COLD NuGet cache, where an analyzer restored in the
        // same MSBuild invocation as the compile is not applied to that compile. Re-run
        // the build (or `dotnet restore` first); every codegen assertion below depends
        // on this, so the failure is loud by design rather than silently vacuous.
        Assert.True(
            (attributesOf CodegenFixtureTypes.Assembly).Length > 0,
            $"{CodegenFixtureTypes.Assembly.GetName().Name} carries no TypeManifestProviderAttribute: "
            + "Orleans codegen did not run for the C# fixture assembly."
        )

    /// Answers "does discovery admit an open generic, and in what CLR shape".
    [<Fact>]
    member _.``real codegen discovery admits open generic grain class and interface into GrainTypeOptions``() =
        let options = liveOptions ()

        Assert.Contains(CodegenFixtureTypes.OpenMarker, options.Classes)
        Assert.Contains(CodegenFixtureTypes.OpenInterface, options.Interfaces)

        // The exact shape: generic type DEFINITION, not a constructed generic.
        for discovered in [ CodegenFixtureTypes.OpenMarker; CodegenFixtureTypes.OpenInterface ] do
            Assert.True(discovered.IsGenericType, $"{discovered.FullName} should be generic")
            Assert.True(discovered.IsGenericTypeDefinition, $"{discovered.FullName} should be a definition")
            Assert.False(discovered.IsConstructedGenericType)
            Assert.Same(discovered, discovered.GetGenericTypeDefinition())

        // …which is precisely the predicate `SeamGrainTypeOptionsPostConfigure` filters on.
        let removalPredicate (t: Type) = t.IsGenericType && not t.IsConstructedGenericType

        Assert.True(removalPredicate CodegenFixtureTypes.OpenMarker)
        Assert.True(removalPredicate CodegenFixtureTypes.OpenInterface)

        // Control: closed functional entries must NOT match the removal predicate.
        Assert.False(removalPredicate (typedefof<FunctionalGrainMarker<_>>.MakeGenericType typeof<ProbeActor>))

    /// Answers "does an open generic entry left in GrainTypeOptions reach the manifest".
    /// Nothing removes the codegen fixture's pair, so the live manifest is the answer:
    /// it does — therefore the removal step is load-bearing, not decorative.
    [<Fact>]
    member _.``an open generic entry left in GrainTypeOptions reaches the final manifest``() =
        let manifest = localManifest ()

        Assert.Contains(manifest.Grains, (fun kv -> fullTypeName kv.Value = CodegenFixtureTypes.OpenMarker.FullName))

        Assert.Contains(
            manifest.Interfaces,
            (fun kv -> string kv.Key = CodegenFixtureTypes.OpenInterface.FullName)
        )

    /// The removal is targeted at the functional pair; other open generics survive.
    [<Fact>]
    member _.``the functional removal is not a blanket open-generic wipe``() =
        let options = liveOptions ()

        Assert.Contains(CodegenFixtureTypes.OpenMarker, options.Classes)
        Assert.Contains(CodegenFixtureTypes.OpenInterface, options.Interfaces)
        Assert.DoesNotContain(typedefof<FunctionalGrainMarker<_>>, options.Classes)
        Assert.DoesNotContain(typedefof<IFunctionalGrainTarget<_>>, options.Interfaces)
