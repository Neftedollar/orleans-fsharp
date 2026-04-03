namespace Orleans.FSharp

open System
open System.Threading.Tasks
open Orleans
open Orleans.Runtime

/// <summary>
/// Module for managing grain reminders -- persistent periodic triggers
/// that survive grain deactivation and cluster restarts.
/// All functions use Task (not Async) to align with Orleans runtime conventions.
/// </summary>
[<RequireQualifiedAccess>]
module Reminder =

    /// <summary>
    /// Register or update a named reminder on a grain.
    /// If a reminder with the given name already exists, it is updated with the new timing parameters.
    /// The grain must inherit from Orleans.Grain for this to work.
    /// </summary>
    /// <param name="grain">The grain instance (must inherit from Grain).</param>
    /// <param name="name">The unique name for the reminder.</param>
    /// <param name="dueTime">The time delay before the first firing.</param>
    /// <param name="period">The interval between subsequent firings.</param>
    /// <returns>A Task containing the IGrainReminder handle for the registered reminder.</returns>
    let register (grain: Grain) (name: string) (dueTime: TimeSpan) (period: TimeSpan) : Task<IGrainReminder> =
        grain.RegisterOrUpdateReminder(name, dueTime, period)

    /// <summary>
    /// Unregister a reminder by name on a grain.
    /// Retrieves the reminder handle by name and then unregisters it.
    /// If the reminder does not exist, the operation completes without error.
    /// </summary>
    /// <param name="grain">The grain instance (must inherit from Grain).</param>
    /// <param name="name">The name of the reminder to unregister.</param>
    /// <returns>A Task that completes when the reminder has been unregistered.</returns>
    let unregister (grain: Grain) (name: string) : Task<unit> =
        task {
            let! reminder = grain.GetReminder(name)

            if not (isNull (box reminder)) then
                do! grain.UnregisterReminder(reminder)
        }

    /// <summary>
    /// Get an existing reminder by name on a grain.
    /// Returns Some if the reminder exists, None otherwise.
    /// </summary>
    /// <param name="grain">The grain instance (must inherit from Grain).</param>
    /// <param name="name">The name of the reminder to retrieve.</param>
    /// <returns>A Task containing Some IGrainReminder if found, None otherwise.</returns>
    let get (grain: Grain) (name: string) : Task<IGrainReminder option> =
        task {
            try
                let! reminder = grain.GetReminder(name)

                if isNull (box reminder) then
                    return None
                else
                    return Some reminder
            with :? Orleans.Runtime.OrleansException ->
                return None
        }
