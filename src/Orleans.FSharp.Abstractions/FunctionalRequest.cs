using System.Reflection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using Orleans.Transactions;

namespace Orleans.FSharp;

/// <summary>
/// The non-transactional request class of the functional transport: one class carries every
/// non-transactional operation of every contract. Only the envelope (field 0) is serialized;
/// method metadata, request options, the target, and cancellation state are reconstructed by the
/// request lifecycle.
/// </summary>
internal sealed class FunctionalRequest : Request<FunctionalReply>
{
    /// <summary>The CLR method name both target interfaces expose.</summary>
    public const string DispatchMethodName = FunctionalRequestBody.DispatchMethodName;

    private readonly FunctionalRequestBody _body;

    /// <summary>Create an uninitialized request for the deserialization activator.</summary>
    internal FunctionalRequest() => _body = new FunctionalRequestBody();

    /// <summary>Create a caller-side request for one envelope.</summary>
    internal FunctionalRequest(FunctionalRequestEnvelope envelope, CancellationToken callerToken) =>
        _body = new FunctionalRequestBody(envelope, callerToken);

    /// <summary>The shared invokable body.</summary>
    internal FunctionalRequestBody Body => _body;

    /// <summary>Field 0 — the fixed request data.</summary>
    internal FunctionalRequestEnvelope Envelope => _body.Envelope;

    /// <summary>The caller-supplied cancellation token, independent of the target-local one.</summary>
    internal CancellationToken CallerToken => _body.CallerToken;

    /// <summary>True once <see cref="SetTarget"/> has created target-local cancellation state.</summary>
    internal bool HasTargetCancellation => _body.HasTargetCancellation;

    /// <summary>True once <see cref="SetTarget"/> has resolved a dispatch target.</summary>
    internal bool HasTarget => _body.HasTarget;

    /// <summary>True once caller-side or target-side call-filter metadata has been stored.</summary>
    internal bool HasCallFilterMetadata => _body.HasCallFilterMetadata;

    /// <summary>Replace field 0. Used by the deserializing codec and by <c>SetArgument(0, …)</c>.</summary>
    internal void SetEnvelope(FunctionalRequestEnvelope envelope) => _body.SetEnvelope(envelope);

    /// <summary>Store the caller-side call-filter metadata.</summary>
    internal void SetCallerMetadata(Type closedInterfaceType, MethodInfo dispatchMethod) =>
        _body.SetCallerMetadata(closedInterfaceType, dispatchMethod);

    /// <summary>The Orleans request options implied by the envelope's admission flags.</summary>
    internal static InvokeMethodOptions OptionsFor(byte admissionFlags) =>
        FunctionalRequestBody.OptionsFor(admissionFlags);

    /// <summary>Restore <c>RequestBase.Options</c> from the validated admission flags before send.</summary>
    internal void ApplyAdmissionOptions() => AddInvokeMethodOptions(OptionsFor(_body.Envelope.AdmissionFlags));

    /// <inheritdoc />
    protected override ValueTask<FunctionalReply> InvokeInner() => _body.Dispatch();

    /// <inheritdoc />
    public override object GetTarget() => _body.Target!;

    /// <inheritdoc />
    public override void SetTarget(ITargetHolder holder) => _body.SetTarget(holder);

    /// <inheritdoc />
    public override int GetArgumentCount() => FunctionalRequestBody.ArgumentCount;

    /// <inheritdoc />
    public override object GetArgument(int index) => _body.GetArgument(index);

    /// <inheritdoc />
    public override void SetArgument(int index, object value) => _body.SetArgument(index, value);

    /// <inheritdoc />
    public override string GetMethodName() => DispatchMethodName;

    /// <inheritdoc />
    public override string GetInterfaceName() => GetInterfaceType().FullName!;

    /// <inheritdoc />
    public override string GetActivityName() => GetInterfaceName() + "/" + DispatchMethodName;

    /// <inheritdoc />
    public override Type GetInterfaceType() => _body.GetInterfaceType();

    /// <inheritdoc />
    public override MethodInfo GetMethod() => _body.GetMethod();

    /// <inheritdoc />
    public override bool IsCancellable => _body.IsCancellable;

    /// <inheritdoc />
    public override CancellationToken GetCancellationToken() => _body.CurrentToken;

    /// <inheritdoc />
    public override bool TryCancel() => _body.TryCancel();

    /// <inheritdoc />
    public override void Dispose() => _body.Dispose();

    /// <inheritdoc />
    public override string ToString() => GetActivityName() + " " + _body.Envelope;
}

/// <summary>
/// The transactional request class of the functional transport: the same fixed envelope, carried
/// by Orleans' own transactional invokable base.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the client-side and target-side transaction plumbing, and none of it is
/// ours. <c>Orleans.TransactionRequestBase</c> implements <c>IOutgoingGrainCallFilter</c>, which
/// <c>OutgoingCallInvoker</c> runs as the last stage of the caller-side pipeline for any request
/// object that implements it, and it overrides <c>IInvokable.Invoke()</c>, which is what the
/// activation's message loop calls on the target side. Deriving from it therefore joins the
/// ambient transaction on the way out, starts one when the option requires it and none exists,
/// establishes <c>TransactionContext</c> around the dispatch, and reports the participant set
/// back to the caller inside a <c>TransactionResponse</c> — exactly as it does for a
/// <c>[Transaction]</c>-attributed codegen grain method, because it is the same code.
/// </para>
/// <para>
/// The generated invokables Orleans emits for <c>[Transaction]</c> methods differ from this class
/// in exactly two respects: they call <c>SetTransactionOptions</c> from a generated constructor,
/// where this class takes the option from the request's own admission flags, and they carry the
/// method's declared arguments, where this class carries the fixed envelope.
/// </para>
/// </remarks>
internal sealed class FunctionalTransactionRequest : TransactionRequest<FunctionalReply>
{
    private readonly FunctionalRequestBody _body;

    /// <summary>Create an uninitialized request for the deserialization activator.</summary>
    internal FunctionalTransactionRequest(
        Serializer<OrleansTransactionAbortedException> exceptionSerializer,
        IServiceProvider serviceProvider)
        : base(exceptionSerializer, serviceProvider) =>
        _body = new FunctionalRequestBody();

    /// <summary>Create a caller-side request for one envelope.</summary>
    internal FunctionalTransactionRequest(
        Serializer<OrleansTransactionAbortedException> exceptionSerializer,
        IServiceProvider serviceProvider,
        FunctionalRequestEnvelope envelope,
        CancellationToken callerToken)
        : base(exceptionSerializer, serviceProvider) =>
        _body = new FunctionalRequestBody(envelope, callerToken);

    /// <summary>The shared invokable body.</summary>
    internal FunctionalRequestBody Body => _body;

    /// <summary>Field 0 — the fixed request data.</summary>
    internal FunctionalRequestEnvelope Envelope => _body.Envelope;

    /// <summary>The caller-supplied cancellation token, independent of the target-local one.</summary>
    internal CancellationToken CallerToken => _body.CallerToken;

    /// <summary>True once <see cref="SetTarget"/> has resolved a dispatch target.</summary>
    internal bool HasTarget => _body.HasTarget;

    /// <summary>True once caller-side or target-side call-filter metadata has been stored.</summary>
    internal bool HasCallFilterMetadata => _body.HasCallFilterMetadata;

    /// <summary>Replace field 0. Used by the deserializing codec and by <c>SetArgument(0, …)</c>.</summary>
    internal void SetEnvelope(FunctionalRequestEnvelope envelope) => _body.SetEnvelope(envelope);

    /// <summary>Store the caller-side call-filter metadata.</summary>
    internal void SetCallerMetadata(Type closedInterfaceType, MethodInfo dispatchMethod) =>
        _body.SetCallerMetadata(closedInterfaceType, dispatchMethod);

    /// <summary>
    /// Restore <c>RequestBase.Options</c> and the Orleans transaction option from the validated
    /// admission flags. The transaction option is derived from the flag byte on both sides rather
    /// than trusted from the wire, so the byte dispatch compares against the hosted descriptor is
    /// the single authority for what this call's transaction policy is.
    /// </summary>
    internal void ApplyAdmissionOptions()
    {
        var flags = _body.Envelope.AdmissionFlags;
        AddInvokeMethodOptions(FunctionalRequestBody.OptionsFor(flags));

        if (!FunctionalAdmissionFlags.TryGetTransactionOption(flags, out var option))
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"a transactional request for operation '{_body.Envelope.OperationId}' on grain type '{_body.Envelope.GrainType}' carries admission flags 0x{flags:x2}, which declare no transaction option.");
        }

        SetTransactionOptions(option);
    }

    /// <inheritdoc />
    protected override ValueTask<FunctionalReply> InvokeInner() => _body.Dispatch();

    /// <inheritdoc />
    public override object GetTarget() => _body.Target!;

    /// <inheritdoc />
    public override void SetTarget(ITargetHolder holder) => _body.SetTarget(holder);

    /// <inheritdoc />
    public override int GetArgumentCount() => FunctionalRequestBody.ArgumentCount;

    /// <inheritdoc />
    public override object GetArgument(int index) => _body.GetArgument(index);

    /// <inheritdoc />
    public override void SetArgument(int index, object value) => _body.SetArgument(index, value);

    /// <inheritdoc />
    public override string GetMethodName() => FunctionalRequestBody.DispatchMethodName;

    /// <inheritdoc />
    public override string GetInterfaceName() => GetInterfaceType().FullName!;

    /// <inheritdoc />
    public override string GetActivityName() =>
        GetInterfaceName() + "/" + FunctionalRequestBody.DispatchMethodName;

    /// <inheritdoc />
    public override Type GetInterfaceType() => _body.GetInterfaceType();

    /// <inheritdoc />
    public override MethodInfo GetMethod() => _body.GetMethod();

    /// <inheritdoc />
    public override bool IsCancellable => _body.IsCancellable;

    /// <inheritdoc />
    public override CancellationToken GetCancellationToken() => _body.CurrentToken;

    /// <inheritdoc />
    public override bool TryCancel() => _body.TryCancel();

    /// <inheritdoc />
    public override void Dispose()
    {
        _body.Dispose();

        // TransactionRequestBase.Dispose clears the carried TransactionInfo; the transaction
        // machinery owns that field, so its own disposal must still run.
        base.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => GetActivityName() + " " + _body.Envelope;
}

/// <summary>
/// The server-streaming request class of the functional transport: the same fixed envelope,
/// carried by Orleans' own <c>IAsyncEnumerable</c> invokable base. Spec 004 item 6.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the client-side and target-side streaming plumbing, and none of it is
/// ours — the same move Phase D made with <c>TransactionRequest&lt;TResult&gt;</c>.
/// <c>Orleans.Runtime.AsyncEnumerableRequest&lt;T&gt;</c> is a public abstract
/// <c>[GenerateSerializer] [SuppressReferenceTracking]</c> class over <c>RequestBase</c> which
/// <b>is itself</b> the <c>IAsyncEnumerable&lt;T&gt;</c> the caller enumerates: its
/// <c>GetAsyncEnumerator</c> creates Orleans' <c>AsyncEnumeratorProxy&lt;T&gt;</c>, which calls
/// <c>StartEnumeration</c> once and then <c>MoveNext</c> per batch on
/// <c>IAsyncEnumerableGrainExtension</c> — an extension Orleans registers for every activation in
/// <c>DefaultSiloServices</c> and auto-installs through <c>ActivationData</c>'s
/// <c>ITargetHolder.GetComponent</c>. Sequence numbering, batching, long-poll heartbeats,
/// cancel-on-dispose and enumerator expiry are all Orleans' own, already compiled into
/// <c>Orleans.Core.Abstractions.dll</c> together with the proxy and every invokable, so this
/// class needs no code generation of ours.
/// </para>
/// <para>
/// Only two things are ours: the target is resolved through the functional dispatch seam
/// (<see cref="FunctionalRequestBody.SetStreamTarget"/>), and the element type is the same fixed
/// <see cref="FunctionalReply"/> a unary call returns — so every item carries its own protocol
/// token and its own payload, and the per-item limit is the ordinary payload limit.
/// </para>
/// <para>
/// <c>Invoke()</c> is deliberately left to the base, which throws: an
/// <c>IAsyncEnumerable</c> request is never invoked as a unary call, and a functional streaming
/// operation can only be reached through <c>StartEnumeration</c>.
/// </para>
/// </remarks>
internal sealed class FunctionalStreamRequest : AsyncEnumerableRequest<FunctionalReply>
{
    /// <summary>The CLR method name the streaming dispatch seam exposes.</summary>
    public const string StreamDispatchMethodName = "DispatchStream";

    private readonly FunctionalRequestBody _body;

    /// <summary>Create an uninitialized request for the deserialization activator.</summary>
    internal FunctionalStreamRequest() => _body = new FunctionalRequestBody();

    /// <summary>Create a caller-side request for one envelope.</summary>
    internal FunctionalStreamRequest(FunctionalRequestEnvelope envelope, CancellationToken callerToken) =>
        _body = new FunctionalRequestBody(envelope, callerToken);

    /// <summary>The caller-supplied cancellation token; never serialized.</summary>
    internal CancellationToken CallerToken => _body.CallerToken;

    /// <summary>Field 0 of the derived segment — the fixed request data.</summary>
    internal FunctionalRequestEnvelope Envelope => _body.Envelope;

    /// <summary>True once <see cref="SetTarget"/> has resolved a dispatch target.</summary>
    internal bool HasTarget => _body.HasTarget;

    /// <summary>True once caller-side or target-side call-filter metadata has been stored.</summary>
    internal bool HasCallFilterMetadata => _body.HasCallFilterMetadata;

    /// <summary>Replace field 0. Used by the deserializing codec and by <c>SetArgument(0, …)</c>.</summary>
    internal void SetEnvelope(FunctionalRequestEnvelope envelope) => _body.SetEnvelope(envelope);

    /// <summary>Store the caller-side call-filter metadata.</summary>
    internal void SetCallerMetadata(Type closedInterfaceType, MethodInfo dispatchMethod) =>
        _body.SetCallerMetadata(closedInterfaceType, dispatchMethod);

    /// <summary>
    /// Validate the admission flags this envelope carries. A streaming operation composes with
    /// none of the four admission policies (contract sealing rejects every one of them), so the
    /// byte must be clear; the check is kept because the envelope can also arrive from the wire.
    /// </summary>
    /// <remarks>
    /// Unlike the unary and transactional requests this does <b>not</b> feed
    /// <c>RequestBase.Options</c>: the message that crosses the network is Orleans'
    /// <c>StartEnumeration</c>/<c>MoveNext</c> invokable, whose scheduling is fixed by the
    /// <c>[AlwaysInterleave]</c> on <c>IAsyncEnumerableGrainExtension</c>, so an option set here
    /// would never be read by anything.
    /// </remarks>
    internal void ValidateAdmissionFlags()
    {
        var flags = _body.Envelope.AdmissionFlags;

        if (flags != FunctionalAdmissionFlags.None)
        {
            throw FunctionalTransportDiagnostics.Fail(
                $"a streaming request for operation '{_body.Envelope.OperationId}' on grain type '{_body.Envelope.GrainType}' carries admission flags 0x{flags:x2}; a streaming operation composes with no admission policy, so the byte must be 0x00.");
        }
    }

    /// <inheritdoc />
    protected override IAsyncEnumerable<FunctionalReply> InvokeInner() => _body.DispatchStream();

    /// <inheritdoc />
    public override object GetTarget() => _body.Target!;

    /// <inheritdoc />
    public override void SetTarget(ITargetHolder holder) => _body.SetStreamTarget(holder);

    /// <inheritdoc />
    public override int GetArgumentCount() => FunctionalRequestBody.ArgumentCount;

    /// <inheritdoc />
    public override object GetArgument(int index) => _body.GetArgument(index);

    /// <inheritdoc />
    public override void SetArgument(int index, object value) => _body.SetArgument(index, value);

    /// <inheritdoc />
    public override string GetMethodName() => StreamDispatchMethodName;

    /// <inheritdoc />
    public override string GetInterfaceName() => GetInterfaceType().FullName!;

    /// <inheritdoc />
    public override string GetActivityName() => GetInterfaceName() + "/" + StreamDispatchMethodName;

    /// <inheritdoc />
    public override Type GetInterfaceType() => _body.GetInterfaceType();

    /// <inheritdoc />
    public override MethodInfo GetMethod() => _body.GetMethod();

    /// <summary>
    /// The caller's own token, so that a <c>callCancellable</c> stream is cancelled by it.
    /// </summary>
    /// <remarks>
    /// Read on the caller side by <c>AsyncEnumeratorProxy</c>'s constructor, which links it with
    /// whatever token <c>GetAsyncEnumerator</c> was given and owns the linked source; and on the
    /// target side by <c>StartEnumeration</c>, where the body has been reconstructed by the
    /// deserializing activator and this is <see cref="CancellationToken.None"/> — a token is
    /// process-local and is never wire data.
    /// </remarks>
    public override CancellationToken GetCancellationToken() => _body.CurrentToken;

    /// <inheritdoc />
    public override void Dispose() => _body.Dispose();

    /// <inheritdoc />
    public override string ToString() => GetActivityName() + " " + _body.Envelope;
}
