namespace Orleans.FSharp

open System
open System.Collections.Generic
open System.Globalization
open Microsoft.Extensions.Options
open Orleans.Configuration
open Orleans.Metadata
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics
open Orleans.FSharp.FunctionalSiloDiagnostics

/// <summary>Maps a registered closed marker CLR type to its explicit Orleans grain type.</summary>
[<Sealed>]
type internal FunctionalGrainTypeProvider(registry: FunctionalGrainRegistry) =

    interface IGrainTypeProvider with
        member _.TryGetGrainType(candidate: Type, grainType: byref<GrainType>) =
            match registry.TryByMarker candidate with
            | Some entry ->
                grainType <- GrainType.Create entry.GrainTypeName
                true
            | None -> false

/// <summary>Maps a registered closed target interface to the stable functional interface ID.</summary>
[<Sealed>]
type internal FunctionalGrainInterfaceTypeProvider(registry: FunctionalGrainRegistry) =

    interface IGrainInterfaceTypeProvider with
        member _.TryGetGrainInterfaceType(candidate: Type, interfaceType: byref<GrainInterfaceType>) =
            match registry.TryByInterface candidate with
            | Some entry ->
                interfaceType <- GrainInterfaceType.Create entry.InterfaceId
                true
            | None -> false

/// <summary>
/// Publishes the fixed internal Orleans interface version and the default grain type of every
/// registered functional interface.
/// </summary>
[<Sealed>]
type internal FunctionalGrainInterfacePropertiesProvider(registry: FunctionalGrainRegistry) =

    interface IGrainInterfacePropertiesProvider with
        member _.Populate(candidate: Type, _id: GrainInterfaceType, properties: Dictionary<string, string>) =
            match registry.TryByInterface candidate with
            | Some entry ->
                properties.[WellKnownGrainInterfaceProperties.Version] <-
                    (int FunctionalIds.InterfaceVersion).ToString CultureInfo.InvariantCulture

                properties.[WellKnownGrainInterfaceProperties.DefaultGrainType] <- entry.GrainTypeName
            | None -> ()

/// <summary>
/// Replaces the implemented-interface property Orleans normalized from the closed functional
/// target interface with the registered closed functional interface ID, leaving
/// <c>IRemindable</c> and every other interface property untouched.
/// </summary>
/// <remarks>
/// The match is exact ordinal equality against a small closed set of values — the open generic
/// target-interface definition's full name (what Orleans' own normalization produces), the ID
/// its interface-type resolver reports for that definition, and the registered closed ID (so a
/// repeated <c>Populate</c> is idempotent). It is deliberately not a substring test: a
/// substring test would also claim an application interface whose name merely contains the
/// transport interface's name.
/// </remarks>
[<Sealed>]
type internal FunctionalGrainPropertiesProvider(registry: FunctionalGrainRegistry) =

    /// <summary>The open generic functional target-interface definition.</summary>
    static member val OpenInterfaceDefinition: Type = typedefof<IFunctionalGrainTarget<_>>

    /// <summary>
    /// The exact values an implemented-interface property can hold for the functional target
    /// interface before this provider has replaced it.
    /// </summary>
    static member NormalizedValues(entry: FunctionalRegistryEntry) =
        [| FunctionalGrainPropertiesProvider.OpenInterfaceDefinition.FullName
           GrainInterfaceType
               .Create(FunctionalGrainPropertiesProvider.OpenInterfaceDefinition.FullName)
               .ToString()
           entry.InterfaceId |]

    /// <summary>True when a property value names the functional target interface exactly.</summary>
    static member IsFunctionalInterfaceValue (entry: FunctionalRegistryEntry) (value: string) =
        not (isNull value)
        && FunctionalGrainPropertiesProvider.NormalizedValues entry
           |> Array.exists (fun candidate -> String.Equals(candidate, value, StringComparison.Ordinal))

    interface IGrainPropertiesProvider with
        member _.Populate(grainClass: Type, _grainType: GrainType, properties: Dictionary<string, string>) =
            match registry.TryByMarker grainClass with
            | None -> ()
            | Some entry ->
                let matches =
                    properties
                    |> Seq.filter (fun pair ->
                        pair.Key.StartsWith(
                            WellKnownGrainTypeProperties.ImplementedInterfacePrefix,
                            StringComparison.Ordinal
                        )
                        && FunctionalGrainPropertiesProvider.IsFunctionalInterfaceValue entry pair.Value)
                    |> Seq.toArray

                if matches.Length <> 1 then
                    fail
                        StartupStage
                        $"grain type '{entry.GrainTypeName}' expected exactly one implemented-interface property naming the functional target interface, but found {matches.Length}. The functional interface property cannot be replaced with the closed interface ID '{entry.InterfaceId}'."

                properties.[matches.[0].Key] <- entry.InterfaceId

                // "Collection age is frozen into manifest properties." Orleans' own
                // GrainTypeSharedContext.GetCollectionAgeLimit reads exactly this well-known
                // property (WellKnownGrainTypeProperties.IdleDeactivationPeriod, "idle-duration")
                // off the published grain manifest via TimeSpan.TryParse, before falling back to
                // a class-specific or the host's stock GrainCollectionOptions.CollectionAge — so
                // an omitted collectionAge publishes no property here and the host default
                // applies exactly as if this grain type were unknown to it. TimeSpan.ToString()
                // with no format specifier always renders the culture-invariant "c" format, which
                // TimeSpan.TryParse round-trips.
                match entry.Definition.CollectionAge with
                | Some age -> properties.[WellKnownGrainTypeProperties.IdleDeactivationPeriod] <- age.ToString()
                | None -> ()

/// <summary>
/// Atomically freezes the registry, removes the open functional marker and target-interface
/// definitions Orleans discovered by default, and adds only the registered closed types.
/// </summary>
[<Sealed>]
type internal FunctionalGrainTypeOptionsPostConfigure(registry: FunctionalGrainRegistry) =

    static let isOpenFunctional (definition: Type) (candidate: Type) =
        candidate = definition
        || (candidate.IsGenericType
            && not candidate.IsConstructedGenericType
            && candidate.GetGenericTypeDefinition() = definition)

    interface IPostConfigureOptions<GrainTypeOptions> with
        member _.PostConfigure(name: string, options: GrainTypeOptions) =
            if String.Equals(name, Options.DefaultName, StringComparison.Ordinal) then
                let snapshot = registry.Freeze()

                let openMarker = typedefof<FunctionalGrainMarker<_>>
                let openInterface = typedefof<IFunctionalGrainTarget<_>>

                options.Classes
                |> Seq.filter (isOpenFunctional openMarker)
                |> Seq.toArray
                |> Array.iter (fun candidate -> options.Classes.Remove candidate |> ignore)

                options.Interfaces
                |> Seq.filter (isOpenFunctional openInterface)
                |> Seq.toArray
                |> Array.iter (fun candidate -> options.Interfaces.Remove candidate |> ignore)

                for entry in snapshot do
                    options.Classes.Add entry.MarkerType |> ignore
                    options.Interfaces.Add entry.InterfaceType |> ignore
