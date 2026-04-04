module Orleans.FSharp.Tests.DashboardConfigTests

open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Runtime

/// <summary>Helper to check if a DashboardConfig is DashboardDefaults.</summary>
let isDashboardDefaults =
    function
    | DashboardDefaults -> true
    | _ -> false

/// <summary>Helper to check if a DashboardConfig is DashboardWithOptions.</summary>
let isDashboardWithOptions =
    function
    | DashboardWithOptions _ -> true
    | _ -> false

[<Fact>]
let ``siloConfig CE default has no dashboard`` () =
    let config = siloConfig { () }
    test <@ config.DashboardConfig.IsNone @>

[<Fact>]
let ``siloConfig CE adds dashboard with defaults`` () =
    let config = siloConfig { addDashboard }
    test <@ config.DashboardConfig.IsSome @>
    test <@ config.DashboardConfig.Value |> isDashboardDefaults @>

[<Fact>]
let ``siloConfig CE adds dashboard with custom options`` () =
    let config = siloConfig { addDashboardWithOptions 2000 200 true }
    test <@ config.DashboardConfig.IsSome @>
    test <@ config.DashboardConfig.Value |> isDashboardWithOptions @>

[<Fact>]
let ``siloConfig CE dashboard with options stores values`` () =
    let config = siloConfig { addDashboardWithOptions 5000 50 false }

    match config.DashboardConfig.Value with
    | DashboardWithOptions(intervalMs, historyLen, hideTrace) ->
        test <@ intervalMs = 5000 @>
        test <@ historyLen = 50 @>
        test <@ hideTrace = false @>
    | other -> failwith $"Expected DashboardWithOptions, got {other}"

[<Fact>]
let ``siloConfig CE dashboard composes with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addDashboard
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.DashboardConfig.IsSome @>

[<Fact>]
let ``siloConfig CE later dashboard overrides earlier`` () =
    let config =
        siloConfig {
            addDashboard
            addDashboardWithOptions 3000 150 true
        }

    test <@ config.DashboardConfig.Value |> isDashboardWithOptions @>

[<Fact>]
let ``siloConfig CE dashboard composes with TLS`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            useTls "my-cert"
            addDashboard
        }

    test <@ config.TlsConfig.IsSome @>
    test <@ config.DashboardConfig.IsSome @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``addDashboardWithOptions stores any positive intervalMs and historyLen`` (intervalMs: PositiveInt) (historyLen: PositiveInt) =
    let config =
        siloConfig {
            addDashboardWithOptions intervalMs.Get historyLen.Get false
        }

    match config.DashboardConfig.Value with
    | DashboardWithOptions(i, h, _) -> i = intervalMs.Get && h = historyLen.Get
    | _ -> false

[<Property>]
let ``addDashboardWithOptions hideTrace flag is preserved`` (hide: bool) =
    let config = siloConfig { addDashboardWithOptions 1000 100 hide }

    match config.DashboardConfig.Value with
    | DashboardWithOptions(_, _, h) -> h = hide
    | _ -> false

[<Property>]
let ``addDashboard and addDashboardWithOptions always set DashboardConfig to Some`` (useDefaults: bool) =
    let config =
        if useDefaults then
            siloConfig { addDashboard }
        else
            siloConfig { addDashboardWithOptions 1000 100 false }

    config.DashboardConfig.IsSome
