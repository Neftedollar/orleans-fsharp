/// <summary>
/// Feature 6 — contract versioning: two contracts over the same <c>grainType</c> differing only
/// in <c>version</c>. The silo hosts version 1; a caller bound to version 2 is rejected before
/// any handler runs, with the exact diagnostic the transport produces.
/// </summary>
namespace FeatureTour.VersioningTour

open System.Threading.Tasks
open Orleans.FSharp

type VersionedActor = private VersionedActor of unit

[<NoEquality; NoComparison>]
type VersionedApi =
    { /// Replies with the contract version the SILO hosts.
      hosted: unit -> Task<string> }

[<RequireQualifiedAccess>]
module VersionedApi =

    /// <summary>The grain type both contracts address. Version is NOT part of grain identity.</summary>
    [<Literal>]
    let GrainType = "tour.versioned"

    /// <summary>The contract the silo hosts, and the one a matching caller binds.</summary>
    let v1 =
        grainContract<VersionedActor, string, VersionedApi> () {
            grainType GrainType
            version 1
            stringKey
        }

    /// <summary>
    /// A second contract over the SAME grain type at a different version. Nothing hosts it: it
    /// exists to show that version matching is exact (<c>=</c>, not <c>&gt;=</c>) and has no
    /// rolling-upgrade tolerance, so a caller one version ahead fails the call outright rather
    /// than negotiating down.
    /// </summary>
    let v2 =
        grainContract<VersionedActor, string, VersionedApi> () {
            grainType GrainType
            version 2
            stringKey
        }

    let refV1 = FunctionalGrain.ref v1
    let refV2 = FunctionalGrain.ref v2

[<RequireQualifiedAccess>]
module VersionedDefinition =
    /// Only v1 is ever registered with AddFunctionalGrain.
    let definition =
        grainFor VersionedApi.v1 {
            defaultState (fun () -> ())
            handle (_.hosted) (fun _context state () -> task { return state, "reply from the version-1 handler" })
        }
