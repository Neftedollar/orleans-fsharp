namespace FeatureTour.Interop;

/// <summary>
/// The observer callback interface the functional grain notifies.
/// </summary>
/// <remarks>
/// Declared in C#, and that is the entire reason this project exists. Orleans' proxy source
/// generators are Roslyn generators, so they never run over an F# assembly: an
/// <see cref="IGrainObserver"/>-derived interface declared in F# has no generated proxy, and
/// <c>IGrainFactory.CreateObjectReference</c> (behind <c>Observer.createRef</c>) fails on it.
/// The constraint is Orleans', not the functional runtime's — it applies identically to the
/// <c>grain { }</c> CE and to ordinary class grains.
/// </remarks>
public interface ITourObserver : IGrainObserver
{
    /// <summary>Invoked by the notifying grain for every live subscriber.</summary>
    /// <param name="message">The notification text.</param>
    Task OnTourEvent(string message);
}
