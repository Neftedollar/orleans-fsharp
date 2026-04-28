namespace Orleans.FSharp.Sample

// FS44: deprecated CE keywords (statelessWorker, maxActivations) — see GrainBuilder.fs.
#nowarn "44"

open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>
/// Commands that can be sent to the processor grain.
/// </summary>
[<GenerateSerializer>]
type ProcessorCommand =
    /// <summary>Process a value and return a result. Returns the activation's unique ID to prove multiple activations.</summary>
    | Process of value: string
    /// <summary>Get the unique activation identifier.</summary>
    | GetActivationId

/// <summary>
/// Grain interface for the stateless worker processor grain. Uses integer key.
/// </summary>
type IProcessorGrain =
    inherit IGrainWithIntegerKey

    /// <summary>Handle a processor command and return the result.</summary>
    abstract HandleMessage: ProcessorCommand -> Task<obj>

/// <summary>
/// Module containing the stateless worker processor grain definition built with the grain { } CE.
/// This grain demonstrates the statelessWorker keyword for load-balanced processing.
/// </summary>
module ProcessorGrainDef =

    /// <summary>
    /// The stateless worker processor grain definition.
    /// Each activation gets a unique GUID to prove multiple activations exist.
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

            statelessWorker
            maxActivations 4
        }
