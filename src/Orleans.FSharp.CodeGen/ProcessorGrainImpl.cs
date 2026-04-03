using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the stateless worker processor grain.
/// The [StatelessWorker(4)] attribute allows multiple activations per silo,
/// matching the F# GrainDefinition's IsStatelessWorker=true and MaxLocalWorkers=Some 4.
/// All behavior is delegated to the F# GrainDefinition via ProcessorGrainDef.processor.
/// </summary>
[StatelessWorker(4)]
public class ProcessorGrainImpl : Grain, IProcessorGrain
{
    private readonly ILogger<ProcessorGrainImpl> _logger;
    private string _activationId;

    public ProcessorGrainImpl(ILogger<ProcessorGrainImpl> logger)
    {
        _logger = logger;
        // Each activation gets its own unique ID
        _activationId = Guid.NewGuid().ToString();
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ProcessorGrainImpl {GrainId} activated with id {ActivationId}",
            this.GetGrainId(), _activationId);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(ProcessorCommand cmd)
    {
        var definition = ProcessorGrainDef.processor;
        var tuple = await GrainDefinition.invokeHandler(definition, _activationId, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _activationId = newState;
        return result;
    }
}
