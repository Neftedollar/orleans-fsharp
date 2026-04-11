namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.Runtime
open Orleans.FSharp

/// <summary>
/// State for the reminder test grain. Tracks how many times the reminder fired.
/// </summary>
[<GenerateSerializer>]
[<Sealed>]
type ReminderStateHolder() =
    /// <summary>The number of times the reminder has fired.</summary>
    [<Id(0u)>]
    member val ReminderFireCount = 0 with get, set

/// <summary>
/// Commands that can be sent to the reminder test grain.
/// </summary>
[<GenerateSerializer>]
type ReminderCommand =
    /// <summary>Get the current reminder fire count.</summary>
    | GetFireCount
    /// <summary>Register a reminder with the given name.</summary>
    | RegisterReminder of name: string
    /// <summary>Unregister a reminder by name.</summary>
    | UnregisterReminder of name: string

/// <summary>
/// Grain interface for the reminder test grain. Uses string key.
/// </summary>
type IReminderTestGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle a reminder test command.</summary>
    abstract HandleMessage: ReminderCommand -> Task<obj>

/// <summary>
/// Module containing the reminder test grain definition.
/// </summary>
module ReminderTestGrainDef =

    /// <summary>
    /// The reminder test grain definition. State tracks reminder fire count.
    /// </summary>
    let reminderTestGrain =
        grain {
            defaultState 0

            handle (fun state cmd ->
                task {
                    match cmd with
                    | GetFireCount -> return state, box state
                    | RegisterReminder _name ->
                        // Registration is handled by the C# impl
                        return state, box true
                    | UnregisterReminder _name ->
                        // Unregistration is handled by the C# impl
                        return state, box true
                })

            onReminder "TestReminder" (fun state _name _status ->
                task { return state + 1 })

            onReminder "SecondReminder" (fun state _name _status ->
                task { return state + 10 })

            persist "Default"
        }
