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
/// The timeout is shortened here rather than on the main Phase F cluster on purpose: a three-second
/// response timeout is a real constraint on every call the silo makes, and the whole suite's
/// clusters are deployed in parallel by xUnit. Confining it to one single-silo cluster that hosts
/// one grain type keeps it from turning unrelated tests flaky.
/// </para>
/// </remarks>
module Orleans.FSharp.Integration.FunctionalPhaseFExpiryFixture

open System
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

/// <summary>The shortened response timeout, and therefore the enumerator cleanup period.</summary>
let expiryPeriod = TimeSpan.FromSeconds 3.0

[<Literal>]
let ExpiringGrainType = "phasef.expiring"

[<NoEquality; NoComparison>]
type ExpiringApi =
    { /// Yields one item immediately and then parks until the enumeration is cancelled.
      once: unit -> IAsyncEnumerable<int>
      /// An ordinary call, so the test can prove the activation is still healthy afterwards.
      ping: unit -> Task<int> }

type ExpiringActor = private ExpiringActor of unit

let expiringContract =
    grainContract<ExpiringActor, string, ExpiringApi> () {
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
                yield 1
                // Parks forever; the enumeration's own token is what releases it, which is exactly
                // what the extension cancels when it collects an abandoned enumerator.
                do! Task.Delay(Timeout.InfiniteTimeSpan, context.cancellationToken)
                yield 2
            })

        handle (_.ping) (fun _ state () -> task { return state, 42 })
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
                options.ResponseTimeout <- expiryPeriod)
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

            let enumerator = (api.once ()).GetAsyncEnumerator CancellationToken.None

            let! first = enumerator.MoveNextAsync()
            Assert.True first
            Assert.Equal(1, enumerator.Current)

            // Stop asking. Every tick of the extension's cleanup timer clears the "seen" flag, and
            // the tick after that removes an enumerator nobody touched, so two periods plus a
            // margin is the wait.
            do! Task.Delay(expiryPeriod + expiryPeriod + TimeSpan.FromSeconds 3.0)

            let! failure = Assert.ThrowsAnyAsync<Exception>(fun () -> enumerator.MoveNextAsync().AsTask() :> Task)

            // Orleans' own diagnosis, unchanged: this runtime adds nothing to it, because the
            // enumerator table and its expiry are entirely Orleans'.
            Assert.Contains("does not have a record of this enumerator", failure.Message)

            do! enumerator.DisposeAsync()

            // The activation is unharmed: an expired enumerator is a per-enumeration fact.
            let! pinged = api.ping ()
            Assert.Equal(42, pinged)
        }
