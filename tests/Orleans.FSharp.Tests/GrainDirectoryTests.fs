module Orleans.FSharp.Tests.GrainDirectoryTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.Hosting
open Orleans.FSharp.GrainDirectory

/// <summary>Tests for GrainDirectory.fs — GrainDirectoryProvider DU and GrainDirectory.configure function.</summary>

// --- GrainDirectoryProvider DU tests ---

[<Fact>]
let ``GrainDirectoryProvider is a discriminated union with 4 cases`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<GrainDirectoryProvider>)

    test <@ cases.Length = 4 @>
    let names = cases |> Array.map (fun c -> c.Name) |> Array.sort
    test <@ names = [| "AzureStorage"; "Custom"; "Default"; "Redis" |] @>

[<Fact>]
let ``GrainDirectoryProvider.Default case can be constructed`` () =
    let provider = GrainDirectoryProvider.Default

    let isDefault =
        match provider with
        | Default -> true
        | _ -> false

    test <@ isDefault @>

[<Fact>]
let ``GrainDirectoryProvider.Redis case carries connection string`` () =
    let provider = Redis "localhost:6379"

    let isRedis =
        match provider with
        | Redis connStr -> connStr = "localhost:6379"
        | _ -> false

    test <@ isRedis @>

[<Fact>]
let ``GrainDirectoryProvider.AzureStorage case carries connection string`` () =
    let provider = AzureStorage "DefaultEndpointsProtocol=https;..."

    let isAzure =
        match provider with
        | AzureStorage connStr -> connStr = "DefaultEndpointsProtocol=https;..."
        | _ -> false

    test <@ isAzure @>

[<Fact>]
let ``GrainDirectoryProvider.Custom case carries configurator function`` () =
    let configurator = fun (builder: ISiloBuilder) -> builder
    let provider = Custom configurator

    let isCustom =
        match provider with
        | Custom _ -> true
        | _ -> false

    test <@ isCustom @>

// --- GrainDirectory.configure function type tests ---

[<Fact>]
let ``GrainDirectory module exists in the assembly`` () =
    let grainDirModule =
        typeof<GrainDirectoryProvider>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "GrainDirectory" && t.IsAbstract && t.IsSealed)

    test <@ grainDirModule.IsSome @>

[<Fact>]
let ``GrainDirectory.configure method exists`` () =
    let grainDirModule =
        typeof<GrainDirectoryProvider>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "GrainDirectory" && t.IsAbstract && t.IsSealed)

    let configureMethod =
        grainDirModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "configure")

    test <@ configureMethod.IsSome @>

[<Fact>]
let ``GrainDirectory.configure returns a function type`` () =
    let result = GrainDirectory.configure Default
    let funcType = result.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``GrainDirectory.configure Default returns callable function`` () =
    let f = GrainDirectory.configure Default
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``GrainDirectory.configure Redis returns callable function`` () =
    let f = GrainDirectory.configure (Redis "localhost:6379")
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``GrainDirectory.configure AzureStorage returns callable function`` () =
    let f = GrainDirectory.configure (AzureStorage "DefaultEndpointsProtocol=https;...")
    let funcType = f.GetType()
    let expectedBase = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
    test <@ expectedBase.IsAssignableFrom(funcType) @>

[<Fact>]
let ``GrainDirectory.configure Custom returns the provided configurator`` () =
    let mutable called = false

    let configurator =
        fun (builder: ISiloBuilder) ->
            called <- true
            builder

    let f = GrainDirectory.configure (Custom configurator)
    // Call it with a null builder to test the function is our configurator
    try
        f (Unchecked.defaultof<ISiloBuilder>) |> ignore
    with
    | _ -> ()

    test <@ called @>

// --- GrainDirectoryProvider DU field tests ---

[<Fact>]
let ``Redis case has exactly one field named connectionString`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<GrainDirectoryProvider>)

    let redisCase = cases |> Array.find (fun c -> c.Name = "Redis")
    let fields = redisCase.GetFields()
    test <@ fields.Length = 1 @>
    test <@ fields.[0].Name = "connectionString" @>

[<Fact>]
let ``AzureStorage case has exactly one field named connectionString`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<GrainDirectoryProvider>)

    let azureCase = cases |> Array.find (fun c -> c.Name = "AzureStorage")
    let fields = azureCase.GetFields()
    test <@ fields.Length = 1 @>
    test <@ fields.[0].Name = "connectionString" @>

[<Fact>]
let ``Custom case has exactly one field named configurator`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<GrainDirectoryProvider>)

    let customCase = cases |> Array.find (fun c -> c.Name = "Custom")
    let fields = customCase.GetFields()
    test <@ fields.Length = 1 @>
    test <@ fields.[0].Name = "configurator" @>

[<Fact>]
let ``Default case has no fields`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<GrainDirectoryProvider>)

    let defaultCase = cases |> Array.find (fun c -> c.Name = "Default")
    let fields = defaultCase.GetFields()
    test <@ fields.Length = 0 @>

[<Fact>]
let ``GrainDirectoryProvider has NoEquality attribute`` () =
    let hasAttr =
        typeof<GrainDirectoryProvider>
            .GetCustomAttributes(typeof<NoEqualityAttribute>, false)
        |> Array.isEmpty
        |> not

    test <@ hasAttr @>

[<Fact>]
let ``GrainDirectoryProvider has NoComparison attribute`` () =
    let hasAttr =
        typeof<GrainDirectoryProvider>
            .GetCustomAttributes(typeof<NoComparisonAttribute>, false)
        |> Array.isEmpty
        |> not

    test <@ hasAttr @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``Redis case always carries the exact connection string provided`` (connStr: NonNull<string>) =
    let provider = Redis connStr.Get
    match provider with
    | Redis s -> s = connStr.Get
    | _ -> false

[<Property>]
let ``AzureStorage case always carries the exact connection string provided`` (connStr: NonNull<string>) =
    let provider = AzureStorage connStr.Get
    match provider with
    | AzureStorage s -> s = connStr.Get
    | _ -> false

[<Property>]
let ``GrainDirectory.configure returns a callable function for built-in providers`` () =
    let providers = [ Default; Redis "test"; AzureStorage "test" ]
    providers |> List.forall (fun p ->
        let f = GrainDirectory.configure p
        let funcType = f.GetType()
        let expected = typedefof<FSharpFunc<_, _>>.MakeGenericType(typeof<ISiloBuilder>, typeof<ISiloBuilder>)
        expected.IsAssignableFrom(funcType))
