/// <summary>
/// Programmatic compile fixtures for spec 003 Phase 1. Every "fails to compile" case in the
/// specification is checked with FSharp.Compiler.Service: the negative snippet must produce
/// errors and its positive twin must compile clean, so a typo in a snippet cannot make a
/// negative case pass for the wrong reason.
/// </summary>
module Orleans.FSharp.Tests.FunctionalCompileFailureTests

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open Xunit
open Swensen.Unquote

// ──────────────────────────────────────────────────────────────────────────────
// FCS harness
// ──────────────────────────────────────────────────────────────────────────────

let private checker = FSharpChecker.Create()

/// <summary>Every assembly the test host already resolved, used as compiler references.</summary>
let private referenceArguments =
    lazy
        (match AppContext.GetData "TRUSTED_PLATFORM_ASSEMBLIES" with
         | :? string as assemblies ->
             assemblies.Split Path.PathSeparator
             |> Array.filter (fun path -> path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
             |> Array.map (fun path -> "-r:" + path)
         | _ -> failwith "TRUSTED_PLATFORM_ASSEMBLIES is unavailable; the compile fixtures cannot resolve references.")

/// <summary>Shared declarations every snippet is compiled against.</summary>
let private preamble =
    """
module CompileProbe

open System
open System.Threading.Tasks
open Orleans
open Orleans.FSharp

type RoomActor = private RoomActor of unit
type OtherActor = private OtherActor of unit

[<Struct>]
type RoomId =
    | RoomId of string

    static member value(RoomId value) = value

[<NoEquality; NoComparison>]
type RoomApi =
    { join: string -> Task<unit>
      say: string -> Task<int64>
      history: int -> Task<string list>
      typing: bool -> Task<unit> }

[<NoEquality; NoComparison>]
type OtherApi = { ping: unit -> Task<unit> }

type RoomState = { count: int }
type OtherState = { total: int64 }

let roomContract =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
    }

let otherContract =
    grainContract<OtherActor, string, OtherApi> {
        grainType "chat.other"
        stringKey
    }

let roomState = PersistentState.create<RoomState> "state" "Default"
let otherState = PersistentState.create<OtherState> "other" "Default"
"""

/// <summary>
/// Type-check the snippet against the preamble and return the diagnostics of the requested
/// severities, formatted as <c>FSnnnn: message</c>. Nothing is suppressed: the fixtures used to
/// pass <c>--nowarn:64</c>, which hid exactly the diagnostic a flexible factory annotation
/// produces at a <c>FunctionalGrain.ref</c> call.
/// </summary>
let private compileWith
    (extraArguments: string list)
    (severities: FSharpDiagnosticSeverity list)
    (snippet: string)
    =
    let file = Path.Combine(Path.GetTempPath(), $"functional_probe_{Guid.NewGuid():N}.fs")
    File.WriteAllText(file, preamble + snippet)

    try
        let arguments =
            [| yield "--noframework"
               yield "--targetprofile:netcore"
               yield "--target:library"
               yield! extraArguments
               yield! referenceArguments.Value
               yield file |]

        let options = checker.GetProjectOptionsFromCommandLineArgs("functional_probe.fsproj", arguments)

        let results =
            checker.ParseAndCheckProject options |> Async.RunSynchronously

        results.Diagnostics
        |> Array.filter (fun diagnostic -> List.contains diagnostic.Severity severities)
        |> Array.map (fun diagnostic -> sprintf "FS%04d: %s" diagnostic.ErrorNumber diagnostic.Message)
    finally
        File.Delete file

/// <summary>The errors the snippet produces.</summary>
let private compileErrors snippet =
    compileWith [] [ FSharpDiagnosticSeverity.Error ] snippet

/// <summary>
/// Every error and warning the snippet produces. Consumers build with
/// <c>TreatWarningsAsErrors</c>, so a default-on warning at a documented call form breaks them
/// exactly as an error does and has to be visible here.
/// </summary>
let private compileDiagnostics snippet =
    compileWith [] [ FSharpDiagnosticSeverity.Error; FSharpDiagnosticSeverity.Warning ] snippet

/// <summary>
/// Every error and warning the snippet produces with extra compiler flags — used to opt the
/// off-by-default implicit-conversion informational warnings in.
/// </summary>
let private compileDiagnosticsWith extraArguments snippet =
    compileWith extraArguments [ FSharpDiagnosticSeverity.Error; FSharpDiagnosticSeverity.Warning ] snippet

/// <summary>Assert that the accepted snippet compiles and the rejected snippet does not.</summary>
let private rejects (accepted: string) (rejected: string) =
    let acceptedErrors = compileErrors accepted
    let rejectedErrors = compileErrors rejected

    test <@ acceptedErrors = Array.empty @>
    test <@ rejectedErrors <> Array.empty @>
    rejectedErrors

// ──────────────────────────────────────────────────────────────────────────────
// Harness control
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``the compile harness accepts the preamble alone`` () =
    test <@ compileErrors "" = Array.empty @>

[<Fact>]
let ``the compile harness reports a plain type error`` () =
    test <@ compileErrors "let broken: int = \"text\"" <> Array.empty @>

// ──────────────────────────────────────────────────────────────────────────────
// Key operations
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``a native key operation requires its exact native key type`` () =
    let accepted =
        """
let ok =
    grainContract<RoomActor, string, RoomApi> {
        grainType "chat.native"
        stringKey
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.native"
        stringKey
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``every native key operation rejects a mismatched key type`` () =
    let accepted =
        """
let okGuid =
    grainContract<RoomActor, Guid, RoomApi> {
        grainType "k.guid"
        guidKey
    }

let okInt =
    grainContract<RoomActor, int64, RoomApi> {
        grainType "k.int"
        int64Key
    }

let okGuidCompound =
    grainContract<RoomActor, Guid * string, RoomApi> {
        grainType "k.guidc"
        guidCompoundKey
    }

let okIntCompound =
    grainContract<RoomActor, int64 * string, RoomApi> {
        grainType "k.intc"
        int64CompoundKey
    }
"""

    let rejected =
        """
let badGuid =
    grainContract<RoomActor, string, RoomApi> {
        grainType "k.guid"
        guidKey
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``an int64 compound key operation rejects a Guid compound key type`` () =
    let accepted =
        """
let ok =
    grainContract<RoomActor, int64 * string, RoomApi> {
        grainType "k.intc"
        int64CompoundKey
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, Guid * string, RoomApi> {
        grainType "k.intc"
        int64CompoundKey
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``a mapped key operation rejects a reversed conversion pair`` () =
    let accepted =
        """
let ok =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.mapped"
        stringKeyMapped RoomId.value RoomId
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.mapped"
        stringKeyMapped RoomId RoomId.value
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``a mapped key operation rejects a conversion into the wrong native space`` () =
    let accepted =
        """
let ok =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.mapped"
        stringKeyMapped RoomId.value RoomId
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.mapped"
        int64KeyMapped RoomId.value RoomId
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``a mapped compound key operation requires a curried decoder`` () =
    let accepted =
        """
let ok =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.compound"
        guidCompoundKeyMapped (fun (RoomId value) -> Guid.Empty, value) (fun _ value -> RoomId value)
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.compound"
        guidCompoundKeyMapped (fun (RoomId value) -> Guid.Empty, value) (fun (_, value) -> RoomId value)
    }
"""

    rejects accepted rejected |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Policies and selectors
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``oneWay rejects a field whose reply is not Task of unit`` () =
    let accepted =
        """
let ok =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
        oneWay (_.typing)
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
        oneWay (_.say)
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``a selector from another API record fails to compile`` () =
    let accepted =
        """
let ok =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
        readOnly (_.history)
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
        readOnly (fun (api: OtherApi) -> api.ping)
    }
"""

    rejects accepted rejected |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Definitions, handlers, and persistent state
// ──────────────────────────────────────────────────────────────────────────────

let private definitionPreamble =
    """
let handlers state =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        handle (_.join) (fun _ s (_: string) -> task { return s, () })
        handle (_.say) (fun _ s (_: string) -> task { return s, 1L })
        handle (_.history) (fun _ s (_: int) -> task { return s, [] })
        handle (_.typing) (fun _ s (_: bool) -> task { return s, () })
    }
"""

[<Fact>]
let ``a handler with the wrong argument type fails to compile`` () =
    let accepted =
        """
let ok =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        handle (_.say) (fun _ state (text: string) -> task { return state, int64 text.Length })
    }
"""

    let rejected =
        """
let bad =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        handle (_.say) (fun _ state (value: int) -> task { return state, int64 value })
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``a handler with the wrong reply type fails to compile`` () =
    let accepted =
        """
let ok =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        handle (_.say) (fun _ state (_: string) -> task { return state, 1L })
    }
"""

    let rejected =
        """
let bad =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        handle (_.say) (fun _ state (_: string) -> task { return state, "one" })
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``stateFrom accepts only the definition's primary state type`` () =
    let accepted =
        """
let ok =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        stateFrom roomState
    }
"""

    let rejected =
        """
let bad =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        stateFrom otherState
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``usePersistentState keeps each stored type exact in the context lookup`` () =
    let accepted =
        """
let ok =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        usePersistentState otherState (fun _ -> { total = 0L })

        handle
            (_.join)
            (fun context state (_: string) ->
                task {
                    let holder = context.persistentState otherState
                    holder.State <- { total = 1L }
                    return state, ()
                })
    }
"""

    let rejected =
        """
let bad =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        usePersistentState otherState (fun _ -> { total = 0L })

        handle
            (_.join)
            (fun context state (_: string) ->
                task {
                    let holder = context.persistentState otherState
                    holder.State <- { count = 1 }
                    return state, ()
                })
    }
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``an initial-state factory must take the domain key`` () =
    let accepted =
        """
let ok =
    grainFor roomContract {
        initialState (fun (RoomId name) -> { count = name.Length })
    }
"""

    let rejected =
        """
let bad =
    grainFor roomContract {
        initialState (fun (name: string) -> { count = name.Length })
    }
"""

    rejects accepted rejected |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Bound references
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``binding rejects the wrong key type`` () =
    let accepted =
        """
let ok (factory: IGrainFactory) = FunctionalGrain.ref roomContract factory (RoomId "general")
"""

    let rejected =
        """
let bad (factory: IGrainFactory) = FunctionalGrain.ref roomContract factory "general"
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``a bound field rejects the wrong argument type`` () =
    let accepted =
        """
let ok (factory: IGrainFactory) =
    let api = FunctionalGrain.ref roomContract factory (RoomId "general")
    api.join "alice"
"""

    let rejected =
        """
let bad (factory: IGrainFactory) =
    let api = FunctionalGrain.ref roomContract factory (RoomId "general")
    api.join 42
"""

    rejects accepted rejected |> ignore

[<Fact>]
let ``a bound value infers the API record without annotation`` () =
    let accepted =
        """
let ok (factory: IGrainFactory) =
    let api = FunctionalGrain.ref roomContract factory (RoomId "general")
    let typed: RoomApi = api
    typed
"""

    let rejected =
        """
let bad (factory: IGrainFactory) =
    let api = FunctionalGrain.ref roomContract factory (RoomId "general")
    let typed: OtherApi = api
    typed
"""

    rejects accepted rejected |> ignore

// ──────────────────────────────────────────────────────────────────────────────
// Inference of the application-owned bindings
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``application-owned bindings infer their complete types in any parameter order`` () =
    let accepted =
        """
module RoomClient =
    let ref roomId factory = FunctionalGrain.ref roomContract factory roomId
    let rawRef roomId factory = FunctionalGrain.rawRef roomContract factory roomId

let useThem (factory: IGrainFactory) =
    let api = RoomClient.ref (RoomId "general") factory
    let raw = RoomClient.rawRef (RoomId "general") factory
    let typedApi: RoomApi = api
    let typedKey: RoomId = raw.key
    typedApi, typedKey
"""

    test <@ compileErrors accepted = Array.empty @>

/// <remarks>
/// The specification's point-free bindings must infer their complete concrete types with no
/// annotation and no use site. This is why <c>FunctionalGrain.ref</c> / <c>rawRef</c> declare
/// <c>contract</c> as their only parameter and return the remaining curried function: F#
/// inserts flexibility for non-sealed *declared parameter* types at every use of a function or
/// member, so a declared <c>factory: IGrainFactory</c> parameter would leave every partial
/// application generic in <c>'_a :&gt; IGrainFactory</c> and hit the value restriction (FS0030).
/// The `flexibleParameterWouldNotInfer` snippet reproduces that failure with a local function
/// of the rejected shape, so this test fails if the library ever regresses to it.
/// </remarks>
[<Fact>]
let ``the point-free bindings infer their complete concrete types unused`` () =
    let pointFreeUnused =
        """
module RoomApiBindings =
    let ref = FunctionalGrain.ref roomContract
    let rawRef = FunctionalGrain.rawRef roomContract
"""

    let pointFreeUsedLater =
        """
module RoomApiBindings =
    let ref = FunctionalGrain.ref roomContract
    let rawRef = FunctionalGrain.rawRef roomContract

let useIt (factory: IGrainFactory) =
    RoomApiBindings.ref factory (RoomId "general"), RoomApiBindings.rawRef factory (RoomId "general")
"""

    // The inferred types are exactly the specification's, checked by annotated re-binding.
    let pointFreeInferredTypes =
        """
module RoomApiBindings =
    let ref = FunctionalGrain.ref roomContract
    let rawRef = FunctionalGrain.rawRef roomContract

let inferredRef: IGrainFactory -> RoomId -> RoomApi = RoomApiBindings.ref

let inferredRawRef: IGrainFactory -> RoomId -> FunctionalGrainRef<RoomActor, RoomId, RoomApi> =
    RoomApiBindings.rawRef
"""

    // Negative twin: the same annotation with the wrong key type must fail, so the check above
    // cannot pass by the annotation merely constraining a still-generic binding.
    let wrongInferredTypes =
        """
module RoomApiBindings =
    let ref = FunctionalGrain.ref roomContract

let inferredRef: IGrainFactory -> string -> RoomApi = RoomApiBindings.ref
"""

    // The shape the library deliberately does NOT use: a declared non-sealed parameter.
    let flexibleParameterWouldNotInfer =
        """
module RoomApiBindings =
    let curried (contract: GrainContract<'Actor, 'Key, 'Api>) (factory: IGrainFactory) (key: 'Key) =
        FunctionalGrain.ref contract factory key

    let ref = curried roomContract
"""

    let etaExpanded =
        """
module RoomApiBindings =
    let rawRef factory key = FunctionalGrain.rawRef roomContract factory key
"""

    // Any IGrainFactory implementation may still be applied to the returned function.
    let subtypeFactory =
        """
module RoomApiBindings =
    let ref = FunctionalGrain.ref roomContract

let useCluster (client: IClusterClient) =
    RoomApiBindings.ref client (RoomId "general"), FunctionalGrain.ref roomContract client (RoomId "general")
"""

    test <@ compileErrors pointFreeUnused = Array.empty @>
    test <@ compileErrors pointFreeUsedLater = Array.empty @>
    test <@ compileErrors pointFreeInferredTypes = Array.empty @>
    test <@ compileErrors etaExpanded = Array.empty @>
    test <@ compileErrors subtypeFactory = Array.empty @>

    test
        <@
            compileErrors wrongInferredTypes
            |> Array.exists (fun message -> message.StartsWith "FS0001")
        @>

    test
        <@
            compileErrors flexibleParameterWouldNotInfer
            |> Array.exists (fun message -> message.StartsWith "FS0030")
        @>

[<Fact>]
let ``the definition preamble used by the handler fixtures compiles`` () =
    test <@ compileErrors definitionPreamble = Array.empty @>

[<Fact>]
let ``the specification's exact grainContract spelling compiles`` () =
    // The spec writes `grainContract<RoomActor, RoomId, RoomApi> { ... }` with no space
    // before the unit argument; the repo's formatter writes `... > () { ... }`. Both must work.
    let noSpace =
        """
let tight =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.tight"
        stringKeyMapped RoomId.value RoomId
    }
"""

    let withSpace =
        """
let spaced =
    grainContract<RoomActor, RoomId, RoomApi> {
        grainType "chat.spaced"
        stringKeyMapped RoomId.value RoomId
    }
"""

    test <@ compileErrors noSpace = Array.empty @>
    test <@ compileErrors withSpace = Array.empty @>

[<Fact>]
let ``a definition declares exactly one state operation`` () =
    let accepted =
        """
let ok =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
    }
"""

    let bothOperations =
        """
let bad =
    grainFor roomContract {
        defaultState (fun () -> { count = 0 })
        initialState (fun (RoomId name) -> { count = name.Length })
    }
"""

    let noStateOperation =
        """
let alsoBad =
    grainFor roomContract {
        collectionAge (TimeSpan.FromMinutes 1.0)
    }
"""

    // Type-checking only: handler coverage is a sealing-time (runtime) rule, so the accepted
    // snippet compiles even though constructing it would fail.
    test <@ compileErrors accepted = Array.empty @>
    test <@ compileErrors bothOperations <> Array.empty @>
    test <@ compileErrors noStateOperation <> Array.empty @>

// ──────────────────────────────────────────────────────────────────────────────
// Factory annotations at the binding call
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// <para>
/// <c>FunctionalGrain.ref</c> / <c>rawRef</c> declare <c>contract</c> as their only parameter and
/// apply the factory to the returned function (see <c>FunctionalBinding.fs</c>), so F# inserts
/// subtype flexibility for the factory only when the call goes through a function value — the
/// application-owned binding — and not at the direct member call. Every form the library's
/// <c>&lt;remarks&gt;</c> recommends is asserted here to be free of errors <em>and warnings</em>:
/// consumers inherit this repo's <c>TreatWarningsAsErrors</c> convention, so a warning at a
/// documented call form is a break. The negative twin below states the cost of the shape.
/// </para>
/// </remarks>
[<Fact>]
let ``the documented factory forms bind without any diagnostic`` () =
    // Plain IGrainFactory annotation — subsumption accepts every implementation.
    let plainAnnotation =
        """
let usePlain (factory: IGrainFactory) =
    FunctionalGrain.ref roomContract factory (RoomId "general"),
    FunctionalGrain.rawRef roomContract factory (RoomId "general")
"""

    // A derived interface value applied directly.
    let clusterClient =
        """
let useClient (client: IClusterClient) =
    FunctionalGrain.ref roomContract client (RoomId "general"),
    FunctionalGrain.rawRef roomContract client (RoomId "general")
"""

    // The application-owned binding is a function value, so flexibility IS inserted for it:
    // flexible and generic callers stay generic through this path.
    let throughTheBinding =
        """
module RoomApiBindings =
    let ref = FunctionalGrain.ref roomContract
    let rawRef = FunctionalGrain.rawRef roomContract

let useFlexible (factory: #IGrainFactory) =
    RoomApiBindings.ref factory (RoomId "general"), RoomApiBindings.rawRef factory (RoomId "general")

let useGeneric<'F when 'F :> IGrainFactory> (factory: 'F) =
    RoomApiBindings.ref factory (RoomId "general")

type Holder<'F when 'F :> IGrainFactory>(factory: 'F) =
    member _.Room = RoomApiBindings.ref factory (RoomId "general")
"""

    // The one-token escape hatch at a direct call: upcast once.
    let upcastAtTheCall =
        """
let useFlexible (factory: #IGrainFactory) =
    FunctionalGrain.ref roomContract (factory :> IGrainFactory) (RoomId "general")

type Holder<'F when 'F :> IGrainFactory>(factory: 'F) =
    member _.Room =
        FunctionalGrain.ref roomContract (factory :> IGrainFactory) (RoomId "general")
"""

    // A generic *function* (unlike a generic class) needs no upcast at all.
    let genericFunctionDirect =
        """
let useGeneric<'F when 'F :> IGrainFactory> (factory: 'F) =
    FunctionalGrain.ref roomContract factory (RoomId "general")
"""

    test <@ compileDiagnostics plainAnnotation = Array.empty @>
    test <@ compileDiagnostics clusterClient = Array.empty @>
    test <@ compileDiagnostics throughTheBinding = Array.empty @>
    test <@ compileDiagnostics upcastAtTheCall = Array.empty @>
    test <@ compileDiagnostics genericFunctionDirect = Array.empty @>

/// <remarks>
/// The documented cost of the member shape, pinned so it cannot drift unnoticed: at a direct
/// <c>FunctionalGrain.ref</c> call a flexible <c>#IGrainFactory</c> annotation is constrained to
/// <c>IGrainFactory</c> (FS0064), and a class type parameter <c>'F :&gt; IGrainFactory</c> fails
/// outright (FS0660/FS0663). Both are diagnostic-free through the forms the test above asserts.
/// If a future declaration shape removes these, delete this test together with the third
/// <c>&lt;remarks&gt;</c> paragraph on <c>FunctionalGrain</c>.
/// </remarks>
[<Fact>]
let ``a flexible factory annotation at a direct call is constrained to IGrainFactory`` () =
    let flexibleDirect =
        """
let useFlexible (factory: #IGrainFactory) =
    FunctionalGrain.ref roomContract factory (RoomId "general")
"""

    let flexibleDirectRaw =
        """
let useFlexible (factory: #IGrainFactory) =
    FunctionalGrain.rawRef roomContract factory (RoomId "general")
"""

    let genericClassDirect =
        """
type Holder<'F when 'F :> IGrainFactory>(factory: 'F) =
    member _.Room = FunctionalGrain.ref roomContract factory (RoomId "general")
"""

    // Warnings only — the code still compiles, which is why the harness has to look at warnings.
    test <@ compileErrors flexibleDirect = Array.empty @>
    test <@ compileErrors flexibleDirectRaw = Array.empty @>

    test
        <@
            compileDiagnostics flexibleDirect
            |> Array.exists (fun message -> message.StartsWith "FS0064")
        @>

    test
        <@
            compileDiagnostics flexibleDirectRaw
            |> Array.exists (fun message -> message.StartsWith "FS0064")
        @>

    test
        <@
            compileErrors genericClassDirect
            |> Array.exists (fun message -> message.StartsWith "FS0663")
        @>

/// <remarks>
/// The same asymmetry shows up in the off-by-default implicit-conversion informationals: applying
/// a derived interface value (<c>IClusterClient</c>) straight to the function returned by
/// <c>FunctionalGrain.ref</c> is an implicit upcast (FS3388 under <c>--warnon:3388</c>), while the
/// application-owned binding and an explicit upcast are silent even with the flag on. Consumers
/// who opt these warnings in get the same two clean forms, which is why the remarks name them.
/// </remarks>
[<Fact>]
let ``opt-in implicit-conversion warnings point at the same clean forms`` () =
    let warnOnConversions =
        [ "--warnon:3388"; "--warnon:3389"; "--warnon:3390"; "--warnon:3391" ]

    let directSubtype =
        """
let useClient (client: IClusterClient) =
    FunctionalGrain.ref roomContract client (RoomId "general")
"""

    let throughTheBinding =
        """
module RoomApiBindings =
    let ref = FunctionalGrain.ref roomContract

let useClient (client: IClusterClient) =
    RoomApiBindings.ref client (RoomId "general")
"""

    let upcastAtTheCall =
        """
let useClient (client: IClusterClient) =
    FunctionalGrain.ref roomContract (client :> IGrainFactory) (RoomId "general")
"""

    // Off by default, so nothing here breaks a stock consumer build.
    test <@ compileDiagnostics directSubtype = Array.empty @>

    test
        <@
            compileDiagnosticsWith warnOnConversions directSubtype
            |> Array.exists (fun message -> message.StartsWith "FS3388")
        @>

    test <@ compileDiagnosticsWith warnOnConversions throughTheBinding = Array.empty @>
    test <@ compileDiagnosticsWith warnOnConversions upcastAtTheCall = Array.empty @>

// ──────────────────────────────────────────────────────────────────────────────
// Multi-input operations are spelled tupled, and only tupled
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// The tupled spelling is the design: an operation takes one argument, and a multi-input
/// operation takes a tuple. A curried field is refused twice over, which is what makes the
/// rule enforceable rather than advisory — the CE's selector-shaped operations cannot even be
/// applied to it (this fixture), and shape reflection refuses to build the contract at all
/// (<c>FunctionalShapeTests."a curried field fails construction"</c>). The positive twin proves
/// the rejection is about the currying and not about the surrounding snippet.
/// </remarks>
[<Fact>]
let ``a curried API field cannot be configured through the contract builder`` () =
    let tupled =
        """
[<NoEquality; NoComparison>]
type TupledApi =
    { tag: (string * string) -> Task<string> }

type TupledActor = private TupledActor of unit

let tupledContract =
    grainContract<TupledActor, string, TupledApi> {
        grainType "chat.tupled"
        stringKey
        readOnly (_.tag)
    }
"""

    let curried =
        """
[<NoEquality; NoComparison>]
type CurriedApi =
    { tag: string -> string -> Task<string> }

type CurriedActor = private CurriedActor of unit

let curriedContract =
    grainContract<CurriedActor, string, CurriedApi> {
        grainType "chat.curried"
        stringKey
        readOnly (_.tag)
    }
"""

    rejects tupled curried |> ignore
