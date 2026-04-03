using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the reentrant aggregator grain.
/// The [Reentrant] attribute allows concurrent message processing,
/// matching the F# GrainDefinition's IsReentrant=true setting.
/// All behavior is delegated to the F# GrainDefinition via AggregatorGrainDef.aggregator.
/// </summary>
[Reentrant]
public class AggregatorGrainImpl : Grain, IAggregatorGrain
{
    private readonly ILogger<AggregatorGrainImpl> _logger;
    private int _currentState;

    public AggregatorGrainImpl(ILogger<AggregatorGrainImpl> logger)
    {
        _logger = logger;
        _currentState = AggregatorGrainDef.aggregator.DefaultState.Value;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "AggregatorGrainImpl {GrainId} activated",
            this.GetGrainId());

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(AggregatorCommand cmd)
    {
        var definition = AggregatorGrainDef.aggregator;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        return result;
    }
}
