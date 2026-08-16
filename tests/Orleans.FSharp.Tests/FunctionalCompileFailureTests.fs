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
    grainContract<RoomActor, RoomId, RoomApi> () {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
    }

let otherContract =
    grainContract<OtherActor, string, OtherApi> () {
        grainType "chat.other"
        stringKey
    }

let roomState = PersistentState.create<RoomState> "state" "Default"
let otherState = PersistentState.create<OtherState> "other" "Default"
"""

let private compileErrors (snippet: string) =
    let file = Path.Combine(Path.GetTempPath(), $"functional_probe_{Guid.NewGuid():N}.fs")
    File.WriteAllText(file, preamble + snippet)

    try
        let arguments =
            [| yield "--noframework"
               yield "--targetprofile:netcore"
               yield "--target:library"
               yield "--nowarn:64"
               yield! referenceArguments.Value
               yield file |]

        let options = checker.GetProjectOptionsFromCommandLineArgs("functional_probe.fsproj", arguments)

        let results =
            checker.ParseAndCheckProject options |> Async.RunSynchronously

        results.Diagnostics
        |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)
        |> Array.map (fun diagnostic -> sprintf "FS%04d: %s" diagnostic.ErrorNumber diagnostic.Message)
    finally
        File.Delete file

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
    grainContract<RoomActor, string, RoomApi> () {
        grainType "chat.native"
        stringKey
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> () {
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
    grainContract<RoomActor, Guid, RoomApi> () {
        grainType "k.guid"
        guidKey
    }

let okInt =
    grainContract<RoomActor, int64, RoomApi> () {
        grainType "k.int"
        int64Key
    }

let okGuidCompound =
    grainContract<RoomActor, Guid * string, RoomApi> () {
        grainType "k.guidc"
        guidCompoundKey
    }

let okIntCompound =
    grainContract<RoomActor, int64 * string, RoomApi> () {
        grainType "k.intc"
        int64CompoundKey
    }
"""

    let rejected =
        """
let badGuid =
    grainContract<RoomActor, string, RoomApi> () {
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
    grainContract<RoomActor, int64 * string, RoomApi> () {
        grainType "k.intc"
        int64CompoundKey
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, Guid * string, RoomApi> () {
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
    grainContract<RoomActor, RoomId, RoomApi> () {
        grainType "chat.mapped"
        stringKeyMapped RoomId.value RoomId
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> () {
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
    grainContract<RoomActor, RoomId, RoomApi> () {
        grainType "chat.mapped"
        stringKeyMapped RoomId.value RoomId
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> () {
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
    grainContract<RoomActor, RoomId, RoomApi> () {
        grainType "chat.compound"
        guidCompoundKeyMapped (fun (RoomId value) -> Guid.Empty, value) (fun _ value -> RoomId value)
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> () {
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
    grainContract<RoomActor, RoomId, RoomApi> () {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
        oneWay (_.typing)
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> () {
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
    grainContract<RoomActor, RoomId, RoomApi> () {
        grainType "chat.room"
        stringKeyMapped RoomId.value RoomId
        readOnly (_.history)
    }
"""

    let rejected =
        """
let bad =
    grainContract<RoomActor, RoomId, RoomApi> () {
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
/// SPEC-DEVIATION evidence. F# inserts flexibility for the non-sealed <c>IGrainFactory</c>
/// parameter at every use of a function, so the specification's point-free module binding
/// <c>let rawRef = FunctionalGrain.rawRef contract</c> stays generalized and hits the value
/// restriction unless the value is applied to a concrete factory later in the same file. The
/// eta-expanded binding always infers its complete concrete type.
/// </remarks>
[<Fact>]
let ``an unused point-free binding hits the value restriction while its eta-expansion does not`` () =
    let pointFreeUnused =
        """
module RoomApiBindings =
    let rawRef = FunctionalGrain.rawRef roomContract
"""

    let pointFreeUsedLater =
        """
module RoomApiBindings =
    let rawRef = FunctionalGrain.rawRef roomContract

let useIt (factory: IGrainFactory) = RoomApiBindings.rawRef factory (RoomId "general")
"""

    let etaExpanded =
        """
module RoomApiBindings =
    let rawRef factory key = FunctionalGrain.rawRef roomContract factory key
"""

    let errors = compileErrors pointFreeUnused

    test <@ errors |> Array.exists (fun message -> message.StartsWith "FS0030") @>
    test <@ compileErrors pointFreeUsedLater = Array.empty @>
    test <@ compileErrors etaExpanded = Array.empty @>

[<Fact>]
let ``the definition preamble used by the handler fixtures compiles`` () =
    test <@ compileErrors definitionPreamble = Array.empty @>

[<Fact>]
let ``the specification's exact grainContract spelling compiles`` () =
    // The spec writes `grainContract<RoomActor, RoomId, RoomApi>() { ... }` with no space
    // before the unit argument; the repo's formatter writes `... > () { ... }`. Both must work.
    let noSpace =
        """
let tight =
    grainContract<RoomActor, RoomId, RoomApi>() {
        grainType "chat.tight"
        stringKeyMapped RoomId.value RoomId
    }
"""

    let withSpace =
        """
let spaced =
    grainContract<RoomActor, RoomId, RoomApi> () {
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
