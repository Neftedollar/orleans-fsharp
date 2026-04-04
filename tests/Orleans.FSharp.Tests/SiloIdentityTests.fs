module Orleans.FSharp.Tests.SiloIdentityTests

open System
open System.Net
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Runtime

/// <summary>Tests for silo identity CE keywords: clusterId, serviceId, siloName, siloPort, gatewayPort,
/// advertisedIpAddress, and grainCollectionAge.</summary>

// ---------------------------------------------------------------------------
// clusterId
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE default has no clusterId`` () =
    let config = siloConfig { () }
    test <@ config.ClusterId.IsNone @>

[<Fact>]
let ``siloConfig CE sets clusterId`` () =
    let config = siloConfig { clusterId "prod-cluster" }
    test <@ config.ClusterId = Some "prod-cluster" @>

[<Fact>]
let ``siloConfig CE later clusterId overrides earlier`` () =
    let config = siloConfig { clusterId "first"; clusterId "second" }
    test <@ config.ClusterId = Some "second" @>

// ---------------------------------------------------------------------------
// serviceId
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE default has no serviceId`` () =
    let config = siloConfig { () }
    test <@ config.ServiceId.IsNone @>

[<Fact>]
let ``siloConfig CE sets serviceId`` () =
    let config = siloConfig { serviceId "my-service" }
    test <@ config.ServiceId = Some "my-service" @>

[<Fact>]
let ``siloConfig CE later serviceId overrides earlier`` () =
    let config = siloConfig { serviceId "v1"; serviceId "v2" }
    test <@ config.ServiceId = Some "v2" @>

// ---------------------------------------------------------------------------
// siloName
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE default has no siloName`` () =
    let config = siloConfig { () }
    test <@ config.SiloName.IsNone @>

[<Fact>]
let ``siloConfig CE sets siloName`` () =
    let config = siloConfig { siloName "silo-1" }
    test <@ config.SiloName = Some "silo-1" @>

// ---------------------------------------------------------------------------
// siloPort / gatewayPort
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE default has no siloPort`` () =
    let config = siloConfig { () }
    test <@ config.SiloPort.IsNone @>

[<Fact>]
let ``siloConfig CE sets siloPort`` () =
    let config = siloConfig { siloPort 11111 }
    test <@ config.SiloPort = Some 11111 @>

[<Fact>]
let ``siloConfig CE default has no gatewayPort`` () =
    let config = siloConfig { () }
    test <@ config.GatewayPort.IsNone @>

[<Fact>]
let ``siloConfig CE sets gatewayPort`` () =
    let config = siloConfig { gatewayPort 30000 }
    test <@ config.GatewayPort = Some 30000 @>

[<Fact>]
let ``siloConfig CE siloPort and gatewayPort compose`` () =
    let config = siloConfig { siloPort 11111; gatewayPort 30000 }
    test <@ config.SiloPort = Some 11111 @>
    test <@ config.GatewayPort = Some 30000 @>

// ---------------------------------------------------------------------------
// advertisedIpAddress
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE default has no advertisedIpAddress`` () =
    let config = siloConfig { () }
    test <@ config.AdvertisedIpAddress.IsNone @>

[<Fact>]
let ``siloConfig CE sets advertisedIpAddress`` () =
    let config = siloConfig { advertisedIpAddress "10.0.0.5" }
    test <@ config.AdvertisedIpAddress = Some "10.0.0.5" @>

// ---------------------------------------------------------------------------
// grainCollectionAge
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig CE default has no grainCollectionAge`` () =
    let config = siloConfig { () }
    test <@ config.GrainCollectionAge.IsNone @>

[<Fact>]
let ``siloConfig CE sets grainCollectionAge`` () =
    let timeout = TimeSpan.FromMinutes 15.
    let config = siloConfig { grainCollectionAge timeout }
    test <@ config.GrainCollectionAge = Some timeout @>

[<Fact>]
let ``siloConfig CE composes all identity options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            clusterId "cluster-1"
            serviceId "service-1"
            siloName "silo-a"
            siloPort 11111
            gatewayPort 30000
            advertisedIpAddress "192.168.1.10"
            grainCollectionAge (TimeSpan.FromMinutes 10.)
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusterId = Some "cluster-1" @>
    test <@ config.ServiceId = Some "service-1" @>
    test <@ config.SiloName = Some "silo-a" @>
    test <@ config.SiloPort = Some 11111 @>
    test <@ config.GatewayPort = Some 30000 @>
    test <@ config.AdvertisedIpAddress = Some "192.168.1.10" @>
    test <@ config.GrainCollectionAge = Some(TimeSpan.FromMinutes 10.) @>

[<Fact>]
let ``SiloConfig.Default has no identity fields set`` () =
    let config = SiloConfig.Default
    test <@ config.ClusterId.IsNone @>
    test <@ config.ServiceId.IsNone @>
    test <@ config.SiloName.IsNone @>
    test <@ config.SiloPort.IsNone @>
    test <@ config.GatewayPort.IsNone @>
    test <@ config.AdvertisedIpAddress.IsNone @>
    test <@ config.GrainCollectionAge.IsNone @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``clusterId stores any non-whitespace string value`` (id: NonNull<string>) =
    String.IsNullOrWhiteSpace id.Get
    || (let config = siloConfig { clusterId id.Get }
        config.ClusterId = Some id.Get)

[<Property>]
let ``serviceId stores any non-whitespace string value`` (id: NonNull<string>) =
    String.IsNullOrWhiteSpace id.Get
    || (let config = siloConfig { serviceId id.Get }
        config.ServiceId = Some id.Get)

[<Property>]
let ``siloPort stores any positive port number`` (port: PositiveInt) =
    let p = port.Get % 65535 + 1  // keep in valid port range
    let config = siloConfig { siloPort p }
    config.SiloPort = Some p

[<Property>]
let ``gatewayPort stores any positive port number`` (port: PositiveInt) =
    let p = port.Get % 65535 + 1
    let config = siloConfig { gatewayPort p }
    config.GatewayPort = Some p

[<Property>]
let ``grainCollectionAge stores any positive TimeSpan`` (minutes: PositiveInt) =
    let timeout = TimeSpan.FromMinutes(float minutes.Get)
    let config = siloConfig { grainCollectionAge timeout }
    config.GrainCollectionAge = Some timeout

[<Property>]
let ``identity settings do not disturb clustering mode`` (id: NonNull<string>) =
    String.IsNullOrWhiteSpace id.Get
    || (let config =
            siloConfig {
                useLocalhostClustering
                clusterId id.Get
                serviceId id.Get
            }

        config.ClusteringMode.IsSome
        && config.ClusterId = Some id.Get
        && config.ServiceId = Some id.Get)
