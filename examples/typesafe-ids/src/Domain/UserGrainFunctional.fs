/// <summary>
/// Functional-runtime equivalent of <c>UserGrainDef.user</c> in <c>UserGrain.fs</c> (the
/// <c>grain { }</c> CE original -- now <c>[&lt;Obsolete&gt;]</c>). Same domain (set/read a user
/// profile) rebuilt as a <c>grainContract</c> + <c>grainFor</c> pair. The functional runtime's
/// own key codec (<c>int64KeyMapped rawId userId</c>, so the grain key IS an
/// <c>int64&lt;UserId&gt;</c>) gives the same "wrong-ID-type is a compile error"
/// guarantee this example demonstrates with units of measure -- see docs/functional-grains.md,
/// "Key-codec identity rules".
/// </summary>
namespace TypeSafeIds.Domain

open System.Threading.Tasks
open Orleans.FSharp
open TypeSafeIds.Domain.Ids

type UserActor = private UserActor of unit

type UserProfile = { Name: string; Email: string }

[<NoEquality; NoComparison>]
type UserApi =
    { setProfile: string * string -> Task<bool>
      getProfile: unit -> Task<UserProfile> }

[<RequireQualifiedAccess>]
module UserApi =
    let contract =
        grainContract<UserActor, int64<UserId>, UserApi> {
            grainType "typesafe-ids.user.functional"
            version 1
            int64KeyMapped rawId userId
        }

    let ref = FunctionalGrain.ref contract

module UserFunctionalDef =
    let user =
        grainFor UserApi.contract {
            defaultState (fun () -> { Name = ""; Email = "" })

            handle
                (_.setProfile)
                (fun _context _state (name, email) -> task { return { Name = name; Email = email }, true })

            handle (_.getProfile) (fun _context state () -> task { return state, state })
        }
