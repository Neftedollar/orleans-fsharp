namespace Orleans.FSharp

open System.Runtime.CompilerServices
open Orleans.Hosting

/// <summary>
/// Silo-side registration of one hosted functional grain definition. The silo path also runs
/// the idempotent client registration before adding server services.
/// </summary>
[<AbstractClass; Sealed; Extension>]
type FunctionalGrainSiloHostingExtensions =

    /// <summary>
    /// Register a hosted definition together with the registry, manifest providers, activator,
    /// persistence, reminder, timer, and silo validation services.
    /// </summary>
    /// <remarks>
    /// Phase 1 ships the compile-only stub: it returns the builder unchanged so contracts,
    /// definitions, and registration code compile before the Phase 3 silo path lands.
    /// </remarks>
    [<Extension>]
    static member AddFunctionalGrain<'Actor, 'Key, 'Api, 'State>
        (builder: ISiloBuilder, definition: FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>)
        : ISiloBuilder =
        ignore definition
        builder
