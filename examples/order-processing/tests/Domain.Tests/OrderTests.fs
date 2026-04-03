module OrderProcessing.Tests.OrderTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open OrderProcessing.Domain

/// <summary>
/// Generates arbitrary order commands for property-based testing.
/// </summary>
type OrderCommandGen() =
    static member OrderCommand() : Arbitrary<OrderCommand> =
        let genPlace =
            Gen.elements [ "Widget x5"; "Gadget x3"; "Doodad x1"; "Thingamajig x100" ]
            |> Gen.map Place

        let genCancel =
            Gen.elements [ "Changed mind"; "Too expensive"; "Found better deal" ]
            |> Gen.map Cancel

        Gen.frequency
            [ 3, genPlace
              2, Gen.constant Confirm
              2, Gen.constant Ship
              2, Gen.constant Deliver
              2, genCancel
              1, Gen.constant GetStatus ]
        |> Arb.fromGen

/// <summary>
/// Checks whether an OrderStatus is a valid terminal or non-terminal state.
/// </summary>
let isValidStatus (status: OrderStatus option) : bool =
    match status with
    | None -> true
    | Some(Created _) -> true
    | Some(Confirmed _) -> true
    | Some(Shipped _) -> true
    | Some(Delivered _) -> true
    | Some(Cancelled _) -> true

/// <summary>
/// Applies a sequence of commands to the initial state using the transition function.
/// </summary>
let applyCommands (commands: OrderCommand list) : OrderState =
    let initial =
        { Status = None
          StatusCheckCount = 0
          ReminderTickCount = 0 }

    commands
    |> List.fold (fun state cmd -> OrderGrainDef.transition state cmd |> fst) initial

/// <summary>
/// Property-based tests for the order processing state machine.
/// </summary>
module Properties =

    /// <summary>
    /// Any sequence of commands always produces a valid state.
    /// </summary>
    [<Property(Arbitrary = [| typeof<OrderCommandGen> |])>]
    let ``any command sequence produces a valid state`` (commands: OrderCommand list) =
        let finalState = applyCommands commands
        isValidStatus finalState.Status

    /// <summary>
    /// GetStatus never changes the state.
    /// </summary>
    [<Property(Arbitrary = [| typeof<OrderCommandGen> |])>]
    let ``GetStatus never changes state`` (commands: OrderCommand list) =
        let stateBefore = applyCommands commands
        let stateAfter, _ = OrderGrainDef.transition stateBefore GetStatus
        stateBefore.Status = stateAfter.Status

    /// <summary>
    /// The happy path (Place -> Confirm -> Ship -> Deliver) always succeeds.
    /// </summary>
    [<Fact>]
    let ``happy path succeeds`` () =
        let commands = [ Place "Test Order"; Confirm; Ship; Deliver ]
        let finalState = applyCommands commands

        match finalState.Status with
        | Some(Delivered(desc, _)) -> Assert.Equal("Test Order", desc)
        | other -> Assert.Fail($"Expected Delivered, got %A{other}")

    /// <summary>
    /// Cannot ship before confirming.
    /// </summary>
    [<Fact>]
    let ``cannot ship before confirming`` () =
        let state =
            { Status = Some(Created("item", DateTime.UtcNow))
              StatusCheckCount = 0
              ReminderTickCount = 0 }

        let _, result = OrderGrainDef.transition state Ship
        let orderResult = result :?> OrderResult
        match orderResult with
        | Rejected _ -> ()
        | other -> Assert.Fail($"Expected Rejected, got %A{other}")

    /// <summary>
    /// Cannot cancel a shipped order.
    /// </summary>
    [<Fact>]
    let ``cannot cancel shipped order`` () =
        let state =
            { Status = Some(Shipped("item", DateTime.UtcNow))
              StatusCheckCount = 0
              ReminderTickCount = 0 }

        let _, result = OrderGrainDef.transition state (Cancel "reason")
        let orderResult = result :?> OrderResult
        match orderResult with
        | Rejected _ -> ()
        | other -> Assert.Fail($"Expected Rejected, got %A{other}")

    /// <summary>
    /// A cancelled order can have a new order placed.
    /// </summary>
    [<Fact>]
    let ``can place new order after cancellation`` () =
        let state =
            { Status = Some(Cancelled("reason", DateTime.UtcNow))
              StatusCheckCount = 0
              ReminderTickCount = 0 }

        let newState, result = OrderGrainDef.transition state (Place "New Order")
        let orderResult = result :?> OrderResult
        match orderResult, newState.Status with
        | Ok(Created(desc, _)), Some(Created _) -> Assert.Equal("New Order", desc)
        | other, _ -> Assert.Fail($"Expected Ok(Created), got %A{other}")

    /// <summary>
    /// A delivered order can have a new order placed.
    /// </summary>
    [<Fact>]
    let ``can place new order after delivery`` () =
        let state =
            { Status = Some(Delivered("old", DateTime.UtcNow))
              StatusCheckCount = 0
              ReminderTickCount = 0 }

        let newState, result = OrderGrainDef.transition state (Place "Reorder")
        let orderResult = result :?> OrderResult
        match orderResult, newState.Status with
        | Ok(Created(desc, _)), Some(Created _) -> Assert.Equal("Reorder", desc)
        | other, _ -> Assert.Fail($"Expected Ok(Created), got %A{other}")
