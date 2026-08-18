namespace Orleans.FSharp

open System.Runtime.CompilerServices
open Orleans.Hosting
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// Client-side registration of the fixed functional transport. Every process which creates
/// functional references installs it once.
/// </summary>
[<AbstractClass; Sealed; Extension>]
type FunctionalGrainClientHostingExtensions =

    /// <summary>
    /// Register the functional reference activator provider, fixed request/reply serialization,
    /// payload codec services, transport options with startup validation, and the F# generalized
    /// codec with its type filter on a client builder. Idempotent.
    /// </summary>
    /// <param name="builder">The client builder to configure.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="System.InvalidOperationException">Thrown when <paramref name="builder"/> is null.</exception>
    [<Extension>]
    static member AddFunctionalGrainClient(builder: IClientBuilder) : IClientBuilder =
        if isNull (box builder) then
            fail BindingStage "AddFunctionalGrainClient requires a client builder."

        FunctionalClientServices.addTo builder.Services |> ignore
        builder
