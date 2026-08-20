/// <summary>
/// Functional-runtime equivalent of <c>OrderGrainDef.order</c> in <c>OrderGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Full-depth twin: the same
/// <c>OrderStatus</c> DU state machine (reused from <c>OrderState.fs</c> verbatim, timestamps and
/// all), a pure <c>transition</c> function mirroring every arm of the original's match (every
/// rejected case there collapses into one typed <c>InvalidTransition</c> here instead of a boxed
/// string message), a <c>readOnly</c> query, explicit <c>stateFrom</c> persistence with writes on
/// every successful transition, a declarative <c>onTimer</c> status-check counter, and a
/// declarative <c>onReminder</c> timeout auto-cancel -- the same two background behaviors the
/// original demonstrated with <c>onTimer</c> / <c>onReminder</c> in <c>grain { }</c>.
///
/// One real constraint, not a functional-runtime gap: Orleans enforces a minimum reminder
/// <c>Period</c> (<c>ReminderOptions.MinimumReminderPeriod</c>, 1 minute by default, and this
/// example does not override it) -- <c>FunctionalSiloHosting</c> validates every declared
/// reminder's period against it at startup, closing the same floor the old reminder service
/// enforces lazily on first registration. The old grain's demo waited only ~12s for a tick; that
/// was never a realistic reminder period, since Orleans would have rejected anything that short
/// the same way at registration. This twin's <c>OrderTimeout</c> reminder is registered for real,
/// on a real 1-minute period -- see this file's `onReminder` declaration and the README for the
/// exact schedule -- but the entry-point demo does not block waiting a full minute for it; it
/// waits for the `onTimer` status check instead, which has no such floor.
/// </summary>
namespace OrderProcessing.Domain

open System
open System.Threading.Tasks
open Orleans.Runtime
open Orleans.FSharp

type OrderActor = private OrderActor of unit

/// <summary>A rejected state transition, replacing the old grain's boxed `Rejected of string`.</summary>
type OrderError =
    /// <summary>No arm of <c>transition</c> permits <c>attempted</c> from <c>from</c>.</summary>
    | InvalidTransition of from: string * attempted: string

[<NoEquality; NoComparison>]
type OrderApi =
    { /// <summary>Place a new order (only valid with no active order, or from a terminal state).</summary>
      place: string -> Task<Result<OrderStatus, OrderError>>
      /// <summary>Confirm a created order.</summary>
      confirm: unit -> Task<Result<OrderStatus, OrderError>>
      /// <summary>Ship a confirmed order.</summary>
      ship: unit -> Task<Result<OrderStatus, OrderError>>
      /// <summary>Mark a shipped order delivered.</summary>
      deliver: unit -> Task<Result<OrderStatus, OrderError>>
      /// <summary>Cancel an active (non-terminal) order with a reason.</summary>
      cancel: string -> Task<Result<OrderStatus, OrderError>>
      /// <summary>Current status, or <c>None</c> if no order has ever been placed.</summary>
      status: unit -> Task<OrderStatus option> }

[<RequireQualifiedAccess>]
module OrderApi =
    let contract =
        grainContract<OrderActor, string, OrderApi> {
            grainType "order-processing.order.functional"
            version 1
            stringKey

            readOnly (_.status)
        }

    let ref = FunctionalGrain.ref contract

/// <summary>The write-side intents <c>transition</c> matches on. A dedicated,
/// <c>RequireQualifiedAccess</c> DU rather than reusing <c>OrderCommand</c> -- <c>OrderCommand</c>
/// also carries <c>GetStatus</c>, which is not a transition and is served directly from state by
/// the <c>status</c> handler instead.</summary>
[<RequireQualifiedAccess>]
type Transition =
    | Place of description: string
    | Confirm
    | Ship
    | Deliver
    | Cancel of reason: string

module OrderFunctionalDef =

    let private describeStatus (status: OrderStatus option) : string =
        match status with
        | None -> "NoOrder"
        | Some(Created _) -> "Created"
        | Some(Confirmed _) -> "Confirmed"
        | Some(Shipped _) -> "Shipped"
        | Some(Delivered _) -> "Delivered"
        | Some(Cancelled _) -> "Cancelled"

    let private describeTransition (t: Transition) : string =
        match t with
        | Transition.Place desc -> $"Place \"{desc}\""
        | Transition.Confirm -> "Confirm"
        | Transition.Ship -> "Ship"
        | Transition.Deliver -> "Deliver"
        | Transition.Cancel reason -> $"Cancel \"{reason}\""

    /// <summary>
    /// Pure state-machine core: no I/O, no clock read (<c>now</c> is a parameter) -- exactly the
    /// property that lets a functional grain publish a replacement state from a handler and let
    /// the framework decide whether/when to persist it. Mirrors every positive arm of
    /// <c>OrderGrainDef.transition</c> in <c>OrderGrain.fs</c> one for one; every one of that
    /// function's individually-worded rejections collapses into the single typed
    /// <c>InvalidTransition</c> case here.
    /// </summary>
    // NOTE: Ok/Error are qualified as Result.Ok/Result.Error throughout this module.
    // OrderCommands.fs (same namespace, compiled earlier per Domain.fsproj) declares its own
    // OrderResult DU with an `Ok` case, which shadows FSharp.Core's `Ok` for unqualified use here.
    let transition (now: DateTime) (status: OrderStatus option) (t: Transition) : Result<OrderStatus, OrderError> =
        match status, t with
        | None, Transition.Place desc -> Result.Ok(Created(desc, now))
        | Some(Cancelled _), Transition.Place desc -> Result.Ok(Created(desc, now))
        | Some(Delivered _), Transition.Place desc -> Result.Ok(Created(desc, now))
        | Some(Created(desc, _)), Transition.Confirm -> Result.Ok(Confirmed(desc, now))
        | Some(Confirmed(desc, _)), Transition.Ship -> Result.Ok(Shipped(desc, now))
        | Some(Shipped(desc, _)), Transition.Deliver -> Result.Ok(Delivered(desc, now))
        | Some(Created _), Transition.Cancel reason -> Result.Ok(Cancelled(reason, now))
        | Some(Confirmed _), Transition.Cancel reason -> Result.Ok(Cancelled(reason, now))
        | _ -> Result.Error(InvalidTransition(describeStatus status, describeTransition t))

    let orderState = PersistentState.create<OrderState> "state" "Default"

    /// <summary>Applies a transition, and -- on success only -- publishes the new state in memory
    /// and writes it through <c>orderState</c>. Shared by all five write handlers below so the
    /// "persist on successful transitions, not on rejections" rule lives in exactly one place.</summary>
    let private applyAndPersist
        (context: FunctionalGrainContext<OrderActor, string>)
        (state: OrderState)
        (t: Transition)
        : Task<OrderState * Result<OrderStatus, OrderError>> =
        task {
            match transition context.utcNow.UtcDateTime state.Status t with
            | Result.Ok status ->
                let next = { state with Status = Some status }
                let storage = context.persistentState orderState
                storage.State <- next
                do! storage.WriteStateAsync()
                return next, Result.Ok status
            | Result.Error e -> return state, Result.Error e
        }

    let order =
        grainFor OrderApi.contract {
            defaultState (fun () ->
                { Status = None
                  StatusCheckCount = 0
                  ReminderTickCount = 0 })

            stateFrom orderState

            // Idle-deactivation threshold for this definition's activations -- once an activation
            // sees no activity (calls, reminders, a KeepAlive timer tick) for 30 minutes, it
            // becomes eligible for collection. Not a data TTL: durable state is untouched either
            // way, and a later call simply reloads it into a fresh activation.
            collectionAge (TimeSpan.FromMinutes 30.0)

            handle (_.place) (fun context state description -> applyAndPersist context state (Transition.Place description))

            handle (_.confirm) (fun context state () -> applyAndPersist context state Transition.Confirm)

            handle (_.ship) (fun context state () -> applyAndPersist context state Transition.Ship)

            handle (_.deliver) (fun context state () -> applyAndPersist context state Transition.Deliver)

            handle (_.cancel) (fun context state reason -> applyAndPersist context state (Transition.Cancel reason))

            handle (_.status) (fun _context state () -> task { return state, state.Status })

            // Status-check counter -- same 5s due / 10s period as the old grain's "StatusCheck"
            // timer. KeepAlive = true: unlike a plain background poll, this grain should stay
            // active while it is monitoring an order, so the timer itself counts as activity
            // against collectionAge. No Orleans-enforced minimum applies to timer periods (only
            // to reminder periods), so this one runs at the same cadence the original demo showed.
            onTimer
                "StatusCheck"
                (GrainTimerCreationOptions(DueTime = TimeSpan.FromSeconds 5.0, Period = TimeSpan.FromSeconds 10.0, KeepAlive = true))
                (fun _context state ->
                    task {
                        let newCount = state.StatusCheckCount + 1

                        match state.Status with
                        | Some status -> printfn "  [Timer] Status check #%d: %A" newCount status
                        | None -> printfn "  [Timer] Status check #%d: no order" newCount

                        return { state with StatusCheckCount = newCount }
                    })

            // Timeout auto-cancel -- registered for real, on Orleans' actual 1-minute reminder
            // floor (ReminderOptions.MinimumReminderPeriod; this example does not override it, so
            // FunctionalSiloHosting's startup validation would reject anything shorter). Due time
            // is short (10s) so the first tick arrives promptly; the period is the real 1-minute
            // floor. Every tick increments ReminderTickCount in memory; a tick that finds a
            // Created order older than 30 minutes cancels it and persists that transition --
            // exactly the old grain's "OrderTimeout" business rule, unchanged.
            onReminder
                "OrderTimeout"
                (TimeSpan.FromSeconds 10.0)
                (TimeSpan.FromMinutes 1.0)
                (fun context state _tickStatus ->
                    task {
                        let newCount = state.ReminderTickCount + 1
                        printfn "  [Reminder] Order timeout check #%d" newCount

                        match state.Status with
                        | Some(Created(desc, createdAt)) when
                            context.utcNow.UtcDateTime - createdAt > TimeSpan.FromMinutes(30.0)
                            ->
                            printfn "  [Reminder] Order '%s' timed out -- cancelling" desc
                            let cancelled = Cancelled("Timed out", context.utcNow.UtcDateTime)
                            let next = { state with Status = Some cancelled; ReminderTickCount = newCount }
                            let storage = context.persistentState orderState
                            storage.State <- next
                            do! storage.WriteStateAsync()
                            return next
                        | _ -> return { state with ReminderTickCount = newCount }
                    })
        }
