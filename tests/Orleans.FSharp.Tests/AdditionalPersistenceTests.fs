module Orleans.FSharp.Tests.AdditionalPersistenceTests

open System
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp.Runtime

/// <summary>Helper to check if a StorageProvider is CosmosStorage.</summary>
let isCosmosStorage =
    function
    | CosmosStorage _ -> true
    | _ -> false

/// <summary>Helper to check if a StorageProvider is DynamoDbStorage.</summary>
let isDynamoDbStorage =
    function
    | DynamoDbStorage _ -> true
    | _ -> false

// --- Cosmos DB storage CE tests ---

[<Fact>]
let ``siloConfig CE adds Cosmos DB storage`` () =
    let config =
        siloConfig { addCosmosStorage "Default" "AccountEndpoint=https://test.documents.azure.com:443/" "MyDatabase" }

    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders.["Default"] |> isCosmosStorage @>

[<Fact>]
let ``siloConfig CE Cosmos storage stores endpoint and database`` () =
    let endpoint = "AccountEndpoint=https://cosmos.example.com:443/"
    let db = "OrleansDb"
    let config = siloConfig { addCosmosStorage "Cosmos" endpoint db }

    match config.StorageProviders.["Cosmos"] with
    | CosmosStorage(ep, dbName) ->
        test <@ ep = endpoint @>
        test <@ dbName = db @>
    | other -> failwith $"Expected CosmosStorage, got {other}"

[<Fact>]
let ``siloConfig CE Cosmos storage composes with other providers`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Temp"
            addCosmosStorage "Cosmos" "AccountEndpoint=..." "Db"
        }

    test <@ config.StorageProviders |> Map.count = 2 @>
    test <@ config.StorageProviders.["Temp"] |> (function Memory -> true | _ -> false) @>
    test <@ config.StorageProviders.["Cosmos"] |> isCosmosStorage @>

// --- DynamoDB storage CE tests ---

[<Fact>]
let ``siloConfig CE adds DynamoDB storage`` () =
    let config = siloConfig { addDynamoDbStorage "Default" "us-east-1" }

    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.StorageProviders.["Default"] |> isDynamoDbStorage @>

[<Fact>]
let ``siloConfig CE DynamoDB storage stores region`` () =
    let config = siloConfig { addDynamoDbStorage "Dynamo" "eu-west-2" }

    match config.StorageProviders.["Dynamo"] with
    | DynamoDbStorage region -> test <@ region = "eu-west-2" @>
    | other -> failwith $"Expected DynamoDbStorage, got {other}"

[<Fact>]
let ``siloConfig CE DynamoDB storage composes with other providers`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addRedisStorage "Redis" "localhost:6379"
            addDynamoDbStorage "Dynamo" "us-west-2"
        }

    test <@ config.StorageProviders |> Map.count = 2 @>
    test <@ config.StorageProviders.["Redis"] |> (function RedisStorage _ -> true | _ -> false) @>
    test <@ config.StorageProviders.["Dynamo"] |> isDynamoDbStorage @>

// --- Override tests ---

[<Fact>]
let ``siloConfig CE Cosmos overrides earlier provider with same name`` () =
    let config =
        siloConfig {
            addMemoryStorage "Default"
            addCosmosStorage "Default" "AccountEndpoint=..." "Db"
        }

    test <@ config.StorageProviders |> Map.count = 1 @>
    test <@ config.StorageProviders.["Default"] |> isCosmosStorage @>

[<Fact>]
let ``siloConfig CE DynamoDB overrides earlier provider with same name`` () =
    let config =
        siloConfig {
            addMemoryStorage "Default"
            addDynamoDbStorage "Default" "us-east-1"
        }

    test <@ config.StorageProviders |> Map.count = 1 @>
    test <@ config.StorageProviders.["Default"] |> isDynamoDbStorage @>

[<Fact>]
let ``siloConfig CE all new persistence providers compose together`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addCosmosStorage "Cosmos" "AccountEndpoint=..." "Db"
            addDynamoDbStorage "Dynamo" "us-east-1"
            addMemoryStorage "Mem"
            addRedisStorage "Redis" "localhost:6379"
        }

    test <@ config.StorageProviders |> Map.count = 4 @>
    test <@ config.StorageProviders.["Cosmos"] |> isCosmosStorage @>
    test <@ config.StorageProviders.["Dynamo"] |> isDynamoDbStorage @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``addCosmosStorage stores any non-whitespace provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addCosmosStorage name.Get "AccountEndpoint=..." "Db" }
        config.StorageProviders |> Map.containsKey name.Get
        && config.StorageProviders.[name.Get] |> isCosmosStorage)

[<Property>]
let ``addDynamoDbStorage stores any non-whitespace provider name`` (name: NonNull<string>) =
    String.IsNullOrWhiteSpace name.Get
    || (let config = siloConfig { addDynamoDbStorage name.Get "us-east-1" }
        config.StorageProviders |> Map.containsKey name.Get
        && config.StorageProviders.[name.Get] |> isDynamoDbStorage)

[<Property>]
let ``CosmosStorage carries the endpoint and database name`` (endpoint: NonNull<string>) (db: NonNull<string>) =
    String.IsNullOrWhiteSpace endpoint.Get
    || String.IsNullOrWhiteSpace db.Get
    || (let config = siloConfig { addCosmosStorage "Provider" endpoint.Get db.Get }

        match config.StorageProviders.["Provider"] with
        | CosmosStorage(ep, dbName) -> ep = endpoint.Get && dbName = db.Get
        | _ -> false)
