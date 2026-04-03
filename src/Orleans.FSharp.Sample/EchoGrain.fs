namespace Orleans.FSharp.Sample

open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Commands that can be sent to the echo grain.
/// </summary>
[<GenerateSerializer>]
type EchoCommand =
    /// <summary>Echo back the given message with the grain's key prepended.</summary>
    | [<Id(0u)>] Echo of message: string
    /// <summary>Get a greeting using the grain's identity.</summary>
    | [<Id(1u)>] Greet

/// <summary>
/// Grain interface for the echo grain. Uses string key.
/// </summary>
type IEchoGrain =
    inherit IGrainWithStringKey

    /// <summary>Handle an echo command and return the result.</summary>
    abstract HandleMessage: EchoCommand -> Task<obj>

/// <summary>
/// Module containing the echo grain definition built with the grain { } CE.
/// </summary>
module EchoGrainDef =

    /// <summary>
    /// The echo grain definition. State is the grain's identity string.
    /// </summary>
    let echo =
        grain {
            defaultState ""

            handle (fun state cmd ->
                task {
                    match cmd with
                    | Echo msg -> return state, box $"{state}:{msg}"
                    | Greet -> return state, box $"Hello from {state}!"
                })
        }
