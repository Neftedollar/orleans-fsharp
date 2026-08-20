/// <summary>
/// Status-matrix row 12 (tour section 10) — stateless workers and flexible placement for a
/// functional grain, now through the first-class <c>statelessWorker</c> / <c>placement</c>
/// definition operations (spec 004 item 4). <see cref="T:FeatureTour.Placement.FunctionalPlacementProvider"/>
/// is kept below as a one-line note that composition via an application-level
/// <c>IGrainPropertiesProvider</c> remains possible for cases the closed operation set does not
/// cover — it is no longer what <c>WorkerDefinition</c> uses.
/// </summary>
namespace FeatureTour.Placement

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks
open Orleans.Concurrency
open Orleans.Metadata
open Orleans.Placement
open Orleans.Runtime
open Orleans.FSharp

/// <summary>
/// Applies an Orleans placement attribute to a functional grain type.
/// </summary>
/// <remarks>
/// <para>
/// Orleans resolves placement from attributes on the grain CLASS. A functional grain's class is
/// the library's own <c>FunctionalGrainMarker&lt;'Actor&gt;</c>, closed over your brand — it is
/// not a type your code declares, so there is nowhere to put <c>[&lt;StatelessWorker&gt;]</c>.
/// </para>
/// <para>
/// The seam that closes the gap is entirely stock Orleans: every placement attribute implements
/// <see cref="IGrainPropertiesProviderAttribute"/>, and an application may register its own
/// <see cref="IGrainPropertiesProvider"/>. So the attribute is applied by hand, to the grain
/// type name rather than to a CLR type. For <c>StatelessWorkerAttribute 4</c> the manifest
/// properties this writes are exactly:
/// <c>placement-strategy = StatelessWorkerPlacement</c>, <c>max-local-instances = 4</c>,
/// <c>remove-idle-workers = True</c>, <c>unordered = true</c>.
/// </para>
/// <para>
/// This is a composition, not a runtime feature: it is untyped (a grain-type string, not the
/// contract), it is silo-side configuration rather than part of the contract, and nothing
/// validates that the named grain type exists. A first-class <c>placement</c> /
/// <c>statelessWorker</c> contract operation is still worth having — but the capability itself
/// is available today.
/// </para>
/// </remarks>
[<Sealed>]
type FunctionalPlacementProvider(services: IServiceProvider, grainTypeName: string, attribute: PlacementAttribute) =

    interface IGrainPropertiesProvider with
        member _.Populate(grainClass: Type, grainType: GrainType, properties: Dictionary<string, string>) =
            if String.Equals(grainType.ToString(), grainTypeName, StringComparison.Ordinal) then
                (attribute :> IGrainPropertiesProviderAttribute)
                    .Populate(services, grainClass, grainType, properties)

type WorkerActor = private WorkerActor of unit

/// <summary>What one <c>work</c> call reports.</summary>
type WorkReport =
    { activation: string
      startedAt: string }

[<NoEquality; NoComparison>]
type WorkerApi =
    { /// Occupies the activation for the requested milliseconds, then reports which one it was.
      work: int -> Task<WorkReport> }

[<RequireQualifiedAccess>]
module WorkerApi =
    /// <summary>The grain type the placement provider targets by name.</summary>
    [<Literal>]
    let GrainType = "tour.worker"

    /// <summary>How many local activations the stateless-worker placement is allowed.</summary>
    [<Literal>]
    let MaxLocalWorkers = 4

    let contract =
        grainContract<WorkerActor, string, WorkerApi> {
            grainType GrainType
            version 1
            stringKey
        }

    let ref = FunctionalGrain.ref contract

[<RequireQualifiedAccess>]
module WorkerDefinition =
    let definition =
        grainFor WorkerApi.contract {
            // One id per activation. Distinct ids across concurrent calls to ONE grain id is the
            // observable signature of stateless-worker placement.
            defaultState (fun () -> Guid.NewGuid().ToString().Substring(0, 8))

            // The first-class operation (spec 004 item 4): publishes the exact same manifest
            // properties FunctionalPlacementProvider below applied by name — verified identical
            // by a property-key exactness test against a live StatelessWorkerAttribute, not
            // assumed. Composition through an application IGrainPropertiesProvider (as this file
            // used to do for the whole feature) remains possible for cases this closed operation
            // set does not cover.
            statelessWorker WorkerApi.MaxLocalWorkers

            handle
                (_.work)
                (fun context state milliseconds ->
                    task {
                        let started = context.utcNow
                        do! Task.Delay milliseconds

                        return
                            state,
                            { activation = state
                              startedAt = started.ToString "HH:mm:ss.fff" }
                    })
        }

[<RequireQualifiedAccess>]
module WorkerRun =
    /// <summary>Fire <paramref name="count"/> concurrent calls and time the whole batch.</summary>
    let concurrentBatch (api: WorkerApi) (count: int) (milliseconds: int) =
        task {
            let clock = Stopwatch.StartNew()
            let! reports = [ for _ in 1..count -> api.work milliseconds ] |> Task.WhenAll
            clock.Stop()
            return reports, clock.ElapsedMilliseconds
        }
