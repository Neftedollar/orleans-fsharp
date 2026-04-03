using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Testbed.Shared;
using Orleans.FSharp;

namespace Testbed.CodeGen;

/// <summary>
/// Concrete grain implementation for the chat grain.
/// Delegates all behavior to the F# GrainDefinition registered in DI.
/// </summary>
public class ChatGrainImpl : Grain, IChatGrain
{
    private readonly GrainDefinition<ChatState, ChatCommand> _definition;
    private readonly IPersistentState<ChatState> _persistentState;
    private readonly ILogger<ChatGrainImpl> _logger;
    private ChatState _currentState;

    public ChatGrainImpl(
        GrainDefinition<ChatState, ChatCommand> definition,
        [PersistentState("state", "Default")] IPersistentState<ChatState> persistentState,
        ILogger<ChatGrainImpl> logger)
    {
        _definition = definition;
        _persistentState = persistentState;
        _logger = logger;
        _currentState = definition.DefaultState.Value;
    }

    public override Task OnActivateAsync(CancellationToken ct)
    {
        if (_persistentState.RecordExists)
            _currentState = _persistentState.State;

        _logger.LogInformation("ChatGrainImpl {GrainId} activated", this.GetGrainId());
        return Task.CompletedTask;
    }

    public async Task<object> HandleMessage(ChatCommand cmd)
    {
        var tuple = await GrainDefinition.invokeHandler(_definition, _currentState, cmd);
        var newState = tuple.Item1;
        var result = tuple.Item2;
        _currentState = newState;
        _persistentState.State = _currentState;
        await _persistentState.WriteStateAsync();
        return result;
    }
}
