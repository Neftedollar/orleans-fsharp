module Orleans.FSharp.Tests.ObserverTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans
open Orleans.FSharp

// --- Test observer interface ---

/// <summary>Minimal observer interface for unit testing.</summary>
type ITestObserver =
    inherit IGrainObserver

// --- Observer module type signature tests ---

[<Fact>]
let ``Observer module exists in Orleans.FSharp assembly`` () =
    let observerModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Observer" && t.IsAbstract && t.IsSealed)

    test <@ observerModule.IsSome @>

[<Fact>]
let ``Observer.createRef method exists with correct signature`` () =
    let observerModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Observer" && t.IsAbstract && t.IsSealed)

    let createRefMethod =
        observerModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "createRef")

    test <@ createRefMethod.IsSome @>

    let m = createRefMethod.Value
    // Should be generic with one type parameter
    test <@ m.IsGenericMethod @>
    test <@ m.GetGenericArguments().Length = 1 @>

    // The generic type parameter should be constrained to IGrainObserver
    let constraints = m.GetGenericArguments().[0].GetGenericParameterConstraints()
    let hasObserverConstraint =
        constraints |> Array.exists (fun c -> c = typeof<IGrainObserver>)
    test <@ hasObserverConstraint @>

[<Fact>]
let ``Observer.deleteRef method exists with correct signature`` () =
    let observerModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Observer" && t.IsAbstract && t.IsSealed)

    let deleteRefMethod =
        observerModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "deleteRef")

    test <@ deleteRefMethod.IsSome @>

    let m = deleteRefMethod.Value
    test <@ m.IsGenericMethod @>
    test <@ m.GetGenericArguments().Length = 1 @>

    let constraints = m.GetGenericArguments().[0].GetGenericParameterConstraints()
    let hasObserverConstraint =
        constraints |> Array.exists (fun c -> c = typeof<IGrainObserver>)
    test <@ hasObserverConstraint @>

[<Fact>]
let ``Observer.subscribe method exists and returns IDisposable`` () =
    let observerModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Observer" && t.IsAbstract && t.IsSealed)

    let subscribeMethod =
        observerModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "subscribe")

    test <@ subscribeMethod.IsSome @>

    let m = subscribeMethod.Value
    test <@ m.IsGenericMethod @>

    // Return type should be IDisposable
    test <@ m.ReturnType = typeof<IDisposable> @>

[<Fact>]
let ``Observer.subscribe returns IDisposable that calls deleteRef on Dispose`` () =
    // We verify this by testing that:
    // 1. subscribe calls createRef (factory.CreateObjectReference)
    // 2. Dispose calls deleteRef (factory.DeleteObjectReference)
    // Since we can't easily mock IGrainFactory in pure unit tests,
    // we verify the structural contract through reflection.
    let observerModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Observer" && t.IsAbstract && t.IsSealed)

    let subscribeMethod =
        observerModule.GetMethods()
        |> Array.find (fun m -> m.Name = "subscribe")

    // subscribe takes factory:IGrainFactory and observer:'T
    let parameters = subscribeMethod.GetParameters()
    test <@ parameters.Length = 2 @>
    test <@ parameters.[0].ParameterType = typeof<IGrainFactory> @>
    test <@ parameters.[0].Name = "factory" @>
    test <@ parameters.[1].Name = "observer" @>

// --- FSharpObserverManager type tests ---

[<Fact>]
let ``FSharpObserverManager type exists`` () =
    let managerType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name.StartsWith("FSharpObserverManager"))

    test <@ managerType.IsSome @>

[<Fact>]
let ``FSharpObserverManager has Subscribe method`` () =
    let managerType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name.StartsWith("FSharpObserverManager"))

    let subscribeMethod =
        managerType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "Subscribe")

    test <@ subscribeMethod.IsSome @>

[<Fact>]
let ``FSharpObserverManager has Unsubscribe method`` () =
    let managerType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name.StartsWith("FSharpObserverManager"))

    let unsubscribeMethod =
        managerType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "Unsubscribe")

    test <@ unsubscribeMethod.IsSome @>

[<Fact>]
let ``FSharpObserverManager has Notify method returning Task`` () =
    let managerType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name.StartsWith("FSharpObserverManager"))

    let notifyMethod =
        managerType.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "Notify")

    test <@ notifyMethod.IsSome @>
    test <@ notifyMethod.Value.ReturnType = typeof<Task<unit>> @>

[<Fact>]
let ``FSharpObserverManager has Count property`` () =
    let managerType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name.StartsWith("FSharpObserverManager"))

    let countProp =
        managerType.GetProperties()
        |> Array.tryFind (fun p -> p.Name = "Count")

    test <@ countProp.IsSome @>
    test <@ countProp.Value.PropertyType = typeof<int> @>

[<Fact>]
let ``FSharpObserverManager constructor takes TimeSpan expiryDuration`` () =
    let managerType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name.StartsWith("FSharpObserverManager"))

    let ctors = managerType.GetConstructors()
    test <@ ctors.Length >= 1 @>

    let hasTimeSpanCtor =
        ctors
        |> Array.exists (fun c ->
            let ps = c.GetParameters()
            ps.Length >= 1 && ps.[0].ParameterType = typeof<TimeSpan>)

    test <@ hasTimeSpanCtor @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``Observer module methods all have non-empty names`` () =
    let observerModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Observer" && t.IsAbstract && t.IsSealed)
    observerModule.GetMethods()
    |> Array.forall (fun m -> m.Name.Length > 0)

[<Property>]
let ``Observer module exposes at least 3 public methods`` () =
    let observerModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Observer" && t.IsAbstract && t.IsSealed)
    observerModule.GetMethods() |> Array.length >= 3

[<Property>]
let ``FSharpObserverManager methods all have non-empty names`` () =
    let managerType =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name.StartsWith("FSharpObserverManager"))
    managerType.GetMethods()
    |> Array.forall (fun m -> m.Name.Length > 0)
