/// <summary>
/// Functional-runtime equivalent of <c>ChatGrainDef.chat</c> in <c>ChatGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Same domain (post a message,
/// read how many have been posted) rebuilt as a <c>grainContract</c> + <c>grainFor</c> pair.
/// Pub/sub observer notification (<c>Subscribe</c>/<c>Unsubscribe</c>/<c>IChatObserver</c>) has
/// no functional-runtime equivalent yet -- see the migration doc's capability-gap note -- so
/// this twin covers only the message-posting slice of the domain.
/// </summary>
namespace ChatRoom.Grains

open System.Threading.Tasks
open Orleans.FSharp

type RoomActor = private RoomActor of unit

[<NoEquality; NoComparison>]
type RoomApi =
    { post: string * string -> Task<int>
      count: unit -> Task<int> }

[<RequireQualifiedAccess>]
module RoomApi =
    let contract =
        grainContract<RoomActor, string, RoomApi> () {
            grainType "chat-room.room.functional"
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

module RoomFunctionalDef =
    let room =
        grainFor RoomApi.contract {
            defaultState (fun () -> 0)

            handle
                (_.post)
                (fun _context state (_sender, _message) ->
                    task {
                        let next = state + 1
                        return next, next
                    })

            handle (_.count) (fun _context state () -> task { return state, state })
        }
