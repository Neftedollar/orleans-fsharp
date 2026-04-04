module Orleans.FSharp.Tests.ReminderTests

open System
open System.Reflection
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans
open Orleans.Runtime
open Orleans.FSharp

// --- GrainDefinition.ReminderHandlers field tests ---

[<Fact>]
let ``GrainDefinition has ReminderHandlers field`` () =
    let defType = typeof<GrainDefinition<int, string>>

    let field =
        defType.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
        |> Array.tryFind (fun p -> p.Name = "ReminderHandlers")

    test <@ field.IsSome @>

[<Fact>]
let ``ReminderHandlers defaults to empty map`` () =
    let def: GrainDefinition<int, string> =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.ReminderHandlers |> Map.isEmpty @>

[<Fact>]
let ``onReminder CE keyword adds handler to GrainDefinition`` () =
    let handler (_state: int) (_name: string) (_status: TickStatus) =
        task { return _state + 1 }

    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onReminder "MyReminder" handler
        }

    test <@ def.ReminderHandlers |> Map.containsKey "MyReminder" @>

[<Fact>]
let ``multiple onReminder calls register multiple handlers`` () =
    let handler1 (_state: int) (_name: string) (_status: TickStatus) =
        task { return _state + 1 }

    let handler2 (_state: int) (_name: string) (_status: TickStatus) =
        task { return _state + 10 }

    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            onReminder "Reminder1" handler1
            onReminder "Reminder2" handler2
        }

    test <@ def.ReminderHandlers |> Map.count = 2 @>
    test <@ def.ReminderHandlers |> Map.containsKey "Reminder1" @>
    test <@ def.ReminderHandlers |> Map.containsKey "Reminder2" @>

[<Fact>]
let ``onReminder handler has correct signature`` () =
    let mutable handlerCalled = false

    let handler (state: int) (_name: string) (_status: TickStatus) =
        task {
            handlerCalled <- true
            return state + 1
        }

    let def =
        grain {
            defaultState 42
            handle (fun state _msg -> task { return state, box state })
            onReminder "TestReminder" handler
        }

    let registeredHandler = def.ReminderHandlers.["TestReminder"]
    let result = registeredHandler 42 "TestReminder" (TickStatus()) |> fun t -> t.Result
    test <@ handlerCalled @>
    test <@ result = 43 @>

// --- Reminder module type signature tests ---

[<Fact>]
let ``Reminder module exists in Orleans.FSharp assembly`` () =
    let reminderModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Reminder" && t.IsAbstract && t.IsSealed)

    test <@ reminderModule.IsSome @>

[<Fact>]
let ``Reminder.register method exists`` () =
    let reminderModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Reminder" && t.IsAbstract && t.IsSealed)

    let registerMethod =
        reminderModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "register")

    test <@ registerMethod.IsSome @>

[<Fact>]
let ``Reminder.unregister method exists`` () =
    let reminderModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Reminder" && t.IsAbstract && t.IsSealed)

    let unregisterMethod =
        reminderModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "unregister")

    test <@ unregisterMethod.IsSome @>

[<Fact>]
let ``Reminder.get method exists`` () =
    let reminderModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Reminder" && t.IsAbstract && t.IsSealed)

    let getMethod =
        reminderModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "get")

    test <@ getMethod.IsSome @>

[<Fact>]
let ``Reminder.register returns Task of IGrainReminder`` () =
    let reminderModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Reminder" && t.IsAbstract && t.IsSealed)

    let registerMethod =
        reminderModule.GetMethods()
        |> Array.find (fun m -> m.Name = "register")

    let returnType = registerMethod.ReturnType
    test <@ returnType.IsGenericType @>
    test <@ returnType.GetGenericTypeDefinition() = typedefof<Task<_>> @>

[<Fact>]
let ``Reminder module functions do not return FSharpAsync`` () =
    let reminderModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Reminder" && t.IsAbstract && t.IsSealed)

    let asyncMethods =
        reminderModule.GetMethods()
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
let ``onReminder stores correct name for any non-whitespace reminder name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let def =
            grain {
                defaultState 0
                handle (fun state (_msg: string) -> task { return state, box state })
                onReminder name.Get (fun state _n _tick -> task { return state + 1 })
            }
        def.ReminderHandlers |> Map.containsKey name.Get)

[<Property>]
let ``onReminder handler increments state correctly for any initial int state`` (initial: int) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            onReminder "inc" (fun state _n _tick -> task { return state + 1 })
        }
    let handler = def.ReminderHandlers.["inc"]
    handler initial "inc" (TickStatus()) |> _.GetAwaiter().GetResult() = initial + 1

[<Property>]
let ``onReminder later registration with same name replaces earlier`` (first: int) (second: int) =
    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            onReminder "dup" (fun _state _n _tick -> task { return first })
            onReminder "dup" (fun _state _n _tick -> task { return second })
        }
    let handler = def.ReminderHandlers.["dup"]
    def.ReminderHandlers |> Map.count = 1
    && handler 0 "dup" (TickStatus()) |> _.GetAwaiter().GetResult() = second
