namespace SignalRRealtime.Web.Hubs

open System.Threading.Tasks
open Microsoft.AspNetCore.SignalR
open Orleans
open SignalRRealtime.Grains

/// <summary>
/// SignalR hub for the dashboard. The grain pushes data to clients via a timer;
/// this hub sends an initial update when a client connects.
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
            let dashboard = grainFactory.GetGrain<IDashboardGrain>("default")
            let! result = dashboard.HandleMessage(GetLatestUpdate)
            let update = result :?> SignalRRealtime.Shared.DashboardUpdate
            do! clients.Caller.SendAsync("ReceiveMetrics", update)
        }
