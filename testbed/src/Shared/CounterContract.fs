/// <summary>
/// The functional-runtime counter contract, shared by the Silo (which hosts it, see
/// <c>Testbed.CounterFunctional.counterDefinition</c>) and the Client (which calls it).
/// A <c>grainContract</c> is the client/server boundary of the functional runtime — the
/// definition stays server-side, the contract has to be visible to both processes, which
/// is why it lives here rather than next to the definition.
/// </summary>
module Testbed.Shared.CounterFunctionalContract

open System.Threading.Tasks
open Orleans.FSharp

type CounterActor = private CounterActor of unit

[<NoEquality; NoComparison>]
type CounterApi =
    { increment: unit -> Task<int>
      decrement: unit -> Task<int>
      value: unit -> Task<int> }

[<RequireQualifiedAccess>]
module CounterApi =
    let contract =
        grainContract<CounterActor, string, CounterApi> () {
            grainType "testbed.counter.functional"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract
