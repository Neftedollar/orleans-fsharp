/// <summary>
/// Surface tests for spec 003 Phase 1: the per-invocation context, the bound-reference
/// wrapper, the compile-only hosting stubs, and construction-stage caching behaviour that the
/// shape and contract suites do not cover.
/// </summary>
module Orleans.FSharp.Tests.FunctionalSurfaceTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.Hosting
open Orleans.Runtime
open Orleans.FSharp

type SurfaceActor = private SurfaceActor of unit

[<NoEquality; NoComparison>]
type SurfaceApi =
    { first: int -> Task<unit>
      second: int -> Task<unit> }

type SurfaceState = { count: int }

let private contract =
    grainContract<SurfaceActor, string, SurfaceApi> () {
        grainType "surface.test"
        stringKey
    }

let private attached = PersistentState.create<SurfaceState> "state" "Default"
let private unattached = PersistentState.create<SurfaceState> "missing" "Default"

// ──────────────────────────────────────────────────────────────────────────────
// Per-invocation context
// ──────────────────────────────────────────────────────────────────────────────

let private makeContext (resolve: PersistentStateDescriptor -> obj) (token: CancellationToken) =
    let mutable deactivated = 0
    let mutable delayed = TimeSpan.Zero

    let core =
        { GrainId = contract.GrainIdOf "general"
          GrainFactory = Unchecked.defaultof<IGrainFactory>
          Services = ServiceCollection().BuildServiceProvider() :> IServiceProvider
          Logger = NullLogger.Instance
          TimeProvider = TimeProvider.System
          CancellationToken = token
          DeactivateOnIdle = fun () -> deactivated <- deactivated + 1
          DelayDeactivation = fun span -> delayed <- span
          ResolvePersistentState = resolve }

    let context = FunctionalGrainContext<SurfaceActor, string>("general", core)
    context, (fun () -> deactivated), (fun () -> delayed)

[<Fact>]
let ``the context exposes the decoded key, grain identity, and clock`` () =
    let context, _, _ = makeContext (fun _ -> null) CancellationToken.None
    let before = DateTimeOffset.UtcNow.AddSeconds -1.0
    let grainTypeText = context.grainId.Type.ToString()

    test <@ context.key = "general" @>
    test <@ grainTypeText = "surface.test" @>
    test <@ obj.ReferenceEquals(context.timeProvider, TimeProvider.System) @>
    test <@ context.utcNow > before @>
    test <@ not (isNull (box context.services)) @>
    test <@ not (isNull (box context.logger)) @>

[<Fact>]
let ``the context carries the callback cancellation token`` () =
    use source = new CancellationTokenSource()
    let context, _, _ = makeContext (fun _ -> null) source.Token

    test <@ not context.cancellationToken.IsCancellationRequested @>
    source.Cancel()
    test <@ context.cancellationToken.IsCancellationRequested @>

[<Fact>]
let ``the context wraps the Orleans deactivation methods`` () =
    let context, deactivations, delay = makeContext (fun _ -> null) CancellationToken.None

    context.deactivateOnIdle ()
    context.delayDeactivation (TimeSpan.FromMinutes 2.0)

    test <@ deactivations () = 1 @>
    test <@ delay () = TimeSpan.FromMinutes 2.0 @>

[<Fact>]
let ``an unattached persistent state fails deterministically`` () =
    let context, _, _ =
        makeContext (fun descriptor -> if descriptor.StateName = "state" then box 1 else null) CancellationToken.None

    let error =
        Assert.Throws<InvalidOperationException>(fun () -> context.persistentState unattached |> ignore)

    test <@ error.Message.Contains "missing" @>
    test <@ error.Message.Contains "is attached to this definition" @>

[<Fact>]
let ``a persistent state descriptor is resolved by its logical identity`` () =
    let mutable requested: PersistentStateDescriptor option = None

    let context, _, _ =
        makeContext
            (fun descriptor ->
                requested <- Some descriptor
                null)
            CancellationToken.None

    Assert.Throws<InvalidOperationException>(fun () -> context.persistentState attached |> ignore)
    |> ignore

    test <@ requested.IsSome @>
    test <@ requested.Value.StateName = "state" @>
    test <@ requested.Value.ProviderName = "Default" @>
    test <@ requested.Value.StoredType = typeof<SurfaceState> @>

[<Fact>]
let ``the context reads, writes, and removes request context values`` () =
    let context, _, _ = makeContext (fun _ -> null) CancellationToken.None

    try
        test <@ context.tryGetRequestContext<string> "tenant" = None @>

        context.setRequestContext "tenant" "acme"
        test <@ context.tryGetRequestContext<string> "tenant" = Some "acme" @>
        test <@ context.tryGetRequestContext<int> "tenant" = None @>

        context.removeRequestContext "tenant"
        test <@ context.tryGetRequestContext<string> "tenant" = None @>
    finally
        RequestContext.Clear()

// ──────────────────────────────────────────────────────────────────────────────
// Bound reference wrapper
// ──────────────────────────────────────────────────────────────────────────────

let private probeApi () =
    (ApiShape.of'<SurfaceApi> ()).Probe :?> SurfaceApi

[<Fact>]
let ``the reference wrapper exposes the key and the cached API instance`` () =
    let api = probeApi ()
    let reference = FunctionalGrainRef<SurfaceActor, string, SurfaceApi>("general", api, contract)

    test <@ reference.key = "general" @>
    test <@ obj.ReferenceEquals(reference.api, api) @>
    test <@ obj.ReferenceEquals(reference.api, reference.api) @>

[<Fact>]
let ``selector-based calls report that they arrive in Phase 2`` () =
    let reference =
        FunctionalGrainRef<SurfaceActor, string, SurfaceApi>("general", probeApi (), contract)

    let call =
        Assert.Throws<NotSupportedException>(fun () -> reference.call (_.first) 1 |> ignore)

    let cancellable =
        Assert.Throws<NotSupportedException>(fun () ->
            reference.callCancellable (_.first) 1 CancellationToken.None |> ignore)

    test <@ call.Message.Contains "FunctionalGrainRef.call" @>
    test <@ cancellable.Message.Contains "FunctionalGrainRef.callCancellable" @>

[<Fact>]
let ``rawRef reports that it arrives in Phase 2`` () =
    let error =
        Assert.Throws<NotSupportedException>(fun () ->
            FunctionalGrain.rawRef contract Unchecked.defaultof<IGrainFactory> "general"
            |> ignore)

    test <@ error.Message.Contains "FunctionalGrain.rawRef" @>

// ──────────────────────────────────────────────────────────────────────────────
// Hosting stubs
// ──────────────────────────────────────────────────────────────────────────────

let private fakeClientBuilder (services: IServiceCollection) =
    { new IClientBuilder with
        member _.Services = services
        member _.Configuration = Unchecked.defaultof<Microsoft.Extensions.Configuration.IConfiguration> }

let private fakeSiloBuilder (services: IServiceCollection) =
    { new ISiloBuilder with
        member _.Services = services
        member _.Configuration = Unchecked.defaultof<Microsoft.Extensions.Configuration.IConfiguration> }

[<Fact>]
let ``the Phase 1 client registration stub returns the builder unchanged`` () =
    let services = ServiceCollection() :> IServiceCollection
    let builder = fakeClientBuilder services
    let returned = builder.AddFunctionalGrainClient()

    test <@ obj.ReferenceEquals(returned, builder) @>
    test <@ services.Count = 0 @>

[<Fact>]
let ``the Phase 1 silo registration stub returns the builder unchanged`` () =
    let definition =
        grainFor contract {
            defaultState (fun () -> { count = 0 })
            handle (_.first) (fun _ state (_: int) -> task { return state, () })
            handle (_.second) (fun _ state (_: int) -> task { return state, () })
        }

    let services = ServiceCollection() :> IServiceCollection
    let builder = fakeSiloBuilder services
    let returned = builder.AddFunctionalGrain definition

    test <@ obj.ReferenceEquals(returned, builder) @>
    test <@ services.Count = 0 @>

// ──────────────────────────────────────────────────────────────────────────────
// Construction-stage behaviour
// ──────────────────────────────────────────────────────────────────────────────

[<Fact>]
let ``policy order does not matter`` () =
    let interleaveFirst =
        grainContract<SurfaceActor, string, SurfaceApi> () {
            grainType "surface.order"
            stringKey
            alwaysInterleave (_.second)
            oneWay (_.second)
        }

    let oneWayFirst =
        grainContract<SurfaceActor, string, SurfaceApi> () {
            grainType "surface.order"
            stringKey
            oneWay (_.second)
            alwaysInterleave (_.second)
        }

    test <@ interleaveFirst.Operations.[1].IsOneWay && interleaveFirst.Operations.[1].IsAlwaysInterleave @>
    test <@ oneWayFirst.Operations.[1].IsOneWay && oneWayFirst.Operations.[1].IsAlwaysInterleave @>

[<Fact>]
let ``a null selector fails with the required diagnostic`` () =
    let shape = ApiShape.of'<SurfaceApi> ()

    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.resolve shape "readOnly" Unchecked.defaultof<OperationSelector<SurfaceApi, int, unit>>
            |> ignore)

    test <@ error.Message.Contains "Use a direct API field selector such as _.join." @>

[<Fact>]
let ``a rejected API shape reports the same diagnostic on every attempt`` () =
    let first =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.ofType typeof<FunctionalShapeTests.AsyncApi> |> ignore)

    let second =
        Assert.Throws<InvalidOperationException>(fun () ->
            ApiShape.ofType typeof<FunctionalShapeTests.AsyncApi> |> ignore)

    test <@ first.Message = second.Message @>

[<Fact>]
let ``concurrent shape construction yields one cached instance`` () =
    let shapes =
        [| 1..64 |]
        |> Array.Parallel.map (fun _ -> ApiShape.ofType typeof<SurfaceApi>)

    let first = shapes.[0]

    test <@ shapes |> Array.forall (fun shape -> obj.ReferenceEquals(shape, first)) @>

[<Fact>]
let ``two contracts over the same API share one cached shape`` () =
    let other =
        grainContract<SurfaceActor, string, SurfaceApi> () {
            grainType "surface.other"
            stringKey
        }

    test <@ obj.ReferenceEquals(contract.Shape, other.Shape) @>
    test <@ obj.ReferenceEquals(contract.Shape.Probe, other.Shape.Probe) @>

// ──────────────────────────────────────────────────────────────────────────────
// Conformance with the normative public API
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>Every custom-operation name declared by a computation-expression builder.</summary>
let private customOperations (builderType: Type) =
    builderType.GetMethods(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)
    |> Array.collect (fun method' ->
        method'.GetCustomAttributes(typeof<CustomOperationAttribute>, false)
        |> Array.map (fun attribute' -> (attribute' :?> CustomOperationAttribute).Name))
    |> Array.sort

[<Fact>]
let ``the contract builder declares exactly the specified custom operations`` () =
    let expected =
        [| "alwaysInterleave"
           "grainType"
           "guidCompoundKey"
           "guidCompoundKeyMapped"
           "guidKey"
           "guidKeyMapped"
           "int64CompoundKey"
           "int64CompoundKeyMapped"
           "int64Key"
           "int64KeyMapped"
           "oneWay"
           "operationId"
           "readOnly"
           "stringKey"
           "stringKeyMapped"
           "version" |]

    test <@ customOperations typeof<GrainContractBuilder<SurfaceActor, string, SurfaceApi>> = expected @>

[<Fact>]
let ``the definition builder declares exactly the specified custom operations`` () =
    let expected =
        [| "collectionAge"
           "defaultState"
           "handle"
           "initialState"
           "onActivate"
           "onDeactivate"
           "onReminder"
           "onTimer"
           "stateFrom"
           "usePersistentState" |]

    test <@ customOperations typeof<FunctionalGrainDefinitionBuilder<SurfaceActor, string, SurfaceApi>> = expected @>

[<Fact>]
let ``the invocation context declares exactly the specified public members`` () =
    let expected =
        [| "cancellationToken"
           "deactivateOnIdle"
           "delayDeactivation"
           "grainFactory"
           "grainId"
           "key"
           "logger"
           "persistentState"
           "removeRequestContext"
           "services"
           "setRequestContext"
           "timeProvider"
           "tryGetRequestContext"
           "utcNow" |]

    let actual =
        typeof<FunctionalGrainContext<SurfaceActor, string>>
            .GetMembers(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)
        |> Array.map (fun member' -> member'.Name)
        |> Array.filter (fun name -> not (name.StartsWith "get_" || name.StartsWith "set_"))
        |> Array.filter (fun name -> not (List.contains name [ "ToString"; "Equals"; "GetHashCode"; "GetType" ]))
        |> Array.distinct
        |> Array.sort

    test <@ actual = expected @>

[<Fact>]
let ``the bound reference declares exactly the specified public members`` () =
    let expected = [| "api"; "call"; "callCancellable"; "key" |]

    let actual =
        typeof<FunctionalGrainRef<SurfaceActor, string, SurfaceApi>>
            .GetMembers(Reflection.BindingFlags.Public ||| Reflection.BindingFlags.Instance)
        |> Array.map (fun member' -> member'.Name)
        |> Array.filter (fun name -> not (name.StartsWith "get_" || name.StartsWith "set_"))
        |> Array.filter (fun name -> not (List.contains name [ "ToString"; "Equals"; "GetHashCode"; "GetType" ]))
        |> Array.distinct
        |> Array.sort

    test <@ actual = expected @>
