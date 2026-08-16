namespace Orleans.FSharp

open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Options
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Metadata
open Orleans.Runtime
open Orleans.Storage
open Orleans.FSharp.FunctionalDiagnostics
open Orleans.FSharp.FunctionalSiloDiagnostics

/// <summary>
/// Silo startup validation: registry and manifest agreement plus serializer availability for
/// every hosted argument and reply type. It runs before the silo admits traffic, so a
/// misconfigured definition fails startup instead of failing the first call.
/// </summary>
[<Sealed>]
type internal FunctionalSiloStartupValidator(services: IServiceProvider, registry: FunctionalGrainRegistry) =

    let validate () =
        // Materializing the options runs the functional post-configure, which atomically freezes
        // the registry; every check below therefore reads the same immutable snapshot.
        let options = services.GetRequiredService<IOptions<GrainTypeOptions>>().Value
        let snapshot = registry.Snapshot

        let openMarker = typedefof<FunctionalGrainMarker<_>>
        let openInterface = typedefof<IFunctionalGrainTarget<_>>

        let isOpen (definition: Type) (candidate: Type) =
            candidate = definition
            || (candidate.IsGenericType
                && not candidate.IsConstructedGenericType
                && candidate.GetGenericTypeDefinition() = definition)

        if options.Classes |> Seq.exists (isOpen openMarker) then
            fail
                StartupStage
                "the open generic functional marker is still registered as a grain class; the functional GrainTypeOptions post-configure did not run."

        if options.Interfaces |> Seq.exists (isOpen openInterface) then
            fail
                StartupStage
                "the open generic functional target interface is still registered as a grain interface; the functional GrainTypeOptions post-configure did not run."

        for entry in snapshot do
            if not (options.Classes.Contains entry.MarkerType) then
                fail
                    StartupStage
                    $"grain type '{entry.GrainTypeName}' is registered but its closed marker '{entry.MarkerType.FullName}' is missing from GrainTypeOptions.Classes."

            if not (options.Interfaces.Contains entry.InterfaceType) then
                fail
                    StartupStage
                    $"grain type '{entry.GrainTypeName}' is registered but its closed target interface '{entry.InterfaceType.FullName}' is missing from GrainTypeOptions.Interfaces."

        // Every hosted argument, reply, and durable state type must have an Orleans serializer on
        // this silo, and every one of them is declared as a top-level payload type so the F#
        // binary codec can resolve an elided field type on the receiving side.
        for entry in snapshot do
            let definition = entry.Definition
            let provider = SerializerPreflight.providerOf services definition.GrainTypeName
            SerializerPreflight.ensure provider definition.GrainTypeName definition.ApiType definition.DeclaredTypes

            SerializerPreflight.ensureStoredTypes
                provider
                definition.GrainTypeName
                (definition.Facets
                 |> Array.map (fun facet -> facet.Descriptor.StateName, facet.Descriptor.StoredType))

        // "Silo startup validates every declared period against the configured
        // ReminderOptions.MinimumReminderPeriod." The real reminder service enforces the same
        // floor lazily, at first RegisterOrUpdateReminder call during activation (an
        // ArgumentException from LocalReminderService) — this check exists to fail the same
        // misconfiguration at startup instead, before the first activation ever reaches it.
        // IOptions<ReminderOptions> resolves to its documented 1-minute default even when no
        // reminder provider is configured at all, so this is safe to call unconditionally.
        let hasAnyReminder =
            snapshot |> Array.exists (fun entry -> entry.Definition.Reminders.Length > 0)

        if hasAnyReminder then
            let reminderOptions = services.GetRequiredService<IOptions<ReminderOptions>>().Value

            for entry in snapshot do
                for reminder in entry.Definition.Reminders do
                    if reminder.Period < reminderOptions.MinimumReminderPeriod then
                        fail
                            StartupStage
                            $"reminder '{reminder.Name}' of grain type '{entry.GrainTypeName}' declares period {reminder.Period}, which is below the configured ReminderOptions.MinimumReminderPeriod of {reminderOptions.MinimumReminderPeriod}."

        // Every named storage provider of every attached facet must be registered on this silo.
        // Orleans resolves a named IGrainStorage as a keyed service, so an unregistered name
        // would otherwise surface as an activation failure on the first call.
        for entry in snapshot do
            let definition = entry.Definition

            for facet in definition.Facets do
                let descriptor = facet.Descriptor

                if isNull (box (services.GetKeyedService<IGrainStorage> descriptor.ProviderName)) then
                    fail
                        StartupStage
                        $"the persistent state '{descriptor.StateName}' (stored type '{descriptor.StoredType.FullName}') of grain type '{definition.GrainTypeName}' names storage provider '{descriptor.ProviderName}', which is not registered on this silo. Add that named IGrainStorage (for example AddMemoryGrainStorage \"{descriptor.ProviderName}\") to every silo which hosts this definition."

        // Manifest agreement: the published local grain manifest carries every registered
        // definition with its closed interface ID.
        let manifest =
            services.GetRequiredService<IClusterManifestProvider>().LocalGrainManifest

        for entry in snapshot do
            let grainType = GrainType.Create entry.GrainTypeName

            match manifest.Grains.TryGetValue grainType with
            | false, _ ->
                fail
                    StartupStage
                    $"grain type '{entry.GrainTypeName}' is registered but does not appear in this silo's grain manifest."
            | true, properties ->
                let published =
                    properties.Properties
                    |> Seq.filter (fun pair ->
                        pair.Key.StartsWith(
                            WellKnownGrainTypeProperties.ImplementedInterfacePrefix,
                            StringComparison.Ordinal
                        )
                        && String.Equals(pair.Value, entry.InterfaceId, StringComparison.Ordinal))
                    |> Seq.length

                if published <> 1 then
                    fail
                        StartupStage
                        $"grain type '{entry.GrainTypeName}' publishes {published} implemented-interface properties naming '{entry.InterfaceId}'; exactly one is required."

            if not (manifest.Interfaces.ContainsKey(GrainInterfaceType.Create entry.InterfaceId)) then
                fail
                    StartupStage
                    $"the functional interface ID '{entry.InterfaceId}' of grain type '{entry.GrainTypeName}' does not appear in this silo's grain manifest."

    interface ILifecycleParticipant<ISiloLifecycle> with
        member _.Participate(lifecycle: ISiloLifecycle) =
            lifecycle.Subscribe(
                "Orleans.FSharp.FunctionalGrainRuntime",
                ServiceLifecycleStage.RuntimeInitialize,
                Func<CancellationToken, Task>(fun _ ->
                    validate ()
                    Task.CompletedTask)
            )
            |> ignore

/// <summary>Silo-side registration of the functional grain runtime.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalSiloServices =

    /// <summary>
    /// The registry instance of this service collection, registering the silo-side services on
    /// first use so repeated <c>AddFunctionalGrain</c> calls share one registry.
    /// </summary>
    let private registryOf (services: IServiceCollection) =
        let existing =
            services
            |> Seq.tryPick (fun descriptor ->
                if descriptor.ServiceType = typeof<FunctionalGrainRegistry> then
                    match descriptor.ImplementationInstance with
                    | :? FunctionalGrainRegistry as registry -> Some registry
                    | _ -> None
                else
                    None)

        match existing with
        | Some registry -> registry
        | None ->
            let registry = FunctionalGrainRegistry()
            services.AddSingleton<FunctionalGrainRegistry> registry |> ignore

            services.AddSingleton<IGrainTypeProvider, FunctionalGrainTypeProvider>() |> ignore

            services.AddSingleton<IGrainInterfaceTypeProvider, FunctionalGrainInterfaceTypeProvider>()
            |> ignore

            services.AddSingleton<IGrainInterfacePropertiesProvider, FunctionalGrainInterfacePropertiesProvider>()
            |> ignore

            // TryAddEnumerable appends after Orleans' own ImplementedInterfaceProvider, so the
            // normalized functional interface value already exists when this provider runs.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IGrainPropertiesProvider, FunctionalGrainPropertiesProvider>()
            )

            services.AddSingleton<IPostConfigureOptions<GrainTypeOptions>, FunctionalGrainTypeOptionsPostConfigure>()
            |> ignore

            services.AddSingleton<IConfigureGrainTypeComponents, FunctionalConfigureGrainTypeComponents>()
            |> ignore

            // An application-provided clock stays authoritative.
            services.TryAddSingleton<TimeProvider>(TimeProvider.System)

            services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>, FunctionalSiloStartupValidator>()
            |> ignore

            registry

    /// <summary>Register one hosted definition together with the silo-side services.</summary>
    let addTo (services: IServiceCollection) (definition: FunctionalHostedDefinition) =
        FunctionalClientServices.addTo services |> ignore
        (registryOf services).Add definition

/// <summary>
/// Silo-side registration of one hosted functional grain definition. The silo path also runs
/// the idempotent client registration before adding server services.
/// </summary>
[<AbstractClass; Sealed; Extension>]
type FunctionalGrainSiloHostingExtensions =

    /// <summary>
    /// Register a hosted definition together with the registry, manifest providers, activator,
    /// and silo startup validation. Repeated registration of the same definition value is
    /// idempotent; conflicting contracts or definitions are configuration errors.
    /// </summary>
    [<Extension>]
    static member AddFunctionalGrain<'Actor, 'Key, 'Api, 'State>
        (builder: ISiloBuilder, definition: FunctionalGrainDefinition<'Actor, 'Key, 'Api, 'State>)
        : ISiloBuilder =
        if isNull (box builder) then
            fail SiloStage "AddFunctionalGrain requires a silo builder."

        let hosted = FunctionalHosted.create definition

        builder.ConfigureServices(fun services -> FunctionalSiloServices.addTo services hosted)
        |> ignore

        builder
