/// <summary>
/// Persistence tests for spec 003 Phase 4: which stored types stock Orleans can hold in an
/// <c>IPersistentState</c> at all, the sealing rules for attached persistent state, and the
/// invocation-bound state facade's expiry and read-only guards.
/// </summary>
module Orleans.FSharp.Tests.FunctionalPersistentStateTests

open System
open System.Collections.Generic
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open Swensen.Unquote
open Orleans.Core
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Serialization.Serializers
open Orleans.FSharp

// ──────────────────────────────────────────────────────────────────────────────
// Ground truth: what stock Orleans can activate as a stored state
// ──────────────────────────────────────────────────────────────────────────────

type StoredRecord = { value: string }

type StoredClass(value: int) =
    member _.Value = value

[<Sealed>]
type StoredPrivateCtor private () =
    static member Make() = StoredPrivateCtor()

[<AbstractClass>]
type StoredAbstract() =
    abstract Describe: unit -> string

type StoredInterface =
    abstract Describe: unit -> string

type NullaryUnion =
    | First
    | Second

type SingleCaseUnion = SingleCaseUnion of int

type UnionWithData =
    | Empty
    | Value of int

[<Struct>]
type StructUnion =
    | StructEmpty
    | StructValue of value: int

/// <summary>
/// A serializer-only container: Orleans resolves the activator of a state type through the very
/// same <c>IActivatorProvider</c> that <c>StateStorageBridge</c> uses, so this is the real
/// mechanism and not an imitation of it.
/// </summary>
let private serializerServices =
    lazy
        (let services = ServiceCollection()
         ServiceCollectionExtensions.AddSerializer(services, Action<ISerializerBuilder>(ignore)) |> ignore
         services.BuildServiceProvider() :> IServiceProvider)

let rec private innermost (error: exn) =
    match error.InnerException with
    | null -> error
    | inner -> innermost inner

/// <summary>Ask stock Orleans to create an instance of one candidate stored type.</summary>
let private stockOrleansCanActivate (stored: Type) =
    let provider =
        serializerServices.Value.GetRequiredService<IActivatorProvider>()

    try
        let closed =
            typeof<IActivatorProvider>
                .GetMethod("GetActivator")
                .MakeGenericMethod [| stored |]

        let activator = closed.Invoke(provider, [||])

        activator
            .GetType()
            .GetMethod("Create")
            .Invoke(activator, [||])
        |> ignore

        Ok()
    with error ->
        Error (innermost error)

/// <summary>
/// Every shape worth asking about, mixing the ones expected to fail with the ones expected to
/// succeed. The test below never names which is which — it re-derives that from Orleans.
/// </summary>
let private candidateStoredTypes: Type list =
    [ typeof<string>
      typeof<byte[]>
      typeof<int[]>
      typeof<string[]>
      typeof<int[,]>
      typeof<Func<int, int>>
      typeof<Action>
      typeof<MulticastDelegate>
      typeof<StoredInterface>
      typeof<StoredAbstract>
      typeof<UnionWithData>
      typeof<Nullable<int>>
      typeof<StoredRecord>
      typeof<StoredClass>
      typeof<StoredPrivateCtor>
      typeof<NullaryUnion>
      typeof<SingleCaseUnion>
      typeof<StructUnion>
      typeof<int>
      typeof<decimal>
      typeof<Guid>
      typeof<DayOfWeek>
      typeof<DateTimeOffset>
      typeof<obj>
      typeof<Uri>
      typeof<StoredRecord list>
      typeof<StoredRecord option>
      typeof<Map<string, int>>
      typeof<Set<string>>
      typeof<ResizeArray<string>>
      typeof<Dictionary<string, int>>
      typeof<int * string>
      typeof<struct (int * string)> ]

/// <remarks>
/// This is the invariant behind the sealing rule, not a snapshot of it: for every candidate the
/// runtime's own verdict is compared with what stock Orleans actually does. It therefore fails
/// both ways — if the runtime rejects a type Orleans can hold (over-rejection) and if it accepts
/// one Orleans cannot (under-rejection) — and it re-derives ground truth on whichever Orleans
/// version the suite runs against.
/// </remarks>
[<Fact>]
let ``the stored-type rule matches exactly what stock Orleans can activate`` () =
    let mismatches =
        candidateStoredTypes
        |> List.choose (fun stored ->
            let orleans = stockOrleansCanActivate stored
            let rule = StoredStateType.unsupportedReason stored

            match orleans, rule with
            | Ok(), None -> None
            | Error _, Some _ -> None
            | Ok(), Some reason -> Some $"{stored.FullName}: Orleans CAN activate it but the rule rejects it ({reason})"
            | Error error, None ->
                Some
                    $"{stored.FullName}: Orleans CANNOT activate it ({error.GetType().Name}: {error.Message}) but the rule accepts it")

    test <@ mismatches = [] @>

/// <remarks>
/// Guards the invariant test above against a degenerate pass: if the candidate list ever stopped
/// containing types stock Orleans rejects, the comparison would hold vacuously.
/// </remarks>
[<Fact>]
let ``the candidate set really contains both accepted and rejected stored types`` () =
    let rejected =
        candidateStoredTypes
        |> List.filter (fun stored -> (stockOrleansCanActivate stored).IsError)

    let accepted =
        candidateStoredTypes
        |> List.filter (fun stored -> (stockOrleansCanActivate stored).IsOk)

    test <@ List.contains typeof<string> rejected @>
    test <@ List.contains typeof<byte[]> rejected @>
    test <@ List.contains typeof<Func<int, int>> rejected @>
    test <@ rejected.Length >= 8 @>
    test <@ accepted.Length >= 15 @>

// ──────────────────────────────────────────────────────────────────────────────
// Sealing: attachment identity and stored types
// ──────────────────────────────────────────────────────────────────────────────

type StateActor = private StateActor of unit

[<NoEquality; NoComparison>]
type StateApi = { touch: string -> Task<int> }

type PrimaryState = { count: int }
type AuditState = { total: int64 }

let private stateContract =
    grainContract<StateActor, string, StateApi> () {
        grainType "state.probe"
        stringKey
    }

let private touchHandler _ state (_: string) = task { return state, 1 }

let private throws (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

[<Fact>]
let ``the primary descriptor must not be repeated with usePersistentState`` () =
    let primary = PersistentState.create<PrimaryState> "shared" "Default"

    let error =
        throws (fun () ->
            grainFor stateContract {
                defaultState (fun () -> { count = 0 })
                stateFrom primary
                usePersistentState primary (fun _ -> { count = 0 })
                handle (_.touch) touchHandler
            }
            |> ignore)

    test <@ error.Message.Contains "attached more than once" @>
    test <@ error.Message.Contains "'stateFrom' descriptor is already attached as the primary state" @>

/// <remarks>
/// Spec "Persistence and activation lifecycle": "State names are unique within a definition even
/// when providers differ, because Orleans activation-migration keys are based on the state name."
/// </remarks>
[<Fact>]
let ``the same stateName under a different provider still fails sealing`` () =
    let blue = PersistentState.create<PrimaryState> "ledger" "Blue"
    let green = PersistentState.create<AuditState> "ledger" "Green"

    let error =
        throws (fun () ->
            grainFor stateContract {
                defaultState (fun () -> { count = 0 })
                stateFrom blue
                usePersistentState green (fun _ -> { total = 0L })
                handle (_.touch) touchHandler
            }
            |> ignore)

    test <@ error.Message.Contains "stateName 'ledger' is attached more than once" @>
    test <@ error.Message.Contains "Blue" @>
    test <@ error.Message.Contains "Green" @>

[<Fact>]
let ``two additional states with one name and one provider but different stored types fail`` () =
    let first = PersistentState.create<PrimaryState> "extra" "Default"
    let second = PersistentState.create<AuditState> "extra" "Default"

    let error =
        throws (fun () ->
            grainFor stateContract {
                defaultState (fun () -> { count = 0 })
                usePersistentState first (fun _ -> { count = 0 })
                usePersistentState second (fun _ -> { total = 0L })
                handle (_.touch) touchHandler
            }
            |> ignore)

    test <@ error.Message.Contains "stateName 'extra' is attached more than once" @>
    test <@ error.Message.Contains typeof<PrimaryState>.FullName @>
    test <@ error.Message.Contains typeof<AuditState>.FullName @>

[<Fact>]
let ``a stored type stock Orleans cannot construct fails sealing with the Orleans reason`` () =
    let text = PersistentState.create<string> "text" "Default"

    let error =
        throws (fun () ->
            grainFor stateContract {
                defaultState (fun () -> { count = 0 })
                usePersistentState text (fun _ -> "")
                handle (_.touch) touchHandler
            }
            |> ignore)

    test <@ error.Message.Contains "cannot be held in an Orleans IPersistentState" @>
    test <@ error.Message.Contains "GetUninitializedObject" @>
    test <@ error.Message.Contains "Uninitialized Strings cannot be created" @>
    test <@ error.Message.Contains "text" @>

[<Fact>]
let ``an unconstructable primary stored type fails sealing too`` () =
    let bytes = PersistentState.create<byte[]> "blob" "Default"

    let error =
        throws (fun () ->
            grainFor stateContract {
                defaultState (fun () -> Array.empty<byte>)
                stateFrom bytes
                handle (_.touch) (fun _ state (_: string) -> task { return state, 1 })
            }
            |> ignore)

    test <@ error.Message.Contains "cannot be held in an Orleans IPersistentState" @>
    test <@ error.Message.Contains "array" @>

[<Fact>]
let ``ordinary F# state types are not rejected by the stored-type rule`` () =
    let primary = PersistentState.create<PrimaryState> "state" "Default"
    let audit = PersistentState.create<AuditState> "audit" "Audit"
    let nullary = PersistentState.create<NullaryUnion> "flag" "Flags"
    let single = PersistentState.create<SingleCaseUnion> "single" "Singles"

    let definition =
        grainFor stateContract {
            defaultState (fun () -> { count = 0 })
            stateFrom primary
            usePersistentState audit (fun _ -> { total = 0L })
            usePersistentState nullary (fun _ -> First)
            usePersistentState single (fun _ -> SingleCaseUnion 1)
            handle (_.touch) touchHandler
        }

    test <@ definition.Primary.IsSome @>
    test <@ definition.Additional.Length = 3 @>

// ──────────────────────────────────────────────────────────────────────────────
// The invocation-bound facade
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A recording <c>IPersistentState</c>: the facade must never reach it when it rejects.</summary>
[<Sealed>]
type private RecordingFacet<'State>(initial: 'State) =
    member val Reads = 0 with get, set
    member val Writes = 0 with get, set
    member val Clears = 0 with get, set
    member val Current = initial with get, set

    interface IPersistentState<'State>

    interface IStorage<'State> with
        member this.State
            with get () = this.Current
            and set value = this.Current <- value

    interface IStorage with
        member _.Etag = "etag-1"
        member _.RecordExists = true

        member this.ReadStateAsync() =
            this.Reads <- this.Reads + 1
            Task.CompletedTask

        member this.WriteStateAsync() =
            this.Writes <- this.Writes + 1
            Task.CompletedTask

        member this.ClearStateAsync() =
            this.Clears <- this.Clears + 1
            Task.CompletedTask

        member this.ReadStateAsync(_cancellationToken: CancellationToken) =
            this.Reads <- this.Reads + 1
            Task.CompletedTask

        member this.WriteStateAsync(_cancellationToken: CancellationToken) =
            this.Writes <- this.Writes + 1
            Task.CompletedTask

        member this.ClearStateAsync(_cancellationToken: CancellationToken) =
            this.Clears <- this.Clears + 1
            Task.CompletedTask

let private facadeOver (inner: RecordingFacet<PrimaryState>) (scope: FunctionalStateScope) =
    let descriptor =
        (PersistentState.create<PrimaryState> "state" "Default").Descriptor

    FunctionalPersistentStateFacade<PrimaryState>(inner, descriptor, scope) :> IPersistentState<PrimaryState>

/// <summary>Every mutating member of the facade, named as the diagnostics name them.</summary>
let private mutations: (string * (IPersistentState<PrimaryState> -> unit)) list =
    [ "the State setter", fun facade -> facade.State <- { count = 99 }
      "ReadStateAsync()", fun facade -> facade.ReadStateAsync().GetAwaiter().GetResult()
      "WriteStateAsync()", fun facade -> facade.WriteStateAsync().GetAwaiter().GetResult()
      "ClearStateAsync()", fun facade -> facade.ClearStateAsync().GetAwaiter().GetResult()
      "ReadStateAsync(CancellationToken)",
      fun facade -> facade.ReadStateAsync(CancellationToken.None).GetAwaiter().GetResult()
      "WriteStateAsync(CancellationToken)",
      fun facade -> facade.WriteStateAsync(CancellationToken.None).GetAwaiter().GetResult()
      "ClearStateAsync(CancellationToken)",
      fun facade -> facade.ClearStateAsync(CancellationToken.None).GetAwaiter().GetResult() ]

[<Fact>]
let ``a read-only scope permits getters and rejects the complete mutation surface`` () =
    let inner = RecordingFacet { count = 1 }
    let scope = FunctionalStateScope("state.probe", "peek", false)
    let facade = facadeOver inner scope

    // Getters stay available.
    test <@ facade.State = { count = 1 } @>
    test <@ facade.Etag = "etag-1" @>
    test <@ facade.RecordExists @>

    for name, mutate in mutations do
        let error = throws (fun () -> mutate facade)
        test <@ error.Message.Contains name @>
        // Reason-agnostic wording: the same scope (allowsMutation=false) also guards an
        // onLifecycle hook's persistent-state facade, which is not "declared readOnly or
        // alwaysInterleave" at all -- see FunctionalStateScope.EnsureMutable.
        test <@ error.Message.Contains "state-neutral for this holder" @>

    // Nothing reached the real facet.
    test <@ inner.Current = { count = 1 } @>
    test <@ inner.Reads = 0 && inner.Writes = 0 && inner.Clears = 0 @>

[<Fact>]
let ``an expired scope rejects the complete surface, getters included`` () =
    let inner = RecordingFacet { count = 1 }
    let scope = FunctionalStateScope("state.probe", "touch", true)
    let facade = facadeOver inner scope

    // While the callback runs the facade is a plain pass-through.
    facade.State <- { count = 2 }
    facade.WriteStateAsync().GetAwaiter().GetResult()
    test <@ inner.Current = { count = 2 } @>
    test <@ inner.Writes = 1 @>

    scope.Expire()

    let getters: (string * (IPersistentState<PrimaryState> -> unit)) list =
        [ "State", fun facade -> facade.State |> ignore
          "Etag", fun facade -> facade.Etag |> ignore
          "RecordExists", fun facade -> facade.RecordExists |> ignore ]

    for name, read in getters @ mutations do
        let error = throws (fun () -> read facade)
        test <@ error.Message.Contains name @>
        test <@ error.Message.Contains "had already completed" @>

    test <@ inner.Current = { count = 2 } @>
    test <@ inner.Writes = 1 && inner.Reads = 0 && inner.Clears = 0 @>

[<Fact>]
let ``a facade diagnostic names the facet without revealing the stored value`` () =
    let inner = RecordingFacet { count = 4242 }
    let scope = FunctionalStateScope("state.probe", "peek", false)
    let facade = facadeOver inner scope

    let error = throws (fun () -> facade.WriteStateAsync().GetAwaiter().GetResult())

    test <@ error.Message.Contains "'state'" @>
    test <@ error.Message.Contains "'Default'" @>
    test <@ error.Message.Contains typeof<PrimaryState>.FullName @>
    test <@ error.Message.Contains "state.probe" @>
    test <@ not (error.Message.Contains "4242") @>
