module Orleans.FSharp.Tests.GrainExtensionTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.Runtime

/// <summary>Tests for GrainExtension module type signatures.</summary>

[<Fact>]
let ``GrainExtension module exists`` () =
    let extModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "GrainExtension" && t.IsAbstract && t.IsSealed)

    test <@ extModule.IsSome @>

[<Fact>]
let ``GrainExtension.getExtension function exists`` () =
    let extModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainExtension" && t.IsAbstract && t.IsSealed)

    let method =
        extModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "getExtension")

    test <@ method.IsSome @>

[<Fact>]
let ``GrainExtension.getExtension is generic`` () =
    let extModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainExtension" && t.IsAbstract && t.IsSealed)

    let method =
        extModule.GetMethods()
        |> Array.find (fun m -> m.Name = "getExtension")

    test <@ method.IsGenericMethod @>

[<Fact>]
let ``GrainExtension.getExtension takes IAddressable parameter`` () =
    let extModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainExtension" && t.IsAbstract && t.IsSealed)

    let method =
        extModule.GetMethods()
        |> Array.find (fun m -> m.Name = "getExtension")

    let parameters = method.GetParameters()
    test <@ parameters.Length = 1 @>
    test <@ parameters.[0].ParameterType = typeof<IAddressable> @>

[<Fact>]
let ``GrainExtension.getExtension has IGrainExtension constraint on type parameter`` () =
    let extModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainExtension" && t.IsAbstract && t.IsSealed)

    let method =
        extModule.GetMethods()
        |> Array.find (fun m -> m.Name = "getExtension")

    let typeParam = method.GetGenericArguments().[0]
    let constraints = typeParam.GetGenericParameterConstraints()
    test <@ constraints |> Array.exists (fun c -> typeof<IGrainExtension>.IsAssignableFrom(c)) @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``GrainExtension module methods all have non-empty names`` () =
    let extModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainExtension" && t.IsAbstract && t.IsSealed)
    extModule.GetMethods()
    |> Array.forall (fun m -> m.Name.Length > 0)

[<Property>]
let ``GrainExtension.getExtension has exactly 1 generic type parameter`` () =
    let extModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainExtension" && t.IsAbstract && t.IsSealed)
    let method =
        extModule.GetMethods()
        |> Array.find (fun m -> m.Name = "getExtension")
    method.GetGenericArguments().Length = 1
