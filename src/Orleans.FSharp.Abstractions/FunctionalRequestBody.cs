using System.Globalization;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Serialization.Invocation;

namespace Orleans.FSharp;

/// <summary>
/// The shared body of every functional invokable: the envelope, the caller and target-local
/// cancellation state, the resolved dispatch target, and the call-filter metadata.
/// </summary>
/// <remarks>
/// The fixed transport has two invokable shells — <see cref="FunctionalRequest"/> over Orleans'
/// plain <c>Request&lt;TResult&gt;</c> and <see cref="FunctionalTransactionRequest"/> over
/// <c>TransactionRequest&lt;TResult&gt;</c> — because Orleans' transaction machinery lives in an
/// invokable base class and C# has no multiple inheritance. Everything that is not "which base
/// class" lives here exactly once, so the two shells cannot drift apart.
/// </remarks>
internal sealed class FunctionalRequestBody
{
    /// <summary>The CLR method name both target interfaces expose.</summary>
    public const string DispatchMethodName = "DispatchAsync";

    /// <summary>
    /// The dispatch method of the closed non-generic seam, used only until caller-side or
    /// target-side metadata has been stored, so that filter metadata is never null.
    /// </summary>
    private static readonly MethodInfo FallbackMethod =
        typeof(IFunctionalDispatchTarget).GetMethod(DispatchMethodName)!;

    private FunctionalRequestEnvelope _envelope;

    /// <summary>The process-local token supplied by the caller; survives a local copy.</summary>
    private CancellationToken _callerToken;

    /// <summary>Argument 1: the caller token before <see cref="SetTarget"/>, the target-local token after it.</summary>
    private CancellationToken _currentToken;

    private IFunctionalDispatchTarget? _target;
    private CancellationTokenSource? _targetCancellation;
    private Type? _interfaceType;
    private MethodInfo? _method;

    /// <summary>Create an uninitialized body for the deserialization activator.</summary>
    internal FunctionalRequestBody() => _envelope = new FunctionalRequestEnvelope();

    /// <summary>Create a caller-side body for one envelope.</summary>
    internal FunctionalRequestBody(FunctionalRequestEnvelope envelope, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _envelope = envelope;
        _callerToken = callerToken;
        _currentToken = callerToken;
    }

    /// <summary>Field 0 — the fixed request data.</summary>
    internal FunctionalRequestEnvelope Envelope => _envelope;

    /// <summary>The caller-supplied cancellation token, independent of the target-local one.</summary>
    internal CancellationToken CallerToken => _callerToken;

    /// <summary>True once <see cref="SetTarget"/> has created target-local cancellation state.</summary>
    internal bool HasTargetCancellation => _targetCancellation is not null;

    /// <summary>True once <see cref="SetTarget"/> has resolved a dispatch target.</summary>
    internal bool HasTarget => _target is not null;

    /// <summary>
    /// True once caller-side or target-side call-filter metadata has been stored, as opposed to
    /// the non-generic fallback <see cref="GetInterfaceType"/> and <see cref="GetMethod"/>
    /// report until then.
    /// </summary>
    internal bool HasCallFilterMetadata => _interfaceType is not null && _method is not null;

    /// <summary>Replace field 0. Used by the deserializing codec and by <c>SetArgument(0, …)</c>.</summary>
    internal void SetEnvelope(FunctionalRequestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _envelope = envelope;
    }

    /// <summary>
    /// Store the caller-side call-filter metadata: the CLOSED actor-specific target interface
    /// and its <c>DispatchAsync</c> method. Never the open generic definition.
    /// </summary>
    internal void SetCallerMetadata(Type closedInterfaceType, MethodInfo dispatchMethod)
    {
        ArgumentNullException.ThrowIfNull(closedInterfaceType);
        ArgumentNullException.ThrowIfNull(dispatchMethod);

        if (closedInterfaceType.ContainsGenericParameters)
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"the caller-side target interface '{closedInterfaceType.FullName}' is an open generic type; the closed actor-specific interface is required.");
        }

        _interfaceType = closedInterfaceType;
        _method = dispatchMethod;
    }

    /// <summary>The Orleans request options implied by the envelope's admission flags.</summary>
    internal static InvokeMethodOptions OptionsFor(byte admissionFlags)
    {
        if (FunctionalAdmissionFlags.HasReservedBits(admissionFlags))
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"the admission flags 0x{admissionFlags:x2} set a reserved bit (mask 0x{FunctionalAdmissionFlags.Reserved:x2}).");
        }

        if (!FunctionalAdmissionFlags.IsTransactionFieldValid(admissionFlags))
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"the admission flags 0x{admissionFlags:x2} carry the unassigned transaction code {FunctionalAdmissionFlags.TransactionCode(admissionFlags)}.");
        }

        var options = InvokeMethodOptions.None;

        if ((admissionFlags & FunctionalAdmissionFlags.ReadOnly) != 0)
        {
            options |= InvokeMethodOptions.ReadOnly;
        }

        if ((admissionFlags & FunctionalAdmissionFlags.OneWay) != 0)
        {
            options |= InvokeMethodOptions.OneWay;
        }

        if ((admissionFlags & FunctionalAdmissionFlags.AlwaysInterleave) != 0)
        {
            options |= InvokeMethodOptions.AlwaysInterleave;
        }

        return options;
    }

    /// <summary>Invoke the resolved dispatch target with the current token.</summary>
    internal ValueTask<FunctionalReply> Dispatch()
    {
        var target = _target ?? throw FunctionalTransportDiagnostics.Fail(
            "the request was invoked before a dispatch target was set.");

        return target.DispatchAsync(_envelope, _currentToken);
    }

    /// <summary>
    /// Open the resolved dispatch target's server-streaming operation. Spec 004 item 6.
    /// </summary>
    /// <remarks>
    /// The target reference is captured now, while the request is still alive, and the returned
    /// enumerable holds it directly. Orleans keeps the enumerator long after the
    /// <c>StartEnumeration</c> message that produced it has completed and been disposed, so the
    /// enumeration must not read anything back out of this body.
    /// </remarks>
    internal IAsyncEnumerable<FunctionalReply> DispatchStream()
    {
        var target = _target ?? throw FunctionalTransportDiagnostics.Fail(
            "the streaming request was invoked before a dispatch target was set.");

        return new TargetStream(target, _envelope);
    }

    /// <summary>
    /// The captured <c>(target, envelope)</c> pair as an <see cref="IAsyncEnumerable{T}"/>, so that
    /// the enumeration's own cancellation token — the one Orleans cancels on caller disposal —
    /// reaches the activation.
    /// </summary>
    private sealed class TargetStream : IAsyncEnumerable<FunctionalReply>
    {
        private readonly IFunctionalDispatchTarget _target;
        private readonly FunctionalRequestEnvelope _envelope;

        public TargetStream(IFunctionalDispatchTarget target, FunctionalRequestEnvelope envelope)
        {
            _target = target;
            _envelope = envelope;
        }

        public IAsyncEnumerator<FunctionalReply> GetAsyncEnumerator(CancellationToken cancellationToken) =>
            _target.DispatchStream(_envelope, cancellationToken);
    }

    /// <summary>The resolved dispatch target, or <c>null</c> before <see cref="SetTarget"/>.</summary>
    internal object? Target => _target;

    /// <summary>
    /// Resolve the dispatch target and the target-side call-filter metadata for a <b>streaming</b>
    /// request, deliberately creating no target-local cancellation state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A unary request owns a <see cref="CancellationTokenSource"/> whose lifetime is the call: the
    /// request lifecycle disposes it when the reply is sent. A streaming request has no such
    /// lifetime — the enumeration outlives the <c>StartEnumeration</c> message by design — so a
    /// source created here would either leak for the life of the enumeration or be disposed
    /// underneath a producer that had registered on its token.
    /// </para>
    /// <para>
    /// Nothing is lost. <c>AsyncEnumerableGrainExtension.StartEnumeration</c> builds its own
    /// <c>CancellationTokenSource.CreateLinkedTokenSource(request.GetCancellationToken())</c> and
    /// hands <i>that</i> token to <c>GetAsyncEnumerator</c>; a linked source over
    /// <see cref="CancellationToken.None"/> is still cancellable, and cancelling it is exactly what
    /// <c>DisposeEnumeratorAsync</c> does when the caller disposes the enumerator.
    /// </para>
    /// </remarks>
    internal void SetStreamTarget(ITargetHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);

        var resolved = holder.GetTarget();

        if (resolved is not IFunctionalDispatchTarget target)
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"the activation target '{resolved?.GetType().FullName ?? "<null>"}' does not implement the functional dispatch seam.");
        }

        _target = target;

        foreach (var candidate in target.GetType().GetInterfaces())
        {
            if (candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IFunctionalGrainTarget<>))
            {
                _interfaceType = candidate;
                _method = candidate.GetMethod(DispatchMethodName);
                break;
            }
        }
    }

    /// <summary>Resolve the dispatch target and create the target-local cancellation state.</summary>
    internal void SetTarget(ITargetHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);

        var resolved = holder.GetTarget();

        if (resolved is not IFunctionalDispatchTarget target)
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"the activation target '{resolved?.GetType().FullName ?? "<null>"}' does not implement the functional dispatch seam.");
        }

        _target = target;

        // Target-side call-filter metadata comes from the actual target, never from the open
        // generic definition.
        foreach (var candidate in target.GetType().GetInterfaces())
        {
            if (candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IFunctionalGrainTarget<>))
            {
                _interfaceType = candidate;
                _method = candidate.GetMethod(DispatchMethodName);
                break;
            }
        }

        // Target-local cancellation state; argument 1 becomes the target-local token.
        _targetCancellation = new CancellationTokenSource();
        _currentToken = _targetCancellation.Token;
    }

    /// <summary>The fixed request has exactly two arguments.</summary>
    internal const int ArgumentCount = 2;

    /// <summary>Read argument 0 (the envelope) or argument 1 (the current token).</summary>
    internal object GetArgument(int index) =>
        index switch
        {
            0 => _envelope,
            1 => _currentToken,
            _ => throw ArgumentIndexOutOfRange(index)
        };

    /// <summary>Write argument 0 (the envelope) or argument 1 (the current token).</summary>
    internal void SetArgument(int index, object value)
    {
        switch (index)
        {
            case 0:
                if (value is not FunctionalRequestEnvelope envelope)
                {
                    throw new ArgumentException(
                        $"{FunctionalTransportDiagnostics.Stage}: argument 0 must be a {nameof(FunctionalRequestEnvelope)}.",
                        nameof(value));
                }

                _envelope = envelope;
                return;

            case 1:
                if (value is not CancellationToken token)
                {
                    throw new ArgumentException(
                        $"{FunctionalTransportDiagnostics.Stage}: argument 1 must be a {nameof(CancellationToken)}.",
                        nameof(value));
                }

                _currentToken = token;
                return;

            default:
                throw ArgumentIndexOutOfRange(index);
        }
    }

    private static ArgumentOutOfRangeException ArgumentIndexOutOfRange(int index) =>
        new(
            "index",
            index,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{FunctionalTransportDiagnostics.Stage}: argument index {index} is out of range; the fixed request has {ArgumentCount} arguments."));

    /// <summary>
    /// The closed actor-specific target interface once caller or target metadata has been
    /// stored. The fallback is the closed non-generic dispatch seam — never the open generic
    /// interface definition, which would make filter metadata meaningless.
    /// </summary>
    internal Type GetInterfaceType() => _interfaceType ?? typeof(IFunctionalDispatchTarget);

    /// <summary>The stored dispatch method, or the non-generic fallback.</summary>
    internal MethodInfo GetMethod() => _method ?? FallbackMethod;

    /// <summary>An acknowledged call is cancellable; a one-way call is not.</summary>
    internal bool IsCancellable =>
        (_envelope.AdmissionFlags & FunctionalAdmissionFlags.OneWay) == 0;

    /// <summary>The token argument 1 currently carries.</summary>
    internal CancellationToken CurrentToken => _currentToken;

    /// <summary>Signal the target-local cancellation source, if one exists.</summary>
    /// <remarks>
    /// The cancel signal and the completion of the call race by construction: Orleans may call
    /// this from the cancellation path while the request lifecycle is already running
    /// <see cref="Dispose"/> on another thread. Reading the field twice would let the null check
    /// see a live source and the call see a field <c>Dispose</c> has since nulled, so the source
    /// is captured into a local exactly once. Ownership still belongs to <c>Dispose</c> —
    /// cancelling must not dispose a source whose token the handler may still register on — so
    /// the capture cannot make the last interleaving impossible: <c>Dispose</c> may run between
    /// the capture and the <c>Cancel</c>. That one is benign and is caught: the call being
    /// cancelled has already finished, and there is nothing left to cancel.
    /// </remarks>
    internal bool TryCancel()
    {
        var cancellation = Volatile.Read(ref _targetCancellation);

        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Release the target-local cancellation source and the target reference.</summary>
    internal void Dispose()
    {
        Interlocked.Exchange(ref _targetCancellation, null)?.Dispose();
        _target = null;
    }
}
