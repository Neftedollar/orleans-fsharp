namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Orleans.Transactions.Abstractions
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The logical identity of one attached transactional state: its state name, storage name, and
/// stored CLR type. Lookup and attachment validation compare this triple.
/// </summary>
type internal TransactionalStateDescriptor =
    { StateName: string
      StorageName: string
      StoredType: Type }

/// <summary>
/// An immutable logical descriptor of one named transactional state facet.
/// Created by <see cref="M:Orleans.FSharp.TransactionalStateModule.create"/> and attached to a
/// definition with <c>transactionalStateFrom</c>.
/// </summary>
[<Sealed>]
type TransactionalStateRef<'State> internal (stateName: string, storageName: string) =

    let descriptor =
        { StateName = stateName
          StorageName = storageName
          StoredType = typeof<'State> }

    /// <summary>The Orleans transactional state name of this facet.</summary>
    member internal _.StateName = stateName

    /// <summary>The Orleans transactional storage name of this facet.</summary>
    member internal _.StorageName = storageName

    /// <summary>The exact stored CLR type of this facet.</summary>
    member internal _.StoredType = typeof<'State>

    /// <summary>The logical <c>(stateName, storageName, storedType)</c> identity of this facet.</summary>
    member internal _.Descriptor = descriptor

    override _.ToString() =
        $"TransactionalStateRef(stateName = '{stateName}', storageName = '{storageName}', storedType = '{typeof<'State>.FullName}')"

/// <summary>Creation of immutable transactional-state descriptors.</summary>
[<RequireQualifiedAccess>]
module TransactionalState =

    /// <summary>
    /// Create an immutable descriptor for a named transactional state facet.
    /// Blank or NUL-containing names and open generic stored types are rejected immediately.
    /// </summary>
    /// <param name="stateName">
    /// Orleans transactional state name; unique within a definition. It is part of the
    /// <c>ParticipantId</c> Orleans uses to address this state during the commit protocol, and of
    /// the storage key, so it is durable identity and must not be renamed casually.
    /// </param>
    /// <param name="storageName">
    /// Name of an <c>ITransactionalStateStorageFactory</c> or, failing that, an
    /// <c>IGrainStorage</c> registration on every hosting silo — that is the exact resolution
    /// order <c>NamedTransactionalStateStorageFactory.Create</c> performs.
    /// </param>
    let create<'State> (stateName: string) (storageName: string) : TransactionalStateRef<'State> =
        if isBlank stateName then
            fail TransactionalStage "stateName must be a non-blank string."

        if containsNul stateName then
            fail TransactionalStage $"stateName '{stateName}' must not contain a NUL character."

        if isBlank storageName then
            fail TransactionalStage $"storageName for stateName '{stateName}' must be a non-blank string."

        if containsNul storageName then
            fail TransactionalStage $"storageName '{storageName}' must not contain a NUL character."

        if typeof<'State>.ContainsGenericParameters then
            fail
                TransactionalStage
                $"the stored type '{typeof<'State>.FullName}' for stateName '{stateName}' must be a closed type."

        TransactionalStateRef<'State>(stateName, storageName)

/// <summary>
/// The invocation-bound transactional-state facade handed to application code by
/// <c>context.transactionalState</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every member is guarded by the callback's scope and then delegates to the real Orleans
/// <c>ITransactionalState</c>, so ordinary Orleans transaction semantics are preserved exactly:
/// the runtime adds no read, no write, no retry, and no rollback of its own.
/// </para>
/// <para>
/// The stored object is never the application's value. Orleans applies an update by mutating the
/// instance it holds, so the runtime holds a <c>FunctionalTransactionalBox&lt;'State&gt;</c> and
/// the application's value rides in its single property. An update function is
/// <c>'State -&gt; 'State</c>: it is handed the current value and returns the replacement, and
/// the only mutation is the runtime's own reference assignment inside Orleans' update callback.
/// </para>
/// <para>
/// Both update overloads are <b>synchronous</b> functions by type, which is not an oversight.
/// Orleans runs them inside the transactional state's reader-writer lock
/// (<c>ReaderWriterLock.EnterLock</c> invokes the callback and completes a
/// <c>TaskCompletionSource</c> with its result), and it explicitly rejects re-entering the same
/// state from inside a callback with <c>LockRecursionException</c>. A function that cannot be
/// <c>await</c>ed cannot call another grain, another transactional state, or any I/O from inside
/// that lock.
/// </para>
/// </remarks>
[<Sealed>]
type FunctionalTransactionalState<'State>
    internal
    (
        inner: ITransactionalState<FunctionalTransactionalBox<'State>>,
        initial: unit -> 'State,
        copy: 'State -> 'State,
        descriptor: TransactionalStateDescriptor,
        grainTypeName: string,
        scope: FunctionalStateScope
    ) =

    let description =
        $"the transactional state '{descriptor.StateName}' (storage '{descriptor.StorageName}', stored type '{descriptor.StoredType.FullName}') of grain type '{grainTypeName}'"

    /// <summary>Read the value the box holds, substituting the declared initial value.</summary>
    /// <remarks>
    /// A transactional state has no "record exists" flag of its own. Orleans materializes a state
    /// that was never written by calling <c>new TState()</c>, so a fresh box arrives with
    /// <c>HasValue = false</c>. Substituting on read is deliberate: it stores nothing, so a pure
    /// read never turns into a write and never marks this participant as written.
    /// </remarks>
    member private _.ValueOf(box: FunctionalTransactionalBox<'State>) =
        if box.HasValue then box.Value else initial ()

    /// <summary>
    /// Run one application function inside an Orleans transactional callback without letting its
    /// result type reach <c>CopyResult</c>.
    /// </summary>
    /// <remarks>
    /// <c>TransactionalState.CopyResult&lt;TResult&gt;</c> resolves a <b>required</b>
    /// <c>ITransactionDataCopier&lt;TResult&gt;</c>, so an arbitrary application result type would
    /// make every projection depend on a copier registration it cannot have. The result is
    /// therefore carried out of the callback in a captured cell and the callback itself returns
    /// <c>true</c>. That is sound because Orleans invokes the callback <b>exactly once</b>:
    /// <c>ReaderWriterLock.EnterLock</c> builds a single <c>completion()</c> closure which either
    /// sets the result of one <c>TaskCompletionSource</c> or its exception, and the cell is only
    /// read after that task has completed successfully.
    /// </remarks>
    member private _.Capture(run: FunctionalTransactionalBox<'State> -> 'Result, invoke) : Task<'Result> =
        let mutable captured = Unchecked.defaultof<'Result>

        task {
            let! _ =
                invoke (
                    Func<FunctionalTransactionalBox<'State>, bool>(fun box ->
                        captured <- run box
                        true)
                )

            return captured
        }

    /// <summary>The current transactional value.</summary>
    /// <remarks>
    /// <para>
    /// The only facade member whose result is the stored value itself, so it is the only one that
    /// is copied before it is returned: a caller must never hold the instance the transactional
    /// state is storing, or mutating it would rewrite committed state behind the transaction's
    /// back. That is the same isolation Orleans' own <c>CopyResult</c> gives a
    /// <c>PerformRead</c> result.
    /// </para>
    /// <para>
    /// The copy is made <b>here</b> rather than by Orleans, and deliberately: <c>CopyResult</c>
    /// resolves a required <c>ITransactionDataCopier&lt;TResult&gt;</c> from the activation's
    /// services, so letting the stored type be <c>TResult</c> would mean registering a copier for
    /// an application type — which would then also serve any classic <c>[TransactionalState]</c>
    /// grain in the same silo whose state happens to be that type. Copying after the lock is
    /// released is sound because the runtime only ever <b>replaces</b> <c>Value</c>: a concurrent
    /// update writes a new reference into a box Orleans has already snapshotted, and never touches
    /// the object this read captured.
    /// </para>
    /// </remarks>
    member this.read() : Task<'State> =
        scope.EnsureTransactionalRead(description, "read()")

        task {
            let! value = this.Capture(this.ValueOf, inner.PerformRead)
            return copy value
        }

    /// <summary>A projection of the current transactional value.</summary>
    /// <param name="project">
    /// Runs inside Orleans' transactional read lock. It must be a pure projection of the value it
    /// is handed and must not mutate anything reachable from it. Its result is the application's
    /// own value and is returned uncopied — use <c>read()</c> when the stored value itself is
    /// wanted.
    /// </param>
    member this.readWith(project: 'State -> 'Result) : Task<'Result> =
        if obj.ReferenceEquals(project, null) then
            fail TransactionalStage "'readWith' requires a projection function."

        scope.EnsureTransactionalRead(description, "readWith(project)")

        this.Capture((fun box -> project (this.ValueOf box)), inner.PerformRead)

    /// <summary>Replace the transactional value.</summary>
    /// <param name="next">
    /// Runs inside Orleans' transactional write lock. It receives the current value and returns
    /// the replacement; the runtime performs the single assignment that stores it.
    /// </param>
    member this.update(next: 'State -> 'State) : Task<unit> =
        if obj.ReferenceEquals(next, null) then
            fail TransactionalStage "'update' requires a replacement function."

        scope.EnsureTransactionalUpdate(description, "update(next)")

        task {
            let! _ =
                inner.PerformUpdate(
                    Func<FunctionalTransactionalBox<'State>, bool>(fun box ->
                        box.Value <- next (this.ValueOf box)
                        box.HasValue <- true
                        true)
                )

            return ()
        }

    /// <summary>Replace the transactional value and return a result computed with it.</summary>
    /// <param name="next">
    /// Runs inside Orleans' transactional write lock. It receives the current value and returns
    /// the replacement paired with a result; the runtime performs the single assignment.
    /// </param>
    member this.updateWith(next: 'State -> 'State * 'Result) : Task<'Result> =
        if obj.ReferenceEquals(next, null) then
            fail TransactionalStage "'updateWith' requires a replacement function."

        scope.EnsureTransactionalUpdate(description, "updateWith(next)")

        this.Capture(
            (fun box ->
                let replacement, result = next (this.ValueOf box)
                box.Value <- replacement
                box.HasValue <- true
                result),
            inner.PerformUpdate
        )

/// <summary>
/// The Orleans transaction data copier of one functional transactional facet.
/// </summary>
/// <remarks>
/// <para>
/// Orleans snapshots a transactional state before the first write of a transaction —
/// <c>TransactionalState.PerformUpdate</c> does
/// <c>record.State = this.copier.DeepCopy(record.State)</c> — so that an abort can restore the
/// previous version. Its default copier is <c>DefaultTransactionDataCopier&lt;TState&gt;</c>, which
/// asks the Orleans serializer for a <c>DeepCopier&lt;TState&gt;</c>; that resolves the generated
/// copier of the box, which in turn needs a <c>DeepCopier</c> for the application's state type.
/// The functional runtime deliberately registers the F# generalized codec <b>without</b> its
/// generalized copier (payloads cross an explicit byte boundary instead), so an ordinary F#
/// record has no Orleans copier and the default path fails with "copier not found".
/// </para>
/// <para>
/// This copier is registered per attached facet, closed over the exact stored type, and takes
/// precedence over Orleans' open-generic default because Microsoft.Extensions.DependencyInjection
/// matches an exact closed service type before an open generic one. It produces a fresh box —
/// which is what the snapshot actually needs, since the runtime only ever replaces
/// <c>Value</c> — and a fresh value, through the runtime's own exact-type payload codec, which is
/// the same byte boundary the transport puts between an argument and its handler. Copying the value as well is deliberate: it costs one
/// serialize plus one deserialize per transaction per written state, and in exchange an
/// application that mutates its own state object in place inside an update function cannot
/// corrupt the version an abort has to restore.
/// </para>
/// </remarks>
[<Sealed>]
type internal FunctionalTransactionDataCopier<'StoredState>(codec: IFunctionalPayloadCodec) =

    interface ITransactionDataCopier<FunctionalTransactionalBox<'StoredState>> with
        member _.DeepCopy(original: FunctionalTransactionalBox<'StoredState>) =
            if obj.ReferenceEquals(original, null) then
                null
            else
                let copy = FunctionalTransactionalBox<'StoredState>()

                if original.HasValue then
                    copy.Value <- codec.Deserialize<'StoredState>(codec.Serialize<'StoredState> original.Value)
                    copy.HasValue <- true

                copy

/// <summary>The Orleans facet configuration of one attached transactional state.</summary>
[<Sealed>]
type internal FunctionalTransactionalStateConfiguration(stateName: string, storageName: string) =
    interface ITransactionalStateConfiguration with
        member _.StateName = stateName
        member _.StorageName = storageName

/// <summary>
/// Everything the activator and the invocation context need to work with one attached
/// transactional facet without knowing its stored type. Every function is closed over the exact
/// stored type when the definition is authored, so no silo-side code closes a generic per
/// activation or per call.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalTransactionalBlueprint =
    {
        /// The logical <c>(stateName, storageName, storedType)</c> identity of the facet.
        Descriptor: TransactionalStateDescriptor
        /// Create the real Orleans transactional facet for one activation, boxed.
        Create: ITransactionalStateFactory -> obj
        /// Wrap a boxed facet in an invocation-bound facade, boxed as the exact facade type.
        /// Arguments: the boxed facet, the boxed initial value, the grain type name, the silo's
        /// exact-type payload codec, and the scope.
        Facade: obj -> obj -> string -> IFunctionalPayloadCodec -> FunctionalStateScope -> obj
        /// The declared initializer, from the boxed domain key to the boxed initial value.
        Initialize: obj -> obj
        /// Register the exact-type transaction data copier of this facet on a silo's service
        /// collection. Closed over the stored type at authoring time, so silo startup never
        /// closes a generic for it.
        RegisterServices: IServiceCollection -> unit
    }

/// <summary>Construction of stored-type-closed transactional facet blueprints.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalTransactionalFacet =

    /// <summary>Close one transactional facet blueprint over its exact stored type.</summary>
    let blueprint<'StoredState> (reference: TransactionalStateRef<'StoredState>) (initialize: obj -> obj) =
        let descriptor = reference.Descriptor

        let configuration =
            TransactionalStateConfiguration(
                FunctionalTransactionalStateConfiguration(descriptor.StateName, descriptor.StorageName)
            )

        let facet (instance: obj) =
            unbox<ITransactionalState<FunctionalTransactionalBox<'StoredState>>> instance

        { Descriptor = descriptor
          Create = fun factory -> box (factory.Create<FunctionalTransactionalBox<'StoredState>> configuration)
          Facade =
            fun instance initial grainTypeName codec scope ->
                let value = unbox<'StoredState> initial

                box (
                    FunctionalTransactionalState<'StoredState>(
                        facet instance,
                        (fun () -> value),
                        (fun current -> codec.Deserialize<'StoredState>(codec.Serialize<'StoredState> current)),
                        descriptor,
                        grainTypeName,
                        scope
                    )
                )
          Initialize = initialize
          RegisterServices =
            fun services ->
                services.TryAddSingleton<
                    ITransactionDataCopier<FunctionalTransactionalBox<'StoredState>>,
                    FunctionalTransactionDataCopier<'StoredState>
                 >()

                |> ignore }
