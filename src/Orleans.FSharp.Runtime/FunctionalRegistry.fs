namespace Orleans.FSharp

open System
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>The prefix identifying the silo-registration stage in every diagnostic.</summary>
module internal FunctionalSiloDiagnostics =

    /// <summary>Prefix identifying the stage in every silo registration diagnostic.</summary>
    [<Literal>]
    let SiloStage = "Orleans.FSharp functional silo registration"

    /// <summary>Prefix identifying the stage in every silo startup validation diagnostic.</summary>
    [<Literal>]
    let StartupStage = "Orleans.FSharp functional silo startup"

/// <summary>
/// One registered definition together with the closed marker type Orleans publishes for it.
/// </summary>
[<Sealed>]
type internal FunctionalRegistryEntry(definition: FunctionalHostedDefinition) =

    let markerType =
        typedefof<FunctionalGrainMarker<_>>.MakeGenericType [| definition.ActorType |]

    /// <summary>The non-generic hosted view of the registered definition.</summary>
    member _.Definition = definition

    /// <summary>The closed <c>FunctionalGrainMarker&lt;'Actor&gt;</c> of this definition.</summary>
    member _.MarkerType = markerType

    /// <summary>The explicit Orleans grain type name.</summary>
    member _.GrainTypeName = definition.GrainTypeName

    /// <summary>The actor-brand CLR type.</summary>
    member _.ActorType = definition.ActorType

    /// <summary>The closed actor-specific Orleans target interface.</summary>
    member _.InterfaceType = definition.InterfaceType

    /// <summary>The reserved functional interface ID of this grain type.</summary>
    member _.InterfaceId = definition.InterfaceId

/// <summary>
/// The silo's definition registry. It is mutable while the host is being configured and is
/// atomically frozen into one immutable snapshot by the <c>GrainTypeOptions</c> post-configure;
/// every type provider, property provider, activator, and validator reads that snapshot, and a
/// registration attempt after the freeze fails.
/// </summary>
[<Sealed>]
type internal FunctionalGrainRegistry() =

    let gate = obj ()
    let pending = ResizeArray<FunctionalRegistryEntry>()
    let mutable frozen: FunctionalRegistryEntry[] option = None

    let describe (entry: FunctionalRegistryEntry) =
        $"grain type '{entry.GrainTypeName}' (actor brand '{entry.ActorType.FullName}')"

    /// <summary>
    /// Register one hosted definition. Repeated registration of the same definition value is
    /// idempotent; a different definition sharing an actor brand or a grain type name — whether
    /// that name was declared explicitly or derived from the actor brand — is a configuration
    /// error.
    /// </summary>
    member _.Add(definition: FunctionalHostedDefinition) =
        lock gate (fun () ->
            match frozen with
            | Some _ ->
                fail
                    FunctionalSiloDiagnostics.SiloStage
                    $"grain type '{definition.GrainTypeName}' cannot be registered because the functional definition registry is already frozen. Register every definition before the silo builds its grain manifest."
            | None ->

            let candidate = FunctionalRegistryEntry definition

            let sameDefinition =
                pending
                |> Seq.tryFind (fun existing -> obj.ReferenceEquals(existing.Definition.Source, definition.Source))

            match sameDefinition with
            | Some _ -> ()
            | None ->
                let conflict =
                    pending
                    |> Seq.tryFind (fun existing ->
                        existing.GrainTypeName = candidate.GrainTypeName
                        || existing.ActorType = candidate.ActorType)

                match conflict with
                | Some existing ->
                    fail
                        FunctionalSiloDiagnostics.SiloStage
                        $"{describe candidate} conflicts with the already registered {describe existing}. Each actor brand and each grain type name maps to exactly one registered contract and hosted definition, whether the name was declared with 'grainType' or derived from the actor brand."
                | None -> pending.Add candidate)

    /// <summary>Atomically freeze the registry and return the immutable snapshot.</summary>
    member _.Freeze() =
        lock gate (fun () ->
            match frozen with
            | Some snapshot -> snapshot
            | None ->
                let snapshot = pending.ToArray()
                frozen <- Some snapshot
                snapshot)

    /// <summary>The frozen snapshot, freezing the registry on first read.</summary>
    member this.Snapshot =
        match frozen with
        | Some snapshot -> snapshot
        | None -> this.Freeze()

    /// <summary>True once the registry has been frozen.</summary>
    member _.IsFrozen = frozen.IsSome

    /// <summary>The registered definition whose closed marker is this CLR type.</summary>
    member this.TryByMarker(markerType: Type) =
        this.Snapshot |> Array.tryFind (fun entry -> entry.MarkerType = markerType)

    /// <summary>The registered definition whose closed target interface is this CLR type.</summary>
    member this.TryByInterface(interfaceType: Type) =
        this.Snapshot |> Array.tryFind (fun entry -> entry.InterfaceType = interfaceType)

    /// <summary>The registered definition of one explicit grain type name.</summary>
    member this.TryByGrainType(grainTypeName: string) =
        this.Snapshot
        |> Array.tryFind (fun entry -> String.Equals(entry.GrainTypeName, grainTypeName, StringComparison.Ordinal))
