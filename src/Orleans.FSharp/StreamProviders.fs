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
    /// Finds a static extension method on ISiloBuilder by name and parameter count, searching loaded assemblies.
    /// Throws InvalidOperationException with a helpful message naming the package if the method is not found.
    /// </summary>
    /// <param name="methodName">The extension method name (e.g., "AddRedisStreams").</param>
    /// <param name="paramCount">The total parameter count including the ISiloBuilder receiver.</param>
    /// <param name="packageHint">The NuGet package to suggest installing when the method is absent.</param>
    let private findExtensionMethod (methodName: string) (paramCount: int) (packageHint: string) : Reflection.MethodInfo =
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
                        && m.GetParameters().Length = paramCount
                        && siloBuilderType.IsAssignableFrom(m.GetParameters().[0].ParameterType))
                else
                    Array.empty)
            |> Array.tryHead

        match extensionMethod with
        | Some m -> m
        | None ->
            invalidOp
                $"Extension method '{methodName}' not found. Install the NuGet package '{packageHint}' and ensure it is referenced in your project."

    /// <summary>
    /// Invokes an extension method on ISiloBuilder by name, searching loaded assemblies.
    /// Throws InvalidOperationException with a helpful message if the method or assembly is not found.
    /// </summary>
    let private invokeExtensionMethod (methodName: string) (args: obj array) (packageHint: string) (siloBuilder: ISiloBuilder) =
        let m = findExtensionMethod methodName (args.Length + 1) packageHint
        m.Invoke(null, Array.append [| box siloBuilder |] args) |> ignore
        siloBuilder

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

    /// <summary>
    /// Creates strongly-typed <c>Action&lt;T&gt;</c> / <c>Action&lt;T1,T2&gt;</c> delegates whose bodies are
    /// untyped reflection closures. Used to build the configurator delegate that the Redis Streams
    /// extension method requires, without referencing the optional package's types at compile time.
    /// </summary>
    [<AbstractClass; Sealed>]
    type private RedisDelegateFactory =
        static member Action1<'T>(body: obj -> unit) : Action<'T> = Action<'T>(fun (t: 'T) -> body (box t))

        static member Action2<'T1, 'T2>(body: obj -> obj -> unit) : Action<'T1, 'T2> =
            Action<'T1, 'T2>(fun (a: 'T1) (b: 'T2) -> body (box a) (box b))

    /// <summary>
    /// Builds the <c>Action&lt;SiloRedisStreamConfigurator&gt;</c> the Redis Streams provider requires,
    /// wiring the supplied connection string into the provider's <c>RedisStreamingOptions.ConfigurationOptions</c>
    /// via the configurator's <c>ConfigureOptions(Action&lt;RedisStreamingOptions, IServiceProvider&gt;)</c> method.
    /// All types are resolved by reflection so the optional package stays off the compile-time dependency set.
    /// </summary>
    let private buildRedisConfigurator (configuratorActionType: Type) (connectionString: string) : obj =
        // configuratorActionType is Action<SiloRedisStreamConfigurator>
        let configuratorType = configuratorActionType.GetGenericArguments().[0]

        // Find ConfigureOptions(Action<RedisStreamingOptions, IServiceProvider>)
        let configureOptions =
            configuratorType.GetMethods()
            |> Array.filter (fun m -> m.Name = "ConfigureOptions" && m.GetParameters().Length = 1)
            |> Array.tryPick (fun m ->
                let pt = m.GetParameters().[0].ParameterType
                if pt.IsGenericType && pt.GetGenericTypeDefinition() = typedefof<Action<_, _>> then
                    Some(m, pt)
                else
                    None)

        match configureOptions with
        | None ->
            invalidOp
                "Could not locate 'ConfigureOptions(Action<RedisStreamingOptions, IServiceProvider>)' on the Redis stream \
                 configurator. The installed 'Microsoft.Orleans.Streaming.Redis' version may be incompatible."
        | Some(configureOptionsMethod, action2Type) ->
            let optionsType = action2Type.GetGenericArguments().[0] // RedisStreamingOptions
            let spType = action2Type.GetGenericArguments().[1] // IServiceProvider

            let configurationOptionsProp =
                match optionsType.GetProperty("ConfigurationOptions") with
                | null ->
                    invalidOp
                        "'RedisStreamingOptions.ConfigurationOptions' was not found. The installed \
                         'Microsoft.Orleans.Streaming.Redis' version may be incompatible."
                | p -> p

            let parseMethod =
                match configurationOptionsProp.PropertyType.GetMethod("Parse", [| typeof<string> |]) with
                | null ->
                    invalidOp
                        "'ConfigurationOptions.Parse(string)' was not found on the StackExchange.Redis configuration type. \
                         Ensure a compatible StackExchange.Redis is referenced."
                | m -> m

            // (options, _serviceProvider) => options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString)
            let innerBody: obj -> obj -> unit =
                fun optionsObj _sp ->
                    let parsed = parseMethod.Invoke(null, [| box connectionString |])
                    configurationOptionsProp.SetValue(optionsObj, parsed)

            let innerDelegate =
                typeof<RedisDelegateFactory>
                    .GetMethod("Action2")
                    .MakeGenericMethod(optionsType, spType)
                    .Invoke(null, [| box innerBody |])

            // configurator => configurator.ConfigureOptions(innerDelegate)
            let outerBody: obj -> unit =
                fun configuratorObj -> configureOptionsMethod.Invoke(configuratorObj, [| innerDelegate |]) |> ignore

            typeof<RedisDelegateFactory>
                .GetMethod("Action1")
                .MakeGenericMethod(configuratorType)
                .Invoke(null, [| box outerBody |])

    /// <summary>
    /// Configure the Redis Streams provider on the silo (Redis Streams transport, introduced in Orleans 10.1.0).
    /// Requires the <c>Microsoft.Orleans.Streaming.Redis</c> NuGet package at runtime.
    /// Uses reflection to invoke the AddRedisStreams extension method and to build the configurator delegate it
    /// requires, so the package stays an optional runtime dependency.
    /// </summary>
    /// <param name="name">The name of the stream provider.</param>
    /// <param name="connectionString">The Redis connection string (e.g., "localhost:6379"), parsed into StackExchange.Redis ConfigurationOptions.</param>
    /// <returns>A function that configures the ISiloBuilder with the Redis Streams provider.</returns>
    let addRedisStreams (name: string) (connectionString: string) : (ISiloBuilder -> ISiloBuilder) =
        fun builder ->
            // Resolve the extension method first so an absent package yields the clean "install the package" error
            // before any configurator reflection runs.
            let m = findExtensionMethod "AddRedisStreams" 3 "Microsoft.Orleans.Streaming.Redis"
            let configuratorActionType = m.GetParameters().[2].ParameterType
            let configurator = buildRedisConfigurator configuratorActionType connectionString
            m.Invoke(null, [| box builder; box name; configurator |]) |> ignore
            builder
