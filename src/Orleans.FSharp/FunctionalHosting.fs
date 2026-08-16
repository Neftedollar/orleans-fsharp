namespace Orleans.FSharp

open System.Runtime.CompilerServices
open Orleans.Hosting

/// <summary>
/// Client-side registration of the fixed functional transport. Every process which creates
/// functional references installs it once.
/// </summary>
[<AbstractClass; Sealed; Extension>]
type FunctionalGrainClientHostingExtensions =

    /// <summary>
    /// Register the functional reference activator provider, fixed request/reply serialization,
    /// payload codec services, and transport options on a client builder. Idempotent.
    /// </summary>
    /// <remarks>
    /// Phase 1 ships the compile-only stub: it returns the builder unchanged so contracts,
    /// definitions, and registration code compile before the Phase 3 transport lands.
    /// </remarks>
    [<Extension>]
    static member AddFunctionalGrainClient(builder: IClientBuilder) : IClientBuilder = builder
