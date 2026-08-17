using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Transactions;

namespace Orleans.FSharp;

/// <summary>
/// The custom <see cref="GrainReference"/> of the functional transport. It owns the protected
/// Orleans send methods and the injected exact-type payload codec; contract descriptors stay in
/// the upper <c>FunctionalGrainRef</c>, so this reference only ever receives a complete fixed
/// request.
/// </summary>
internal sealed class FunctionalGrainReference : GrainReference
{
    private readonly IFunctionalPayloadCodec _payloadCodec;
    private readonly IServiceProvider _services;

    /// <summary>
    /// The exception serializer every transactional request needs, resolved on first
    /// transactional send. Non-transactional contracts never touch it, so a process that never
    /// calls a transactional operation never resolves it.
    /// </summary>
    private Serializer<OrleansTransactionAbortedException>? _transactionExceptionSerializer;

    /// <summary>Create a reference over the shared state built by the functional provider.</summary>
    internal FunctionalGrainReference(
        GrainReferenceShared shared,
        IdSpan key,
        IFunctionalPayloadCodec payloadCodec,
        IServiceProvider services)
        : base(shared, key)
    {
        ArgumentNullException.ThrowIfNull(payloadCodec);
        ArgumentNullException.ThrowIfNull(services);
        _payloadCodec = payloadCodec;
        _services = services;
    }

    /// <summary>The exact-type payload codec of the process which created this reference.</summary>
    internal IFunctionalPayloadCodec PayloadCodec => _payloadCodec;

    /// <summary>The services of the client or activation which created this reference.</summary>
    internal IServiceProvider Services => _services;

    /// <summary>
    /// Send an acknowledged request and await the fixed reply. Caller metadata is the CLOSED
    /// actor-specific target interface taken from the contract descriptor, and the Orleans
    /// request options are restored from the envelope's validated admission flags before send.
    /// </summary>
    internal Task<FunctionalReply> SendAsync(
        FunctionalRequestEnvelope envelope,
        Type closedInterfaceType,
        MethodInfo dispatchMethod,
        CancellationToken cancellationToken)
    {
        var request = CreateRequest(envelope, closedInterfaceType, dispatchMethod, cancellationToken);
        return InvokeAsync<FunctionalReply>(request).AsTask();
    }

    /// <summary>
    /// Send a one-way request. Completion means the message entered the local Orleans send
    /// path; there is no target acknowledgement.
    /// </summary>
    internal void SendOneWay(
        FunctionalRequestEnvelope envelope,
        Type closedInterfaceType,
        MethodInfo dispatchMethod)
    {
        var request = CreateRequest(
            envelope,
            closedInterfaceType,
            dispatchMethod,
            CancellationToken.None);

        Invoke(request);
    }

    /// <summary>
    /// Send an acknowledged transactional request and await the fixed reply. The request rides
    /// Orleans' own transactional invokable base, so the ambient transaction is joined (or a new
    /// one started) by <c>TransactionRequestBase</c> itself, on both the caller and target sides.
    /// </summary>
    internal Task<FunctionalReply> SendTransactionalAsync(
        FunctionalRequestEnvelope envelope,
        Type closedInterfaceType,
        MethodInfo dispatchMethod,
        CancellationToken cancellationToken)
    {
        var exceptionSerializer =
            _transactionExceptionSerializer ??=
                _services.GetRequiredService<Serializer<OrleansTransactionAbortedException>>();

        var request = new FunctionalTransactionRequest(
            exceptionSerializer,
            _services,
            envelope,
            cancellationToken);

        request.SetCallerMetadata(closedInterfaceType, dispatchMethod);
        request.ApplyAdmissionOptions();
        return InvokeAsync<FunctionalReply>(request).AsTask();
    }

    private static FunctionalRequest CreateRequest(
        FunctionalRequestEnvelope envelope,
        Type closedInterfaceType,
        MethodInfo dispatchMethod,
        CancellationToken cancellationToken)
    {
        var request = new FunctionalRequest(envelope, cancellationToken);
        request.SetCallerMetadata(closedInterfaceType, dispatchMethod);
        request.ApplyAdmissionOptions();
        return request;
    }
}
