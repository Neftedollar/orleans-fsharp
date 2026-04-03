namespace Testbed.Shared

open System
open System.Threading.Tasks
open Orleans

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

/// Grain interface for the counter grain. String-keyed for easy multi-grain testing.
type ICounterGrain =
    inherit IGrainWithStringKey
    abstract HandleMessage: CounterCommand -> Task<obj>

/// Grain interface for the chat grain.
type IChatGrain =
    inherit IGrainWithStringKey
    abstract HandleMessage: ChatCommand -> Task<obj>

// ── Stream types ──

type MetricEvent =
    { Source: string
      Value: float
      Timestamp: DateTime }

type AlertEvent =
    | HighCpu of float
    | HighMemory of float
    | ErrorRate of float

/// Grain that publishes metric events to a stream.
type IMetricProducerGrain =
    inherit IGrainWithStringKey
    abstract StartProducing: unit -> Task
    abstract StopProducing: unit -> Task
    abstract GetProducedCount: unit -> Task<int>

/// Grain that subscribes to metric stream and collects events.
type IMetricCollectorGrain =
    inherit IGrainWithStringKey
    abstract Subscribe: string -> Task
    abstract GetCollectedCount: unit -> Task<int>
    abstract GetLastEvent: unit -> Task<MetricEvent>
