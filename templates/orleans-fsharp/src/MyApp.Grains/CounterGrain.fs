namespace MyApp.Grains

open System.Threading.Tasks
open Orleans.FSharp

/// <summary>Phantom actor brand for the counter contract; never constructed.</summary>
type CounterActor = private CounterActor of unit

/// <summary>The counter's typed functional API.</summary>
[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      decrement: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    /// <summary>The counter grain's type, version, and key mapping.</summary>
    let contract =
        grainContract<CounterActor, int64, CounterApi> {
            grainType "myapp.counter"
            version 1
            int64Key
            readOnly (_.value)
        }

    /// <summary>Create a typed counter reference from an Orleans grain factory and key.</summary>
    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module Counter =
    /// <summary>Increment a counter state.</summary>
    let increment state = state + 1

    /// <summary>Decrement a counter state without going below zero.</summary>
    let decrement state = max 0 (state - 1)

module CounterGrain =
    /// <summary>The functional counter grain definition.</summary>
    let definition =
        grainFor CounterApi.contract {
            defaultState (fun () -> 0)

            handle
                (_.increment)
                (fun _context state () ->
                    task {
                        let next = Counter.increment state
                        return next, next
                    })

            handle
                (_.decrement)
                (fun _context state () ->
                    task {
                        let next = Counter.decrement state
                        return next, next
                    })

            handleQuery (_.value) (fun _context state () -> Task.FromResult state)
        }
