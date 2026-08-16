/// Phase 0 seam proof — client and silo registration (spec 003
/// "Builder registration" and "Custom reference selection").
namespace Orleans.FSharp.SeamProof

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Options
open Orleans.Configuration
open Orleans.GrainReferences
open Orleans.Hosting
open Orleans.Metadata
open Orleans.Runtime

[<RequireQualifiedAccess>]
module SeamRegistration =

    /// Orleans installs its default `IGrainReferenceActivatorProvider`s before the
    /// builder-extension callbacks run; the functional provider descriptor must be
    /// inserted immediately before the first existing provider descriptor.
    let insertReferenceActivatorProvider (services: IServiceCollection) =
        let index =
            services
            |> Seq.tryFindIndex (fun d -> d.ServiceType = typeof<IGrainReferenceActivatorProvider>)

        match index with
        | None ->
            invalidOp
                "No existing IGrainReferenceActivatorProvider descriptor found; functional reference selection cannot be ordered."
        | Some i ->
            let descriptor =
                ServiceDescriptor.Singleton<IGrainReferenceActivatorProvider>(fun (sp: IServiceProvider) ->
                    SeamGrainReferenceActivatorProvider sp :> IGrainReferenceActivatorProvider)

            services.Insert(i, descriptor)

    /// Client-side (and shared) transport registration.
    let addClientServices (services: IServiceCollection) =
        insertReferenceActivatorProvider services
        services

    /// Silo-side registry, manifest providers, and activation seam.
    let addSiloServices (services: IServiceCollection) (registry: SeamRegistry) =
        addClientServices services |> ignore

        services.AddSingleton<SeamRegistry>(registry) |> ignore

        services.AddSingleton<IGrainTypeProvider, SeamGrainTypeProvider>() |> ignore

        services.AddSingleton<IGrainInterfaceTypeProvider, SeamGrainInterfaceTypeProvider>()
        |> ignore

        services.AddSingleton<IGrainInterfacePropertiesProvider, SeamGrainInterfacePropertiesProvider>()
        |> ignore

        // TryAddEnumerable appends after Orleans' own ImplementedInterfaceProvider.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IGrainPropertiesProvider, SeamGrainPropertiesProvider>()
        )

        services.AddSingleton<IPostConfigureOptions<GrainTypeOptions>, SeamGrainTypeOptionsPostConfigure>()
        |> ignore

        services.AddSingleton<IConfigureGrainTypeComponents, SeamConfigureGrainTypeComponents>()
        |> ignore

        services
