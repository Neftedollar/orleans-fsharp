using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the non-reentrant sequential grain.
/// No [Reentrant] attribute — messages are processed one at a time (default Orleans behavior).
/// All behavior is delegated to the F# GrainDefinition via SequentialGrainDef.sequential.
/// </summary>
public class SequentialGrainImpl : Grain, ISequentialGrain
{
    private readonly ILogger<SequentialGrainImpl> _logger;
    private int _currentState;

    public SequentialGrainImpl(ILogger<SequentialGrainImpl> logger)
    {
        _logger = logger;
        _currentState = SequentialGrainDef.sequential.DefaultState.Value;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "SequentialGrainImpl {GrainId} activated",
            this.GetGrainId());

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(SequentialCommand cmd)
    {
        var definition = SequentialGrainDef.sequential;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        return result;
    }
}
