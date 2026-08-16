/// <summary>
/// Functional-runtime equivalent of <c>ChatGrainDef.chat</c> in <c>ChatGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Same domain (post a message,
/// read how many have been posted) rebuilt as a <c>grainContract</c> + <c>grainFor</c> pair.
/// This twin covers the message-posting slice of the domain. Pub/sub observer notification
/// (<c>Subscribe</c>/<c>Unsubscribe</c>/<c>IChatObserver</c>) is deliberately left out of the
/// twin, and is <em>not</em> a functional-runtime capability gap: <c>Observer.createRef</c> and
/// <c>FSharpObserverManager</c> are orthogonal to this deprecation and work unchanged inside
/// <c>grainFor</c> handlers -- proven end to end by
/// <c>tests/Orleans.FSharp.Integration/FunctionalObserverIntegrationTests.fs</c>, and described
/// under "Observers, streams, and the other orthogonal surfaces" in <c>docs/functional-grains.md</c>.
/// The one real constraint is Orleans' own: the observer interface needs a source-generated
/// proxy, so it must be declared in a C# project. This example declares <c>IChatObserver</c> in
/// F# (<c>ChatTypes.fs</c>), which is why the observer slice is not reproduced here -- the same
/// constraint the <c>grain { }</c> original is subject to.
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
