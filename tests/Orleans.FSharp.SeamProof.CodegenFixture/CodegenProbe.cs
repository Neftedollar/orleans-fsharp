namespace Orleans.FSharp.SeamProof.CodegenFixture;

/// <summary>
/// An open generic grain interface that Orleans' source generator discovers and
/// publishes through this assembly's generated type manifest.
/// </summary>
public interface ICodegenProbeTarget<T> : IGrainWithStringKey
{
    Task<string> PingAsync();
}

/// <summary>
/// An open generic grain class implementing <see cref="ICodegenProbeTarget{T}"/>.
/// Discovered by Orleans codegen exactly the way a C# functional marker would be.
/// </summary>
public sealed class CodegenProbeMarker<T> : Grain, ICodegenProbeTarget<T>
{
    public Task<string> PingAsync() => Task.FromResult($"pong:{typeof(T).Name}");
}

/// <summary>Actor brand used to close the open generic probe types.</summary>
public sealed class CodegenProbeActor;
