using System.Globalization;
using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace Orleans.FSharp;

/// <summary>
/// The single request class of the functional transport: one class carries every operation of
/// every contract. Only the envelope (field 0) is serialized; method metadata, request
/// options, the target, and cancellation state are reconstructed by the request lifecycle.
/// </summary>
internal sealed class FunctionalRequest : Request<FunctionalReply>
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

    /// <summary>Create an uninitialized request for the deserialization activator.</summary>
    internal FunctionalRequest() => _envelope = new FunctionalRequestEnvelope();

    /// <summary>Create a caller-side request for one envelope.</summary>
    internal FunctionalRequest(FunctionalRequestEnvelope envelope, CancellationToken callerToken)
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

    /// <summary>Copy the caller-side cancellation state onto a local copy of this request.</summary>
    internal void SetCallerToken(CancellationToken callerToken)
    {
        _callerToken = callerToken;
        _currentToken = callerToken;
    }

    /// <summary>The Orleans request options implied by the envelope's admission flags.</summary>
    internal static InvokeMethodOptions OptionsFor(byte admissionFlags)
    {
        if (FunctionalAdmissionFlags.HasReservedBits(admissionFlags))
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"the admission flags 0x{admissionFlags:x2} set a reserved bit (mask 0x{FunctionalAdmissionFlags.Reserved:x2}).");
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

    /// <summary>Restore <c>RequestBase.Options</c> from the validated admission flags before send.</summary>
    internal void ApplyAdmissionOptions() => AddInvokeMethodOptions(OptionsFor(_envelope.AdmissionFlags));

    /// <inheritdoc />
    protected override ValueTask<FunctionalReply> InvokeInner()
    {
        var target = _target ?? throw FunctionalTransportDiagnostics.Fail(
            "the request was invoked before a dispatch target was set.");

        return target.DispatchAsync(_envelope, _currentToken);
    }

    /// <inheritdoc />
    public override object GetTarget() => _target!;

    /// <inheritdoc />
    public override void SetTarget(ITargetHolder holder)
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

    /// <inheritdoc />
    public override int GetArgumentCount() => 2;

    /// <inheritdoc />
    public override object GetArgument(int index) =>
        index switch
        {
            0 => _envelope,
            1 => _currentToken,
            _ => throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{FunctionalTransportDiagnostics.Stage}: argument index {index} is out of range; the fixed request has 2 arguments."))
        };

    /// <inheritdoc />
    public override void SetArgument(int index, object value)
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
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{FunctionalTransportDiagnostics.Stage}: argument index {index} is out of range; the fixed request has 2 arguments."));
        }
    }

    /// <inheritdoc />
    public override string GetMethodName() => DispatchMethodName;

    /// <inheritdoc />
    public override string GetInterfaceName() => GetInterfaceType().FullName!;

    /// <inheritdoc />
    public override string GetActivityName() => GetInterfaceName() + "/" + DispatchMethodName;

    /// <summary>
    /// The closed actor-specific target interface once caller or target metadata has been
    /// stored. The fallback is the closed non-generic dispatch seam — never the open generic
    /// interface definition, which would make filter metadata meaningless.
    /// </summary>
    public override Type GetInterfaceType() => _interfaceType ?? typeof(IFunctionalDispatchTarget);

    /// <inheritdoc />
    public override MethodInfo GetMethod() => _method ?? FallbackMethod;

    /// <inheritdoc />
    public override bool IsCancellable =>
        (_envelope.AdmissionFlags & FunctionalAdmissionFlags.OneWay) == 0;

    /// <inheritdoc />
    public override CancellationToken GetCancellationToken() => _currentToken;

    /// <inheritdoc />
    public override bool TryCancel()
    {
        if (_targetCancellation is null)
        {
            return false;
        }

        _targetCancellation.Cancel();
        return true;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _targetCancellation?.Dispose();
        _targetCancellation = null;
        _target = null;
    }

    /// <inheritdoc />
    public override string ToString() => GetActivityName() + " " + _envelope;
}
