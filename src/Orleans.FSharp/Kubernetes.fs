namespace Orleans.FSharp.Kubernetes

open System
open Orleans.Configuration
open Orleans.Hosting

/// <summary>
/// Functions for configuring Kubernetes clustering on an Orleans silo using reflection,
/// so that the <c>Microsoft.Orleans.Hosting.Kubernetes</c> NuGet package remains an optional runtime dependency.
/// </summary>
[<RequireQualifiedAccess>]
module Kubernetes =

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
    /// Configure Kubernetes clustering for an Orleans silo.
    /// Uses the Kubernetes API to discover silo endpoints via <c>UseKubernetesHosting</c>.
    /// Requires the <c>Microsoft.Orleans.Hosting.Kubernetes</c> NuGet package at runtime.
    /// </summary>
    /// <returns>A function that configures the ISiloBuilder with Kubernetes clustering.</returns>
    let useKubernetesClustering : (ISiloBuilder -> ISiloBuilder) =
        fun builder ->
            invokeExtensionMethod
                "UseKubernetesHosting"
                Array.empty
                "Microsoft.Orleans.Hosting.Kubernetes"
                builder

    /// <summary>
    /// Configure Kubernetes clustering with a custom namespace.
    /// Calls <c>UseKubernetesHosting</c> and then configures the <c>ClusterOptions.ServiceId</c>
    /// to the specified namespace for multi-tenant Kubernetes deployments.
    /// Requires the <c>Microsoft.Orleans.Hosting.Kubernetes</c> NuGet package at runtime.
    /// </summary>
    /// <param name="ns">The Kubernetes namespace to use as the service identifier.</param>
    /// <returns>A function that configures the ISiloBuilder with Kubernetes clustering and custom namespace.</returns>
    let useKubernetesClusteringWithNamespace (ns: string) : (ISiloBuilder -> ISiloBuilder) =
        fun builder ->
            let builder =
                invokeExtensionMethod
                    "UseKubernetesHosting"
                    Array.empty
                    "Microsoft.Orleans.Hosting.Kubernetes"
                    builder

            builder.Configure<ClusterOptions>(fun (options: ClusterOptions) ->
                options.ServiceId <- ns)
            |> ignore

            builder
