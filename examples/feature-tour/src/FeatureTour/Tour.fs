/// <summary>
/// Shared console formatting and small waiting helpers for the feature tour driver.
/// </summary>
module FeatureTour.Tour

open System
open System.Threading.Tasks

/// <summary>Print a clearly-labeled section banner.</summary>
let section (number: int) (title: string) =
    printfn ""
    printfn "════════════════════════════════════════════════════════════════════"
    printfn " %d. %s" number title
    printfn "════════════════════════════════════════════════════════════════════"

/// <summary>Print one indented observation line.</summary>
let say (text: string) = printfn "   %s" text

/// <summary>Print an indented sub-observation line.</summary>
let detail (text: string) = printfn "     %s" text

/// <summary>Print the verdict line a README status-matrix row is derived from.</summary>
let verdict (text: string) = printfn "   -> %s" text

/// <summary>Render an exception the way the tour reports a deliberate failure.</summary>
let rec describe (error: exn) =
    match error with
    | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
        describe aggregate.InnerExceptions.[0]
    | _ -> $"{error.GetType().Name}: {error.Message}"

/// <summary>
/// Poll <paramref name="condition"/> until it holds or the deadline passes. Returns whether it
/// held. Used instead of a fixed sleep so the transcript is stable on a slow machine.
/// </summary>
let waitUntil (timeout: TimeSpan) (condition: unit -> Task<bool>) : Task<bool> =
    task {
        let deadline = DateTimeOffset.UtcNow + timeout
        let mutable ok = false

        while not ok && DateTimeOffset.UtcNow < deadline do
            let! current = condition ()
            ok <- current

            if not ok then
                do! Task.Delay 100

        return ok
    }
