/// <summary>
/// A second, deliberately small Phase F cluster whose only job is the abandoned-enumerator story.
/// </summary>
/// <remarks>
/// <para>
/// Expiry is driven by <c>MessagingOptions.ResponseTimeout</c>: Orleans'
/// <c>AsyncEnumerableGrainExtension</c> registers an interleaving, non-keep-alive grain timer with
/// <c>DueTime = Period = ResponseTimeout</c>, clears a per-enumerator "seen" flag on every tick, and
/// removes any enumerator that was not touched since the previous one — so an abandoned enumerator
/// is collected after one to two periods. At the 30-second default that is a minute-long test.
/// </para>
/// <para>
/// Only the silo's timeout is shortened: it controls the extension's cleanup period. The client
/// keeps a normal RPC budget so delayed scheduling cannot replace the missing-enumerator reply
/// with a client timeout. This single-silo fixture hosts one grain type and makes no cross-grain
/// calls. Tests observe producer cleanup directly instead of guessing when timer ticks ran.
/// </para>
/// </remarks>
module Orleans.FSharp.Integration.FunctionalPhaseFExpiryFixture

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp
open Xunit

/// <summary>The shortened silo response timeout, and therefore the enumerator cleanup period.</summary>
let expiryPeriod = TimeSpan.FromSeconds 3.0

let private rpcTimeout = TimeSpan.FromSeconds 30.0

/// <summary>Out-of-band observation; TestCluster's silo and its test share one process.</summary>
[<RequireQualifiedAccess>]
module private ExpiryProbe =
    let private completions = ConcurrentDictionary<string, TaskCompletionSource<bool>>()

    let private cell key =
        completions.GetOrAdd(key, fun _ -> TaskCompletionSource<bool> TaskCreationOptions.RunContinuationsAsynchronously)

    let record key cancelled = (cell key).TrySetResult cancelled |> ignore

    let wait key = (cell key).Task.WaitAsync(TimeSpan.FromSeconds 45.0)

[<Literal>]
let ExpiringGrainType = "phasef.expiring"

[<NoEquality; NoComparison>]
type ExpiringApi =
    { /// Yields one item immediately and then parks until the enumeration is cancelled.
      once: unit -> IAsyncEnumerable<int>
      /// An ordinary call with a deliberate reply delay, independent of stream expiry.
      ping: int -> Task<int> }

type ExpiringActor = private ExpiringActor of unit

let expiringContract =
    grainContract<ExpiringActor, string, ExpiringApi> {
        grainType ExpiringGrainType
        version 1
        stringKey
        readOnly (_.ping)
    }

let expiringRef = FunctionalGrain.ref expiringContract

let expiringDefinition =
    grainFor expiringContract {
        defaultState (fun () -> ())

        handleStream (_.once) (fun context _ () ->
            taskSeq {
                try
                    yield 1
                    // Parks forever; the enumeration's own token is what releases it, which is exactly
                    // what the extension cancels when it collects an abandoned enumerator.
                    do! Task.Delay(Timeout.InfiniteTimeSpan, context.cancellationToken)
                    yield 2
                finally
                    ExpiryProbe.record context.key context.cancellationToken.IsCancellationRequested
            })

        handle (_.ping) (fun _ state (replyDelay: int) ->
            task {
                do! Task.Delay replyDelay
                return state, 42
            })
    }

type PhaseFExpirySiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.Services.Configure<SiloMessagingOptions>(fun (options: SiloMessagingOptions) ->
                options.ResponseTimeout <- expiryPeriod)
            |> ignore

            siloBuilder.AddFunctionalGrain expiringDefinition |> ignore

type PhaseFExpiryClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration: IConfiguration, clientBuilder: IClientBuilder) =
            clientBuilder.Services.Configure<ClientMessagingOptions>(fun (options: ClientMessagingOptions) ->
                options.ResponseTimeout <- rpcTimeout)
            |> ignore

            clientBuilder.AddFunctionalGrainClient() |> ignore

[<Sealed>]
type FunctionalPhaseFExpiryFixture() =
    let cluster =
        let builder = TestClusterBuilder 1s
        builder.AddSiloBuilderConfigurator<PhaseFExpirySiloConfigurator>() |> ignore
        builder.AddClientBuilderConfigurator<PhaseFExpiryClientConfigurator>() |> ignore
        let cluster = builder.Build()
        cluster.Deploy()
        cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()
        cluster

    member _.Client = cluster.Client

    interface IDisposable with
        member _.Dispose() =
            cluster.StopAllSilos()
            cluster.Dispose()

[<CollectionDefinition("FunctionalPhaseFExpiry")>]
type FunctionalPhaseFExpiryCollection() =
    interface ICollectionFixture<FunctionalPhaseFExpiryFixture>

[<Collection("FunctionalPhaseFExpiry")>]
type FunctionalPhaseFExpiryTests(fixture: FunctionalPhaseFExpiryFixture) =

    [<Fact>]
    member _.``stream expiry does not shorten the client RPC budget``() =
        task {
            let api = expiringRef fixture.Client (Guid.NewGuid().ToString "N")
            let! pinged = api.ping 4000
            Assert.Equal(42, pinged)
        }

    /// <summary>
    /// A caller that takes one item and then walks away leaves an enumerator behind on the target.
    /// Orleans collects it, and a caller that comes back afterwards is told so by name rather than
    /// silently resuming a stream that no longer has a producer.
    /// </summary>
    [<Fact>]
    member _.``an abandoned enumerator expires and the caller is told``() =
        task {
            let key = Guid.NewGuid().ToString "N"
            let api = expiringRef fixture.Client key

            use enumerator = (api.once ()).GetAsyncEnumerator CancellationToken.None

            let! first = enumerator.MoveNextAsync()
            Assert.True first
            Assert.Equal(1, enumerator.Current)

            // Stop asking, then observe the producer's cancellation/finally out of band. Orleans
            // removes the table entry BEFORE cancelling/disposing the producer, so this signal
            // proves cleanup has actually run without touching the enumerator's "seen" flag.
            let! cancelled = ExpiryProbe.wait key
            Assert.True(cancelled, "the abandoned producer ended without Orleans cancelling it")

            let! failure = Assert.ThrowsAnyAsync<Exception>(fun () -> enumerator.MoveNextAsync().AsTask() :> Task)

            // Orleans' own diagnosis, unchanged: this runtime adds nothing to it, because the
            // enumerator table and its expiry are entirely Orleans'.
            Assert.Contains("does not have a record of this enumerator", failure.Message)

            // The activation is unharmed: an expired enumerator is a per-enumeration fact.
            let! pinged = api.ping 0
            Assert.Equal(42, pinged)
        }
