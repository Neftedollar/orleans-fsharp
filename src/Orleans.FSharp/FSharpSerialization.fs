namespace Orleans.FSharp.FSharpSerialization

open System
open Orleans.Hosting

/// <summary>
/// Functions for registering F# type support with the Orleans serializer.
/// Uses reflection to invoke the AddSerializationFSharpSupport extension method
/// from the <c>Microsoft.Orleans.Serialization.FSharp</c> NuGet package,
/// which remains an optional runtime dependency.
/// </summary>
[<RequireQualifiedAccess>]
module FSharpSerialization =

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
    /// Register F# type support (discriminated unions, records, options, lists, etc.)
    /// with the Orleans serializer.
    /// Requires the <c>Microsoft.Orleans.Serialization.FSharp</c> NuGet package at runtime.
    /// This complements <c>FSharp.SystemTextJson</c> by providing native Orleans serializer
    /// support for F# types in grain method arguments and state.
    /// </summary>
    /// <returns>A function that configures the ISiloBuilder with F# serialization support.</returns>
    let addFSharpSerialization : (ISiloBuilder -> ISiloBuilder) =
        fun builder ->
            invokeExtensionMethod
                "AddSerializationFSharpSupport"
                Array.empty
                "Microsoft.Orleans.Serialization.FSharp"
                builder
