namespace SignalRRealtime.Web.Hubs

open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open Orleans
open SignalRRealtime.Grains

(*
    Classic grain { } model -- cannot run standalone.

    F# assemblies carry none of Orleans' source-generated
    [assembly: ApplicationPart] / [assembly: TypeManifestProvider] attributes (Roslyn generators
    never run on an F# project), so grainFactory.GetGrain<IDashboardGrain>(...) fails with
    "Could not find an implementation for interface IDashboardGrain" the moment it runs -- every
    browser connection to this hub would crash instead of receiving metrics. See
    docs/functional-grains.md, "Running a silo from a standalone F# process" for the exact
    mechanism, and "Migrating from the grain { } CE" for the rewrite this file demonstrates.

    type DashboardHub(grainFactory: IGrainFactory) =
        inherit Hub()

        override this.OnConnectedAsync() : Task =
            let clients = this.Clients

            task {
                let dashboard = grainFactory.GetGrain<IDashboardGrain>("default")
                let! result = dashboard.HandleMessage(GetLatestUpdate)
                let update = result :?> SignalRRealtime.Shared.DashboardUpdate
                do! clients.Caller.SendAsync("ReceiveMetrics", update)
            }
*)

/// <summary>
/// SignalR hub for the dashboard. The grain's declarative timer advances the sequence number on
/// its own; this hub sends a freshly generated update (via the functional twin's
/// <c>latestUpdate</c>, which bumps the sequence number again) when a client connects.
/// </summary>
type DashboardHub(grainFactory: IGrainFactory) =
    inherit Hub()

    /// <summary>
    /// Called when a client connects. Sends an initial dashboard update to the caller.
    /// </summary>
    override this.OnConnectedAsync() : Task =
        // Capture protected members before entering task CE
        let clients = this.Clients

        task {
            let dashboard = DashboardApi.ref grainFactory "default"
            let! update = dashboard.latestUpdate ()
            do! clients.Caller.SendAsync("ReceiveMetrics", update)
        }
