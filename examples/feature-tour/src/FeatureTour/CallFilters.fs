/// <summary>
/// Feature 3 — grain call filters over the functional transport: an
/// <c>IIncomingGrainCallFilter</c> that reads <c>IFunctionalRequestMetadata</c>, logs every
/// functional call, and rejects one designated operation so the rejection surfaces to the caller.
/// </summary>
namespace FeatureTour.CallFilters

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

/// <summary>One line of what the filter saw, kept for the driver to print.</summary>
type ObservedCall =
    { grainType: string
      operationId: string
      contractVersion: int
      isReadOnly: bool
      isOneWay: bool
      isAlwaysInterleave: bool
      payloadLength: int
      rejected: bool }

/// <summary>Filter observations, collected in-process for the transcript.</summary>
[<RequireQualifiedAccess>]
module FilterLog =
    let private observed = ConcurrentQueue<ObservedCall>()

    /// <summary>Record one observation.</summary>
    let add (call: ObservedCall) = observed.Enqueue call

    /// <summary>Everything observed so far, in arrival order.</summary>
    let all () = observed |> List.ofSeq

    /// <summary>Observations for one grain type only.</summary>
    let forGrainType (grainType: string) =
        all () |> List.filter (fun call -> call.grainType = grainType)

/// <summary>
/// The tour's incoming filter.
/// </summary>
/// <remarks>
/// <para>
/// Note how a functional request is recognised. The envelope type itself
/// (<c>FunctionalRequest</c>) is <c>internal</c> to <c>Orleans.FSharp.Abstractions</c>, so
/// application code cannot type-test it the way the library's own integration fixture does.
/// The supported surface is <see cref="IFunctionalRequestMetadata"/>, published as argument 0 of
/// every functional request — so the type test goes on the ARGUMENT, not on the request.
/// </para>
/// <para>
/// Everything else on the call context (<c>InterfaceMethod</c>, <c>Grain</c>, <c>Invoke</c>) is
/// stock Orleans and behaves exactly as it does for a generated grain interface.
/// </para>
/// </remarks>
[<Sealed>]
type TourIncomingFilter(rejectedOperation: string) =

    /// <summary>The functional metadata of this call, when it is a functional call at all.</summary>
    static member TryMetadata(context: IIncomingGrainCallContext) : IFunctionalRequestMetadata option =
        let request = context.Request

        if isNull (box request) || request.GetArgumentCount() = 0 then
            None
        else
            match request.GetArgument 0 with
            | :? IFunctionalRequestMetadata as metadata -> Some metadata
            | _ -> None

    interface IIncomingGrainCallFilter with
        member _.Invoke(context: IIncomingGrainCallContext) =
            task {
                match TourIncomingFilter.TryMetadata context with
                | None ->
                    // Not a functional call (Orleans' own system grains use this pipeline too).
                    do! context.Invoke()
                | Some metadata ->
                    let rejected =
                        String.Equals(metadata.OperationId, rejectedOperation, StringComparison.Ordinal)

                    FilterLog.add
                        { grainType = metadata.GrainType
                          operationId = metadata.OperationId
                          contractVersion = metadata.ContractVersion
                          isReadOnly = metadata.IsReadOnly
                          isOneWay = metadata.IsOneWay
                          isAlwaysInterleave = metadata.IsAlwaysInterleave
                          payloadLength = metadata.PayloadLength
                          rejected = rejected }

                    if rejected then
                        raise (
                            InvalidOperationException
                                $"filter rejected operation '{metadata.OperationId}' on grain type '{metadata.GrainType}'"
                        )

                    do! context.Invoke()
            }
            :> Task

type GatewayActor = private GatewayActor of unit

[<NoEquality; NoComparison>]
type GatewayApi =
    { /// Passes the filter and reaches the handler.
      allowed: string -> Task<string>
      /// The designated operation the filter rejects before any handler runs.
      forbidden: string -> Task<string>
      /// Declared readOnly + alwaysInterleave purely so the filter can show the flags.
      peek: unit -> Task<string> }

[<RequireQualifiedAccess>]
module GatewayApi =
    let contract =
        grainContract<GatewayActor, string, GatewayApi> () {
            grainType "tour.gateway"
            version 1
            stringKey

            readOnly (_.peek)
            alwaysInterleave (_.peek)
        }

    let ref = FunctionalGrain.ref contract

    /// <summary>The operation the tour's filter is configured to reject.</summary>
    [<Literal>]
    let RejectedOperation = "forbidden"

[<RequireQualifiedAccess>]
module GatewayDefinition =
    let definition =
        grainFor GatewayApi.contract {
            defaultState (fun () -> 0)

            handle (_.allowed) (fun _context state text -> task { return state + 1, $"handler ran: {text}" })

            handle
                (_.forbidden)
                (fun _context state text ->
                    task {
                        // Never reached: the filter raises before context.Invoke().
                        return state + 1, $"handler ran (unexpectedly): {text}"
                    })

            handle (_.peek) (fun _context state () -> task { return state, $"handler calls so far: {state}" })
        }
