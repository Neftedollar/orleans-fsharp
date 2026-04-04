namespace Orleans.FSharp.Integration

open System
open System.Threading.Tasks
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.TestingHost
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Sample

// ---------------------------------------------------------------------------
// RequiresRedis — custom FactAttribute that skips if env var is absent
// ---------------------------------------------------------------------------

/// <summary>
/// Marks a test as requiring a running Redis instance.
/// If <c>ORLEANS_FSHARP_REDIS</c> is not set the test is reported as Skipped.
/// Start Redis: <c>docker run -d -p 6379:6379 redis:7-alpine</c>
/// Then set <c>ORLEANS_FSHARP_REDIS=localhost:6379</c>.
/// </summary>
type RequiresRedisAttribute() =
    inherit FactAttribute()
    do
        if String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORLEANS_FSHARP_REDIS")) then
            base.Skip <-
                "Set ORLEANS_FSHARP_REDIS env var (e.g., localhost:6379) to enable Redis tests. "
                + "Start Redis with: docker run -d -p 6379:6379 redis:7-alpine"

// ---------------------------------------------------------------------------
// RedisClusterFixture — TestCluster with Redis storage for "Default" provider
// ---------------------------------------------------------------------------

/// <summary>
/// Silo configurator that wires a Redis grain storage provider named "Default".
/// Uses the typed grain path (<c>OrderGrainImpl</c> from the Sample project /
/// CodeGen assembly), which calls <c>IPersistentState.WriteStateAsync</c> on
/// every message — so state is durably stored in Redis after each command.
/// </summary>
type RedisSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            let connStr =
                Environment.GetEnvironmentVariable("ORLEANS_FSHARP_REDIS")
                |> Option.ofObj
                |> Option.defaultValue "localhost:6379"

            siloBuilder.AddRedisGrainStorage(
                "Default",
                fun (opts: Orleans.Persistence.RedisStorageOptions) ->
                    opts.ConfigurationOptions <-
                        StackExchange.Redis.ConfigurationOptions.Parse(connStr))
            |> ignore

/// <summary>
/// xUnit fixture that starts a TestCluster backed by real Redis grain storage.
/// The <c>IOrderGrain</c> (from <c>Orleans.FSharp.Sample</c> via <c>Orleans.FSharp.CodeGen</c>)
/// persists state to Redis on every handled command via its <c>IPersistentState</c> slot.
/// <para>
/// When <c>ORLEANS_FSHARP_REDIS</c> is not set, no cluster is started and all
/// tests in the collection are skipped, so the CI suite never requires Redis.
/// </para>
/// </summary>
type RedisClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    let redisAvailable =
        not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORLEANS_FSHARP_REDIS")))

    /// <summary>Gets the GrainFactory for creating grain references, or <c>null</c> if Redis is unavailable.</summary>
    member _.GrainFactory = if redisAvailable then cluster.GrainFactory else null

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                if redisAvailable then
                    // Load CodeGen and Abstractions assemblies so Orleans discovers grain proxies.
                    let _ = typeof<Orleans.FSharp.CodeGen.CodeGenAssemblyMarker>.Assembly.GetTypes()
                    let _ = typeof<Orleans.FSharp.IFSharpGrain>.Assembly.GetTypes()

                    let builder = TestClusterBuilder()
                    builder.Options.InitialSilosCount <- 1s
                    builder.AddSiloBuilderConfigurator<RedisSiloConfigurator>() |> ignore
                    cluster <- builder.Build()
                    do! cluster.DeployAsync()
            }

        member _.DisposeAsync() =
            task {
                if redisAvailable && not (isNull (box cluster)) then
                    do! cluster.StopAllSilosAsync()
                    cluster.Dispose()
            }

/// <summary>xUnit collection definition for Redis integration tests.</summary>
[<CollectionDefinition("RedisCollection")>]
type RedisCollection() =
    interface ICollectionFixture<RedisClusterFixture>

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

/// <summary>
/// Integration tests that verify Orleans.FSharp grains persist state to and
/// reload it from a live Redis instance using the typed grain path
/// (<c>OrderGrainImpl</c> → <c>IPersistentState</c> → Redis).
///
/// Run with Redis active:
/// <code>
/// docker run -d -p 6379:6379 redis:7-alpine
/// ORLEANS_FSHARP_REDIS=localhost:6379 dotnet test --filter RedisStorage
/// </code>
/// </summary>
[<Collection("RedisCollection")>]
type RedisStorageTests(fixture: RedisClusterFixture) =

    let key () = Guid.NewGuid().ToString("N")

    [<RequiresRedis>]
    member _.``order grain starts Idle with Redis storage`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(key())
            let! result = grain.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> result = Idle @>
        }

    [<RequiresRedis>]
    member _.``Place command persists Processing state to Redis`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(key())
            let! result = grain.HandleMessage(Place "Widget order")
            test <@ unbox<OrderStatus> result = Processing "Widget order" @>
        }

    [<RequiresRedis>]
    member _.``Place then Confirm persists Completed state to Redis`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(key())
            let! _ = grain.HandleMessage(Place "My order")
            let! result = grain.HandleMessage(Confirm)
            test <@ unbox<OrderStatus> result = Completed "Order confirmed: My order" @>
        }

    [<RequiresRedis>]
    member _.``two distinct grain keys have independent state in Redis`` () =
        task {
            let g1 = fixture.GrainFactory.GetGrain<IOrderGrain>(key())
            let g2 = fixture.GrainFactory.GetGrain<IOrderGrain>(key())

            let! _ = g1.HandleMessage(Place "Order A")
            let! _ = g2.HandleMessage(Place "Order B")
            let! _ = g2.HandleMessage(Confirm)

            let! s1 = g1.HandleMessage(GetStatus)
            let! s2 = g2.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> s1 = Processing "Order A" @>
            test <@ unbox<OrderStatus> s2 = Completed "Order confirmed: Order B" @>
        }

    [<RequiresRedis>]
    member _.``invalid transition returns error from Redis-backed grain`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(key())
            let! result = grain.HandleMessage(Ship)
            let msg = unbox<string> result
            test <@ msg = "Cannot ship: no order is being processed" @>
        }

    [<RequiresRedis>]
    member _.``failed order can be retried with Redis storage`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(key())
            let! _ = grain.HandleMessage(Place "First attempt")
            let! _ = grain.HandleMessage(Cancel)
            let! result = grain.HandleMessage(Place "Second attempt")
            test <@ unbox<OrderStatus> result = Processing "Second attempt" @>
        }

    [<RequiresRedis>]
    member _.``Completed state survives grain deactivation and reloads from Redis`` () =
        task {
            let k = key()
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(k)

            // Write Completed state to Redis
            let! _ = grain.HandleMessage(Place "Persistent Order")
            let! r = grain.HandleMessage(Confirm)
            test <@ unbox<OrderStatus> r = Completed "Order confirmed: Persistent Order" @>

            // Deactivate all grain activations in the silo
            let mgmt = fixture.GrainFactory.GetGrain<IManagementGrain>(0)
            do! mgmt.ForceActivationCollection(TimeSpan.Zero)
            do! Task.Delay(1200)

            // Fresh activation loads state from Redis
            let! reloaded = grain.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> reloaded = Completed "Order confirmed: Persistent Order" @>
        }

    [<RequiresRedis>]
    member _.``Failed state survives grain deactivation and reloads from Redis`` () =
        task {
            let k = key()
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(k)

            let! _ = grain.HandleMessage(Place "Doomed order")
            let! _ = grain.HandleMessage(Cancel)

            let mgmt = fixture.GrainFactory.GetGrain<IManagementGrain>(0)
            do! mgmt.ForceActivationCollection(TimeSpan.Zero)
            do! Task.Delay(1200)

            let! reloaded = grain.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> reloaded = Failed "Cancelled by user" @>
        }

    [<RequiresRedis>]
    member _.``full lifecycle persists correctly via Redis across multiple commands`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>(key())

            let! r1 = grain.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> r1 = Idle @>

            let! r2 = grain.HandleMessage(Place "Lifecycle Order")
            test <@ unbox<OrderStatus> r2 = Processing "Lifecycle Order" @>

            let! r3 = grain.HandleMessage(Confirm)
            test <@ unbox<OrderStatus> r3 = Completed "Order confirmed: Lifecycle Order" @>

            let! r4 = grain.HandleMessage(GetStatus)
            test <@ unbox<OrderStatus> r4 = Completed "Order confirmed: Lifecycle Order" @>
        }
