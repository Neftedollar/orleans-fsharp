namespace Orleans.FSharp

open Orleans.Hosting
open Orleans.FSharp.Runtime

/// <summary>
/// One functional grain definition boxed for hosting through
/// <see cref="M:Orleans.FSharp.FunctionalScripting.startOnPorts"/>, erasing its four type
/// parameters (<c>'Actor</c>, <c>'Key</c>, <c>'Api</c>, <c>'State</c>) into a single registration
/// closure so a heterogeneous set of definitions can share one plain F# list.
/// </summary>
[<Sealed>]
type FunctionalGrainRegistration internal (register: ISiloBuilder -> unit) =

    /// <summary>Apply this registration's <c>AddFunctionalGrain</c> call to the silo builder.</summary>
    member internal _.Apply(builder: ISiloBuilder) = register builder

/// <summary>Construction of boxed functional grain registrations.</summary>
[<RequireQualifiedAccess>]
module FunctionalGrainRegistration =

    /// <summary>
    /// Box one sealed functional grain definition for
    /// <see cref="M:Orleans.FSharp.FunctionalScripting.startOnPorts"/>.
    /// </summary>
    let of' (definition: FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>) : FunctionalGrainRegistration =
        FunctionalGrainRegistration(fun builder -> builder.AddFunctionalGrain definition |> ignore)

/// <summary>
/// <c>Scripting</c>, extended to host functional grain definitions. A separate module from
/// <see cref="T:Orleans.FSharp.Scripting"/> rather than an overload of it: <c>AddFunctionalGrain</c>
/// and <c>FunctionalGrainDefinition</c> live in this (<c>Orleans.FSharp.Runtime</c>) assembly,
/// one layer above <c>Orleans.FSharp</c>, which cannot depend back on it -- so
/// <c>Scripting.startOnPorts</c> itself cannot take a functional-definition parameter. This
/// module reuses <c>Scripting</c>'s own host-building core (<c>Scripting.startOnPortsWith</c>,
/// <c>internal</c> and visible here through this project's <c>InternalsVisibleTo</c> grant from
/// <c>Orleans.FSharp</c>) rather than duplicating the localhost-clustering / memory-storage /
/// memory-streams recipe, and returns the same <see cref="T:Orleans.FSharp.Scripting.SiloHandle"/>
/// <c>Scripting.startOnPorts</c> does, so <c>Scripting.getGrain</c> and <c>Scripting.shutdown</c>
/// work unchanged against it.
/// </summary>
[<RequireQualifiedAccess>]
module FunctionalScripting =

    /// <summary>
    /// Starts a silo on specific ports hosting the given functional grain definitions, applying
    /// each through <c>AddFunctionalGrain</c> inside the same builder callback
    /// <c>Scripting.startOnPorts</c> uses, plus the manifest pre-load a standalone F# host needs
    /// (<c>SiloConfig.manifestAssemblies</c> -- see its remarks: an F#-only host never runs the
    /// Roslyn-generated <c>[assembly: ApplicationPart]</c> scan that would otherwise discover the
    /// functional transport proxies and the in-memory storage/reminder/stream grains).
    /// </summary>
    /// <param name="siloPort">The silo-to-silo communication port.</param>
    /// <param name="gatewayPort">The client-to-silo gateway port.</param>
    /// <param name="registrations">The functional grain definitions to host, boxed with
    /// <c>FunctionalGrainRegistration.of'</c>.</param>
    /// <returns>A Task containing a SiloHandle for interacting with the silo.</returns>
    let startOnPorts
        (siloPort: int)
        (gatewayPort: int)
        (registrations: FunctionalGrainRegistration list)
        : System.Threading.Tasks.Task<Scripting.SiloHandle> =
        SiloConfig.manifestAssemblies.Force() |> ignore

        Scripting.startOnPortsWith siloPort gatewayPort (fun siloBuilder ->
            for registration in registrations do
                registration.Apply siloBuilder)
