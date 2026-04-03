/// <summary>
/// Router grain that uses Active Patterns for message routing.
/// Active Patterns decompose messages into categories via pattern matching --
/// impossible to express in C# without if/else chains or class hierarchies.
/// </summary>
namespace TypeSafeIds.Domain

open System.Threading.Tasks
open Orleans
open Orleans.FSharp
open TypeSafeIds.Domain.Routing

/// <summary>
/// State of the router grain tracking processing statistics.
/// </summary>
type RouterState =
    {
        /// <summary>Number of messages successfully processed.</summary>
        Processed: int
        /// <summary>Number of messages dropped as spam.</summary>
        Dropped: int
    }

/// <summary>
/// Grain interface for the router grain.
/// </summary>
type IRouterGrain =
    inherit IGrainWithStringKey

    /// <summary>Routes an incoming message and returns the destination queue name.</summary>
    abstract HandleMessage: IncomingMessage -> Task<obj>

/// <summary>
/// Router grain definition using active patterns for message classification.
/// </summary>
module RouterGrainDef =

    /// <summary>
    /// The router grain: classifies and routes messages using composed active patterns.
    /// Spam is dropped; all other messages are routed to the appropriate queue.
    /// </summary>
    let router =
        grain {
            defaultState { Processed = 0; Dropped = 0 }

            handle (fun state msg ->
                task {
                    let route = routeMessage msg

                    match msg with
                    | Spam ->
                        return { state with Dropped = state.Dropped + 1 }, box route
                    | _ ->
                        return { state with Processed = state.Processed + 1 }, box route
                })
        }
