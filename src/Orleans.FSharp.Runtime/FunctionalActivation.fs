namespace Orleans.FSharp

open System
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Metadata
open Orleans.Runtime
open Orleans.FSharp.FunctionalDiagnostics
open Orleans.FSharp.FunctionalSiloDiagnostics

/// <summary>
/// The functional grain activator of one actor brand. It resolves <c>IGrainRuntime</c> from the
/// activation services, builds the target as an F# object expression over
/// <see cref="T:Orleans.FSharp.FunctionalGrainTargetBase"/> implementing the exact closed
/// <c>IFunctionalGrainTarget&lt;'Actor&gt;</c>, the non-generic dispatch seam, and
/// <c>IRemindable</c>, and verifies that the target really carries the supplied activation
/// context before returning it.
/// </summary>
/// <typeparam name="TActor">The actor brand of the hosted definition.</typeparam>
[<Sealed>]
type internal FunctionalGrainActivator<'Actor>(definition: FunctionalHostedDefinition) =

    interface IGrainActivator with
        member _.CreateInstance(grainContext: IGrainContext) : obj =
            let services = grainContext.ActivationServices
            let grainRuntime = services.GetRequiredService<IGrainRuntime>()
            let grainFactory = services.GetRequiredService<IGrainFactory>()
            let codec = services.GetRequiredService<FunctionalPayloadCodec>()

            let timeProvider =
                match services.GetService typeof<TimeProvider> with
                | :? TimeProvider as provider -> provider
                | _ -> TimeProvider.System

            let logger =
                services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger($"Orleans.FSharp.Functional.{definition.GrainTypeName}")

            // The domain key is decoded once per activation. The primary state itself is created
            // in OnActivateAsync (activation step 3), never here, so a definition without a
            // durable record and one with it follow the same ordering.
            let key = definition.DecodeKey grainContext.GrainId

            // Step 1 of the activation order: every attached facet is created here,
            // synchronously and before the target is returned, so each one subscribes to the
            // activation lifecycle in time for the stock GrainLifecycleStage.SetupState load.
            // IPersistentStateFactory.Create refuses to run once the lifecycle has started.
            let facets =
                if definition.Facets.Length = 0 then
                    Array.empty
                else
                    let stateFactory = services.GetRequiredService<IPersistentStateFactory>()

                    definition.Facets
                    |> Array.map (fun blueprint ->
                        { Blueprint = blueprint
                          Instance = blueprint.Create stateFactory grainContext })

            let mutable deactivate = fun () -> ()
            let mutable delay = fun (_: TimeSpan) -> ()

            let env =
                { Definition = definition
                  GrainContext = grainContext
                  Services = services
                  GrainFactory = grainFactory
                  Logger = logger
                  TimeProvider = timeProvider
                  Codec = codec
                  MaxPayloadBytes = FunctionalTransportConfiguration.maxPayloadBytes services
                  Key = key
                  State = FunctionalActivationState(definition, facets)
                  DeactivateOnIdle = fun () -> deactivate ()
                  DelayDeactivation = fun timeSpan -> delay timeSpan }

            let target =
                { new FunctionalGrainTargetBase(grainContext, grainRuntime) with
                    member _.OnDisposing() = ()

                    member _.OnActivating(cancellationToken) =
                        FunctionalLifecycle.activate env cancellationToken

                    member _.OnDeactivating(reason, cancellationToken) =
                        FunctionalLifecycle.deactivate env reason cancellationToken

                  interface IFunctionalDispatchTarget with
                      member _.DispatchAsync(envelope, cancellationToken) =
                          FunctionalDispatch.dispatch env envelope cancellationToken

                  interface IFunctionalGrainTarget<'Actor> with
                      member _.DispatchAsync(envelope, cancellationToken) =
                          FunctionalDispatch.dispatch env envelope cancellationToken

                  interface IRemindable with
                      member _.ReceiveReminder(reminderName: string, _status: TickStatus) =
                          logger.LogError(
                              "Grain {GrainId} of functional grain type {GrainType} received unknown reminder {ReminderName}",
                              grainContext.GrainId,
                              definition.GrainTypeName,
                              reminderName
                          )

                          Task.FromException(
                              InvalidOperationException(
                                  $"{StartupStage}: grain '{grainContext.GrainId}' of grain type '{definition.GrainTypeName}' received reminder '{reminderName}', which the hosted definition does not declare."
                              )
                          ) }

            deactivate <- fun () -> target.DeactivateNow()
            delay <- fun timeSpan -> target.DelayDeactivationFor timeSpan

            if not (obj.ReferenceEquals((target :> IGrainBase).GrainContext, grainContext)) then
                fail
                    StartupStage
                    $"the functional activation target of grain type '{definition.GrainTypeName}' did not receive the supplied IGrainContext."

            box target

        member _.DisposeInstance(_grainContext: IGrainContext, instance: obj) =
            match instance with
            | :? IDisposable as disposable ->
                disposable.Dispose()
                ValueTask.CompletedTask
            | _ -> ValueTask.CompletedTask

/// <summary>
/// Installs the functional <c>IGrainActivator</c> on every registered functional grain type and
/// leaves every other grain type untouched.
/// </summary>
[<Sealed>]
type internal FunctionalConfigureGrainTypeComponents(registry: FunctionalGrainRegistry) =

    /// <summary>Close the functional activator over one definition's actor brand.</summary>
    static member CreateActivator(definition: FunctionalHostedDefinition) : IGrainActivator =
        let closed =
            typedefof<FunctionalGrainActivator<_>>.MakeGenericType [| definition.ActorType |]

        match Activator.CreateInstance(closed, [| box definition |]) with
        | :? IGrainActivator as activator -> activator
        | _ ->
            fail
                StartupStage
                $"the functional grain activator for grain type '{definition.GrainTypeName}' could not be created."

    interface IConfigureGrainTypeComponents with
        member _.Configure(grainType: GrainType, _properties: GrainProperties, shared: GrainTypeSharedContext) =
            match registry.TryByGrainType(grainType.ToString()) with
            | Some entry ->
                shared.SetComponent<IGrainActivator>(
                    FunctionalConfigureGrainTypeComponents.CreateActivator entry.Definition
                )
            | None -> ()
