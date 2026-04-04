module Orleans.FSharp.Tests.PersistenceProviderTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Runtime

// --- StorageProvider DU tests ---

/// <summary>Helper to check if a StorageProvider is Redis.</summary>
let isRedisStorage =
    function
    | RedisStorage _ -> true
    | _ -> false

/// <summary>Helper to check if a StorageProvider is AzureBlobStorage.</summary>
let isAzureBlobStorage =
    function
    | AzureBlobStorage _ -> true
    | _ -> false

/// <summary>Helper to check if a StorageProvider is AzureTableStorage.</summary>
let isAzureTableStorage =
    function
    | AzureTableStorage _ -> true
    | _ -> false

/// <summary>Helper to check if a StorageProvider is AdoNetStorage.</summary>
let isAdoNetStorage =
    function
    | AdoNetStorage _ -> true
    | _ -> false

/// <summary>Helper to check if a ClusteringMode is RedisClustering.</summary>
let isRedisClustering =
    function
    | RedisClustering _ -> true
    | _ -> false

/// <summary>Helper to check if a ClusteringMode is AzureTableClustering.</summary>
let isAzureTableClustering =
    function
    | AzureTableClustering _ -> true
    | _ -> false

/// <summary>Helper to check if a ClusteringMode is AdoNetClustering.</summary>
let isAdoNetClustering =
    function
    | AdoNetClustering _ -> true
    | _ -> false

/// <summary>Helper to check if a StorageProvider is Memory.</summary>
let isMemory =
    function
    | Memory -> true
    | _ -> false

// --- Redis storage CE tests ---

[<Fact>]
let ``siloConfig CE adds Redis storage`` () =
    let config = siloConfig { addRedisStorage "Default" "localhost:6379" }

    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders.["Default"] |> isRedisStorage @>

[<Fact>]
let ``siloConfig CE Redis storage stores connection string`` () =
    let config = siloConfig { addRedisStorage "Cache" "redis.example.com:6379" }

    match config.StorageProviders.["Cache"] with
    | RedisStorage connStr -> test <@ connStr = "redis.example.com:6379" @>
    | other -> failwith $"Expected RedisStorage, got {other}"

// --- Redis clustering CE tests ---

[<Fact>]
let ``siloConfig CE adds Redis clustering`` () =
    let config = siloConfig { addRedisClustering "localhost:6379" }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isRedisClustering @>

[<Fact>]
let ``siloConfig CE Redis clustering stores connection string`` () =
    let config = siloConfig { addRedisClustering "redis.example.com:6379" }

    match config.ClusteringMode.Value with
    | RedisClustering connStr -> test <@ connStr = "redis.example.com:6379" @>
    | other -> failwith $"Expected RedisClustering, got {other}"

// --- Azure Blob storage CE tests ---

[<Fact>]
let ``siloConfig CE adds Azure Blob storage`` () =
    let config =
        siloConfig { addAzureBlobStorage "Default" "DefaultEndpointsProtocol=https;AccountName=test" }

    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders.["Default"] |> isAzureBlobStorage @>

[<Fact>]
let ``siloConfig CE Azure Blob storage stores connection string`` () =
    let connStr = "DefaultEndpointsProtocol=https;AccountName=test"
    let config = siloConfig { addAzureBlobStorage "Blobs" connStr }

    match config.StorageProviders.["Blobs"] with
    | AzureBlobStorage cs -> test <@ cs = connStr @>
    | other -> failwith $"Expected AzureBlobStorage, got {other}"

// --- Azure Table storage CE tests ---

[<Fact>]
let ``siloConfig CE adds Azure Table storage`` () =
    let config =
        siloConfig { addAzureTableStorage "Default" "DefaultEndpointsProtocol=https;AccountName=test" }

    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders.["Default"] |> isAzureTableStorage @>

[<Fact>]
let ``siloConfig CE Azure Table storage stores connection string`` () =
    let connStr = "DefaultEndpointsProtocol=https;AccountName=test"
    let config = siloConfig { addAzureTableStorage "Tables" connStr }

    match config.StorageProviders.["Tables"] with
    | AzureTableStorage cs -> test <@ cs = connStr @>
    | other -> failwith $"Expected AzureTableStorage, got {other}"

// --- Azure Table clustering CE tests ---

[<Fact>]
let ``siloConfig CE adds Azure Table clustering`` () =
    let config =
        siloConfig { addAzureTableClustering "DefaultEndpointsProtocol=https;AccountName=test" }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isAzureTableClustering @>

[<Fact>]
let ``siloConfig CE Azure Table clustering stores connection string`` () =
    let connStr = "DefaultEndpointsProtocol=https;AccountName=test"
    let config = siloConfig { addAzureTableClustering connStr }

    match config.ClusteringMode.Value with
    | AzureTableClustering cs -> test <@ cs = connStr @>
    | other -> failwith $"Expected AzureTableClustering, got {other}"

// --- ADO.NET storage CE tests ---

[<Fact>]
let ``siloConfig CE adds ADO.NET storage`` () =
    let config =
        siloConfig { addAdoNetStorage "Default" "Host=localhost;Database=orleans" "Npgsql" }

    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders.["Default"] |> isAdoNetStorage @>

[<Fact>]
let ``siloConfig CE ADO.NET storage stores connection string and invariant`` () =
    let connStr = "Host=localhost;Database=orleans"
    let invariant = "Npgsql"
    let config = siloConfig { addAdoNetStorage "Sql" connStr invariant }

    match config.StorageProviders.["Sql"] with
    | AdoNetStorage(cs, inv) ->
        test <@ cs = connStr @>
        test <@ inv = invariant @>
    | other -> failwith $"Expected AdoNetStorage, got {other}"

// --- ADO.NET clustering CE tests ---

[<Fact>]
let ``siloConfig CE adds ADO.NET clustering`` () =
    let config =
        siloConfig { addAdoNetClustering "Host=localhost;Database=orleans" "Npgsql" }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isAdoNetClustering @>

[<Fact>]
let ``siloConfig CE ADO.NET clustering stores connection string and invariant`` () =
    let connStr = "Host=localhost;Database=orleans"
    let invariant = "Npgsql"
    let config = siloConfig { addAdoNetClustering connStr invariant }

    match config.ClusteringMode.Value with
    | AdoNetClustering(cs, inv) ->
        test <@ cs = connStr @>
        test <@ inv = invariant @>
    | other -> failwith $"Expected AdoNetClustering, got {other}"

// --- Composition tests ---

[<Fact>]
let ``siloConfig CE composes Redis storage with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addRedisStorage "Default" "localhost:6379"
            addMemoryStorage "Temp"
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.count = 2 @>
    test <@ config.StorageProviders.["Default"] |> isRedisStorage @>
    test <@ config.StorageProviders.["Temp"] |> isMemory @>

[<Fact>]
let ``siloConfig CE Redis clustering replaces localhost clustering`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addRedisClustering "localhost:6379"
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.ClusteringMode.Value |> isRedisClustering @>

[<Fact>]
let ``siloConfig CE composes ADO.NET with Azure providers`` () =
    let config =
        siloConfig {
            addAzureTableClustering "ConnStr"
            addAdoNetStorage "Sql" "Host=localhost" "Npgsql"
            addAzureBlobStorage "Blobs" "AzureConnStr"
        }

    test <@ config.ClusteringMode.Value |> isAzureTableClustering @>
    test <@ config.StorageProviders |> Map.count = 2 @>
    test <@ config.StorageProviders.["Sql"] |> isAdoNetStorage @>
    test <@ config.StorageProviders.["Blobs"] |> isAzureBlobStorage @>

[<Fact>]
let ``siloConfig CE later storage overrides earlier with same name for new providers`` () =
    let config =
        siloConfig {
            addRedisStorage "Default" "localhost:6379"
            addAzureBlobStorage "Default" "AzureConnStr"
        }

    test <@ config.StorageProviders |> Map.count = 1 @>
    test <@ config.StorageProviders.["Default"] |> isAzureBlobStorage @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``addRedisStorage stores any non-whitespace provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addRedisStorage name.Get "localhost:6379" }
        config.StorageProviders |> Map.containsKey name.Get
        && config.StorageProviders.[name.Get] |> isRedisStorage)

[<Property>]
let ``addAzureBlobStorage stores any non-whitespace provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addAzureBlobStorage name.Get "connStr" }
        config.StorageProviders |> Map.containsKey name.Get
        && config.StorageProviders.[name.Get] |> isAzureBlobStorage)

[<Property>]
let ``addAdoNetStorage stores any non-whitespace provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addAdoNetStorage name.Get "connStr" "npgsql" }
        config.StorageProviders |> Map.containsKey name.Get
        && config.StorageProviders.[name.Get] |> isAdoNetStorage)
