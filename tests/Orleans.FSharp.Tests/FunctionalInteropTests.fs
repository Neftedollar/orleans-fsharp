/// <summary>
/// C#-callable facade tests for spec 004 item 9: every binding rule
/// <c>FunctionalGrainInterop.For</c> applies, each one proven to fire while the facade is
/// created rather than on a call, plus the happy path of every argument and reply shape over
/// the in-memory transport and the preclosing promise the hot path depends on.
/// </summary>
/// <remarks>
/// The facade interfaces are C#-declared (<c>tests/Orleans.FSharp.Tests.Facades</c>): two
/// rejection rules -- default interface methods and events -- cannot be written in F# at all, and
/// every accepted interface there is literally the C# a consumer writes. The F#-declared
/// interfaces in this file cover what C# cannot express instead: an interface that is not public,
/// and the attribute applied from F#.
/// </remarks>
module Orleans.FSharp.Tests.FunctionalInteropTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Tests.Facades
open Orleans.FSharp.Tests.FunctionalTransportHarness

type InteropActor = private InteropActor of unit

/// <summary>
/// The contract the C# facades bind to. Every shape the rules distinguish is present: a
/// unit-argument operation, single-argument operations, tuple-argument operations, a unit reply,
/// a <c>Result</c> reply, and a list reply.
/// </summary>
[<NoEquality; NoComparison>]
type FacadeApi =
    { join: string -> Task<unit>
      leave: string -> Task<unit>
      say: (string * string) -> Task<Result<int64, string>>
      history: int -> Task<string list>
      memberCount: unit -> Task<int>
      typing: (string * bool) -> Task<unit> }

let private contract =
    grainContract<InteropActor, string, FacadeApi> {
        grainType "interop.room"
        version 2
        stringKey
        readOnly (_.history)
    }

type AmbiguousActor = private AmbiguousActor of unit

/// <summary>
/// Two operations whose IDs differ only by case. Contract sealing compares operation IDs with
/// ordinal equality, so this is a legal contract -- and it is exactly the contract the facade's
/// case-insensitive member match cannot resolve on its own.
/// </summary>
[<NoEquality; NoComparison>]
type AmbiguousApi =
    { say: string -> Task<int>
      Say: string -> Task<int> }

let private ambiguousContract =
    grainContract<AmbiguousActor, string, AmbiguousApi> {
        grainType "interop.ambiguous"
        stringKey
    }

// ──────────────────────────────────────────────────────────────────────────────
// Fixtures
// ──────────────────────────────────────────────────────────────────────────────

let private newTarget (services: IServiceProvider) =
    let target = InMemoryTarget(services, "interop.room", 2)
    target.Handle<string, unit>("join", fun _ -> ())
    target.Handle<string, unit>("leave", fun _ -> ())

    target.Handle<string * string, Result<int64, string>>(
        "say",
        fun (author, text) ->
            if String.IsNullOrWhiteSpace text then
                Error "empty"
            else
                Ok(int64 (author.Length + text.Length))
    )

    target.Handle<int, string list>("history", fun take -> [ for index in 1..take -> $"m{index}" ])
    target.Handle<unit, int>("memberCount", fun () -> 7)
    target.Handle<string * bool, unit>("typing", fun _ -> ())
    target

let private newAmbiguousTarget (services: IServiceProvider) =
    let target = InMemoryTarget(services, "interop.ambiguous", 1)
    target.Handle<string, int>("say", fun text -> text.Length)
    target.Handle<string, int>("Say", fun text -> -text.Length)
    target

/// <summary>A facade over a fresh in-memory transport carrying the room contract.</summary>
let private facadeFor<'TFacade when 'TFacade: not struct> () =
    let services = buildServices true None
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)
    transport, FunctionalGrainInterop.For<'TFacade>(contract, transport, "general")

let private ambiguousFacadeFor<'TFacade when 'TFacade: not struct> () =
    let services = buildServices true None
    let target = newAmbiguousTarget services
    let transport = InMemoryTransport(services, target.Dispatch)
    transport, FunctionalGrainInterop.For<'TFacade>(ambiguousContract, transport, "general")

/// <summary>
/// Create a facade the rules must reject. The factory is the unconfigured one on purpose: a rule
/// that fired only after the reference was bound would fail with the transport's diagnostic
/// instead of the facade's, so every message asserted below is proof the rule ran first.
/// </summary>
let private rejected<'TFacade when 'TFacade: not struct> () =
    Assert.Throws<InvalidOperationException>(fun () ->
        FunctionalGrainInterop.For<'TFacade>(contract, UnconfiguredFactory(), "general") |> ignore)

let private rejectedAmbiguous<'TFacade when 'TFacade: not struct> () =
    Assert.Throws<InvalidOperationException>(fun () ->
        FunctionalGrainInterop.For<'TFacade>(ambiguousContract, UnconfiguredFactory(), "general")
        |> ignore)

let private throws (action: unit -> unit) =
    Assert.Throws<InvalidOperationException>(action)

// ──────────────────────────────────────────────────────────────────────────────
// Happy path: every argument and reply shape
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a facade calls every argument and reply shape through the contract`` () =
    let transport, room = facadeFor<IRoomFacade> ()

    task {
        // Single argument, unit reply declared as the non-generic Task.
        do! room.Join "alice"
        // Single argument, unit reply declared as Task<Unit>.
        let! left = room.Leave "bob"
        test <@ isNull (box left) @>
        // Tuple argument packed from two parameters, Result reply.
        let! posted = room.Say("alice", "hello")
        test <@ posted = Ok 10L @>
        let! rejected = room.Say("alice", "   ")
        test <@ rejected = Error "empty" @>
        // Single argument, F# list reply.
        let! history = room.History 3
        test <@ List.ofSeq history = [ "m1"; "m2"; "m3" ] @>
        // Unit argument: a parameterless member.
        let! count = room.MemberCount()
        test <@ count = 7 @>
        // Tuple argument with a non-string element.
        do! room.Typing("alice", true)

        test
            <@
                transport.Calls
                |> Array.map (fun call -> call.Envelope.OperationId) = [| "join"
                                                                          "leave"
                                                                          "say"
                                                                          "say"
                                                                          "history"
                                                                          "memberCount"
                                                                          "typing" |]
            @>

        // The facade sends the same envelope an F# caller sends: same grain type, same contract
        // version, and the contract's own admission flags (history is readOnly).
        test <@ transport.Calls |> Array.forall (fun call -> call.Envelope.GrainType = "interop.room") @>
        test <@ transport.Calls |> Array.forall (fun call -> call.Envelope.ContractVersion = 2) @>

        test
            <@
                transport.Calls
                |> Array.find (fun call -> call.Envelope.OperationId = "history")
                |> fun call -> call.Envelope.AdmissionFlags = AdmissionFlags.ReadOnly
            @>
    }

[<Fact>]
let ``a facade may cover only part of the contract`` () =
    let transport, room = facadeFor<IPartialFacade> ()

    task {
        do! room.Join "alice"
        let! count = room.MemberCount()
        test <@ count = 7 @>
        test <@ transport.Calls.Length = 2 @>
    }

[<Fact>]
let ``a tuple argument may also be taken as one parameter of the tuple type`` () =
    let _transport, room = facadeFor<ITupleAsSingleFacade> ()

    task {
        let! posted = room.Say(Tuple.Create("alice", "hi"))
        test <@ posted = Ok 7L @>
    }

[<Fact>]
let ``members inherited from an extended interface are bound too`` () =
    let transport, room = facadeFor<IExtendedFacade> ()

    task {
        do! room.Join "alice"
        let! count = room.MemberCount()
        test <@ count = 7 @>

        test
            <@
                transport.Calls
                |> Array.map (fun call -> call.Envelope.OperationId)
                |> Array.sort = [| "join"; "memberCount" |]
            @>
    }

[<Fact>]
let ``a member name maps to an operation ID case-insensitively`` () =
    // MemberCount -> memberCount is the whole PascalCase-to-camelCase story; nothing else is
    // needed for a C# member to reach an F# record field.
    let transport, room = facadeFor<IPartialFacade> ()

    task {
        let! _ = room.MemberCount()
        test <@ transport.LastCall.Envelope.OperationId = "memberCount" @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// The explicit override
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the operation attribute overrides the member name`` () =
    let transport, room = facadeFor<IRenamedFacade> ()

    task {
        let! posted = room.Post("alice", "hello")
        test <@ posted = Ok 10L @>
        test <@ transport.LastCall.Envelope.OperationId = "say" @>
    }

[<Fact>]
let ``two members may alias one operation`` () =
    let transport, room = facadeFor<IAliasFacade> ()

    task {
        do! room.Join "alice"
        do! room.Enter "bob"

        test
            <@ transport.Calls |> Array.map (fun call -> call.Envelope.OperationId) = [| "join"; "join" |] @>
    }

[<Fact>]
let ``an override that names no operation is rejected`` () =
    let error = rejected<IUnknownOverrideFacade> ()
    test <@ error.Message.Contains "[FunctionalOperation(\"shout\")]" @>
    test <@ error.Message.Contains "names no operation" @>
    test <@ error.Message.Contains "'join'" @>

[<Fact>]
let ``an override is matched exactly, not case-insensitively`` () =
    let error = rejected<ICaseFoldedOverrideFacade> ()
    test <@ error.Message.Contains "[FunctionalOperation(\"JOIN\")]" @>
    test <@ error.Message.Contains "matched exactly" @>

[<Fact>]
let ``a blank override is rejected`` () =
    let error = rejected<IBlankOverrideFacade> ()
    test <@ error.Message.Contains "blank operation ID" @>

// ──────────────────────────────────────────────────────────────────────────────
// Ambiguity
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a member matching two operations case-insensitively is rejected, naming both`` () =
    let error = rejectedAmbiguous<IAmbiguousFacade> ()
    test <@ error.Message.Contains "matches 2 operations" @>
    test <@ error.Message.Contains "'say'" @>
    test <@ error.Message.Contains "'Say'" @>
    test <@ error.Message.Contains "[FunctionalOperation(\"...\")]" @>

[<Fact>]
let ``the attribute disambiguates two operations differing only by case`` () =
    let transport, room = ambiguousFacadeFor<IDisambiguatedFacade> ()

    task {
        let! value = room.SAY "abc"
        // The negative reply is the 'Say' handler, so this proves which of the two it reached.
        test <@ value = -3 @>
        test <@ transport.LastCall.Envelope.OperationId = "Say" @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// Rule 2: every member must map
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a member matching no operation is rejected, listing the candidates`` () =
    let error = rejected<IUnmappedFacade> ()
    test <@ error.Message.Contains "'Shout'" @>
    test <@ error.Message.Contains "matches no operation" @>
    test <@ error.Message.Contains "'join', 'leave', 'say', 'history', 'memberCount', 'typing'" @>

[<Fact>]
let ``an unmapped member of an extended interface is rejected, naming that interface`` () =
    let error = rejected<IExtendedUnmappedFacade> ()
    test <@ error.Message.Contains "'Shout'" @>
    test <@ error.Message.Contains "matches no operation" @>

// ──────────────────────────────────────────────────────────────────────────────
// Rule 5: member shapes a facade cannot dispatch
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a generic member is rejected`` () =
    let error = rejected<IGenericMemberFacade> ()
    test <@ error.Message.Contains "'Join'" @>
    test <@ error.Message.Contains "is generic" @>

[<Fact>]
let ``a ref parameter is rejected`` () =
    let error = rejected<IRefParameterFacade> ()
    test <@ error.Message.Contains "a 'ref' parameter" @>

[<Fact>]
let ``an out parameter is rejected`` () =
    let error = rejected<IOutParameterFacade> ()
    test <@ error.Message.Contains "an 'out' parameter" @>

[<Fact>]
let ``an in parameter is rejected`` () =
    let error = rejected<IInParameterFacade> ()
    test <@ error.Message.Contains "an 'in' parameter" @>

[<Fact>]
let ``a property is rejected`` () =
    let error = rejected<IPropertyFacade> ()
    test <@ error.Message.Contains "declares property 'Join'" @>

[<Fact>]
let ``an event is rejected`` () =
    let error = rejected<IEventFacade> ()
    test <@ error.Message.Contains "declares event 'Typing'" @>

[<Fact>]
let ``a default interface method is rejected`` () =
    let error = rejected<IDefaultImplementationFacade> ()
    test <@ error.Message.Contains "carries a default implementation" @>

[<Fact>]
let ``a static member is rejected`` () =
    let error = rejected<IStaticMemberFacade> ()
    test <@ error.Message.Contains "'Helper'" @>
    test <@ error.Message.Contains "is static" @>

[<Fact>]
let ``an inherited BCL interface whose member is not a grain call is rejected`` () =
    // IDisposable.Dispose returns void, so it fails the reply rule rather than being ignored.
    let error = rejected<IDisposableFacade> ()
    test <@ error.Message.Contains "'Dispose'" @>

// ──────────────────────────────────────────────────────────────────────────────
// Rule 4: the reply shape
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a void member is rejected`` () =
    let error = rejected<IVoidReplyFacade> ()
    test <@ error.Message.Contains "returns 'System.Void'" @>
    test <@ error.Message.Contains "'System.Threading.Tasks.Task'" @>

[<Fact>]
let ``a ValueTask member is rejected`` () =
    let error = rejected<IValueTaskReplyFacade> ()
    test <@ error.Message.Contains "System.Threading.Tasks.ValueTask" @>

[<Fact>]
let ``a member whose Task element type is not the reply type is rejected`` () =
    let error = rejected<IWrongReplyFacade> ()
    test <@ error.Message.Contains "'Say'" @>
    test <@ error.Message.Contains "Task<Microsoft.FSharp.Core.FSharpResult`2" @>

[<Fact>]
let ``the bare Task is accepted only for a unit reply`` () =
    let error = rejected<IBareTaskForNonUnitReplyFacade> ()
    test <@ error.Message.Contains "'MemberCount'" @>
    test <@ error.Message.Contains "requires 'Task<System.Int32>'" @>

// ──────────────────────────────────────────────────────────────────────────────
// Rule 3: the argument shape
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a parameterless member for a non-unit argument is rejected`` () =
    let error = rejected<IMissingArgumentFacade> ()
    test <@ error.Message.Contains "declares no parameters" @>
    test <@ error.Message.Contains "one parameter of type 'System.String'" @>

[<Fact>]
let ``a parameter of the wrong type is rejected`` () =
    let error = rejected<IWrongArgumentTypeFacade> ()
    test <@ error.Message.Contains "declares ('System.Int32')" @>
    test <@ error.Message.Contains "one parameter of type 'System.String'" @>

[<Fact>]
let ``too many parameters for a single argument are rejected`` () =
    let error = rejected<ITooManyArgumentsFacade> ()
    test <@ error.Message.Contains "declares ('System.String', 'System.String')" @>
    test <@ error.Message.Contains "one parameter of type 'System.String'" @>

[<Fact>]
let ``a parameter count other than the tuple arity is rejected`` () =
    let error = rejected<ITupleArityFacade> ()
    test <@ error.Message.Contains "or 2 parameters" @>

[<Fact>]
let ``a parameter whose type is not the tuple element is rejected`` () =
    let error = rejected<ITupleElementFacade> ()
    test <@ error.Message.Contains "declares ('System.String', 'System.Int32')" @>
    test <@ error.Message.Contains "'System.String', 'System.String'" @>

[<Fact>]
let ``a parameter on a unit-argument operation is rejected`` () =
    let error = rejected<IUnitArgumentWithParameterFacade> ()
    test <@ error.Message.Contains "'MemberCount'" @>
    test <@ error.Message.Contains "takes no parameters" @>

[<Fact>]
let ``a cancellation token is not part of the facade surface`` () =
    // The cancellable form is reached through FunctionalGrainRef.callCancellable, not through a
    // trailing CancellationToken parameter: to the argument rule this is simply an extra
    // parameter, and the diagnostic says so rather than silently dropping the token.
    let error = rejected<ICancellableFacade> ()
    test <@ error.Message.Contains "System.Threading.CancellationToken" @>
    test <@ error.Message.Contains "one parameter of type 'System.String'" @>

// ──────────────────────────────────────────────────────────────────────────────
// What F# can express and C# cannot
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>A facade interface that is not public.</summary>
type internal IInternalFacade =
    abstract Join: user: string -> Task
    abstract MemberCount: unit -> Task<int>

/// <summary>The attribute applied from F#, where the internal FunctionalOperation record of the
/// same base name is also in scope.</summary>
type IFSharpRenamedFacade =
    [<FunctionalOperation("say")>]
    abstract Post: author: string * text: string -> Task<Result<int64, string>>

[<Fact>]
let ``a non-public facade interface is supported`` () =
    // DispatchProxy emits IgnoresAccessChecksTo for a non-public interface, so this works and is
    // not rejected. Recorded as a test because it is a behaviour, not an accident.
    let transport, room = facadeFor<IInternalFacade> ()

    task {
        do! room.Join "alice"
        let! count = room.MemberCount()
        test <@ count = 7 @>
        test <@ transport.Calls.Length = 2 @>
    }

[<Fact>]
let ``the operation attribute resolves from F# too`` () =
    let transport, room = facadeFor<IFSharpRenamedFacade> ()

    task {
        let! posted = room.Post("alice", "hello")
        test <@ posted = Ok 10L @>
        test <@ transport.LastCall.Envelope.OperationId = "say" @>
    }

// ──────────────────────────────────────────────────────────────────────────────
// The facade's own arguments
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a facade type that is not an interface is rejected`` () =
    let error =
        throws (fun () ->
            FunctionalGrainInterop.For<string>(contract, UnconfiguredFactory(), "general") |> ignore)

    test <@ error.Message.Contains "is not an interface" @>

[<Fact>]
let ``a null contract is rejected`` () =
    let error =
        throws (fun () ->
            FunctionalGrainInterop.For<IRoomFacade>(
                Unchecked.defaultof<FunctionalContract>,
                UnconfiguredFactory(),
                "general"
            )
            |> ignore)

    test <@ error.Message.Contains "requires a contract" @>

[<Fact>]
let ``a null grain factory is rejected`` () =
    let error =
        throws (fun () ->
            FunctionalGrainInterop.For<IRoomFacade>(contract, Unchecked.defaultof<IGrainFactory>, "general")
            |> ignore)

    test <@ error.Message.Contains "requires a grain factory" @>

[<Fact>]
let ``a null key is rejected`` () =
    let error =
        throws (fun () ->
            FunctionalGrainInterop.For<IRoomFacade>(contract, UnconfiguredFactory(), null) |> ignore)

    test <@ error.Message.Contains "requires a domain key of type 'System.String'" @>
    test <@ error.Message.Contains "null was supplied" @>

[<Fact>]
let ``a key of the wrong type is rejected`` () =
    let error =
        throws (fun () ->
            FunctionalGrainInterop.For<IRoomFacade>(contract, UnconfiguredFactory(), box 42) |> ignore)

    test <@ error.Message.Contains "requires a domain key of type 'System.String'" @>
    test <@ error.Message.Contains "a 'System.Int32' was supplied" @>

// ──────────────────────────────────────────────────────────────────────────────
// Preclosing: what a call does and does not do
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a facade call performs no reflection, selector evaluation, or generic closing`` () =
    let services = buildServices true None
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)
    let room = FunctionalGrainInterop.For<IRoomFacade>(contract, transport, "general")

    task {
        // Warm every payload codec first: a cold codec build is not what this test is about.
        do! room.Join "warm"
        let! _ = room.Leave "warm"
        let! _ = room.Say("warm", "warm")
        let! _ = room.History 1
        let! _ = room.MemberCount()
        do! room.Typing("warm", false)

        let counters = FunctionalInstrumentation.start ()

        try
            do! room.Join "alice"
            let! _ = room.Leave "bob"
            let! _ = room.Say("alice", "hello")
            let! _ = room.History 2
            let! _ = room.MemberCount()
            do! room.Typing("alice", true)

            test <@ counters.ApiShapeBuilds = 0 @>
            test <@ counters.SelectorEvaluations = 0 @>
            test <@ counters.GenericClosings = 0 @>
            test <@ counters.CodecBuilds = 0 @>
            // The payload really crossed the codec, so the three zeros above are a fact about a
            // call that did the whole round trip and not about a call that did nothing.
            test <@ counters.PayloadSerializations > 0 @>
            test <@ counters.PayloadDeserializations > 0 @>
        finally
            FunctionalInstrumentation.stop ()

        test <@ transport.Calls.Length = 12 @>
    }

type CounterweightActor = private CounterweightActor of unit

/// <summary>An API type nothing else in this file plans a facade for, so its plan is really cold.</summary>
[<NoEquality; NoComparison>]
type CounterweightApi = { ping: string -> Task<int> }

type ICounterweightFacade =
    abstract Ping: value: string -> Task<int>

[<Fact>]
let ``creating a facade is where the per-member generic closing happens`` () =
    // The counterweight to the test above: if planning closed no generics either, the zero there
    // would say nothing about where the work moved to.
    let counterweightContract =
        grainContract<CounterweightActor, string, CounterweightApi> {
            grainType "interop.counterweight"
            stringKey
        }

    let services = buildServices true None
    let target = InMemoryTarget(services, "interop.counterweight", 1)
    target.Handle<string, int>("ping", fun value -> value.Length)
    let transport = InMemoryTransport(services, target.Dispatch)

    let counters = FunctionalInstrumentation.start ()

    try
        FunctionalGrainInterop.For<ICounterweightFacade>(counterweightContract, transport, "first")
        |> ignore

        test <@ counters.GenericClosings > 0 @>

        // The plan and the typed contract binder are both cached, so a second facade over the
        // same interface and contract -- which is what binding a second grain key is -- closes
        // nothing at all.
        let warm = counters.GenericClosings

        FunctionalGrainInterop.For<ICounterweightFacade>(counterweightContract, transport, "second")
        |> ignore

        test <@ counters.GenericClosings = warm @>
    finally
        FunctionalInstrumentation.stop ()

// ──────────────────────────────────────────────────────────────────────────────
// Shapes the C# fixtures cannot reach
// ──────────────────────────────────────────────────────────────────────────────

type ExtraActor = private ExtraActor of unit

/// <summary>
/// A struct-tuple argument and a <c>oneWay</c> operation: neither is expressible in the C#
/// fixture set against the room contract, and both go through the same two rules.
/// </summary>
[<NoEquality; NoComparison>]
type ExtraApi =
    { ping: struct (string * int) -> Task<int>
      notify: string -> Task<unit> }

let private extraContract =
    grainContract<ExtraActor, string, ExtraApi> {
        grainType "interop.extra"
        stringKey
        oneWay (_.notify)
    }

type IExtraFacade =
    abstract Ping: name: string * count: int -> Task<int>
    abstract Notify: message: string -> Task

/// <summary>A facade interface that is generic, closed at the call site.</summary>
type IGenericFacade<'Key> =
    abstract Notify: message: 'Key -> Task

let private extraFacadeFor<'TFacade when 'TFacade: not struct> () =
    let services = buildServices true None
    let target = InMemoryTarget(services, "interop.extra", 1)
    target.Handle<struct (string * int), int>("ping", fun (struct (name, count)) -> name.Length + count)
    target.Handle<string, unit>("notify", fun _ -> ())
    let transport = InMemoryTransport(services, target.Dispatch)
    transport, FunctionalGrainInterop.For<'TFacade>(extraContract, transport, "general")

[<Fact>]
let ``a struct-tuple argument is packed from its parameters like any other tuple`` () =
    let transport, extra = extraFacadeFor<IExtraFacade> ()

    task {
        let! value = extra.Ping("alice", 4)
        test <@ value = 9 @>
        test <@ transport.LastCall.Envelope.OperationId = "ping" @>
    }

[<Fact>]
let ``a oneWay operation keeps its admission flags through a facade`` () =
    let transport, extra = extraFacadeFor<IExtraFacade> ()

    task {
        do! extra.Notify "fire and forget"
        test <@ transport.LastCall.IsOneWay @>
        test <@ transport.LastCall.Envelope.AdmissionFlags = AdmissionFlags.OneWay @>
    }

[<Fact>]
let ``a constructed generic interface is a valid facade`` () =
    // The member is not generic once the interface is closed, so the generic-member rejection
    // must not fire on it.
    let transport, extra = extraFacadeFor<IGenericFacade<string>> ()

    task {
        do! extra.Notify "hello"
        test <@ transport.LastCall.Envelope.OperationId = "notify" @>
    }

[<Fact>]
let ``a facade addresses the key it was created with`` () =
    let services = buildServices true None
    let target = newTarget services
    let transport = InMemoryTransport(services, target.Dispatch)
    let general = FunctionalGrainInterop.For<IPartialFacade>(contract, transport, "general")
    let random = FunctionalGrainInterop.For<IPartialFacade>(contract, transport, "random")

    task {
        do! general.Join "alice"
        do! random.Join "bob"

        let keys = transport.Calls |> Array.map (fun call -> call.GrainId.Key.ToString())
        test <@ keys = [| "general"; "random" |] @>
    }
