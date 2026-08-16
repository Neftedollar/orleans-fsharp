/// Phase 0 seam proof — custom reference selection and custom target activation
/// (spec 003 "Custom reference selection" and "Marker and activation target").
namespace Orleans.FSharp.SeamProof

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.GrainReferences
open Orleans.CodeGeneration
open Orleans.Metadata
open Orleans.Runtime
open Orleans.Serialization
open Orleans.Serialization.Cloning
open Orleans.Serialization.Serializers

// ── Custom reference ────────────────────────────────────────────────────────

[<Sealed>]
type private SeamGrainReferenceActivator(shared: GrainReferenceShared) =
    interface IGrainReferenceActivator with
        member _.CreateReference(grainId: GrainId) =
            FunctionalGrainReference(shared, grainId.Key) :> GrainReference

/// Accepts only `orleans.fsharp.functional/<grainType>` whose suffix exactly
/// equals the supplied `GrainId.Type`. Every other ID is declined.
[<Sealed>]
type SeamGrainReferenceActivatorProvider(services: IServiceProvider) =

    let runtime = lazy (services.GetRequiredService<IGrainReferenceRuntime>())
    let codecProvider = lazy (services.GetRequiredService<CodecProvider>())
    let copyContextPool = lazy (services.GetRequiredService<CopyContextPool>())

    interface IGrainReferenceActivatorProvider with
        member _.TryGet(grainType: GrainType, interfaceType: GrainInterfaceType, activator: byref<IGrainReferenceActivator>) =
            let id = interfaceType.ToString()

            if isNull id || not (id.StartsWith(FunctionalIds.Prefix, StringComparison.Ordinal)) then
                false
            else
                let suffix = id.Substring FunctionalIds.Prefix.Length

                if String.IsNullOrEmpty suffix || suffix.Contains '\000' then
                    false
                elif suffix <> grainType.ToString() then
                    false
                else
                    let shared =
                        GrainReferenceShared(
                            grainType,
                            interfaceType,
                            FunctionalIds.InterfaceVersion,
                            runtime.Value,
                            InvokeMethodOptions.None,
                            codecProvider.Value,
                            copyContextPool.Value,
                            services
                        )

                    activator <- SeamGrainReferenceActivator shared
                    true

// ── Persistent state configuration ──────────────────────────────────────────

[<Sealed>]
type SeamPersistentStateConfiguration(stateName: string, storageName: string) =
    interface IPersistentStateConfiguration with
        member _.StateName = stateName
        member _.StorageName = storageName

// ── Custom target ───────────────────────────────────────────────────────────

/// The base the functional target object expression derives from. It supplies
/// narrow internal wrappers for the protected `Grain` deactivation members.
[<AbstractClass>]
type FunctionalGrainTargetBase(grainContext: IGrainContext, grainRuntime: IGrainRuntime) =
    inherit Grain(grainContext, grainRuntime)

    member this.DeactivateNow() = this.DeactivateOnIdle()
    member this.DelayDeactivationFor(span: TimeSpan) = this.DelayDeactivation span

    abstract OnActivated: unit -> unit
    default _.OnActivated() = ()

    override this.OnActivateAsync(cancellationToken: CancellationToken) =
        this.OnActivated()
        base.OnActivateAsync cancellationToken

/// Creates the functional target for registered functional grain types.
/// Generic over the actor brand so the produced object expression implements the
/// exact closed `IFunctionalGrainTarget<'Actor>`.
[<Sealed>]
type SeamGrainActivator<'Actor>(definition: SeamDefinition) =

    interface IGrainActivator with
        member _.CreateInstance(grainContext: IGrainContext) : obj =
            let services = grainContext.ActivationServices
            let grainRuntime = services.GetRequiredService<IGrainRuntime>()
            let stateFactory = services.GetRequiredService<IPersistentStateFactory>()
            let serializer = services.GetRequiredService<Serializer>()
            let grainFactory = services.GetRequiredService<IGrainFactory>()

            // Every attached facet is created here — synchronously, before the
            // Orleans lifecycle runs — so it can subscribe at SetupState.
            let early =
                stateFactory.Create<ResizeArray<string>>(grainContext, SeamPersistentStateConfiguration("early", "Default"))

            let second =
                stateFactory.Create<ResizeArray<string>>(grainContext, SeamPersistentStateConfiguration("second", "Default"))

            // Deliberate negative control: attempting the same creation once the
            // activation lifecycle has started must be rejected by Orleans.
            let createFacetNow () =
                try
                    stateFactory.Create<ResizeArray<string>>(
                        grainContext,
                        SeamPersistentStateConfiguration("too-late", "Default")
                    )
                    |> ignore

                    "created"
                with ex ->
                    $"rejected:{ex.GetType().Name}:{ex.Message}"

            let probe = ActivationProbe()

            let env =
                { Context = grainContext
                  GrainType = definition.GrainType
                  Serializer = serializer
                  GrainFactory = grainFactory
                  EarlyState = early
                  SecondState = second
                  CreateFacetNow = createFacetNow
                  Probe = probe }

            let target =
                { new FunctionalGrainTargetBase(grainContext, grainRuntime) with
                    member this.OnActivated() =
                        probe.RecordExistsAtActivation <- early.RecordExists
                        probe.StateAtActivation <- StateBox.read early.State
                        probe.SecondRecordExistsAtActivation <- second.RecordExists
                        probe.SecondStateAtActivation <- StateBox.read second.State

                  interface IFunctionalDispatchTarget with
                      member _.DispatchAsync(envelope, cancellationToken) =
                          Dispatcher.dispatch env envelope cancellationToken

                  interface IFunctionalGrainTarget<'Actor> with
                      member _.DispatchAsync(envelope, cancellationToken) =
                          Dispatcher.dispatch env envelope cancellationToken

                  interface IRemindable with
                      member _.ReceiveReminder(_reminderName, _status) = Task.CompletedTask }

            probe.TargetTypeName <- target.GetType().Name
            probe.Deactivate <- fun () -> target.DeactivateNow()

            probe.ContextIsSuppliedContext <-
                Object.ReferenceEquals((target :> IGrainBase).GrainContext, grainContext)

            if not probe.ContextIsSuppliedContext then
                invalidOp
                    $"Functional target for '{definition.GrainType}' did not receive the supplied IGrainContext."

            box target

        member _.DisposeInstance(_grainContext: IGrainContext, _instance: obj) = ValueTask.CompletedTask

/// Installs the functional activator for registered functional grain types, and
/// leaves every other grain type unchanged.
[<Sealed>]
type SeamConfigureGrainTypeComponents(registry: SeamRegistry) =

    static member CreateActivator(definition: SeamDefinition) : IGrainActivator =
        let closed = typedefof<SeamGrainActivator<_>>.MakeGenericType(definition.ActorType)
        Activator.CreateInstance(closed, [| box definition |]) :?> IGrainActivator

    interface IConfigureGrainTypeComponents with
        member _.Configure(grainType: GrainType, _properties: GrainProperties, shared: GrainTypeSharedContext) =
            match registry.TryByGrainType(grainType.ToString()) with
            | Some definition ->
                shared.SetComponent<IGrainActivator>(SeamConfigureGrainTypeComponents.CreateActivator definition)
            | None -> ()
