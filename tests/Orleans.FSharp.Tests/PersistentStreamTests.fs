module Orleans.FSharp.Tests.PersistentStreamTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Runtime
open Orleans.Streams

/// <summary>Tests for persistent stream provider CE keywords.</summary>

/// <summary>Helper to check if a StreamProvider is a PersistentStream.</summary>
let isPersistentStream =
    function
    | PersistentStream _ -> true
    | _ -> false

[<Fact>]
let ``siloConfig CE addPersistentStreams stores config correctly`` () =
    let factory =
        Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>)

    let configurator =
        Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ -> ())

    let config =
        siloConfig {
            addPersistentStreams "EventHub" factory configurator
        }

    test <@ config.StreamProviders |> Map.containsKey "EventHub" @>
    test <@ config.StreamProviders.["EventHub"] |> isPersistentStream @>

[<Fact>]
let ``siloConfig CE addPersistentStreams stores factory and configurator`` () =
    let mutable factoryCalled = false
    let mutable configuratorCalled = false

    let factory =
        Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ ->
            factoryCalled <- true
            Unchecked.defaultof<IQueueAdapterFactory>)

    let configurator =
        Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ ->
            configuratorCalled <- true)

    let config =
        siloConfig {
            addPersistentStreams "MyStream" factory configurator
        }

    match config.StreamProviders.["MyStream"] with
    | PersistentStream(f, c) ->
        // Invoke factory to verify it's the correct one
        f.Invoke(Unchecked.defaultof<IServiceProvider>, "test") |> ignore
        test <@ factoryCalled @>
        // Invoke configurator to verify it's the correct one
        c.Invoke(Unchecked.defaultof<Orleans.Hosting.ISiloPersistentStreamConfigurator>)
        test <@ configuratorCalled @>
    | other -> failwith $"Expected PersistentStream, got {other}"

[<Fact>]
let ``siloConfig CE addPersistentStreams composes with memory streams`` () =
    let factory =
        Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>)

    let configurator =
        Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ -> ())

    let config =
        siloConfig {
            addMemoryStreams "MemoryProvider"
            addPersistentStreams "EventHub" factory configurator
        }

    test <@ config.StreamProviders |> Map.count = 2 @>
    test <@ config.StreamProviders |> Map.containsKey "MemoryProvider" @>
    test <@ config.StreamProviders |> Map.containsKey "EventHub" @>

[<Fact>]
let ``siloConfig CE addPersistentStreams composes with other options`` () =
    let factory =
        Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>)

    let configurator =
        Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ -> ())

    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addPersistentStreams "EventHub" factory configurator
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StreamProviders |> Map.containsKey "EventHub" @>

[<Fact>]
let ``siloConfig CE later persistent stream overrides earlier with same name`` () =
    let factory1 =
        Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>)

    let factory2 =
        Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>)

    let configurator =
        Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ -> ())

    let config =
        siloConfig {
            addPersistentStreams "Stream" factory1 configurator
            addPersistentStreams "Stream" factory2 configurator
        }

    test <@ config.StreamProviders |> Map.count = 1 @>

    match config.StreamProviders.["Stream"] with
    | PersistentStream(f, _) ->
        // Verify it's the second factory (factory2), not the first
        test <@ obj.ReferenceEquals(f, factory2) @>
    | other -> failwith $"Expected PersistentStream, got {other}"

[<Fact>]
let ``PersistentStream DU case exists in StreamProvider`` () =
    // Verify the DU case exists by pattern matching
    let provider =
        PersistentStream(
            Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>),
            Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ -> ())
        )

    test <@ provider |> isPersistentStream @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``addPersistentStreams stores any non-whitespace stream provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let factory =
            Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>)

        let configurator =
            Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ -> ())

        let config = siloConfig { addPersistentStreams name.Get factory configurator }
        config.StreamProviders |> Map.containsKey name.Get
        && config.StreamProviders.[name.Get] |> isPersistentStream)

[<Property>]
let ``n addPersistentStreams with distinct names yields n stream providers`` (n: PositiveInt) =
    let count = min n.Get 5

    let factory =
        Func<IServiceProvider, string, IQueueAdapterFactory>(fun _ _ -> Unchecked.defaultof<IQueueAdapterFactory>)

    let configurator =
        Action<Orleans.Hosting.ISiloPersistentStreamConfigurator>(fun _ -> ())

    let builder = SiloConfigBuilder()
    let mutable cfg = SiloConfig.Default

    for i in 1 .. count do
        cfg <- builder.AddPersistentStreams(cfg, $"Stream{i}", factory, configurator)

    cfg.StreamProviders |> Map.count = count
