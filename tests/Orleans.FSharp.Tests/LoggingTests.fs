module Orleans.FSharp.Tests.LoggingTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Microsoft.Extensions.Logging
open Orleans.FSharp
open Orleans.FSharp.Testing

// --- logInfo tests ---

[<Fact>]
let ``logInfo produces Information level entry`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")

    Log.logInfo logger "Processing order {OrderId}" [| box 42 |]

    let entries = LogCapture.captureLogs factory
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Level = LogLevel.Information @>

[<Fact>]
let ``logInfo preserves structured template`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")

    Log.logInfo logger "User {UserId} logged in" [| box "user-123" |]

    let entries = LogCapture.captureLogs factory
    test <@ entries.[0].Template.Contains("{UserId}") @>

[<Fact>]
let ``logInfo captures template argument values`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")

    Log.logInfo logger "Order {OrderId} for {Amount}" [| box 42; box 99.5 |]

    let entries = LogCapture.captureLogs factory
    test <@ entries.[0].Properties |> Map.containsKey "OrderId" @>
    test <@ entries.[0].Properties |> Map.containsKey "Amount" @>

// --- logWarning tests ---

[<Fact>]
let ``logWarning produces Warning level entry`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")

    Log.logWarning logger "Slow query {Duration}ms" [| box 5000 |]

    let entries = LogCapture.captureLogs factory
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Level = LogLevel.Warning @>

// --- logError tests ---

[<Fact>]
let ``logError produces Error level entry with exception`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")
    let ex = InvalidOperationException("test error")

    Log.logError logger ex "Failed to process {OrderId}" [| box 42 |]

    let entries = LogCapture.captureLogs factory
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Level = LogLevel.Error @>
    test <@ entries.[0].Exception.IsSome @>
    test <@ entries.[0].Exception.Value.Message = "test error" @>

// --- logDebug tests ---

[<Fact>]
let ``logDebug produces Debug level entry`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")

    Log.logDebug logger "Cache hit for {Key}" [| box "item-1" |]

    let entries = LogCapture.captureLogs factory
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Level = LogLevel.Debug @>

// --- withCorrelation tests ---

[<Fact>]
let ``withCorrelation sets correlation ID during scope`` () =
    task {
        let mutable capturedId = None

        do!
            Log.withCorrelation "corr-123" (fun () ->
                task {
                    capturedId <- Log.currentCorrelationId ()
                    return ()
                })

        test <@ capturedId = Some "corr-123" @>
    }

[<Fact>]
let ``withCorrelation restores previous ID after scope`` () =
    task {
        do!
            Log.withCorrelation "outer" (fun () ->
                task {
                    do!
                        Log.withCorrelation "inner" (fun () ->
                            task {
                                let id = Log.currentCorrelationId ()
                                test <@ id = Some "inner" @>
                                return ()
                            })

                    let id = Log.currentCorrelationId ()
                    test <@ id = Some "outer" @>
                    return ()
                })
    }

[<Fact>]
let ``withCorrelation attaches CorrelationId to log entries`` () =
    task {
        let factory = LogCapture.create ()
        let logger = (factory :> ILoggerFactory).CreateLogger("Test")

        do!
            Log.withCorrelation "corr-456" (fun () ->
                task {
                    Log.logInfo logger "Test message {Value}" [| box 1 |]
                    return ()
                })

        let entries = LogCapture.captureLogs factory
        test <@ entries.Length = 1 @>
        test <@ entries.[0].Properties |> Map.containsKey "CorrelationId" @>
        test <@ entries.[0].Properties.["CorrelationId"] |> unbox<string> = "corr-456" @>
    }

// --- currentCorrelationId tests ---

[<Fact>]
let ``currentCorrelationId returns None when no scope active`` () =
    // Reset correlation state (AsyncLocal should be null/empty outside withCorrelation)
    let id = Log.currentCorrelationId ()
    test <@ id = None @>

[<Fact>]
let ``currentCorrelationId returns expected value within scope`` () =
    task {
        do!
            Log.withCorrelation "test-id" (fun () ->
                task {
                    let id = Log.currentCorrelationId ()
                    test <@ id = Some "test-id" @>
                    return ()
                })
    }

// --- Multiple log entries test ---

[<Fact>]
let ``multiple log calls within correlation scope all share same ID`` () =
    task {
        let factory = LogCapture.create ()
        let logger = (factory :> ILoggerFactory).CreateLogger("Test")

        do!
            Log.withCorrelation "shared-id" (fun () ->
                task {
                    Log.logInfo logger "First {Step}" [| box 1 |]
                    Log.logWarning logger "Second {Step}" [| box 2 |]
                    Log.logDebug logger "Third {Step}" [| box 3 |]
                    return ()
                })

        let entries = LogCapture.captureLogs factory
        test <@ entries.Length = 3 @>

        for entry in entries do
            test <@ entry.Properties |> Map.containsKey "CorrelationId" @>
            test <@ entry.Properties.["CorrelationId"] |> unbox<string> = "shared-id" @>
    }

// --- CapturedLogEntry tests ---

[<Fact>]
let ``CapturedLogEntry has correct Timestamp`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")
    let before = DateTimeOffset.UtcNow

    Log.logInfo logger "Timestamp test" [||]

    let entries = LogCapture.captureLogs factory
    let after = DateTimeOffset.UtcNow
    test <@ entries.[0].Timestamp >= before @>
    test <@ entries.[0].Timestamp <= after @>

[<Fact>]
let ``CapturingLogger Clear removes all entries`` () =
    let factory = LogCapture.create ()
    let logger = (factory :> ILoggerFactory).CreateLogger("Test")

    Log.logInfo logger "Entry 1" [||]
    Log.logInfo logger "Entry 2" [||]

    test <@ (LogCapture.captureLogs factory).Length = 2 @>
    factory.Clear()
    test <@ (LogCapture.captureLogs factory).Length = 0 @>
