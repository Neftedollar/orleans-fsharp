/// <summary>
/// Feature 6 — contract versioning: two contracts over the same <c>grainType</c> differing only
/// in <c>version</c>. The silo hosts version 1; a caller bound to version 2 is rejected before
/// any handler runs, with the exact diagnostic the transport produces. The second half of the
/// section shows the opt-in the other way round: a version-3 host that <c>acceptsVersions</c>
/// down to 2, with one operation marked <c>sinceVersion 3</c>.
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
        grainContract<VersionedActor, string, VersionedApi> {
            grainType GrainType
            version 1
            stringKey
        }

    /// <summary>
    /// A second contract over the SAME grain type at a different version. Nothing hosts it: it
    /// exists to show that the DEFAULT policy is exact (<c>=</c>, not <c>&gt;=</c>) and has no
    /// rolling-upgrade tolerance, so a caller one version ahead fails the call outright rather
    /// than negotiating down.
    /// </summary>
    let v2 =
        grainContract<VersionedActor, string, VersionedApi> {
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

// ── The opt-in: a host that admits older callers ─────────────────────────────

type RollingActor = private RollingActor of unit

[<NoEquality; NoComparison>]
type RollingApi =
    { /// Present since version 1: an older caller may still invoke it.
      settle: string -> Task<string>
      /// Introduced at version 3: an older caller must not reach it.
      refund: string -> Task<string> }

[<RequireQualifiedAccess>]
module RollingApi =

    [<Literal>]
    let GrainType = "tour.rolling"

    /// <summary>
    /// The hosted contract. <c>acceptsVersions (BackwardCompatible 2)</c> admits 2 and 3;
    /// <c>sinceVersion 3</c> says <c>refund</c> did not exist at 2. Accepting a version ASSERTS
    /// that the argument and reply shapes of every operation an admitted caller can invoke are
    /// still the ones this definition declares — nothing converts between shapes.
    /// </summary>
    let v3 =
        grainContract<RollingActor, string, RollingApi> {
            grainType GrainType
            version 3
            stringKey
            acceptsVersions (BackwardCompatible 2)
            sinceVersion 3 (_.refund)
        }

    /// <summary>What the previous release still sends during a rolling deploy.</summary>
    let v2 =
        grainContract<RollingActor, string, RollingApi> {
            grainType GrainType
            version 2
            stringKey
        }

    /// <summary>One release older still — below the admitted floor.</summary>
    let v1 =
        grainContract<RollingActor, string, RollingApi> {
            grainType GrainType
            version 1
            stringKey
        }

    let refV3 = FunctionalGrain.ref v3
    let refV2 = FunctionalGrain.ref v2
    let refV1 = FunctionalGrain.ref v1

[<RequireQualifiedAccess>]
module RollingDefinition =
    /// Only v3 is registered: one hosted definition, three caller shapes.
    let definition =
        grainFor RollingApi.v3 {
            defaultState (fun () -> "")

            handle (_.settle) (fun _ _ (order: string) -> task { return order, $"settled {order}" })
            handle (_.refund) (fun _ state (order: string) -> task { return state, $"refunded {order}" })
        }
