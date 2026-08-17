/// <summary>
/// Spec 003 Phase 4 durability: explicitly written functional state survives the loss of the
/// silo which hosted the activation, and survives a complete cluster restart when the storage
/// itself is retained.
/// </summary>
/// <remarks>
/// <para>
/// Placement of a given grain identity is stable in a running cluster, so recycling an
/// activation is not a way to reach another silo. These tests therefore remove the hosting silo
/// instead, which is both deterministic and a stronger statement: the state comes back on a
/// silo which never saw the write.
/// </para>
/// <para>
/// The non-Redis arm uses a process-wide storage provider so the data outlives any silo. Stock
/// memory storage keeps its data in ordinary grains, which would die with the silo and prove
/// nothing about the functional runtime.
/// </para>
/// </remarks>
module Orleans.FSharp.Integration.FunctionalStateRestartTests

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.Storage
open Orleans.TestingHost
open Orleans.FSharp
open Orleans.FSharp.Integration.FunctionalStateFixture
open Xunit

// ──────────────────────────────────────────────────────────────────────────────
// A storage provider whose data outlives every silo of the test process
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>The process-wide records of the retained storage provider.</summary>
[<RequireQualifiedAccess>]
module RetainedStore =
    let records = ConcurrentDictionary<string, obj * string>()

    let key (provider: string) (stateName: string) (grainId: GrainId) = $"{provider}/{stateName}/{grainId}"

/// <summary>
/// A minimal <c>IGrainStorage</c> backed by a process-wide table. It keeps ordinary Orleans
/// ETag and record-existence semantics and is deliberately independent of any silo, so a silo
/// can be stopped without taking the durable record with it.
/// </summary>
[<Sealed>]
type RetainedGrainStorage(provider: string) =

    interface IGrainStorage with
        member _.ReadStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            match RetainedStore.records.TryGetValue(RetainedStore.key provider stateName grainId) with
            | true, (state, etag) ->
                grainState.State <- unbox<'T> state
                grainState.ETag <- etag
                grainState.RecordExists <- true
            | _ -> grainState.RecordExists <- false

            Task.CompletedTask

        member _.WriteStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            let etag = Guid.NewGuid().ToString "N"
            RetainedStore.records.[RetainedStore.key provider stateName grainId] <- (box grainState.State, etag)
            grainState.ETag <- etag
            grainState.RecordExists <- true
            Task.CompletedTask

        member _.ClearStateAsync<'T>(stateName: string, grainId: GrainId, grainState: IGrainState<'T>) =
            RetainedStore.records.TryRemove(RetainedStore.key provider stateName grainId) |> ignore
            grainState.ETag <- null
            grainState.RecordExists <- false
            Task.CompletedTask

type RetainedStorageSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            let retain (name: string) =
                siloBuilder.Services.AddKeyedSingleton<IGrainStorage>(
                    name,
                    Func<IServiceProvider, obj, IGrainStorage>(fun _ _ -> RetainedGrainStorage name :> IGrainStorage)
                )
                |> ignore

            retain FunctionalStateProviders.Ledger
            retain FunctionalStateProviders.Audit

            siloBuilder.AddFunctionalGrain ledgerDefinition |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Tests
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``explicitly written state reloads on a silo which never saw the write`` () =
    let builder = TestClusterBuilder 2s
    builder.AddSiloBuilderConfigurator<RetainedStorageSiloConfigurator>() |> ignore
    builder.AddClientBuilderConfigurator<FunctionalStateClientConfigurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster.WaitForLivenessToStabilizeAsync().GetAwaiter().GetResult()

    task {
        try
            let name = $"restart-{Guid.NewGuid():N}"
            let api = ledgerRef cluster.Client name

            do! api.writeNow "durable"
            let! before = api.snapshot ()
            Assert.Equal("v1:[activated,durable]", before)

            let! hostingSilo = api.whereAmI ()

            let handle =
                cluster.Silos
                |> Seq.find (fun silo -> string silo.SiloAddress = hostingSilo)

            // Remove the silo which owns the activation. Its in-memory state is gone with it;
            // only the explicitly written record remains.
            do! cluster.StopSiloAsync handle
            do! cluster.WaitForLivenessToStabilizeAsync()

            let! after = api.snapshot ()
            let! newSilo = api.whereAmI ()

            Assert.NotEqual<string>(hostingSilo, newSilo)
            // The new activation loaded the written record and ran its own activation hook.
            Assert.Equal("v1:[activated,durable,activated]", after)
        finally
            cluster.StopAllSilos()
            cluster.Dispose()
    }

// ──────────────────────────────────────────────────────────────────────────────
// Redis-gated full-cluster restart
// ──────────────────────────────────────────────────────────────────────────────
// The Redis gate itself is `RequiresRedisAttribute` from RedisIntegrationTests.fs (same
// namespace, compiled first): one gate, one env var (ORLEANS_FSHARP_REDIS), one skip text. A
// second near-identical attribute type used to live here (Task-6 close-out 6).

type RedisFunctionalSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            let connection =
                Environment.GetEnvironmentVariable "ORLEANS_FSHARP_REDIS"
                |> Option.ofObj
                |> Option.defaultValue "localhost:6379"

            let addRedis (name: string) =
                siloBuilder.AddRedisGrainStorage(
                    name,
                    fun (options: Orleans.Persistence.RedisStorageOptions) ->
                        options.ConfigurationOptions <- StackExchange.Redis.ConfigurationOptions.Parse connection
                )
                |> ignore

            addRedis FunctionalStateProviders.Ledger
            addRedis FunctionalStateProviders.Audit

            siloBuilder.AddFunctionalGrain ledgerDefinition |> ignore

/// <remarks>
/// Both deployments share one <c>ServiceId</c> and <c>ClusterId</c>: Orleans scopes durable
/// storage keys by service identity, so a freshly generated one would address a different key
/// space and the test would pass or fail for the wrong reason.
/// </remarks>
let private deployRedisCluster (serviceId: string) =
    let builder = TestClusterBuilder 1s
    builder.Options.ServiceId <- serviceId
    builder.Options.ClusterId <- serviceId
    builder.AddSiloBuilderConfigurator<RedisFunctionalSiloConfigurator>() |> ignore
    builder.AddClientBuilderConfigurator<FunctionalStateClientConfigurator>() |> ignore
    let cluster = builder.Build()
    cluster.Deploy()
    cluster

/// <remarks>
/// The spec's Redis durability job in test form: stop and recreate the silo process while
/// retaining Redis, then verify that the same <c>GrainId</c> reloads its committed state. It
/// also asserts the negative half — an in-memory change which was never written does not come
/// back — so a provider which silently wrote everything would fail this test too.
/// </remarks>
[<RequiresRedis>]
let ``functional state committed to Redis reloads after the cluster is recreated`` () =
    task {
        let name = $"redis-{Guid.NewGuid():N}"
        let serviceId = $"fn-redis-{Guid.NewGuid():N}"
        let first = deployRedisCluster serviceId

        try
            let api = ledgerRef first.Client name
            do! api.writeNow "committed-to-redis"
            do! api.auditWrite 11

            // Not written: it must NOT survive the restart.
            let! _ = api.append "never-written"
            let! before = api.snapshot ()
            Assert.Equal("v2:[activated,committed-to-redis,never-written]", before)
        finally
            first.StopAllSilos()
            first.Dispose()

        let second = deployRedisCluster serviceId

        try
            let api = ledgerRef second.Client name
            let! reloaded = api.snapshot ()
            let! audit = api.auditPeek ()

            Assert.Equal("v1:[activated,committed-to-redis,activated]", reloaded)
            Assert.Equal(11, audit)
        finally
            second.StopAllSilos()
            second.Dispose()
    }
