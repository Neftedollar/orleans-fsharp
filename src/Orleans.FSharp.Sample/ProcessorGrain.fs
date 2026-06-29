namespace Orleans.FSharp.Sample

open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Commands that can be sent to the processor grain.
/// </summary>
[<GenerateSerializer>]
type ProcessorCommand =
    /// <summary>Process a value and return a result. Returns the activation's unique ID.</summary>
    | Process of value: string
    /// <summary>Get the unique activation identifier.</summary>
    | GetActivationId

/// <summary>
/// Grain interface for the processor grain. Uses integer key.
/// </summary>
type IProcessorGrain =
    inherit IGrainWithIntegerKey

    /// <summary>Handle a processor command and return the result.</summary>
    abstract HandleMessage: ProcessorCommand -> Task<obj>

/// <summary>
/// Module containing the processor grain definition built with the grain { } CE.
/// </summary>
module ProcessorGrainDef =

    /// <summary>
    /// The processor grain definition.
    /// Each activation gets a unique GUID as its state identifier.
    /// </summary>
    let processor =
        grain {
            defaultState (Guid.NewGuid().ToString())

            handle (fun state cmd ->
                task {
                    match cmd with
                    | Process value ->
                        let result = $"{state}:{value}"
                        return state, box result
                    | GetActivationId -> return state, box state
                })
        }
