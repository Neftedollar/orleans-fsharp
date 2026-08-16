/// Phase 0 seam proof — the frozen silo registry plus the Orleans manifest
/// providers described in spec 003 "Silo registry and manifest".
namespace Orleans.FSharp.SeamProof

open System
open System.Collections.Generic
open Microsoft.Extensions.Options
open Orleans.Configuration
open Orleans.Metadata
open Orleans.Runtime

/// One hosted functional definition (spike shape).
type SeamDefinition =
    { GrainType: string
      ActorType: Type
      MarkerType: Type
      InterfaceType: Type }

    member this.InterfaceId = FunctionalIds.interfaceId this.GrainType

[<RequireQualifiedAccess>]
module SeamDefinition =

    let create<'Actor> (grainType: string) =
        { GrainType = grainType
          ActorType = typeof<'Actor>
          MarkerType = typedefof<FunctionalGrainMarker<_>>.MakeGenericType(typeof<'Actor>)
          InterfaceType = typedefof<IFunctionalGrainTarget<_>>.MakeGenericType(typeof<'Actor>) }

/// Mutable during host configuration, atomically frozen by the
/// `IPostConfigureOptions<GrainTypeOptions>` callback. Every type provider,
/// property provider and activator reads the frozen snapshot.
[<Sealed>]
type SeamRegistry() =
    let gate = obj ()
    let pending = ResizeArray<SeamDefinition>()
    let mutable frozen: SeamDefinition[] option = None

    member _.Add(definition: SeamDefinition) =
        lock gate (fun () ->
            match frozen with
            | Some _ -> invalidOp $"SeamRegistry is frozen; cannot register '{definition.GrainType}'."
            | None ->
                let clash =
                    pending
                    |> Seq.tryFind (fun d ->
                        (d.GrainType = definition.GrainType && d.ActorType <> definition.ActorType)
                        || (d.ActorType = definition.ActorType && d.GrainType <> definition.GrainType))

                match clash with
                | Some other ->
                    invalidOp
                        $"SeamRegistry conflict: '{definition.GrainType}'/{definition.ActorType.Name} vs '{other.GrainType}'/{other.ActorType.Name}."
                | None ->
                    if not (pending |> Seq.exists (fun d -> d.GrainType = definition.GrainType)) then
                        pending.Add definition)

    member _.Freeze() =
        lock gate (fun () ->
            match frozen with
            | Some snapshot -> snapshot
            | None ->
                let snapshot = pending.ToArray()
                frozen <- Some snapshot
                snapshot)

    member this.Snapshot =
        match frozen with
        | Some snapshot -> snapshot
        | None -> this.Freeze()

    member this.IsFrozen = frozen.IsSome

    member this.TryByMarker(markerType: Type) =
        this.Snapshot |> Array.tryFind (fun d -> d.MarkerType = markerType)

    member this.TryByInterface(interfaceType: Type) =
        this.Snapshot |> Array.tryFind (fun d -> d.InterfaceType = interfaceType)

    member this.TryByGrainType(grainType: string) =
        this.Snapshot |> Array.tryFind (fun d -> d.GrainType = grainType)

// ── Manifest providers ──────────────────────────────────────────────────────

/// Maps the closed marker CLR type to the explicit grain type.
[<Sealed>]
type SeamGrainTypeProvider(registry: SeamRegistry) =
    interface IGrainTypeProvider with
        member _.TryGetGrainType(t: Type, grainType: byref<GrainType>) =
            match registry.TryByMarker t with
            | Some definition ->
                grainType <- GrainType.Create definition.GrainType
                true
            | None -> false

/// Maps the closed target interface to the stable functional interface ID.
[<Sealed>]
type SeamGrainInterfaceTypeProvider(registry: SeamRegistry) =
    interface IGrainInterfaceTypeProvider with
        member _.TryGetGrainInterfaceType(t: Type, interfaceType: byref<GrainInterfaceType>) =
            match registry.TryByInterface t with
            | Some definition ->
                interfaceType <- GrainInterfaceType.Create definition.InterfaceId
                true
            | None -> false

/// Publishes the fixed Orleans interface version and the default grain type.
[<Sealed>]
type SeamGrainInterfacePropertiesProvider(registry: SeamRegistry) =
    interface IGrainInterfacePropertiesProvider with
        member _.Populate(interfaceType: Type, _id: GrainInterfaceType, properties: Dictionary<string, string>) =
            match registry.TryByInterface interfaceType with
            | Some definition ->
                properties[WellKnownGrainInterfaceProperties.Version] <-
                    string (int FunctionalIds.InterfaceVersion)

                properties[WellKnownGrainInterfaceProperties.DefaultGrainType] <- definition.GrainType
            | None -> ()

/// Replaces the implemented-interface property that Orleans normalized from the
/// closed functional interface with the registered closed interface ID.
/// Zero or multiple matching normalized entries fail silo startup.
[<Sealed>]
type SeamGrainPropertiesProvider(registry: SeamRegistry) =

    /// Recognizes an `interface.N` value that names the functional target
    /// interface in any Orleans-normalized spelling.
    static member IsFunctionalInterfaceValue(value: string) =
        not (isNull value)
        && (value.StartsWith(FunctionalIds.Prefix, StringComparison.Ordinal)
            || value.Contains(nameof IFunctionalGrainTarget, StringComparison.Ordinal))

    interface IGrainPropertiesProvider with
        member _.Populate(grainClass: Type, _grainType: GrainType, properties: Dictionary<string, string>) =
            match registry.TryByMarker grainClass with
            | None -> ()
            | Some definition ->
                let matches =
                    properties
                    |> Seq.filter (fun kv ->
                        kv.Key.StartsWith(WellKnownGrainTypeProperties.ImplementedInterfacePrefix, StringComparison.Ordinal)
                        && SeamGrainPropertiesProvider.IsFunctionalInterfaceValue kv.Value)
                    |> Seq.toArray

                if matches.Length <> 1 then
                    invalidOp
                        $"Functional grain '{definition.GrainType}' expected exactly one normalized functional interface property, found {matches.Length}."

                properties[matches[0].Key] <- definition.InterfaceId

/// Freezes the registry, removes the open functional marker/interface entries
/// discovered by default, and adds only the registered closed types.
[<Sealed>]
type SeamGrainTypeOptionsPostConfigure(registry: SeamRegistry) =
    interface IPostConfigureOptions<GrainTypeOptions> with
        member _.PostConfigure(name: string, options: GrainTypeOptions) =
            if name = Options.DefaultName then
                let snapshot = registry.Freeze()

                let openMarker = typedefof<FunctionalGrainMarker<_>>
                let openInterface = typedefof<IFunctionalGrainTarget<_>>

                options.Classes
                |> Seq.filter (fun t ->
                    t = openMarker
                    || (t.IsGenericType && not t.IsConstructedGenericType && t.GetGenericTypeDefinition() = openMarker))
                |> Seq.toArray
                |> Array.iter (fun t -> options.Classes.Remove t |> ignore)

                options.Interfaces
                |> Seq.filter (fun t ->
                    t = openInterface
                    || (t.IsGenericType
                        && not t.IsConstructedGenericType
                        && t.GetGenericTypeDefinition() = openInterface))
                |> Seq.toArray
                |> Array.iter (fun t -> options.Interfaces.Remove t |> ignore)

                for definition in snapshot do
                    options.Classes.Add definition.MarkerType |> ignore
                    options.Interfaces.Add definition.InterfaceType |> ignore
