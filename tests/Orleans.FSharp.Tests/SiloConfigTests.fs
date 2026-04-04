module Orleans.FSharp.Tests.SiloConfigTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Microsoft.Extensions.DependencyInjection
open Orleans
open Orleans.FSharp
open Orleans.FSharp.Runtime

/// <summary>Helper to check if a ClusteringMode is Localhost.</summary>
let isLocalhost =
    function
    | Localhost -> true
    | _ -> false

/// <summary>Helper to check if a StorageProvider is Memory.</summary>
let isMemory =
    function
    | Memory -> true
    | _ -> false

/// <summary>Helper to check if a StreamProvider is MemoryStream.</summary>
let isMemoryStream =
    function
    | MemoryStream -> true
    | _ -> false

/// <summary>Helper to check if a ReminderProvider is MemoryReminder.</summary>
let isMemoryReminder =
    function
    | MemoryReminder -> true
    | _ -> false

[<Fact>]
let ``siloConfig CE produces default config`` () =
    let config = siloConfig { () }
    test <@ config.ClusteringMode.IsNone @>
    test <@ config.StorageProviders |> Map.isEmpty @>
    test <@ config.StreamProviders |> Map.isEmpty @>
    test <@ config.UseSerilog = false @>
    test <@ config.CustomServices.Length = 0 @>

[<Fact>]
let ``siloConfig CE sets useLocalhostClustering`` () =
    let config = siloConfig { useLocalhostClustering }
    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isLocalhost @>

[<Fact>]
let ``siloConfig CE adds memory storage`` () =
    let config = siloConfig { addMemoryStorage "Default" }
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders.["Default"] |> isMemory @>

[<Fact>]
let ``siloConfig CE adds multiple memory storage providers`` () =
    let config =
        siloConfig {
            addMemoryStorage "Default"
            addMemoryStorage "Archive"
        }

    test <@ config.StorageProviders |> Map.count = 2 @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders |> Map.containsKey "Archive" @>

[<Fact>]
let ``siloConfig CE adds memory streams`` () =
    let config = siloConfig { addMemoryStreams "StreamProvider" }
    test <@ config.StreamProviders |> Map.containsKey "StreamProvider" @>
    test <@ config.StreamProviders.["StreamProvider"] |> isMemoryStream @>

[<Fact>]
let ``siloConfig CE sets useSerilog`` () =
    let config = siloConfig { useSerilog }
    test <@ config.UseSerilog = true @>

[<Fact>]
let ``siloConfig CE adds custom services`` () =
    let mutable called = false

    let config =
        siloConfig {
            configureServices (fun (_services: IServiceCollection) -> called <- true)
        }

    test <@ config.CustomServices.Length = 1 @>
    // Execute the service registration to verify it works
    let services = ServiceCollection() :> IServiceCollection
    config.CustomServices |> List.iter (fun f -> f services)
    test <@ called @>

[<Fact>]
let ``siloConfig CE composes all options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addMemoryStreams "StreamProvider"
            useSerilog
            configureServices (fun _ -> ())
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isLocalhost @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StreamProviders |> Map.containsKey "StreamProvider" @>
    test <@ config.UseSerilog = true @>
    test <@ config.CustomServices.Length = 1 @>

[<Fact>]
let ``siloConfig CE later storage overrides earlier with same name`` () =
    let config =
        siloConfig {
            addMemoryStorage "Default"
            addCustomStorage "Default" (fun builder -> builder)
        }

    test <@ config.StorageProviders |> Map.count = 1 @>

    match config.StorageProviders.["Default"] with
    | CustomStorage _ -> ()
    | other -> failwith $"Expected CustomStorage, got {other}"

[<Fact>]
let ``siloConfig CE multiple configureServices accumulate`` () =
    let mutable count = 0

    let config =
        siloConfig {
            configureServices (fun _ -> count <- count + 1)
            configureServices (fun _ -> count <- count + 1)
        }

    test <@ config.CustomServices.Length = 2 @>
    let services = ServiceCollection() :> IServiceCollection
    config.CustomServices |> List.iter (fun f -> f services)
    test <@ count = 2 @>

[<Fact>]
let ``SiloConfig.Default has empty values`` () =
    let config = SiloConfig.Default
    test <@ config.ClusteringMode.IsNone @>
    test <@ config.StorageProviders |> Map.isEmpty @>
    test <@ config.StreamProviders |> Map.isEmpty @>
    test <@ config.UseSerilog = false @>
    test <@ config.CustomServices.Length = 0 @>

[<Fact>]
let ``siloConfig CE adds memory reminder service`` () =
    let config = siloConfig { addMemoryReminderService }
    test <@ config.ReminderProvider.IsSome @>
    test <@ config.ReminderProvider.Value |> isMemoryReminder @>

[<Fact>]
let ``siloConfig CE default has no reminder service`` () =
    let config = siloConfig { () }
    test <@ config.ReminderProvider.IsNone @>

[<Fact>]
let ``siloConfig CE composes reminder service with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addMemoryReminderService
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.ReminderProvider.IsSome @>
    test <@ config.ReminderProvider.Value |> isMemoryReminder @>

[<Fact>]
let ``siloConfig CE validate detects missing clustering`` () =
    let config = siloConfig { addMemoryStorage "Default" }

    let errors = SiloConfig.validate config
    test <@ errors |> List.exists (fun e -> e.Contains("clustering")) @>

[<Fact>]
let ``siloConfig CE validate passes with clustering set`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
        }

    let errors = SiloConfig.validate config
    test <@ errors = [] @>

[<Fact>]
let ``siloConfig CE default has no filters`` () =
    let config = siloConfig { () }
    test <@ config.IncomingFilters.Length = 0 @>
    test <@ config.OutgoingFilters.Length = 0 @>

[<Fact>]
let ``siloConfig CE adds incoming filter`` () =
    let filter =
        Filter.incoming (fun _ctx -> Task.FromResult())

    let config = siloConfig { addIncomingFilter filter }
    test <@ config.IncomingFilters.Length = 1 @>

[<Fact>]
let ``siloConfig CE adds outgoing filter`` () =
    let filter =
        Filter.outgoing (fun _ctx -> Task.FromResult())

    let config = siloConfig { addOutgoingFilter filter }
    test <@ config.OutgoingFilters.Length = 1 @>

[<Fact>]
let ``siloConfig CE multiple filters accumulate`` () =
    let inFilter1 =
        Filter.incoming (fun _ctx -> Task.FromResult())

    let inFilter2 =
        Filter.incoming (fun _ctx -> Task.FromResult())

    let outFilter =
        Filter.outgoing (fun _ctx -> Task.FromResult())

    let config =
        siloConfig {
            addIncomingFilter inFilter1
            addIncomingFilter inFilter2
            addOutgoingFilter outFilter
        }

    test <@ config.IncomingFilters.Length = 2 @>
    test <@ config.OutgoingFilters.Length = 1 @>

[<Fact>]
let ``siloConfig CE composes filters with other options`` () =
    let filter =
        Filter.incoming (fun _ctx -> Task.FromResult())

    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            addIncomingFilter filter
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.IncomingFilters.Length = 1 @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``addMemoryStorage stores correct name for any non-whitespace name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addMemoryStorage name.Get }
        config.StorageProviders |> Map.containsKey name.Get
        && config.StorageProviders.[name.Get] |> isMemory)

[<Property>]
let ``addMemoryStreams stores correct name for any non-whitespace name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addMemoryStreams name.Get }
        config.StreamProviders |> Map.containsKey name.Get
        && config.StreamProviders.[name.Get] |> isMemoryStream)

[<Property>]
let ``SiloConfig.validate with missing clustering always returns error list`` () =
    let config = siloConfig { () }
    let errors = SiloConfig.validate config
    errors |> List.exists (fun e -> e.Length > 0)

[<Property>]
let ``SiloConfig.validate returns empty list when clustering is set`` () =
    let config = siloConfig { useLocalhostClustering }
    SiloConfig.validate config = []
