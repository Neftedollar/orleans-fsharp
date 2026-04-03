namespace Orleans.FSharp.GrainDirectory

open Orleans.Hosting

/// <summary>
/// Grain directory provider types that determine how grain locations are stored and resolved.
/// The grain directory maps grain identities to their physical silo locations.
/// </summary>
[<NoEquality; NoComparison>]
type GrainDirectoryProvider =
    /// <summary>In-memory distributed grain directory (the Orleans default). No additional packages required.</summary>
    | Default
    /// <summary>Redis-backed grain directory. Requires the <c>Microsoft.Orleans.GrainDirectory.Redis</c> NuGet package.</summary>
    | Redis of connectionString: string
    /// <summary>Azure Table-backed grain directory. Requires the <c>Microsoft.Orleans.GrainDirectory.AzureStorage</c> NuGet package.</summary>
    | AzureStorage of connectionString: string
    /// <summary>Custom grain directory configuration via an ISiloBuilder transformation.</summary>
    | Custom of configurator: (ISiloBuilder -> ISiloBuilder)

/// <summary>
/// Functions for configuring the grain directory provider on an Orleans silo.
/// </summary>
[<RequireQualifiedAccess>]
module GrainDirectory =

    /// <summary>
    /// Invokes an extension method on ISiloBuilder by name, searching loaded assemblies.
    /// Throws InvalidOperationException with a helpful message if the method or assembly is not found.
    /// </summary>
    let private invokeExtensionMethod (methodName: string) (args: obj array) (packageHint: string) (siloBuilder: ISiloBuilder) =
        let siloBuilderType = typeof<ISiloBuilder>

        let extensionMethod =
            System.AppDomain.CurrentDomain.GetAssemblies()
            |> Array.collect (fun asm ->
                try asm.GetTypes() with _ -> Array.empty)
            |> Array.collect (fun t ->
                if t.IsAbstract && t.IsSealed then
                    t.GetMethods(System.Reflection.BindingFlags.Static ||| System.Reflection.BindingFlags.Public)
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
    /// Creates a typed Action delegate that sets a named property on the options object via reflection.
    /// Used to configure provider-specific options without hard compile-time dependencies.
    /// </summary>
    let private makeOptionsAction (propertyName: string) (value: obj) : System.Action<obj> =
        System.Action<obj>(fun options ->
            let prop = options.GetType().GetProperty(propertyName)
            if prop <> null then
                prop.SetValue(options, value))

    /// <summary>
    /// Configures the grain directory for the silo based on the specified provider.
    /// Returns a function that can be applied to an ISiloBuilder.
    /// </summary>
    /// <param name="provider">The grain directory provider to configure.</param>
    /// <returns>A function that configures the ISiloBuilder with the specified grain directory.</returns>
    let configure (provider: GrainDirectoryProvider) : (ISiloBuilder -> ISiloBuilder) =
        match provider with
        | Default ->
            fun builder -> builder
        | Redis connectionString ->
            fun builder ->
                invokeExtensionMethod
                    "UseRedisGrainDirectoryAsDefault"
                    [| box (makeOptionsAction "ConnectionString" connectionString) |]
                    "Microsoft.Orleans.GrainDirectory.Redis"
                    builder
        | AzureStorage connectionString ->
            fun builder ->
                invokeExtensionMethod
                    "UseAzureTableGrainDirectoryAsDefault"
                    [| box (makeOptionsAction "ConnectionString" connectionString) |]
                    "Microsoft.Orleans.GrainDirectory.AzureStorage"
                    builder
        | Custom configurator ->
            configurator
