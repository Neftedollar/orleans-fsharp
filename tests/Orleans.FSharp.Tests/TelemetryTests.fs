module Orleans.FSharp.Tests.TelemetryTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

/// <summary>Tests for Telemetry.fs — constant values and function signatures.</summary>

// --- Constant value tests ---

[<Fact>]
let ``runtimeActivitySourceName is Microsoft.Orleans.Runtime`` () =
    test <@ Telemetry.runtimeActivitySourceName = "Microsoft.Orleans.Runtime" @>

[<Fact>]
let ``applicationActivitySourceName is Microsoft.Orleans.Application`` () =
    test <@ Telemetry.applicationActivitySourceName = "Microsoft.Orleans.Application" @>

[<Fact>]
let ``meterName is Microsoft.Orleans`` () =
    test <@ Telemetry.meterName = "Microsoft.Orleans" @>

// --- activitySourceNames list tests ---

[<Fact>]
let ``activitySourceNames contains both runtime and application sources`` () =
    test <@ Telemetry.activitySourceNames |> List.length = 2 @>

[<Fact>]
let ``activitySourceNames contains runtime source`` () =
    test <@ Telemetry.activitySourceNames |> List.contains "Microsoft.Orleans.Runtime" @>

[<Fact>]
let ``activitySourceNames contains application source`` () =
    test <@ Telemetry.activitySourceNames |> List.contains "Microsoft.Orleans.Application" @>

[<Fact>]
let ``activitySourceNames first element is runtime source`` () =
    test <@ Telemetry.activitySourceNames.[0] = "Microsoft.Orleans.Runtime" @>

[<Fact>]
let ``activitySourceNames second element is application source`` () =
    test <@ Telemetry.activitySourceNames.[1] = "Microsoft.Orleans.Application" @>

// --- enableActivityPropagation function tests ---

[<Fact>]
let ``Telemetry module exists in the assembly`` () =
    let telemetryModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Telemetry" && t.IsAbstract && t.IsSealed)

    test <@ telemetryModule.IsSome @>

[<Fact>]
let ``enableActivityPropagation method exists`` () =
    let telemetryModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Telemetry" && t.IsAbstract && t.IsSealed)

    let method =
        telemetryModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "enableActivityPropagation")

    test <@ method.IsSome @>

[<Fact>]
let ``enableActivityPropagation takes ISiloBuilder and returns ISiloBuilder`` () =
    let telemetryModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Telemetry" && t.IsAbstract && t.IsSealed)

    let method =
        telemetryModule.GetMethods()
        |> Array.find (fun m -> m.Name = "enableActivityPropagation")

    let parameters = method.GetParameters()
    test <@ parameters.Length = 1 @>
    test <@ parameters.[0].ParameterType = typeof<Orleans.Hosting.ISiloBuilder> @>
    test <@ method.ReturnType = typeof<Orleans.Hosting.ISiloBuilder> @>

// --- Literal attribute tests ---

[<Fact>]
let ``runtimeActivitySourceName is a literal`` () =
    let telemetryModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Telemetry" && t.IsAbstract && t.IsSealed)

    let field =
        telemetryModule.GetField(
            "runtimeActivitySourceName",
            System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Static
        )

    // Literal fields compile as const fields which are IsLiteral=true
    test <@ field <> null @>
    test <@ field.IsLiteral @>

[<Fact>]
let ``meterName is a literal`` () =
    let telemetryModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Telemetry" && t.IsAbstract && t.IsSealed)

    let field =
        telemetryModule.GetField(
            "meterName",
            System.Reflection.BindingFlags.Public ||| System.Reflection.BindingFlags.Static
        )

    test <@ field <> null @>
    test <@ field.IsLiteral @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``activitySourceNames contains no empty strings`` () =
    Telemetry.activitySourceNames |> List.forall (fun s -> s.Length > 0)

[<Property>]
let ``activitySourceNames has all distinct elements`` () =
    let names = Telemetry.activitySourceNames
    names |> List.distinct |> List.length = names.Length

[<Property>]
let ``all telemetry constant strings are non-empty`` () =
    Telemetry.runtimeActivitySourceName.Length > 0
    && Telemetry.applicationActivitySourceName.Length > 0
    && Telemetry.meterName.Length > 0
