using Orleans.BroadcastChannel;

namespace FeatureTour.Interop;

/// <summary>
/// Read-back surface for what the implicit broadcast-channel consumer actually received.
/// </summary>
/// <remarks>
/// A broadcast-channel consumer has no reply path of its own — <c>OnSubscribed</c> hands it a
/// subscription it attaches a callback to — so the demo needs an ordinary grain call to observe
/// the delivered messages. This interface is that call.
/// </remarks>
public interface IBroadcastConsumerGrain : IGrainWithStringKey
{
    /// <summary>Returns every message this consumer has received so far, oldest first.</summary>
    Task<List<string>> Received();
}

/// <summary>
/// A C# consumer of the tour's broadcast channel.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ImplicitChannelSubscriptionAttribute"/> is what makes this an implicit subscriber:
/// Orleans routes every publish on a matching channel namespace to the grain of the same key,
/// activating it on demand. The attribute plus <see cref="IOnBroadcastChannelSubscribed"/> is
/// the whole consumer contract, and both need the Orleans code generator, which is why the
/// consumer lives in this C# project while the *producer* is an ordinary F# functional handler.
/// </para>
/// <para>
/// The producer side needs none of this: <c>BroadcastChannel.publish</c> from
/// <c>Orleans.FSharp</c> works directly out of a functional handler.
/// </para>
/// </remarks>
[ImplicitChannelSubscription(TourChannels.Namespace)]
public sealed class BroadcastConsumerGrain : Grain, IBroadcastConsumerGrain, IOnBroadcastChannelSubscribed
{
    private readonly List<string> _received = [];

    /// <inheritdoc/>
    public Task<List<string>> Received() => Task.FromResult(_received.ToList());

    /// <inheritdoc/>
    public Task OnSubscribed(IBroadcastChannelSubscription subscription)
    {
        return subscription.Attach<string>(
            item =>
            {
                _received.Add(item);
                return Task.CompletedTask;
            },
            error =>
            {
                _received.Add("error: " + error.Message);
                return Task.CompletedTask;
            });
    }
}

/// <summary>Channel identifiers shared by the F# producer and the C# consumer.</summary>
public static class TourChannels
{
    /// <summary>The broadcast-channel namespace the tour publishes on.</summary>
    public const string Namespace = "tour-broadcast";

    /// <summary>The registered broadcast-channel provider name.</summary>
    public const string Provider = "TourBroadcast";
}
