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

// ──────────────────────────────────────────────────────────────────────────────
// Manifest pre-load
// ──────────────────────────────────────────────────────────────────────────────

/// <remarks>
/// <para>
/// Orleans snapshots its grain manifest from the assemblies already loaded when AddSerializer
/// first runs, and an Orleans assembly reached only through an F# hop is not among them unless
/// something touches it first. The pre-load derives that set from Orleans.FSharp.Runtime's own
/// Orleans references; this test re-derives the same ground truth independently and requires the
/// two to agree, so an Orleans package added to the runtime project — or one that fails to load
/// at run time and is silently skipped — fails here instead of at a consumer's first grain call.
/// </para>
/// <para>
/// What it does NOT prove: that the pre-load is what loaded a given assembly in this process.
/// Everything here is already loaded by the time a test host runs, so causality is out of reach;
/// the assertion is about the SET the pre-load resolves, which is the part that used to drift.
/// </para>
/// </remarks>
[<Fact>]
let ``the manifest pre-load covers every Orleans assembly the runtime references`` () =
    let runtime = typeof<SiloConfig>.Assembly

    let expected =
        runtime.GetReferencedAssemblies()
        |> Array.choose (fun reference ->
            if not (isNull reference.Name) && reference.Name.StartsWith("Orleans", StringComparison.Ordinal) then
                Some reference.Name
            else
                None)
        |> Array.sort

    let preloaded =
        SiloConfig.manifestAssemblies.Value
        |> Array.map (fun assembly -> assembly.GetName().Name)
        |> Array.sort

    test <@ preloaded = expected @>

/// <remarks>
/// The four assemblies whose grains an F# host reaches only through an F# hop, named so the
/// symptom each one causes is greppable: IMemoryStorageGrain, IReminderTableGrain, the memory
/// stream queue grains, and FSharpGrainImpl / the functional transport proxies. Each is also
/// checked to really carry Orleans' code-generated ApplicationPart attribute, which is what
/// makes loading it early matter at all.
/// </remarks>
[<Fact>]
let ``the pre-loaded set includes every assembly an F# host reaches only through an F# hop`` () =
    let byName =
        SiloConfig.manifestAssemblies.Value
        |> Array.map (fun assembly -> assembly.GetName().Name, assembly)
        |> Map.ofArray

    let required =
        [ "Orleans.Persistence.Memory" // IMemoryStorageGrain, for addMemoryStorage
          "Orleans.Reminders" // IReminderTableGrain, for addMemoryReminderService
          "Orleans.Streaming" // the memory stream queue grains, for addMemoryStreams
          "Orleans.FSharp.Abstractions" ] // FSharpGrainImpl and the functional transport proxies

    for name in required do
        test <@ byName.ContainsKey name @>

        let contributesToTheManifest =
            byName.[name].GetCustomAttributesData()
            |> Seq.exists (fun attribute' -> attribute'.AttributeType.Name = "ApplicationPartAttribute")

        test <@ contributesToTheManifest @>

/// <remarks>
/// A WebApplicationBuilder host calls <c>SiloConfig.applyToSiloBuilder</c> from inside
/// <c>builder.Host.UseOrleans(...)</c>, by which point UseOrleans has already taken the manifest
/// snapshot — so the pre-load also has to run when the configuration VALUE is built, which every
/// host shape does before UseOrleans.
/// </remarks>
[<Fact>]
let ``building a silo config forces the manifest pre-load`` () =
    let config = siloConfig { useLocalhostClustering }

    test <@ config.ClusteringMode |> Option.exists isLocalhost @>
    test <@ SiloConfig.manifestAssemblies.IsValueCreated @>
