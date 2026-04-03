namespace Testbed.Shared

open System

type CounterState = { Count: int }

type CounterCommand =
    | Increment
    | Decrement
    | GetValue
    | GetSiloInfo

type ChatMessage =
    { Sender: string
      Text: string
      Timestamp: DateTime }

type ChatCommand =
    | Send of ChatMessage
    | GetHistory
    | GetMessageCount

type ChatState = { Messages: ChatMessage list }

// ── Stream types ──

type MetricEvent =
    { Source: string
      Value: float
      Timestamp: DateTime }

type AlertEvent =
    | HighCpu of float
    | HighMemory of float
    | ErrorRate of float
