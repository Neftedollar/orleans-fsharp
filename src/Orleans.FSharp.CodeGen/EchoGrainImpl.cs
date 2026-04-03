using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the echo grain.
/// This C# class exists in the CodeGen project so Orleans source generators
/// can produce the necessary grain metadata.
/// All behavior is delegated to the F# GrainDefinition via EchoGrainDef.echo.
/// </summary>
public class EchoGrainImpl : Grain, IEchoGrain
{
    private readonly ILogger<EchoGrainImpl> _logger;
    private string _currentState;

    public EchoGrainImpl(ILogger<EchoGrainImpl> logger)
    {
        _logger = logger;
        _currentState = EchoGrainDef.echo.DefaultState;
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Set the state to the grain's string key so it knows its identity
        _currentState = this.GetPrimaryKeyString();

        _logger.LogInformation(
            "EchoGrainImpl {GrainId} activated",
            this.GetGrainId());

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<object> HandleMessage(EchoCommand cmd)
    {
        var definition = EchoGrainDef.echo;
        var tuple = await GrainDefinition.invokeHandler(definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        return result;
    }
}
