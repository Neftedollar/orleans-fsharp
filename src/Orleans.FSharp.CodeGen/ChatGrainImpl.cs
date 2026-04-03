using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.FSharp.Sample;
using Orleans.FSharp;

namespace Orleans.FSharp.CodeGen;

/// <summary>
/// Concrete grain implementation for the chat grain with observer-based pub/sub.
/// This C# class exists in the CodeGen project so Orleans source generators
/// can produce the necessary grain metadata (grain type mapping, activation, etc.)
/// that is not possible in F# projects.
/// Uses FSharpObserverManager to manage observer subscriptions with auto-expiry.
/// </summary>
public class ChatGrainImpl : Grain, IChatGrain
{
    private readonly ILogger<ChatGrainImpl> _logger;
    private readonly FSharpObserverManager<IChatObserver> _observerManager;

    public ChatGrainImpl(ILogger<ChatGrainImpl> logger)
    {
        _logger = logger;
        // Use a short expiry duration for testing (3 seconds)
        _observerManager = new FSharpObserverManager<IChatObserver>(TimeSpan.FromSeconds(3));
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "ChatGrainImpl {GrainId} activated",
            this.GetGrainId());

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Subscribe(IChatObserver observer)
    {
        _observerManager.Subscribe(observer);
        _logger.LogInformation(
            "Observer subscribed to chat grain {GrainId}. Total subscribers: {Count}",
            this.GetGrainId(),
            _observerManager.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Unsubscribe(IChatObserver observer)
    {
        _observerManager.Unsubscribe(observer);
        _logger.LogInformation(
            "Observer unsubscribed from chat grain {GrainId}. Total subscribers: {Count}",
            this.GetGrainId(),
            _observerManager.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SendMessage(string sender, string message)
    {
        _logger.LogInformation(
            "Chat grain {GrainId} sending message from {Sender}: {Message}",
            this.GetGrainId(),
            sender,
            message);

        await _observerManager.NotifyAsync(
            observer => observer.ReceiveMessage(sender, message));
    }

    /// <inheritdoc/>
    public Task<int> GetSubscriberCount()
    {
        return Task.FromResult(_observerManager.Count);
    }
}
