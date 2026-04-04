module Orleans.FSharp.Tests.ApiParityTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.Runtime
open Orleans.FSharp
open Orleans.FSharp.Runtime
open Orleans.FSharp.Streaming

// ============================================================================
// GAP #1: Cluster identity config
// ============================================================================

[<Fact>]
let ``siloConfig CE sets clusterId`` () =
    let config = siloConfig { clusterId "my-cluster" }
    test <@ config.ClusterId = Some "my-cluster" @>

[<Fact>]
let ``siloConfig CE sets serviceId`` () =
    let config = siloConfig { serviceId "my-service" }
    test <@ config.ServiceId = Some "my-service" @>

[<Fact>]
let ``siloConfig CE sets siloName`` () =
    let config = siloConfig { siloName "silo-1" }
    test <@ config.SiloName = Some "silo-1" @>

[<Fact>]
let ``siloConfig CE sets all cluster identity fields together`` () =
    let config =
        siloConfig {
            clusterId "my-cluster"
            serviceId "my-service"
            siloName "silo-1"
        }

    test <@ config.ClusterId = Some "my-cluster" @>
    test <@ config.ServiceId = Some "my-service" @>
    test <@ config.SiloName = Some "silo-1" @>

[<Fact>]
let ``clientConfig CE sets clusterId`` () =
    let config = clientConfig { clusterId "my-cluster" }
    test <@ config.ClusterId = Some "my-cluster" @>

[<Fact>]
let ``clientConfig CE sets serviceId`` () =
    let config = clientConfig { serviceId "my-service" }
    test <@ config.ServiceId = Some "my-service" @>

[<Fact>]
let ``clientConfig CE sets both clusterId and serviceId`` () =
    let config =
        clientConfig {
            clusterId "my-cluster"
            serviceId "my-service"
        }

    test <@ config.ClusterId = Some "my-cluster" @>
    test <@ config.ServiceId = Some "my-service" @>

[<Fact>]
let ``siloConfig default has no cluster identity`` () =
    let config = SiloConfig.Default
    test <@ config.ClusterId.IsNone @>
    test <@ config.ServiceId.IsNone @>
    test <@ config.SiloName.IsNone @>

[<Fact>]
let ``clientConfig default has no cluster identity`` () =
    let config = ClientConfig.Default
    test <@ config.ClusterId.IsNone @>
    test <@ config.ServiceId.IsNone @>

// ============================================================================
// GAP #2: Endpoint config
// ============================================================================

[<Fact>]
let ``siloConfig CE sets siloPort`` () =
    let config = siloConfig { siloPort 11111 }
    test <@ config.SiloPort = Some 11111 @>

[<Fact>]
let ``siloConfig CE sets gatewayPort`` () =
    let config = siloConfig { gatewayPort 30000 }
    test <@ config.GatewayPort = Some 30000 @>

[<Fact>]
let ``siloConfig CE sets advertisedIpAddress`` () =
    let config = siloConfig { advertisedIpAddress "10.0.0.1" }
    test <@ config.AdvertisedIpAddress = Some "10.0.0.1" @>

[<Fact>]
let ``siloConfig CE sets all endpoint fields together`` () =
    let config =
        siloConfig {
            siloPort 11111
            gatewayPort 30000
            advertisedIpAddress "10.0.0.1"
        }

    test <@ config.SiloPort = Some 11111 @>
    test <@ config.GatewayPort = Some 30000 @>
    test <@ config.AdvertisedIpAddress = Some "10.0.0.1" @>

[<Fact>]
let ``siloConfig default has no endpoint config`` () =
    let config = SiloConfig.Default
    test <@ config.SiloPort.IsNone @>
    test <@ config.GatewayPort.IsNone @>
    test <@ config.AdvertisedIpAddress.IsNone @>

// ============================================================================
// GAP #3: Grain deactivation timeout
// ============================================================================

[<Fact>]
let ``siloConfig CE sets grainCollectionAge`` () =
    let config =
        siloConfig { grainCollectionAge (TimeSpan.FromMinutes 30.) }

    test <@ config.GrainCollectionAge = Some(TimeSpan.FromMinutes 30.) @>

[<Fact>]
let ``siloConfig default has no grainCollectionAge`` () =
    let config = SiloConfig.Default
    test <@ config.GrainCollectionAge.IsNone @>

[<Fact>]
let ``grain CE sets deactivationTimeout`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            deactivationTimeout (TimeSpan.FromMinutes 10.)
        }

    test <@ def.DeactivationTimeout = Some(TimeSpan.FromMinutes 10.) @>

[<Fact>]
let ``grain CE default has no deactivationTimeout`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.DeactivationTimeout.IsNone @>

// ============================================================================
// GAP #4: Grain identity access from handler
// ============================================================================

[<Fact>]
let ``GrainContext has GrainId field`` () =
    let gid = GrainId.Create("test/grain", "key-1")

    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = Some gid
            PrimaryKey = Some(box "key-1")
        }

    test <@ ctx.GrainId = Some gid @>

[<Fact>]
let ``GrainContext.grainId returns GrainId when available`` () =
    let gid = GrainId.Create("test/grain", "key-1")

    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = Some gid
            PrimaryKey = None
        }

    test <@ GrainContext.grainId ctx = gid @>

[<Fact>]
let ``GrainContext.grainId throws when None`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    raises<InvalidOperationException> <@ GrainContext.grainId ctx @>

[<Fact>]
let ``GrainContext.primaryKeyString returns string key`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = Some(box "my-key")
        }

    test <@ GrainContext.primaryKeyString ctx = "my-key" @>

[<Fact>]
let ``GrainContext.primaryKeyGuid returns Guid key`` () =
    let guid = Guid.Parse("12345678-1234-1234-1234-123456789abc")

    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = Some(box guid)
        }

    test <@ GrainContext.primaryKeyGuid ctx = guid @>

[<Fact>]
let ``GrainContext.primaryKeyInt64 returns int64 key`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = Some(box 42L)
        }

    test <@ GrainContext.primaryKeyInt64 ctx = 42L @>

[<Fact>]
let ``GrainContext.primaryKeyString throws when key is not string`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = Some(box 42L)
        }

    raises<InvalidOperationException> <@ GrainContext.primaryKeyString ctx @>

[<Fact>]
let ``GrainContext.primaryKeyGuid throws when key is not Guid`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = Some(box "not-a-guid")
        }

    raises<InvalidOperationException> <@ GrainContext.primaryKeyGuid ctx @>

[<Fact>]
let ``GrainContext.primaryKeyInt64 throws when None`` () =
    let ctx: GrainContext =
        {
            GrainFactory = Unchecked.defaultof<IGrainFactory>
            ServiceProvider = Unchecked.defaultof<IServiceProvider>
            States = Map.empty
            DeactivateOnIdle = None
            DelayDeactivation = None
            GrainId = None
            PrimaryKey = None
        }

    raises<InvalidOperationException> <@ GrainContext.primaryKeyInt64 ctx @>

[<Fact>]
let ``grain CE handleWithContext receives GrainId in context`` () =
    task {
        let gid = GrainId.Create("test/grain", "key-1")
        let mutable receivedGrainId = Unchecked.defaultof<GrainId>

        let def =
            grain {
                defaultState 0

                handleWithContext (fun ctx state (_msg: string) ->
                    task {
                        receivedGrainId <- GrainContext.grainId ctx
                        return state, box state
                    })
            }

        let ctx: GrainContext =
            {
                GrainFactory = Unchecked.defaultof<IGrainFactory>
                ServiceProvider = ServiceCollection().BuildServiceProvider()
                States = Map.empty
                DeactivateOnIdle = None
                DelayDeactivation = None
                GrainId = Some gid
                PrimaryKey = Some(box "key-1")
            }

        let handler = GrainDefinition.getContextHandler def
        let! _ = handler ctx 0 "test"
        test <@ receivedGrainId = gid @>
    }

// ============================================================================
// GAP #5: Call timeouts on grain references
// ============================================================================

/// <summary>Test grain interface for timeout tests.</summary>
type ISlowGrain =
    inherit IGrainWithStringKey
    abstract SlowMethod: unit -> Task<int>

/// <summary>Fake grain that delays for testing timeouts.</summary>
type FakeSlowGrain(delay: TimeSpan) =
    interface ISlowGrain with
        member _.SlowMethod() =
            task {
                do! Task.Delay(delay)
                return 42
            }

[<Fact>]
let ``GrainRef.invokeWithTimeout succeeds when call completes in time`` () =
    task {
        let ref: GrainRef<ISlowGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "fast"
                Grain = FakeSlowGrain(TimeSpan.FromMilliseconds(10.))
            }

        let! result =
            GrainRef.invokeWithTimeout ref (TimeSpan.FromSeconds 5.) (fun g -> g.SlowMethod())

        test <@ result = 42 @>
    }

[<Fact>]
let ``GrainRef.invokeWithTimeout throws TimeoutException when call exceeds timeout`` () =
    task {
        let ref: GrainRef<ISlowGrain, string> =
            {
                Factory = Unchecked.defaultof<IGrainFactory>
                Key = "slow"
                Grain = FakeSlowGrain(TimeSpan.FromSeconds(30.))
            }

        let! exn =
            Assert.ThrowsAsync<TimeoutException>(fun () ->
                GrainRef.invokeWithTimeout ref (TimeSpan.FromMilliseconds 50.) (fun g -> g.SlowMethod())
                :> Task)

        test <@ exn.Message.Contains("timed out") @>
    }

// ============================================================================
// GAP #6: Immutable<T> wrapper
// ============================================================================

[<Fact>]
let ``immutable wraps value`` () =
    let wrapped = immutable 42
    Assert.Equal(42, wrapped.Value)

[<Fact>]
let ``unwrapImmutable extracts value`` () =
    let wrapped = immutable "hello"
    let value = unwrapImmutable wrapped
    Assert.Equal("hello", value)

[<Fact>]
let ``immutable round-trips complex types`` () =
    let data = [1; 2; 3]
    let wrapped = immutable data
    let unwrapped = unwrapImmutable wrapped
    test <@ unwrapped = [1; 2; 3] @>

[<Fact>]
let ``Immutable type alias matches Orleans Concurrency Immutable`` () =
    let wrapped = immutable 42
    let typeName = wrapped.GetType().FullName
    test <@ typeName.Contains("Orleans.Concurrency.Immutable") @>

// ============================================================================
// GAP #7: Stream subscription management
// ============================================================================

[<Fact>]
let ``Stream.getSubscriptions function exists with correct signature`` () =
    // Verify the function is accessible with the expected type signature
    let _fn: StreamRef<int> -> Task<StreamSubscription<int> list> = Stream.getSubscriptions
    test <@ true @>

[<Fact>]
let ``Stream.resumeAll function exists with correct signature`` () =
    // Verify the function is accessible with the expected type signature
    let _fn: StreamRef<int> -> (int -> Task<unit>) -> Task<unit> = Stream.resumeAll
    test <@ true @>

// ============================================================================
// GAP #8: Client gateway config
// ============================================================================

[<Fact>]
let ``clientConfig CE sets gatewayListRefreshPeriod`` () =
    let config =
        clientConfig { gatewayListRefreshPeriod (TimeSpan.FromSeconds 30.) }

    test <@ config.GatewayListRefreshPeriod = Some(TimeSpan.FromSeconds 30.) @>

[<Fact>]
let ``clientConfig CE sets preferredGatewayIndex`` () =
    let config = clientConfig { preferredGatewayIndex 0 }
    test <@ config.PreferredGatewayIndex = Some 0 @>

[<Fact>]
let ``clientConfig CE sets both gateway options together`` () =
    let config =
        clientConfig {
            gatewayListRefreshPeriod (TimeSpan.FromSeconds 30.)
            preferredGatewayIndex 0
        }

    test <@ config.GatewayListRefreshPeriod = Some(TimeSpan.FromSeconds 30.) @>
    test <@ config.PreferredGatewayIndex = Some 0 @>

[<Fact>]
let ``clientConfig default has no gateway config`` () =
    let config = ClientConfig.Default
    test <@ config.GatewayListRefreshPeriod.IsNone @>
    test <@ config.PreferredGatewayIndex.IsNone @>

// ============================================================================
// GAP #9: Grain call context details in filters
// ============================================================================

[<Fact>]
let ``FilterContext module exists and has methodName function`` () =
    // Verify the FilterContext module is accessible
    let _fn : IIncomingGrainCallContext -> string = FilterContext.methodName
    test <@ true @>

[<Fact>]
let ``FilterContext module has interfaceType function`` () =
    let _fn : IIncomingGrainCallContext -> Type = FilterContext.interfaceType
    test <@ true @>

[<Fact>]
let ``FilterContext module has grainInstance function`` () =
    let _fn : IIncomingGrainCallContext -> obj option = FilterContext.grainInstance
    test <@ true @>

// ============================================================================
// GAP #10: GrainType attribute support
// ============================================================================

[<Fact>]
let ``grain CE sets grainType`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            grainType "my-custom-grain-type"
        }

    test <@ def.GrainTypeName = Some "my-custom-grain-type" @>

[<Fact>]
let ``grain CE default has no grainType`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
        }

    test <@ def.GrainTypeName.IsNone @>

[<Fact>]
let ``grain CE can combine grainType with other options`` () =
    let def =
        grain {
            defaultState 0
            handle (fun state _msg -> task { return state, box state })
            grainType "custom-type"
            reentrant
            persist "Default"
        }

    test <@ def.GrainTypeName = Some "custom-type" @>
    test <@ def.IsReentrant @>
    test <@ def.PersistenceName = Some "Default" @>

// ============================================================================
// Combined integration tests
// ============================================================================

[<Fact>]
let ``siloConfig CE combines cluster, endpoint, and collection options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            clusterId "prod-cluster"
            serviceId "my-svc"
            siloName "silo-0"
            siloPort 11111
            gatewayPort 30000
            advertisedIpAddress "192.168.1.100"
            grainCollectionAge (TimeSpan.FromMinutes 45.)
            addMemoryStorage "Default"
        }

    test <@ config.ClusterId = Some "prod-cluster" @>
    test <@ config.ServiceId = Some "my-svc" @>
    test <@ config.SiloName = Some "silo-0" @>
    test <@ config.SiloPort = Some 11111 @>
    test <@ config.GatewayPort = Some 30000 @>
    test <@ config.AdvertisedIpAddress = Some "192.168.1.100" @>
    test <@ config.GrainCollectionAge = Some(TimeSpan.FromMinutes 45.) @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>

[<Fact>]
let ``clientConfig CE combines cluster and gateway options`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            clusterId "prod-cluster"
            serviceId "my-svc"
            gatewayListRefreshPeriod (TimeSpan.FromSeconds 15.)
            preferredGatewayIndex 1
        }

    test <@ config.ClusterId = Some "prod-cluster" @>
    test <@ config.ServiceId = Some "my-svc" @>
    test <@ config.GatewayListRefreshPeriod = Some(TimeSpan.FromSeconds 15.) @>
    test <@ config.PreferredGatewayIndex = Some 1 @>

[<Fact>]
let ``grain CE combines deactivationTimeout and grainType`` () =
    let def =
        grain {
            defaultState "init"
            handle (fun state _msg -> task { return state, box state })
            deactivationTimeout (TimeSpan.FromMinutes 20.)
            grainType "my-grain"
        }

    test <@ def.DeactivationTimeout = Some(TimeSpan.FromMinutes 20.) @>
    test <@ def.GrainTypeName = Some "my-grain" @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``deactivationTimeout stores any positive TimeSpan for grain definition`` (seconds: PositiveInt) =
    let timeout = TimeSpan.FromSeconds(float seconds.Get)

    let def =
        grain {
            defaultState 0
            handle (fun state (_msg: string) -> task { return state, box state })
            deactivationTimeout timeout
        }

    def.DeactivationTimeout = Some timeout

[<Property>]
let ``grain CE deactivationTimeout and grainType do not disturb DefaultState for any initial int`` (initial: int) =
    let def =
        grain {
            defaultState initial
            handle (fun state (_msg: string) -> task { return state, box state })
            deactivationTimeout (TimeSpan.FromMinutes 5.)
            grainType "my-grain"
        }

    def.DefaultState = Some initial && def.GrainTypeName = Some "my-grain"
