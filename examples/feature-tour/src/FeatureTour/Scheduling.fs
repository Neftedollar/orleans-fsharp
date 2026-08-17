/// <summary>
/// Feature 2 — timers and reminders declared directly on the definition: a visibly ticking
/// <c>onTimer</c> and a real, durably registered <c>onReminder</c>.
/// </summary>
namespace FeatureTour.Scheduling

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Runtime
// IReminderRegistry lives in Orleans.Timers (assembly Orleans.Reminders), not Orleans.Runtime —
// docs/functional-grains.md's reminder-retirement snippet omits this open.
open Orleans.Timers
open Orleans.FSharp

type SchedulerActor = private SchedulerActor of unit

type SchedulerState =
    { ticks: int
      reminderFires: int
      lastReminderAt: DateTimeOffset option }

/// <summary>What one <c>report</c> call tells the driver.</summary>
type SchedulerReport =
    { ticks: int
      reminderFires: int
      lastReminderAt: string
      registeredReminders: string list }

[<NoEquality; NoComparison>]
type SchedulerApi =
    { /// Current tick / fire counters plus what Orleans' reminder table actually holds.
      report: unit -> Task<SchedulerReport> }

[<RequireQualifiedAccess>]
module SchedulerApi =
    let contract =
        grainContract<SchedulerActor, string, SchedulerApi> () {
            // Explicit grainType is required for any definition declaring onReminder: the durable
            // registration lives in Orleans' reminder table under this exact name.
            grainType "tour.scheduler"
            version 1
            stringKey

            readOnly (_.report)
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module SchedulerDefinition =

    /// <summary>
    /// Orleans' <c>ReminderOptions.MinimumReminderPeriod</c> defaults to one minute and the
    /// functional silo validates every declared PERIOD against it at startup, so a sub-minute
    /// period fails the silo, not the first tick. The DUE TIME carries no such floor, which is
    /// what lets this tour show a reminder genuinely firing inside a short run: it is due after
    /// a few seconds and then repeats on the one-minute floor.
    /// </summary>
    [<Literal>]
    let ReminderName = "heartbeat"

    let dueTime = TimeSpan.FromSeconds 3.0
    let period = TimeSpan.FromMinutes 1.0

    let definition =
        grainFor SchedulerApi.contract {
            defaultState (fun () ->
                { ticks = 0
                  reminderFires = 0
                  lastReminderAt = None })

            // Timers are ordinary in-memory recurrences: no durable registration, and they only
            // run while this activation is alive. KeepAlive = false means ticking does NOT hold
            // the activation open against idle collection.
            onTimer
                "tick"
                (GrainTimerCreationOptions(
                    TimeSpan.FromMilliseconds 200.0,
                    TimeSpan.FromMilliseconds 200.0,
                    KeepAlive = false
                ))
                (fun _context state -> task { return { state with ticks = state.ticks + 1 } })

            onReminder
                ReminderName
                dueTime
                period
                (fun context state _tickStatus ->
                    task {
                        context.logger.LogInformation "tour.scheduler heartbeat reminder fired"

                        return
                            { state with
                                reminderFires = state.reminderFires + 1
                                lastReminderAt = Some context.utcNow }
                    })

            handle
                (_.report)
                (fun context state () ->
                    task {
                        // The functional context deliberately exposes no reminder API of its own;
                        // the stock IReminderRegistry is one context.services lookup away.
                        let registry = context.services.GetRequiredService<IReminderRegistry>()
                        let! registered = registry.GetReminders context.grainId

                        return
                            state,
                            { ticks = state.ticks
                              reminderFires = state.reminderFires
                              lastReminderAt =
                                state.lastReminderAt
                                |> Option.map (fun at -> at.ToString "HH:mm:ss")
                                |> Option.defaultValue "(not yet)"
                              registeredReminders =
                                registered |> Seq.map (fun reminder -> reminder.ReminderName) |> List.ofSeq }
                    })
        }
