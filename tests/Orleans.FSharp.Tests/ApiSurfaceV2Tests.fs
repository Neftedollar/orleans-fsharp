module Orleans.FSharp.Tests.ApiSurfaceV2Tests

open System
open System.Reflection
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit

/// <summary>Get the Orleans.FSharp assembly via the marker type.</summary>
let private orleansFSharpAssembly =
    typeof<Orleans.FSharp.AssemblyMarker>.Assembly

let private publicTypes = orleansFSharpAssembly.GetExportedTypes()

let private isFSharpAsyncType (t: Type) =
    t.IsGenericType
    && t.GetGenericTypeDefinition().FullName = "Microsoft.FSharp.Control.FSharpAsync`1"

let private isFSharpAsyncNonGeneric (t: Type) =
    t.FullName = "Microsoft.FSharp.Control.FSharpAsync"

// --- v2 modules: no public method returns Async<T> ---

[<Fact>]
let ``No v2 public method returns FSharpAsync`` () =
    // This scans the entire Orleans.FSharp assembly (covers v1 + v2 modules)
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
let ``No v2 public property returns FSharpAsync`` () =
    let asyncProperties =
        publicTypes
        |> Array.collect (fun t ->
            t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.Static))
        |> Array.filter (fun p ->
            let ret = p.PropertyType
            isFSharpAsyncType ret || isFSharpAsyncNonGeneric ret)
        |> Array.map (fun p -> $"{p.DeclaringType.Name}.{p.Name}")

    test <@ asyncProperties = Array.empty @>

// --- v2 module existence checks ---

[<Fact>]
let ``Reminder module is publicly accessible`` () =
    let hasReminder =
        publicTypes
        |> Array.exists (fun t -> t.Name = "Reminder" && t.IsAbstract && t.IsSealed)

    test <@ hasReminder @>

[<Fact>]
let ``Timers module is publicly accessible`` () =
    let hasTimers =
        publicTypes
        |> Array.exists (fun t -> t.Name = "Timers" && t.IsAbstract && t.IsSealed)

    test <@ hasTimers @>

[<Fact>]
let ``GrainDefinition has ReminderHandlers field`` () =
    let defType = typeof<Orleans.FSharp.GrainDefinition<int, string>>

    let field =
        defType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.tryFind (fun p -> p.Name = "ReminderHandlers")

    test <@ field.IsSome @>

[<Fact>]
let ``GrainBuilder has onReminder custom operation`` () =
    let builderType = typeof<Orleans.FSharp.GrainBuilder>

    let onReminderMethod =
        builderType.GetMethods(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.tryFind (fun m -> m.Name = "OnReminder")

    test <@ onReminderMethod.IsSome @>

    // Should have CustomOperation attribute
    let attrs = onReminderMethod.Value.GetCustomAttributes(typeof<FSharp.Core.CustomOperationAttribute>, false)
    test <@ attrs.Length > 0 @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``all public types in v2 assembly have non-empty names`` () =
    publicTypes |> Array.forall (fun t -> t.Name.Length > 0)

[<Property>]
let ``GrainBuilder has at least 10 public instance methods`` () =
    let builderType = typeof<Orleans.FSharp.GrainBuilder>
    builderType.GetMethods(BindingFlags.Public ||| BindingFlags.Instance).Length >= 10
