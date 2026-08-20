/// <summary>
/// The specification's contract module laid out the way the spec shows it: a contract file of
/// its own, with the point-free <c>ref</c> / <c>rawRef</c> bindings and <b>no use site anywhere
/// in this file</b>. F# generalizes a module-level value binding at the end of the file it is
/// declared in, so if <c>FunctionalGrain.ref</c> / <c>rawRef</c> ever regress to a shape whose
/// partial application stays generic, this file stops compiling with FS0030 — no test needs to
/// run. <c>FunctionalSurfaceTests</c> pins the inferred types from another file.
/// </summary>
namespace Chat.PointFree

open System.Threading.Tasks
open Orleans.FSharp

type LobbyActor = private LobbyActor of unit

[<Struct>]
type LobbyId =
    | LobbyId of string

    static member value(LobbyId value) = value

[<NoEquality; NoComparison>]
type LobbyApi =
    { enter: string -> Task<unit>
      count: unit -> Task<int> }

[<RequireQualifiedAccess>]
module Lobby =
    let contract =
        grainContract<LobbyActor, LobbyId, LobbyApi> {
            grainType "chat.lobby"
            version 1
            stringKeyMapped LobbyId.value LobbyId

            readOnly (_.count)
        }

    let ref = FunctionalGrain.ref contract
    let rawRef = FunctionalGrain.rawRef contract
