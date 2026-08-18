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
    /// <summary>The exact-type payload codec of the process which created this reference.</summary>
    private readonly IFunctionalPayloadCodec _payloadCodec;

    /// <summary>The services of the client or activation which created this reference.</summary>
    private readonly IServiceProvider _services;

    /// <summary>
    /// The exception serializer every transactional request needs, resolved on first
    /// transactional send. Non-transactional contracts never touch it, so a process that never
    /// calls a transactional operation never resolves it.
    /// </summary>
    private Serializer<OrleansTransactionAbortedException>? _transactionExceptionSerializer;

    /// <summary>Create a reference over the shared state built by the functional provider.</summary>
    /// <param name="shared">The Orleans reference-shared state to construct the base <see cref="GrainReference"/> over.</param>
    /// <param name="key">The grain's identity key.</param>
    /// <param name="payloadCodec">The exact-type payload codec to store on the reference.</param>
    /// <param name="services">The services of the client or activation creating this reference.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="payloadCodec"/> or <paramref name="services"/> is null.</exception>
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
    /// <param name="envelope">The validated fixed request to send.</param>
    /// <param name="closedInterfaceType">The closed actor-specific target interface from the contract descriptor.</param>
    /// <param name="dispatchMethod">The interface method the target should dispatch to.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The fixed reply once the target has completed the call.</returns>
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
    /// <param name="envelope">The validated fixed request to send.</param>
    /// <param name="closedInterfaceType">The closed actor-specific target interface from the contract descriptor.</param>
    /// <param name="dispatchMethod">The interface method the target should dispatch to.</param>
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
    /// <param name="envelope">The validated fixed request to send.</param>
    /// <param name="closedInterfaceType">The closed actor-specific target interface from the contract descriptor.</param>
    /// <param name="dispatchMethod">The interface method the target should dispatch to.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>The fixed reply once the target has completed the call.</returns>
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

    /// <summary>
    /// Open a server-streaming operation. Spec 004 item 6. The returned value is Orleans' own
    /// <c>AsyncEnumerableRequest</c> bound to this reference: nothing is sent until a consumer
    /// enumerates it, and each enumeration is an independent remote enumeration with its own
    /// request id, exactly as it is for a codegen grain method returning
    /// <c>IAsyncEnumerable&lt;T&gt;</c>.
    /// </summary>
    /// <param name="envelope">The validated fixed request describing the stream to open.</param>
    /// <param name="closedInterfaceType">The closed actor-specific target interface from the contract descriptor.</param>
    /// <param name="dispatchMethod">The interface method the target should dispatch to.</param>
    /// <param name="cancellationToken">The token that cancels the enumeration.</param>
    /// <returns>The unbound stream of fixed replies; enumerating it drives the remote call.</returns>
    internal IAsyncEnumerable<FunctionalReply> OpenStream(
        FunctionalRequestEnvelope envelope,
        Type closedInterfaceType,
        MethodInfo dispatchMethod,
        CancellationToken cancellationToken)
    {
        var request = new FunctionalStreamRequest(envelope, cancellationToken);
        request.SetCallerMetadata(closedInterfaceType, dispatchMethod);
        request.ValidateAdmissionFlags();

        // The attribute Orleans puts on AsyncEnumerableRequest<T> for its own code generator,
        // [ReturnValueProxy(nameof(InitializeRequest))], means exactly this call: bind the request
        // to the reference it will enumerate against, and hand the request back as the enumerable.
        return request.InitializeRequest(this);
    }

    /// <summary>Build and configure a non-transactional, non-streaming request from a validated envelope.</summary>
    /// <param name="envelope">The validated fixed request to wrap.</param>
    /// <param name="closedInterfaceType">The closed actor-specific target interface from the contract descriptor.</param>
    /// <param name="dispatchMethod">The interface method the target should dispatch to.</param>
    /// <param name="cancellationToken">The token that cancels the call.</param>
    /// <returns>A request configured with caller metadata and admission options, ready to invoke.</returns>
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
