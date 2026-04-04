module Orleans.FSharp.Tests.TimerTests

open System
open System.Reflection
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// --- Timers module type signature tests ---

[<Fact>]
let ``Timers module exists in Orleans.FSharp assembly`` () =
    let timersModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)

    test <@ timersModule.IsSome @>

[<Fact>]
let ``Timers.register method exists`` () =
    let timersModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)

    let registerMethod =
        timersModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "register")

    test <@ registerMethod.IsSome @>

[<Fact>]
let ``Timers.registerWithState method exists`` () =
    let timersModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)

    let registerWithStateMethod =
        timersModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "registerWithState")

    test <@ registerWithStateMethod.IsSome @>

[<Fact>]
let ``Timers.register returns IGrainTimer`` () =
    let timersModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)

    let registerMethod =
        timersModule.GetMethods()
        |> Array.find (fun m -> m.Name = "register")

    let returnType = registerMethod.ReturnType
    test <@ typeof<Orleans.Runtime.IGrainTimer>.IsAssignableFrom(returnType) @>

[<Fact>]
let ``Timers module functions do not return FSharpAsync`` () =
    let timersModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)

    let asyncMethods =
        timersModule.GetMethods()
        |> Array.filter (fun m ->
            let ret = m.ReturnType

            (ret.IsGenericType
             && ret.GetGenericTypeDefinition().FullName = "Microsoft.FSharp.Control.FSharpAsync`1")
            || ret.FullName = "Microsoft.FSharp.Control.FSharpAsync")

    test <@ asyncMethods = Array.empty @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``Timers module methods all have non-empty names`` () =
    let timersModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)
    timersModule.GetMethods()
    |> Array.forall (fun m -> m.Name.Length > 0)

[<Property>]
let ``Timers module exposes at least 2 public methods`` () =
    let timersModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)
    timersModule.GetMethods() |> Array.length >= 2
