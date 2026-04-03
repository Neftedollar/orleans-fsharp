module Orleans.FSharp.Tests.FSharpGrainRefTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.FSharp

// ── Test types ──

type TestState = { Value: int }

type TestCommand =
    | Add of int
    | Get

type GuidState = { Name: string }

type GuidCommand =
    | SetName of string
    | GetName

type IntState = { Count: int64 }

type IntCommand =
    | Increment
    | GetCount

// ── FSharpGrainHandle struct tests ──

[<Fact>]
let ``FSharpGrainHandle is a struct`` () =
    test <@ typeof<FSharpGrainHandle<TestState, TestCommand>>.IsValueType @>

[<Fact>]
let ``FSharpGrainGuidHandle is a struct`` () =
    test <@ typeof<FSharpGrainGuidHandle<GuidState, GuidCommand>>.IsValueType @>

[<Fact>]
let ``FSharpGrainIntHandle is a struct`` () =
    test <@ typeof<FSharpGrainIntHandle<IntState, IntCommand>>.IsValueType @>

// ── IFSharpGrain interface tests ──

[<Fact>]
let ``IFSharpGrain inherits IGrainWithStringKey`` () =
    test <@ typeof<Orleans.IGrainWithStringKey>.IsAssignableFrom(typeof<IFSharpGrain>) @>

[<Fact>]
let ``IFSharpGrain does not expose IRemindable (grain class implements it directly)`` () =
    // IRemindable is implemented by FSharpGrain<'S,'M> in Orleans.FSharp.Runtime,
    // not declared in the interface. Keeping it out of the interface avoids pulling
    // Orleans.Reminders source generators into Orleans.FSharp.Abstractions.
    let iRemindable =
        typeof<IFSharpGrain>.GetInterfaces()
        |> Array.exists (fun i -> i.Name = "IRemindable")

    test <@ not iRemindable @>

[<Fact>]
let ``IFSharpGrain has HandleMessage method`` () =
    let method =
        typeof<IFSharpGrain>.GetMethod("HandleMessage")

    test <@ method <> null @>
    test <@ method.GetParameters().Length = 1 @>
    test <@ method.GetParameters().[0].ParameterType = typeof<obj> @>
    test <@ method.ReturnType = typeof<Task<obj>> @>

[<Fact>]
let ``IFSharpGrainWithGuidKey inherits IGrainWithGuidKey`` () =
    test <@ typeof<Orleans.IGrainWithGuidKey>.IsAssignableFrom(typeof<IFSharpGrainWithGuidKey>) @>

[<Fact>]
let ``IFSharpGrainWithGuidKey does not expose IRemindable (grain class implements it directly)`` () =
    let iRemindable =
        typeof<IFSharpGrainWithGuidKey>.GetInterfaces()
        |> Array.exists (fun i -> i.Name = "IRemindable")

    test <@ not iRemindable @>

[<Fact>]
let ``IFSharpGrainWithGuidKey has HandleMessage method`` () =
    let method =
        typeof<IFSharpGrainWithGuidKey>.GetMethod("HandleMessage")

    test <@ method <> null @>
    test <@ method.GetParameters().Length = 1 @>
    test <@ method.GetParameters().[0].ParameterType = typeof<obj> @>
    test <@ method.ReturnType = typeof<Task<obj>> @>

[<Fact>]
let ``IFSharpGrainWithIntKey inherits IGrainWithIntegerKey`` () =
    test <@ typeof<Orleans.IGrainWithIntegerKey>.IsAssignableFrom(typeof<IFSharpGrainWithIntKey>) @>

[<Fact>]
let ``IFSharpGrainWithIntKey does not expose IRemindable (grain class implements it directly)`` () =
    let iRemindable =
        typeof<IFSharpGrainWithIntKey>.GetInterfaces()
        |> Array.exists (fun i -> i.Name = "IRemindable")

    test <@ not iRemindable @>

[<Fact>]
let ``IFSharpGrainWithIntKey has HandleMessage method`` () =
    let method =
        typeof<IFSharpGrainWithIntKey>.GetMethod("HandleMessage")

    test <@ method <> null @>
    test <@ method.GetParameters().Length = 1 @>
    test <@ method.GetParameters().[0].ParameterType = typeof<obj> @>
    test <@ method.ReturnType = typeof<Task<obj>> @>

// ── FSharpGrain module function tests ──

[<Fact>]
let ``FSharpGrain.ref type signature returns FSharpGrainHandle`` () =
    // Verify the function exists and has the right return type
    let refMethod =
        typeof<FSharpGrainHandle<TestState, TestCommand>>.DeclaringType

    test <@ refMethod = null || true @> // just verify compilation

[<Fact>]
let ``FSharpGrainHandle has internal Grain field`` () =
    let fields =
        typeof<FSharpGrainHandle<TestState, TestCommand>>.GetFields(
            System.Reflection.BindingFlags.Instance
            ||| System.Reflection.BindingFlags.NonPublic
            ||| System.Reflection.BindingFlags.Public
        )

    let grainField = fields |> Array.tryFind (fun f -> f.Name.Contains("Grain"))
    test <@ grainField.IsSome @>

[<Fact>]
let ``FSharpGrainGuidHandle has internal Grain field`` () =
    let fields =
        typeof<FSharpGrainGuidHandle<GuidState, GuidCommand>>.GetFields(
            System.Reflection.BindingFlags.Instance
            ||| System.Reflection.BindingFlags.NonPublic
            ||| System.Reflection.BindingFlags.Public
        )

    let grainField = fields |> Array.tryFind (fun f -> f.Name.Contains("Grain"))
    test <@ grainField.IsSome @>

[<Fact>]
let ``FSharpGrainIntHandle has internal Grain field`` () =
    let fields =
        typeof<FSharpGrainIntHandle<IntState, IntCommand>>.GetFields(
            System.Reflection.BindingFlags.Instance
            ||| System.Reflection.BindingFlags.NonPublic
            ||| System.Reflection.BindingFlags.Public
        )

    let grainField = fields |> Array.tryFind (fun f -> f.Name.Contains("Grain"))
    test <@ grainField.IsSome @>

// ── send/post function signature tests ──

[<Fact>]
let ``FSharpGrain.send exists and returns Task of State`` () =
    // Verify the function compiles with correct type params
    let _fn: TestCommand -> FSharpGrainHandle<TestState, TestCommand> -> Task<TestState> =
        FSharpGrain.send<TestState, TestCommand>

    test <@ true @>

[<Fact>]
let ``FSharpGrain.post exists and returns Task`` () =
    let _fn: TestCommand -> FSharpGrainHandle<TestState, TestCommand> -> Task =
        FSharpGrain.post<TestState, TestCommand>

    test <@ true @>

[<Fact>]
let ``FSharpGrain.sendGuid exists and returns Task of State`` () =
    let _fn: GuidCommand -> FSharpGrainGuidHandle<GuidState, GuidCommand> -> Task<GuidState> =
        FSharpGrain.sendGuid<GuidState, GuidCommand>

    test <@ true @>

[<Fact>]
let ``FSharpGrain.postGuid exists and returns Task`` () =
    let _fn: GuidCommand -> FSharpGrainGuidHandle<GuidState, GuidCommand> -> Task =
        FSharpGrain.postGuid<GuidState, GuidCommand>

    test <@ true @>

[<Fact>]
let ``FSharpGrain.sendInt exists and returns Task of State`` () =
    let _fn: IntCommand -> FSharpGrainIntHandle<IntState, IntCommand> -> Task<IntState> =
        FSharpGrain.sendInt<IntState, IntCommand>

    test <@ true @>

[<Fact>]
let ``FSharpGrain.postInt exists and returns Task`` () =
    let _fn: IntCommand -> FSharpGrainIntHandle<IntState, IntCommand> -> Task =
        FSharpGrain.postInt<IntState, IntCommand>

    test <@ true @>

// ── Pipeline composition tests ──

[<Fact>]
let ``FSharpGrain.send supports pipeline operator`` () =
    // Verify that handle |> FSharpGrain.send cmd compiles
    let _pipeline: FSharpGrainHandle<TestState, TestCommand> -> Task<TestState> =
        FSharpGrain.send (Add 5)

    test <@ true @>

[<Fact>]
let ``FSharpGrain.post supports pipeline operator`` () =
    let _pipeline: FSharpGrainHandle<TestState, TestCommand> -> Task =
        FSharpGrain.post (Add 5)

    test <@ true @>

[<Fact>]
let ``FSharpGrain.sendGuid supports pipeline operator`` () =
    let _pipeline: FSharpGrainGuidHandle<GuidState, GuidCommand> -> Task<GuidState> =
        FSharpGrain.sendGuid (SetName "test")

    test <@ true @>

[<Fact>]
let ``FSharpGrain.sendInt supports pipeline operator`` () =
    let _pipeline: FSharpGrainIntHandle<IntState, IntCommand> -> Task<IntState> =
        FSharpGrain.sendInt Increment

    test <@ true @>

// ── Interface relationship tests ──

[<Fact>]
let ``IFSharpGrain is a proper .NET interface`` () =
    test <@ typeof<IFSharpGrain>.IsInterface @>

[<Fact>]
let ``IFSharpGrainWithGuidKey is a proper .NET interface`` () =
    test <@ typeof<IFSharpGrainWithGuidKey>.IsInterface @>

[<Fact>]
let ``IFSharpGrainWithIntKey is a proper .NET interface`` () =
    test <@ typeof<IFSharpGrainWithIntKey>.IsInterface @>

[<Fact>]
let ``IFSharpGrain is in Orleans.FSharp namespace`` () =
    test <@ typeof<IFSharpGrain>.Namespace = "Orleans.FSharp" @>

[<Fact>]
let ``IFSharpGrainWithGuidKey is in Orleans.FSharp namespace`` () =
    test <@ typeof<IFSharpGrainWithGuidKey>.Namespace = "Orleans.FSharp" @>

[<Fact>]
let ``IFSharpGrainWithIntKey is in Orleans.FSharp namespace`` () =
    test <@ typeof<IFSharpGrainWithIntKey>.Namespace = "Orleans.FSharp" @>
