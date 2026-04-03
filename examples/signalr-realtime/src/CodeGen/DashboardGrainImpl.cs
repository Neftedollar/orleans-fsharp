using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using Orleans;
using Orleans.Runtime;
using SignalRRealtime.Grains;
using SignalRRealtime.Shared;
using Orleans.FSharp;

namespace SignalRRealtime.CodeGen;

/// <summary>
/// Concrete grain implementation for the dashboard grain.
/// Registers a grain timer that generates random metrics every 2 seconds
/// and pushes them to connected SignalR clients via IHubContext.
/// </summary>
public class DashboardGrainImpl : Grain, IDashboardGrain
{
    private readonly ILogger<DashboardGrainImpl> _logger;
    private DashboardState _currentState;
    private IGrainTimer? _timer;

    /// <summary>Creates a new DashboardGrainImpl instance.</summary>
    public DashboardGrainImpl(ILogger<DashboardGrainImpl> logger)
    {
        _logger = logger;
        _currentState = DashboardGrainDef.dashboard.DefaultState.Value;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("DashboardGrainImpl {GrainId} activated", this.GetGrainId());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(DashboardCommand cmd)
    {
        var definition = DashboardGrainDef.dashboard;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        _currentState = tuple.Item1;
        return tuple.Item2;
    }

    /// <summary>
    /// Starts the periodic metric generation timer.
    /// The timer fires every 2 seconds and pushes updates to SignalR clients.
    /// </summary>
    public Task StartTimer()
    {
        if (_timer is null)
        {
            _timer = this.RegisterGrainTimer(
                static async (state, ct) =>
                {
                    var self = (DashboardGrainImpl)state;
                    var update = await self.GetLatestUpdate();

                    // Push to all connected SignalR clients via IHubContext
                    var hubContext = self.ServiceProvider.GetService<IHubContext<DashboardHub>>();
                    if (hubContext is not null)
                    {
                        await hubContext.Clients.All.SendAsync(
                            "ReceiveMetrics",
                            update,
                            ct);
                    }

                    self._logger.LogDebug(
                        "Dashboard tick #{Seq}: cpu={Cpu}%",
                        update.SequenceNumber,
                        update.Metrics[0].Value);
                },
                this,
                new GrainTimerCreationOptions
                {
                    DueTime = TimeSpan.FromSeconds(2),
                    Period = TimeSpan.FromSeconds(2)
                });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the latest dashboard update with freshly generated metrics.
    /// </summary>
    public async Task<DashboardUpdate> GetLatestUpdate()
    {
        var result = await HandleMessage(DashboardCommand.GetLatestUpdate);
        return (DashboardUpdate)result;
    }
}

/// <summary>
/// SignalR hub for the dashboard. Declared here so the grain can reference IHubContext.
/// The actual hub implementation is minimal -- the grain pushes data to clients.
/// </summary>
public class DashboardHub : Hub
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>Creates a new DashboardHub instance.</summary>
    public DashboardHub(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    /// <summary>
    /// Called when a client connects. Ensures the dashboard grain's timer is running.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var dashboard = _grainFactory.GetGrain<IDashboardGrain>("default");
        await dashboard.StartTimer();
        await base.OnConnectedAsync();
    }
}
