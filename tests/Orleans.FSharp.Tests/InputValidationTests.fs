module Orleans.FSharp.Tests.InputValidationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp
open Orleans.FSharp.Runtime

// ---------------------------------------------------------------------------
// GrainBuilder: persist
// ---------------------------------------------------------------------------

[<Fact>]
let ``persist with empty string throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            grain {
                defaultState 0
                handle (fun s (_m: string) -> task { return s, box s })
                persist ""
            }
        @>

[<Fact>]
let ``persist with whitespace throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            grain {
                defaultState 0
                handle (fun s (_m: string) -> task { return s, box s })
                persist "   "
            }
        @>

// ---------------------------------------------------------------------------
// GrainBuilder: grainType
// ---------------------------------------------------------------------------

[<Fact>]
let ``grainType with empty string throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            grain {
                defaultState 0
                handle (fun s (_m: string) -> task { return s, box s })
                grainType ""
            }
        @>

// ---------------------------------------------------------------------------
// GrainBuilder: onReminder
// ---------------------------------------------------------------------------

[<Fact>]
let ``onReminder with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            grain {
                defaultState 0
                handle (fun s (_m: string) -> task { return s, box s })
                onReminder "" (fun s _name _tick -> task { return s })
            }
        @>

// ---------------------------------------------------------------------------
// GrainBuilder: onTimer
// ---------------------------------------------------------------------------

[<Fact>]
let ``onTimer with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            grain {
                defaultState 0
                handle (fun s (_m: string) -> task { return s, box s })
                onTimer "" TimeSpan.Zero TimeSpan.Zero (fun s -> task { return s })
            }
        @>

// ---------------------------------------------------------------------------
// GrainBuilder: siloRolePlacement
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloRolePlacement with empty role throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            grain {
                defaultState 0
                handle (fun s (_m: string) -> task { return s, box s })
                siloRolePlacement ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: clusterId
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig clusterId with empty string throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                clusterId ""
            }
        @>

[<Fact>]
let ``siloConfig clusterId with whitespace throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                clusterId "  "
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: serviceId
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig serviceId with empty string throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                serviceId ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: siloName
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig siloName with empty string throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                siloName ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addMemoryStorage
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addMemoryStorage with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addMemoryStorage ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addRedisStorage
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addRedisStorage with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addRedisStorage "" "localhost:6379"
            }
        @>

[<Fact>]
let ``siloConfig addRedisStorage with empty connectionString throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addRedisStorage "Default" ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addRedisClustering
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addRedisClustering with empty connectionString throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                addRedisClustering ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addAzureBlobStorage
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addAzureBlobStorage with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addAzureBlobStorage "" "UseDevelopmentStorage=true"
            }
        @>

[<Fact>]
let ``siloConfig addAzureBlobStorage with empty connectionString throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addAzureBlobStorage "Default" ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addAzureTableStorage
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addAzureTableStorage with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addAzureTableStorage "" "UseDevelopmentStorage=true"
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addAzureTableClustering
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addAzureTableClustering with empty connectionString throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                addAzureTableClustering ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addAdoNetStorage
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addAdoNetStorage with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addAdoNetStorage "" "connStr" "Npgsql"
            }
        @>

[<Fact>]
let ``siloConfig addAdoNetStorage with empty connectionString throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addAdoNetStorage "Default" "" "Npgsql"
            }
        @>

[<Fact>]
let ``siloConfig addAdoNetStorage with empty invariant throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addAdoNetStorage "Default" "connStr" ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addAdoNetClustering
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addAdoNetClustering with empty connectionString throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                addAdoNetClustering "" "Npgsql"
            }
        @>

[<Fact>]
let ``siloConfig addAdoNetClustering with empty invariant throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                addAdoNetClustering "connStr" ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addMemoryStreams
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addMemoryStreams with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addMemoryStreams ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addCosmosStorage
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addCosmosStorage with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addCosmosStorage "" "https://endpoint" "db"
            }
        @>

[<Fact>]
let ``siloConfig addCosmosStorage with empty endpoint throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addCosmosStorage "Default" "" "db"
            }
        @>

[<Fact>]
let ``siloConfig addCosmosStorage with empty databaseName throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addCosmosStorage "Default" "https://endpoint" ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addDynamoDbStorage
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addDynamoDbStorage with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addDynamoDbStorage "" "us-east-1"
            }
        @>

[<Fact>]
let ``siloConfig addDynamoDbStorage with empty region throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addDynamoDbStorage "Default" ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addRedisReminderService
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addRedisReminderService with empty connectionString throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addRedisReminderService ""
            }
        @>

// ---------------------------------------------------------------------------
// SiloConfigBuilder: addBroadcastChannel
// ---------------------------------------------------------------------------

[<Fact>]
let ``siloConfig addBroadcastChannel with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            siloConfig {
                useLocalhostClustering
                addBroadcastChannel ""
            }
        @>

// ---------------------------------------------------------------------------
// ClientConfigBuilder: clusterId
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig clusterId with empty string throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            clientConfig {
                useLocalhostClustering
                clusterId ""
            }
        @>

[<Fact>]
let ``clientConfig clusterId with whitespace throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            clientConfig {
                useLocalhostClustering
                clusterId "  "
            }
        @>

// ---------------------------------------------------------------------------
// ClientConfigBuilder: serviceId
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig serviceId with empty string throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            clientConfig {
                useLocalhostClustering
                serviceId ""
            }
        @>

// ---------------------------------------------------------------------------
// ClientConfigBuilder: addMemoryStreams
// ---------------------------------------------------------------------------

[<Fact>]
let ``clientConfig addMemoryStreams with empty name throws ArgumentException`` () =
    raises<ArgumentException>
        <@
            clientConfig {
                useLocalhostClustering
                addMemoryStreams ""
            }
        @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

/// Helper: create a minimal grain definition, run an action, return whether an
/// ArgumentException was thrown.
let private grainPersistThrows (providerName: string) =
    let mutable threw = false
    try
        grain {
            defaultState 0
            handle (fun s (_m: string) -> task { return s, box s })
            persist providerName
        }
        |> ignore
    with :? ArgumentException ->
        threw <- true
    threw

[<Property>]
let ``persist throws for any string consisting only of spaces`` (n: NonNegativeInt) =
    let ws = String(' ', n.Get)
    grainPersistThrows ws

[<Property>]
let ``persist throws for any string consisting only of tabs`` (n: PositiveInt) =
    let ws = String('\t', n.Get)
    grainPersistThrows ws

[<Property>]
let ``persist accepts any non-whitespace provider name`` (name: NonNull<string>) =
    // Vacuously true for whitespace names (covered by separate tests); validates the non-throwing path
    String.IsNullOrWhiteSpace name.Get || not (grainPersistThrows name.Get)

[<Property>]
let ``siloConfig clusterId throws for any whitespace-only string`` (n: PositiveInt) =
    let ws = String(' ', n.Get)
    let mutable threw = false
    try
        siloConfig {
            useLocalhostClustering
            clusterId ws
        }
        |> ignore
    with :? ArgumentException ->
        threw <- true
    threw

[<Property>]
let ``siloConfig clusterId accepts any non-whitespace id`` (id: NonNull<string>) =
    // Vacuously true for whitespace ids; validates the non-throwing path
    if String.IsNullOrWhiteSpace id.Get then true
    else
        let mutable ok = false
        try
            siloConfig { useLocalhostClustering; clusterId id.Get } |> ignore
            ok <- true
        with :? ArgumentException -> ()
        ok

[<Property>]
let ``siloConfig addMemoryStorage throws for any whitespace-only name`` (n: PositiveInt) =
    let ws = String(' ', n.Get)
    let mutable threw = false
    try
        siloConfig { useLocalhostClustering; addMemoryStorage ws } |> ignore
    with :? ArgumentException ->
        threw <- true
    threw

[<Property>]
let ``clientConfig clusterId throws for any whitespace-only string`` (n: PositiveInt) =
    let ws = String(' ', n.Get)
    let mutable threw = false
    try
        clientConfig { useLocalhostClustering; clusterId ws } |> ignore
    with :? ArgumentException ->
        threw <- true
    threw
