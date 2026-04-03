module Orleans.FSharp.Tests.ApiSurfaceTests

open System
open System.Reflection
open Xunit
open Swensen.Unquote

/// <summary>Get the Orleans.FSharp assembly via the marker type.</summary>
let private orleansFSharpAssembly =
    typeof<Orleans.FSharp.AssemblyMarker>.Assembly

let private publicTypes =
    orleansFSharpAssembly.GetExportedTypes()

let private isFSharpAsyncType (t: Type) =
    t.IsGenericType
    && t.GetGenericTypeDefinition().FullName = "Microsoft.FSharp.Control.FSharpAsync`1"

let private isFSharpAsyncNonGeneric (t: Type) =
    t.FullName = "Microsoft.FSharp.Control.FSharpAsync"

[<Fact>]
let ``No public method returns FSharpAsync`` () =
    let asyncMethods =
        publicTypes
        |> Array.collect (fun t ->
            t.GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static))
        |> Array.filter (fun m ->
            let ret = m.ReturnType
            isFSharpAsyncType ret || isFSharpAsyncNonGeneric ret)
        |> Array.map (fun m -> $"{m.DeclaringType.Name}.{m.Name}")

    test <@ asyncMethods = Array.empty @>

[<Fact>]
let ``No public property returns FSharpAsync`` () =
    let asyncProperties =
        publicTypes
        |> Array.collect (fun t ->
            t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static))
        |> Array.filter (fun p ->
            let ret = p.PropertyType
            isFSharpAsyncType ret || isFSharpAsyncNonGeneric ret)
        |> Array.map (fun p -> $"{p.DeclaringType.Name}.{p.Name}")

    test <@ asyncProperties = Array.empty @>

[<Fact>]
let ``Public types exist in assembly`` () =
    test <@ publicTypes.Length > 0 @>

[<Fact>]
let ``TaskHelpers module is publicly accessible`` () =
    let hasTaskHelpers =
        publicTypes
        |> Array.exists (fun t -> t.Name.Contains("TaskHelpers"))

    test <@ hasTaskHelpers @>
