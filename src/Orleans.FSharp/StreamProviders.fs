namespace Orleans.FSharp.StreamProviders

open System
open Orleans.Hosting

/// <summary>
/// Functions for configuring stream providers on an Orleans silo using reflection,
/// so that the underlying NuGet packages (Microsoft.Orleans.Streaming.EventHubs,
/// Microsoft.Orleans.Streaming.AzureStorage) remain optional runtime dependencies.
/// </summary>
[<RequireQualifiedAccess>]
module StreamProviders =

    /// <summary>
    /// Invokes an extension method on ISiloBuilder by name, searching loaded assemblies.
    /// Throws InvalidOperationException with a helpful message if the method or assembly is not found.
    /// </summary>
    let private invokeExtensionMethod (methodName: string) (args: obj array) (packageHint: string) (siloBuilder: ISiloBuilder) =
        let siloBuilderType = typeof<ISiloBuilder>

        let extensionMethod =
            AppDomain.CurrentDomain.GetAssemblies()
            |> Array.collect (fun asm ->
                try asm.GetTypes() with _ -> Array.empty)
            |> Array.collect (fun t ->
                if t.IsAbstract && t.IsSealed then
                    t.GetMethods(Reflection.BindingFlags.Static ||| Reflection.BindingFlags.Public)
                    |> Array.filter (fun m ->
                        m.Name = methodName
                        && m.GetParameters().Length = args.Length + 1
                        && siloBuilderType.IsAssignableFrom(m.GetParameters().[0].ParameterType))
                else
                    Array.empty)
            |> Array.tryHead

        match extensionMethod with
        | Some m ->
            m.Invoke(null, Array.append [| box siloBuilder |] args) |> ignore
            siloBuilder
        | None ->
            invalidOp
                $"Extension method '{methodName}' not found. Install the NuGet package '{packageHint}' and ensure it is referenced in your project."

    /// <summary>
    /// Configure Azure Event Hubs stream provider on the silo.
    /// Requires the <c>Microsoft.Orleans.Streaming.EventHubs</c> NuGet package at runtime.
    /// Uses reflection to invoke the AddEventHubStreams extension method.
    /// </summary>
    /// <param name="name">The name of the stream provider.</param>
    /// <param name="connStr">The Event Hubs connection string.</param>
    /// <param name="hubName">The Event Hub name (path).</param>
    /// <returns>A function that configures the ISiloBuilder with the Event Hubs stream provider.</returns>
    let addEventHubStreams (name: string) (connStr: string) (hubName: string) : (ISiloBuilder -> ISiloBuilder) =
        fun builder ->
            invokeExtensionMethod
                "AddEventHubStreams"
                [| box name; box connStr; box hubName |]
                "Microsoft.Orleans.Streaming.EventHubs"
                builder

    /// <summary>
    /// Configure Azure Queue stream provider on the silo.
    /// Requires the <c>Microsoft.Orleans.Streaming.AzureStorage</c> NuGet package at runtime.
    /// Uses reflection to invoke the AddAzureQueueStreams extension method.
    /// </summary>
    /// <param name="name">The name of the stream provider.</param>
    /// <param name="connStr">The Azure Storage connection string.</param>
    /// <returns>A function that configures the ISiloBuilder with the Azure Queue stream provider.</returns>
    let addAzureQueueStreams (name: string) (connStr: string) : (ISiloBuilder -> ISiloBuilder) =
        fun builder ->
            invokeExtensionMethod
                "AddAzureQueueStreams"
                [| box name; box connStr |]
                "Microsoft.Orleans.Streaming.AzureStorage"
                builder
