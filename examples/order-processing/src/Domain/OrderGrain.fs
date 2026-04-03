namespace OrderProcessing.Domain

open System
open Orleans.FSharp

/// <summary>
/// Module containing the order grain definition with DU state machine,
/// reminder handling, and timer-based status checks.
/// </summary>
module OrderGrainDef =

    /// <summary>
    /// Validates and applies a state transition for the order state machine.
    /// </summary>
    /// <param name="state">The current order state.</param>
    /// <param name="cmd">The command to process.</param>
    /// <returns>The new state and a boxed OrderResult.</returns>
    let transition (state: OrderState) (cmd: OrderCommand) : OrderState * obj =
        let now = DateTime.UtcNow

        match state.Status, cmd with
        // Get status (read-only, always valid)
        | Some status, GetStatus -> state, box (Ok status)
        | None, GetStatus -> state, box NoOrder

        // Place a new order (only when no active order or terminal state)
        | None, Place desc ->
            let status = Created(desc, now)
            { state with Status = Some status }, box (Ok status)
        | Some(Cancelled _), Place desc ->
            let status = Created(desc, now)
            { state with Status = Some status }, box (Ok status)
        | Some(Delivered _), Place desc ->
            let status = Created(desc, now)
            { state with Status = Some status }, box (Ok status)

        // Confirm a created order
        | Some(Created(desc, _)), Confirm ->
            let status = Confirmed(desc, now)
            { state with Status = Some status }, box (Ok status)

        // Ship a confirmed order
        | Some(Confirmed(desc, _)), Ship ->
            let status = Shipped(desc, now)
            { state with Status = Some status }, box (Ok status)

        // Deliver a shipped order
        | Some(Shipped(desc, _)), Deliver ->
            let status = Delivered(desc, now)
            { state with Status = Some status }, box (Ok status)

        // Cancel any active (non-terminal) order
        | Some(Created _), Cancel reason ->
            let status = Cancelled(reason, now)
            { state with Status = Some status }, box (Ok status)
        | Some(Confirmed _), Cancel reason ->
            let status = Cancelled(reason, now)
            { state with Status = Some status }, box (Ok status)

        // All remaining invalid transitions
        | None, _ -> state, box (Rejected "No order exists")
        | Some(Shipped _), Cancel _ -> state, box (Rejected "Cannot cancel a shipped order")
        | Some(Created _), Ship -> state, box (Rejected "Cannot ship: order must be confirmed first")
        | Some(Created _), Deliver -> state, box (Rejected "Cannot deliver: order must be shipped first")
        | Some(Confirmed _), Deliver -> state, box (Rejected "Cannot deliver: order must be shipped first")
        | Some(Shipped _), Confirm -> state, box (Rejected "Cannot confirm: order is already shipped")
        | Some _, _ -> state, box (Rejected $"Invalid transition for command: %A{cmd}")

    /// <summary>
    /// The order grain definition using the grain computation expression.
    /// Includes a timer for periodic status checks and a reminder for timeout warnings.
    /// </summary>
    let order =
        grain {
            defaultState
                { Status = None
                  StatusCheckCount = 0
                  ReminderTickCount = 0 }

            handle (fun state cmd ->
                task { return transition state cmd })

            onTimer
                "StatusCheck"
                (TimeSpan.FromSeconds(5.0))
                (TimeSpan.FromSeconds(10.0))
                (fun state ->
                    task {
                        let newCount = state.StatusCheckCount + 1

                        match state.Status with
                        | Some status ->
                            printfn "  [Timer] Status check #%d: %A" newCount status
                        | None ->
                            printfn "  [Timer] Status check #%d: no order" newCount

                        return { state with StatusCheckCount = newCount }
                    })

            onReminder "OrderTimeout" (fun state _name _status ->
                task {
                    let newCount = state.ReminderTickCount + 1
                    printfn "  [Reminder] Order timeout check #%d" newCount

                    match state.Status with
                    | Some(Created(desc, createdAt)) when DateTime.UtcNow - createdAt > TimeSpan.FromMinutes(30.0) ->
                        printfn "  [Reminder] Order '%s' timed out -- cancelling" desc
                        let cancelled = Cancelled("Timed out", DateTime.UtcNow)
                        return { state with Status = Some cancelled; ReminderTickCount = newCount }
                    | _ ->
                        return { state with ReminderTickCount = newCount }
                })

            persist "Default"
        }
