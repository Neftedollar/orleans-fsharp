using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using ChatRoom.Grains;
using Orleans.FSharp;

namespace ChatRoom.CodeGen;

/// <summary>
/// Concrete grain implementation for the chat grain with observer-based pub/sub.
/// Uses FSharpObserverManager for subscription lifecycle and auto-expiry.
/// All behavior delegates to F# definitions where possible.
/// </summary>
public class ChatGrainImpl : Grain, IChatGrain
{
    private readonly ILogger<ChatGrainImpl> _logger;
    private readonly FSharpObserverManager<IChatObserver> _observerManager;

    /// <summary>Creates a new ChatGrainImpl instance.</summary>
    public ChatGrainImpl(ILogger<ChatGrainImpl> logger)
    {
        _logger = logger;
        _observerManager = new FSharpObserverManager<IChatObserver>(TimeSpan.FromMinutes(5));
    }

    /// <inheritdoc/>
    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ChatGrainImpl {GrainId} activated", this.GetGrainId());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Subscribe(IChatObserver observer)
    {
        _observerManager.Subscribe(observer);
        _logger.LogInformation(
            "Observer subscribed to {GrainId}. Subscribers: {Count}",
            this.GetGrainId(), _observerManager.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Unsubscribe(IChatObserver observer)
    {
        _observerManager.Unsubscribe(observer);
        _logger.LogInformation(
            "Observer unsubscribed from {GrainId}. Subscribers: {Count}",
            this.GetGrainId(), _observerManager.Count);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task SendMessage(string sender, string message)
    {
        _logger.LogInformation(
            "[{GrainId}] {Sender}: {Message}",
            this.GetGrainId(), sender, message);

        await _observerManager.NotifyAsync(
            observer => observer.ReceiveMessage(sender, message));
    }

    /// <inheritdoc/>
    public Task<int> GetSubscriberCount()
    {
        return Task.FromResult(_observerManager.Count);
    }
}
