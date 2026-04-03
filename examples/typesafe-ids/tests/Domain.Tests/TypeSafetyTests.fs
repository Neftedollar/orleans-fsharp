module TypeSafeIds.Tests.TypeSafetyTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open TypeSafeIds.Domain.Ids
open TypeSafeIds.Domain.Routing
open TypeSafeIds.Domain

// =========================================================================
// Units of Measure — ID type safety
// =========================================================================

/// <summary>
/// rawId extracts the underlying int64 from a userId.
/// </summary>
[<Fact>]
let ``rawId extracts correct value from userId`` () =
    let id = userId 42L
    Assert.Equal(42L, rawId id)

/// <summary>
/// rawId extracts the underlying int64 from an orderId.
/// </summary>
[<Fact>]
let ``rawId extracts correct value from orderId`` () =
    let id = orderId 99L
    Assert.Equal(99L, rawId id)

/// <summary>
/// rawId extracts the underlying int64 from an accountId.
/// </summary>
[<Fact>]
let ``rawId extracts correct value from accountId`` () =
    let id = accountId 7L
    Assert.Equal(7L, rawId id)

/// <summary>
/// rawId extracts the underlying int64 from a productId.
/// </summary>
[<Fact>]
let ``rawId extracts correct value from productId`` () =
    let id = productId 256L
    Assert.Equal(256L, rawId id)

/// <summary>
/// toStringKey converts a typed ID to the expected string representation.
/// </summary>
[<Fact>]
let ``toStringKey converts typed ID to string`` () =
    let id = userId 123L
    Assert.Equal("123", toStringKey id)

/// <summary>
/// userId and orderId from the same raw value produce equal raw values but are distinct types.
/// (This test documents the compile-time guarantee -- both rawId values are equal as int64,
/// but userId 1L and orderId 1L cannot be used interchangeably at compile time.)
/// </summary>
[<Fact>]
let ``userId and orderId with same raw value have equal rawId`` () =
    let u = userId 10L
    let o = orderId 10L
    Assert.Equal(rawId u, rawId o)
    // But: userId 10L and orderId 10L are different TYPES at compile time.
    // Passing orderId where userId is expected is a compile error.

/// <summary>
/// Property: rawId is the inverse of userId for all int64 values.
/// </summary>
[<Property>]
let ``rawId is inverse of userId`` (raw: int64) =
    rawId (userId raw) = raw

/// <summary>
/// Property: rawId is the inverse of orderId for all int64 values.
/// </summary>
[<Property>]
let ``rawId is inverse of orderId`` (raw: int64) =
    rawId (orderId raw) = raw

/// <summary>
/// Property: toStringKey produces the same result as converting the raw value to string.
/// </summary>
[<Property>]
let ``toStringKey matches string of rawId`` (raw: int64) =
    toStringKey (userId raw) = string raw

// =========================================================================
// Active Patterns — message routing
// =========================================================================

/// <summary>
/// Generates arbitrary IncomingMessage values for property-based testing.
/// </summary>
type IncomingMessageGen() =
    static member IncomingMessage() : Arbitrary<IncomingMessage> =
        let genContent =
            Gen.oneof
                [ Gen.constant "What is this?"
                  Gen.constant "/do something"
                  Gen.constant "Hello friend"
                  Gen.constant "Just a normal message"
                  Gen.constant (String.replicate 100 "verbose ") ]

        let genMsg =
            gen {
                let! senderId = Gen.choose (1, 1000) |> Gen.map int64
                let! content = genContent
                let! isVip = Arb.generate<bool>
                let! spamScore = Gen.elements [ 0.0; 0.1; 0.3; 0.5; 0.85; 0.95 ]

                return
                    { SenderId = senderId
                      Content = content
                      Timestamp = DateTime.UtcNow
                      IsVip = isVip
                      SpamScore = spamScore }
            }

        genMsg |> Arb.fromGen

/// <summary>
/// Spam messages (spamScore > 0.8) are routed to the spam drop queue.
/// </summary>
[<Fact>]
let ``spam messages are dropped`` () =
    let msg =
        { SenderId = 1L
          Content = "Buy now!!!"
          Timestamp = DateTime.UtcNow
          IsVip = false
          SpamScore = 0.95 }

    Assert.Equal("dropped:spam", routeMessage msg)

/// <summary>
/// VIP messages with a question are routed to the VIP question queue.
/// </summary>
[<Fact>]
let ``VIP question routed to vip question queue`` () =
    let msg =
        { SenderId = 1L
          Content = "What is my balance?"
          Timestamp = DateTime.UtcNow
          IsVip = true
          SpamScore = 0.1 }

    Assert.Equal("vip:question-queue", routeMessage msg)

/// <summary>
/// VIP messages with a command prefix are routed to the VIP command processor.
/// </summary>
[<Fact>]
let ``VIP command routed to vip command processor`` () =
    let msg =
        { SenderId = 1L
          Content = "/upgrade plan"
          Timestamp = DateTime.UtcNow
          IsVip = true
          SpamScore = 0.0 }

    Assert.Equal("vip:command-processor", routeMessage msg)

/// <summary>
/// VIP messages without question or command go to VIP general.
/// </summary>
[<Fact>]
let ``VIP general message routed to vip general`` () =
    let msg =
        { SenderId = 1L
          Content = "Thanks for the help"
          Timestamp = DateTime.UtcNow
          IsVip = true
          SpamScore = 0.0 }

    Assert.Equal("vip:general", routeMessage msg)

/// <summary>
/// Standard questions are routed to the standard question queue.
/// </summary>
[<Fact>]
let ``standard question routed correctly`` () =
    let msg =
        { SenderId = 2L
          Content = "How do I reset?"
          Timestamp = DateTime.UtcNow
          IsVip = false
          SpamScore = 0.1 }

    Assert.Equal("standard:question-queue", routeMessage msg)

/// <summary>
/// Standard commands (starting with /) are routed to the command processor.
/// </summary>
[<Fact>]
let ``standard command routed to command processor`` () =
    let msg =
        { SenderId = 3L
          Content = "/cancel order 42"
          Timestamp = DateTime.UtcNow
          IsVip = false
          SpamScore = 0.0 }

    Assert.Equal("standard:command-processor", routeMessage msg)

/// <summary>
/// Greetings are routed to the greeting bot.
/// </summary>
[<Fact>]
let ``greeting routed to greeting bot`` () =
    let msg =
        { SenderId = 4L
          Content = "Hello there!"
          Timestamp = DateTime.UtcNow
          IsVip = false
          SpamScore = 0.1 }

    Assert.Equal("standard:greeting-bot", routeMessage msg)

/// <summary>
/// Long messages (over 500 chars) from non-VIP senders go to low priority.
/// </summary>
[<Fact>]
let ``long messages routed to low priority`` () =
    let msg =
        { SenderId = 5L
          Content = String.replicate 100 "verbose "
          Timestamp = DateTime.UtcNow
          IsVip = false
          SpamScore = 0.1 }

    Assert.Equal("batch:low-priority", routeMessage msg)

/// <summary>
/// Property: every message routes to exactly one non-empty queue name.
/// </summary>
[<Property(Arbitrary = [| typeof<IncomingMessageGen> |])>]
let ``every message routes to exactly one queue`` (msg: IncomingMessage) =
    let route = routeMessage msg
    not (String.IsNullOrWhiteSpace route)

/// <summary>
/// Property: spam always gets dropped regardless of other fields.
/// </summary>
[<Property>]
let ``spam always dropped regardless of other fields`` (isVip: bool) =
    let msg =
        { SenderId = 1L
          Content = "/important command?"
          Timestamp = DateTime.UtcNow
          IsVip = isVip
          SpamScore = 0.9 }

    routeMessage msg = "dropped:spam"

// =========================================================================
// Exhaustive matching — order state transitions
// =========================================================================

/// <summary>
/// Valid transition: Pending -> Confirmed.
/// </summary>
[<Fact>]
let ``Pending transitions to Confirmed`` () =
    Assert.Equal(Confirmed, OrderGrainDef.tryTransition Pending Confirmed)

/// <summary>
/// Valid transition: Confirmed -> Shipped.
/// </summary>
[<Fact>]
let ``Confirmed transitions to Shipped`` () =
    Assert.Equal(Shipped, OrderGrainDef.tryTransition Confirmed Shipped)

/// <summary>
/// Valid transition: Shipped -> Delivered.
/// </summary>
[<Fact>]
let ``Shipped transitions to Delivered`` () =
    Assert.Equal(Delivered, OrderGrainDef.tryTransition Shipped Delivered)

/// <summary>
/// Valid transition: Pending -> Cancelled.
/// </summary>
[<Fact>]
let ``Pending can be Cancelled`` () =
    Assert.Equal(Cancelled, OrderGrainDef.tryTransition Pending Cancelled)

/// <summary>
/// Valid transition: Confirmed -> Cancelled.
/// </summary>
[<Fact>]
let ``Confirmed can be Cancelled`` () =
    Assert.Equal(Cancelled, OrderGrainDef.tryTransition Confirmed Cancelled)

/// <summary>
/// Invalid transition: Delivered -> Cancelled is a no-op.
/// </summary>
[<Fact>]
let ``Delivered cannot be Cancelled`` () =
    Assert.Equal(Delivered, OrderGrainDef.tryTransition Delivered Cancelled)

/// <summary>
/// Invalid transition: Shipped -> Confirmed is a no-op.
/// </summary>
[<Fact>]
let ``Shipped cannot go back to Confirmed`` () =
    Assert.Equal(Shipped, OrderGrainDef.tryTransition Shipped Confirmed)

/// <summary>
/// Invalid transition: Cancelled -> any state is a no-op.
/// </summary>
[<Fact>]
let ``Cancelled is a terminal state`` () =
    Assert.Equal(Cancelled, OrderGrainDef.tryTransition Cancelled Confirmed)
    Assert.Equal(Cancelled, OrderGrainDef.tryTransition Cancelled Shipped)
    Assert.Equal(Cancelled, OrderGrainDef.tryTransition Cancelled Delivered)
    Assert.Equal(Cancelled, OrderGrainDef.tryTransition Cancelled Pending)
