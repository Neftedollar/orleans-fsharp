module Orleans.FSharp.Tests.ClientConfigIdentityTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Runtime

/// <summary>Tests for clientConfig CE identity keywords: clusterId, serviceId,
/// gatewayListRefreshPeriod, and preferredGatewayIndex.</summary>

// ---------------------------------------------------------------------------
// clusterId
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig CE default has no clusterId`` () =
    let config = clientConfig { () }
    test <@ config.ClusterId.IsNone @>

[<Fact>]
let ``clientConfig CE sets clusterId`` () =
    let config = clientConfig { clusterId "prod-cluster" }
    test <@ config.ClusterId = Some "prod-cluster" @>

[<Fact>]
let ``clientConfig CE later clusterId overrides earlier`` () =
    let config = clientConfig { clusterId "first"; clusterId "second" }
    test <@ config.ClusterId = Some "second" @>

// ---------------------------------------------------------------------------
// serviceId
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig CE default has no serviceId`` () =
    let config = clientConfig { () }
    test <@ config.ServiceId.IsNone @>

[<Fact>]
let ``clientConfig CE sets serviceId`` () =
    let config = clientConfig { serviceId "my-service" }
    test <@ config.ServiceId = Some "my-service" @>

[<Fact>]
let ``clientConfig CE later serviceId overrides earlier`` () =
    let config = clientConfig { serviceId "v1"; serviceId "v2" }
    test <@ config.ServiceId = Some "v2" @>

// ---------------------------------------------------------------------------
// gatewayListRefreshPeriod
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig CE default has no gatewayListRefreshPeriod`` () =
    let config = clientConfig { () }
    test <@ config.GatewayListRefreshPeriod.IsNone @>

[<Fact>]
let ``clientConfig CE sets gatewayListRefreshPeriod`` () =
    let period = TimeSpan.FromSeconds 30.
    let config = clientConfig { gatewayListRefreshPeriod period }
    test <@ config.GatewayListRefreshPeriod = Some period @>

[<Fact>]
let ``clientConfig CE later gatewayListRefreshPeriod overrides earlier`` () =
    let config =
        clientConfig {
            gatewayListRefreshPeriod (TimeSpan.FromSeconds 30.)
            gatewayListRefreshPeriod (TimeSpan.FromMinutes 2.)
        }

    test <@ config.GatewayListRefreshPeriod = Some(TimeSpan.FromMinutes 2.) @>

// ---------------------------------------------------------------------------
// preferredGatewayIndex
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig CE default has no preferredGatewayIndex`` () =
    let config = clientConfig { () }
    test <@ config.PreferredGatewayIndex.IsNone @>

[<Fact>]
let ``clientConfig CE sets preferredGatewayIndex`` () =
    let config = clientConfig { preferredGatewayIndex 2 }
    test <@ config.PreferredGatewayIndex = Some 2 @>

[<Fact>]
let ``clientConfig CE later preferredGatewayIndex overrides earlier`` () =
    let config = clientConfig { preferredGatewayIndex 0; preferredGatewayIndex 3 }
    test <@ config.PreferredGatewayIndex = Some 3 @>

// ---------------------------------------------------------------------------
// Composition
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig CE composes all identity options`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            clusterId "cluster-1"
            serviceId "service-1"
            gatewayListRefreshPeriod (TimeSpan.FromSeconds 60.)
            preferredGatewayIndex 1
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusterId = Some "cluster-1" @>
    test <@ config.ServiceId = Some "service-1" @>
    test <@ config.GatewayListRefreshPeriod = Some(TimeSpan.FromSeconds 60.) @>
    test <@ config.PreferredGatewayIndex = Some 1 @>

[<Fact>]
let ``ClientConfig.Default has no identity fields set`` () =
    let config = ClientConfig.Default
    test <@ config.ClusterId.IsNone @>
    test <@ config.ServiceId.IsNone @>
    test <@ config.GatewayListRefreshPeriod.IsNone @>
    test <@ config.PreferredGatewayIndex.IsNone @>

[<Fact>]
let ``clientConfig CE identity options do not affect clustering mode`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            clusterId "c1"
            serviceId "s1"
        }

    test <@ config.ClusteringMode.IsSome @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``clusterId stores any non-whitespace string value`` (id: NonNull<string>) =
    String.IsNullOrWhiteSpace id.Get
    || (let config = clientConfig { clusterId id.Get }
        config.ClusterId = Some id.Get)

[<Property>]
let ``serviceId stores any non-whitespace string value`` (id: NonNull<string>) =
    String.IsNullOrWhiteSpace id.Get
    || (let config = clientConfig { serviceId id.Get }
        config.ServiceId = Some id.Get)

[<Property>]
let ``gatewayListRefreshPeriod stores any positive TimeSpan`` (minutes: PositiveInt) =
    let period = TimeSpan.FromMinutes(float minutes.Get)
    let config = clientConfig { gatewayListRefreshPeriod period }
    config.GatewayListRefreshPeriod = Some period

[<Property>]
let ``preferredGatewayIndex stores any non-negative int`` (n: NonNegativeInt) =
    let config = clientConfig { preferredGatewayIndex n.Get }
    config.PreferredGatewayIndex = Some n.Get

[<Property>]
let ``identity settings do not disturb clustering mode`` (id: NonNull<string>) =
    String.IsNullOrWhiteSpace id.Get
    || (let config =
            clientConfig {
                useLocalhostClustering
                clusterId id.Get
                serviceId id.Get
            }

        config.ClusteringMode.IsSome
        && config.ClusterId = Some id.Get
        && config.ServiceId = Some id.Get)
