namespace SignalRRealtime.Web.Hubs

/// <summary>
/// This module re-exports the DashboardHub from CodeGen so ASP.NET Core can map it.
/// The actual hub implementation lives in the C# CodeGen project alongside the grain.
/// </summary>
module DashboardHubRef =

    /// <summary>
    /// Type alias for referencing the hub from F# code.
    /// </summary>
    type Hub = SignalRRealtime.CodeGen.DashboardHub
