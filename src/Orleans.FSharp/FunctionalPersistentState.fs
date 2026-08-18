namespace Orleans.FSharp

open System
open System.Threading
open System.Threading.Tasks
open Orleans.Core
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics

/// <summary>
/// The logical identity of one attached persistent state: its state name, provider name,
/// and stored CLR type. Lookup and attachment validation compare this triple.
/// </summary>
type internal PersistentStateDescriptor =
    { /// The Orleans state name of this facet.
      StateName: string
      /// The Orleans storage provider name of this facet.
      ProviderName: string
      /// The exact stored CLR type of this facet.
      StoredType: Type }

/// <summary>
/// An immutable logical descriptor of one named persistent state facet.
/// Created by <see cref="M:Orleans.FSharp.PersistentStateModule.create"/> and attached to a
/// definition with <c>stateFrom</c> or <c>usePersistentState</c>.
/// </summary>
[<Sealed>]
type PersistentStateRef<'State> internal (stateName: string, providerName: string) =

    let descriptor =
        { StateName = stateName
          ProviderName = providerName
          StoredType = typeof<'State> }

    /// <summary>The Orleans state name of this facet.</summary>
    member internal _.StateName = stateName

    /// <summary>The Orleans storage provider name of this facet.</summary>
    member internal _.ProviderName = providerName

    /// <summary>The exact stored CLR type of this facet.</summary>
    member internal _.StoredType = typeof<'State>

    /// <summary>The logical <c>(stateName, providerName, storedType)</c> identity of this facet.</summary>
    member internal _.Descriptor = descriptor

    override _.ToString() =
        $"PersistentStateRef(stateName = '{stateName}', providerName = '{providerName}', storedType = '{typeof<'State>.FullName}')"

/// <summary>Creation of immutable persistent-state descriptors.</summary>
[<RequireQualifiedAccess>]
module PersistentState =

    /// <summary>
    /// Create an immutable descriptor for a named persistent state facet.
    /// Blank or NUL-containing names and open generic stored types are rejected immediately.
    /// </summary>
    /// <param name="stateName">Orleans state name; unique within a definition.</param>
    /// <param name="providerName">Name of an <c>IGrainStorage</c> registration on every hosting silo.</param>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="stateName"/> or <paramref name="providerName"/> is blank or contains a NUL
    /// character, or 'State is an open generic type.
    /// </exception>
    let create<'State> (stateName: string) (providerName: string) : PersistentStateRef<'State> =
        if isBlank stateName then
            fail PersistentStage "stateName must be a non-blank string."

        if containsNul stateName then
            fail PersistentStage $"stateName '{stateName}' must not contain a NUL character."

        if isBlank providerName then
            fail PersistentStage $"providerName for stateName '{stateName}' must be a non-blank string."

        if containsNul providerName then
            fail PersistentStage $"providerName '{providerName}' must not contain a NUL character."

        if typeof<'State>.ContainsGenericParameters then
            fail
                PersistentStage
                $"the stored type '{typeof<'State>.FullName}' for stateName '{stateName}' must be a closed type."

        PersistentStateRef<'State>(stateName, providerName)

/// <summary>
/// Which closed stored types stock Orleans cannot hold in an <c>IPersistentState</c> at all.
/// </summary>
/// <remarks>
/// Orleans builds the in-memory state instance of a facet through its serializer activator
/// (<c>DefaultReferenceTypeActivator</c> / <c>DefaultValueTypeActivator</c>), which calls
/// <c>RuntimeHelpers.GetUninitializedObject</c>. That method rejects a fixed set of shapes
/// outright, so an <c>IPersistentState</c> over one of them can never be created — on any
/// storage provider, on Orleans 10.1.0 and 10.2.2 alike. The set below is exactly the set
/// proven to fail by <c>StoredStateActivationTests</c>; every other closed type — including
/// records, classes without a public constructor, structs, enums, F# lists, maps, options,
/// single-case and nullary unions — succeeds and must not be rejected here.
/// </remarks>
[<RequireQualifiedAccess>]
module internal StoredStateType =

    /// <summary>The shared explanation of how Orleans activates a stored persistent-state instance.</summary>
    [<Literal>]
    let private Activator =
        "Orleans creates the in-memory instance of a persistent state with its serializer activator, which calls System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject"

    /// <summary>
    /// The reason stock Orleans cannot activate this stored type, or <c>None</c> when it can.
    /// </summary>
    /// <param name="stored">The candidate stored state type; <c>null</c> is reported as supported.</param>
    let unsupportedReason (stored: Type) : string option =
        if isNull stored then
            None
        elif stored.IsArray then
            Some $"{Activator}; that method cannot create array instances, so an array type is not a usable stored state type"
        elif stored = typeof<string> then
            Some
                $"{Activator}; that method rejects System.String ('Uninitialized Strings cannot be created'), so string is not a usable stored state type. Wrap the value in a record or another class"
        elif typeof<Delegate>.IsAssignableFrom stored then
            Some $"{Activator}; that method cannot create delegate instances, so a delegate type is not a usable stored state type"
        elif stored.IsInterface then
            Some
                $"{Activator}; that method cannot create an instance of an interface, so an interface is not a usable stored state type. Use the concrete stored type"
        elif stored.IsAbstract then
            Some
                $"{Activator}; that method cannot create an instance of an abstract class, so an abstract type is not a usable stored state type. Note that an F# union with two or more cases of which at least one carries data compiles to an abstract base class"
        elif not (isNull (Nullable.GetUnderlyingType stored)) then
            Some
                "Orleans resolves the activator of a value-type state through DefaultValueTypeActivator, whose type parameter excludes System.Nullable, so a nullable value type is not a usable stored state type"
        else
            None

/// <summary>
/// What a callback may do with the transactional facets of its activation.
/// </summary>
/// <remarks>
/// A transactional read and a transactional update both require an ambient
/// <c>TransactionContext</c>: <c>TransactionalState&lt;TState&gt;.PerformRead</c> and
/// <c>PerformUpdate</c> both start with <c>TransactionContext.GetRequiredTransactionInfo()</c>,
/// which throws when none is set. Only a request whose operation declares a transaction option
/// that carries a context ever has one, so every other callback kind gets
/// <see cref="F:Orleans.FSharp.TransactionalAccess.Unavailable"/> and a diagnostic that names the
/// declaration it is missing rather than Orleans' "did you forget a [Transaction] attribute?".
/// </remarks>
type internal TransactionalAccess =
    /// <summary>No ambient transaction is possible in this callback; reads and updates are rejected.</summary>
    | Unavailable
    /// <summary>
    /// A transaction context is available but the transaction is read-only, so an update is
    /// rejected. Orleans would reject it too — <c>PerformUpdate</c> throws
    /// <c>OrleansReadOnlyViolatedException</c> when <c>TransactionInfo.IsReadOnly</c> — but only
    /// for a transaction this call started; the rejection here is unconditional and names the
    /// <c>readOnly</c> declaration that caused it.
    /// </summary>
    | ReadOnlyTransaction
    /// <summary>Reads and updates are both available.</summary>
    | ReadWriteTransaction

/// <summary>
/// The lifetime and mutability guard shared by every state facade handed to one callback. A
/// facade is bound to its invocation: once the callback's task has completed the facade rejects
/// every member, and in a <c>readOnly</c> or <c>alwaysInterleave</c> callback it permits getters
/// while rejecting the <c>State</c> setter and both overloads of <c>ReadStateAsync</c>,
/// <c>WriteStateAsync</c>, and <c>ClearStateAsync</c>. The transactional axis is separate:
/// see <see cref="T:Orleans.FSharp.TransactionalAccess"/>.
/// </summary>
[<Sealed>]
type internal FunctionalStateScope
    (
        grainTypeName: string,
        callbackName: string,
        allowsMutation: bool,
        transactionalAccess: TransactionalAccess
    ) =

    let mutable expired = 0

    /// <summary>The ordinary scope of a callback which can never carry a transaction context.</summary>
    /// <param name="grainTypeName">The grain type name to name in facade diagnostics.</param>
    /// <param name="callbackName">The callback name to name in facade diagnostics.</param>
    /// <param name="allowsMutation">Whether the callback may mutate state or issue storage calls.</param>
    new(grainTypeName: string, callbackName: string, allowsMutation: bool) =
        FunctionalStateScope(grainTypeName, callbackName, allowsMutation, Unavailable)

    /// <summary>True once the owning callback has completed.</summary>
    member _.IsExpired = Volatile.Read(&expired) = 1

    /// <summary>Expire every facade of this callback. Idempotent.</summary>
    member _.Expire() = Volatile.Write(&expired, 1)

    /// <summary>Describe one facet for a diagnostic; never includes the stored value.</summary>
    /// <param name="descriptor">The facet identity to describe.</param>
    member private _.Describe(descriptor: PersistentStateDescriptor) =
        $"the persistent state '{descriptor.StateName}' (provider '{descriptor.ProviderName}', stored type '{descriptor.StoredType.FullName}') of grain type '{grainTypeName}'"

    /// <summary>Reject use of a facade whose callback has already completed.</summary>
    /// <param name="descriptor">The facet identity to describe if rejected.</param>
    /// <param name="memberName">The facade member being used.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The callback which resolved this facade has already completed.
    /// </exception>
    member this.EnsureUsable(descriptor: PersistentStateDescriptor, memberName: string) =
        if this.IsExpired then
            fail
                PersistentStage
                $"{this.Describe descriptor} was used through '{memberName}' after the '{callbackName}' callback which resolved it had already completed. A persistent-state facade is bound to its invocation."

    /// <summary>
    /// Reject use of this activation's journal after the callback which resolved it completed.
    /// </summary>
    /// <remarks>
    /// The journal facade is bound to its invocation for the same reason every other facade is: a
    /// captured context is an ordinary F# value that outlives the turn, and an append made from one
    /// would land outside the per-turn confirmation the whole model rests on.
    /// </remarks>
    /// <param name="memberName">The journal member being used.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The callback which resolved this journal has already completed.
    /// </exception>
    member this.EnsureJournalUsable(memberName: string) =
        if this.IsExpired then
            fail
                JournalStage
                $"the journal of grain type '{grainTypeName}' was used through '{memberName}' after the '{callbackName}' callback which resolved it had already completed. A journal facade is bound to its invocation."

    /// <summary>Reject a journal append from a callback which may run beside another turn.</summary>
    /// <param name="memberName">The journal member being used.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The callback which resolved this journal has already completed, or this callback is
    /// state-neutral and may run beside another turn.
    /// </exception>
    member this.EnsureJournalAppend(memberName: string) =
        this.EnsureJournalUsable memberName

        if not allowsMutation then
            fail
                JournalStage
                $"the journal of grain type '{grainTypeName}' rejects '{memberName}' in the '{callbackName}' callback, which is state-neutral. A 'readOnly' or 'alwaysInterleave' operation may run while another turn of this activation is in flight, so its appends could not be ordered against that turn's."

    /// <summary>Reject a mutating member in a read-only or state-neutral interleaved callback.</summary>
    /// <param name="descriptor">The facet identity to describe if rejected.</param>
    /// <param name="memberName">The facade member being used.</param>
    /// <exception cref="System.InvalidOperationException">
    /// The callback which resolved this facade has already completed, or this callback is
    /// state-neutral and may not mutate the facet or issue storage calls.
    /// </exception>
    member this.EnsureMutable(descriptor: PersistentStateDescriptor, memberName: string) =
        this.EnsureUsable(descriptor, memberName)

        if not allowsMutation then
            fail
                PersistentStage
                $"{this.Describe descriptor} rejects '{memberName}' in the '{callbackName}' callback, which is state-neutral for this holder: it may read it but may not set it or issue storage calls."

    /// <summary>
    /// Reject a transactional read in a callback which can carry no transaction context, or one
    /// whose facade has already expired.
    /// </summary>
    /// <param name="description">
    /// The transactional facet, already described by its facade — the scope deliberately knows
    /// nothing about transactional descriptors, so it stays declared before them.
    /// </param>
    /// <param name="memberName">The facade member being used.</param>
    /// <exception cref="System.InvalidOperationException">
    /// This facade has already expired, or this callback never runs inside an Orleans transaction.
    /// </exception>
    member _.EnsureTransactionalRead(description: string, memberName: string) =
        if Volatile.Read(&expired) = 1 then
            fail
                TransactionalStage
                $"{description} was used through '{memberName}' after the '{callbackName}' callback which resolved it had already completed. A transactional-state facade is bound to its invocation."

        match transactionalAccess with
        | Unavailable ->
            fail
                TransactionalStage
                $"{description} rejects '{memberName}' in the '{callbackName}' callback, which never runs inside an Orleans transaction. Only an operation declared 'transactional' with Create, CreateOrJoin, Join, or Supported carries a transaction context; timers, reminders, lifecycle hooks, and stream deliveries never do."
        | ReadOnlyTransaction
        | ReadWriteTransaction -> ()

    /// <summary>Reject a transactional update in a read-only or non-transactional callback.</summary>
    /// <param name="description">The transactional facet, already described by its facade.</param>
    /// <param name="memberName">The facade member being used.</param>
    /// <exception cref="System.InvalidOperationException">
    /// This facade has already expired, this callback never runs inside an Orleans transaction, or
    /// the transaction is declared 'readOnly'.
    /// </exception>
    member this.EnsureTransactionalUpdate(description: string, memberName: string) =
        this.EnsureTransactionalRead(description, memberName)

        match transactionalAccess with
        | ReadOnlyTransaction ->
            fail
                TransactionalStage
                $"{description} rejects '{memberName}' in the '{callbackName}' callback, which is declared 'readOnly'. A read-only transaction refuses every update: Orleans throws OrleansReadOnlyViolatedException from PerformUpdate when TransactionInfo.IsReadOnly."
        | Unavailable
        | ReadWriteTransaction -> ()

/// <summary>
/// The invocation-bound <c>IPersistentState&lt;'State&gt;</c> handed to application code by
/// <c>context.persistentState</c>. Every member is guarded by the callback's scope and then
/// delegates to the real Orleans facet, so ordinary Orleans semantics are preserved exactly and
/// the runtime adds no read, write, clear, retry, or rollback of its own.
/// </summary>
[<Sealed>]
type internal FunctionalPersistentStateFacade<'State>
    (inner: IPersistentState<'State>, descriptor: PersistentStateDescriptor, scope: FunctionalStateScope) =

    interface IPersistentState<'State>

    interface IStorage<'State> with

        /// <summary>
        /// The current stored value. The getter is guarded by <c>EnsureUsable</c>; the setter is
        /// guarded by <c>EnsureMutable</c> and only replaces the in-memory holder value, never
        /// writing storage.
        /// </summary>
        member _.State
            with get () =
                scope.EnsureUsable(descriptor, "State")
                inner.State
            and set (value: 'State) =
                scope.EnsureMutable(descriptor, "the State setter")
                inner.State <- value

    interface IStorage with

        /// <summary>The storage provider's opaque version tag. Guarded by <c>EnsureUsable</c>.</summary>
        member _.Etag =
            scope.EnsureUsable(descriptor, "Etag")
            inner.Etag

        /// <summary>Whether a durable record exists in storage. Guarded by <c>EnsureUsable</c>.</summary>
        member _.RecordExists =
            scope.EnsureUsable(descriptor, "RecordExists")
            inner.RecordExists

        /// <summary>Reload the value from storage. Guarded by <c>EnsureMutable</c>.</summary>
        member _.ReadStateAsync() =
            scope.EnsureMutable(descriptor, "ReadStateAsync()")
            inner.ReadStateAsync()

        /// <summary>Persist the current value to storage. Guarded by <c>EnsureMutable</c>.</summary>
        member _.WriteStateAsync() =
            scope.EnsureMutable(descriptor, "WriteStateAsync()")
            inner.WriteStateAsync()

        /// <summary>Clear the durable record. Guarded by <c>EnsureMutable</c>.</summary>
        member _.ClearStateAsync() =
            scope.EnsureMutable(descriptor, "ClearStateAsync()")
            inner.ClearStateAsync()

        /// <summary>Reload the value from storage. Guarded by <c>EnsureMutable</c>.</summary>
        /// <param name="cancellationToken">Propagated to the inner Orleans facet.</param>
        member _.ReadStateAsync(cancellationToken: CancellationToken) : Task =
            scope.EnsureMutable(descriptor, "ReadStateAsync(CancellationToken)")
            inner.ReadStateAsync cancellationToken

        /// <summary>Persist the current value to storage. Guarded by <c>EnsureMutable</c>.</summary>
        /// <param name="cancellationToken">Propagated to the inner Orleans facet.</param>
        member _.WriteStateAsync(cancellationToken: CancellationToken) : Task =
            scope.EnsureMutable(descriptor, "WriteStateAsync(CancellationToken)")
            inner.WriteStateAsync cancellationToken

        /// <summary>Clear the durable record. Guarded by <c>EnsureMutable</c>.</summary>
        /// <param name="cancellationToken">Propagated to the inner Orleans facet.</param>
        member _.ClearStateAsync(cancellationToken: CancellationToken) : Task =
            scope.EnsureMutable(descriptor, "ClearStateAsync(CancellationToken)")
            inner.ClearStateAsync cancellationToken

/// <summary>The Orleans facet configuration of one attached persistent state.</summary>
[<Sealed>]
type internal FunctionalPersistentStateConfiguration(stateName: string, storageName: string) =
    interface IPersistentStateConfiguration with
        /// <summary>The Orleans state name.</summary>
        member _.StateName = stateName
        /// <summary>The Orleans storage provider name.</summary>
        member _.StorageName = storageName

/// <summary>
/// Everything the activator, the lifecycle, and the invocation context need to work with one
/// attached facet without knowing its stored type. Every function is closed over the exact
/// stored type when the definition is authored, so no silo-side code closes a generic per
/// activation or per call.
/// </summary>
[<ReferenceEquality>]
type internal FunctionalFacetBlueprint =
    {
        /// The logical <c>(stateName, providerName, storedType)</c> identity of the facet.
        Descriptor: PersistentStateDescriptor
        /// Create the real Orleans facet for one activation, boxed.
        Create: IPersistentStateFactory -> IGrainContext -> obj
        /// Wrap a boxed facet in an invocation-bound facade, boxed as the exact interface.
        Facade: obj -> FunctionalStateScope -> obj
        /// Read the current holder value of a boxed facet.
        GetState: obj -> obj
        /// Replace the holder value of a boxed facet. Never writes storage.
        SetState: obj -> obj -> unit
        /// Whether the boxed facet reports a durable record.
        RecordExists: obj -> bool
        /// The declared initializer, from the boxed domain key to the boxed stored state.
        Initialize: obj -> obj
    }

/// <summary>Construction of stored-type-closed facet blueprints.</summary>
[<RequireQualifiedAccess>]
module internal FunctionalFacet =

    /// <summary>Close one facet blueprint over its exact stored type.</summary>
    /// <param name="reference">The descriptor identifying the facet to close over.</param>
    /// <param name="initialize">The declared initializer, from the boxed domain key to the boxed stored state.</param>
    let blueprint<'StoredState> (reference: PersistentStateRef<'StoredState>) (initialize: obj -> obj) =
        let descriptor = reference.Descriptor

        let configuration =
            FunctionalPersistentStateConfiguration(descriptor.StateName, descriptor.ProviderName)
            :> IPersistentStateConfiguration

        let facet (instance: obj) = unbox<IPersistentState<'StoredState>> instance

        { Descriptor = descriptor
          Create = fun factory context -> box (factory.Create<'StoredState>(context, configuration))
          Facade =
            fun instance scope ->
                box (FunctionalPersistentStateFacade<'StoredState>(facet instance, descriptor, scope))
          GetState = fun instance -> box (facet instance).State
          SetState = fun instance value -> (facet instance).State <- unbox<'StoredState> value
          RecordExists = fun instance -> (facet instance).RecordExists
          Initialize = initialize }
