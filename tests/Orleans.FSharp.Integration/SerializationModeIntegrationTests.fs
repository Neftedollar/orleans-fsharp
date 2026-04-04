module Orleans.FSharp.Integration.SerializationModeIntegrationTests

open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp.Sample

/// <summary>
/// Silo configurator that enables JSON fallback serialization for clean F# types.
/// Types without [GenerateSerializer] will be serialized using System.Text.Json
/// with FSharp.SystemTextJson converters.
/// </summary>
type JsonFallbackSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.UseInMemoryReminderService() |> ignore

            // Enable JSON fallback serialization
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                siloBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.Serialization.SerializationHostingExtensions.AddJsonSerializer(
                        serializerBuilder,
                        isSupported = System.Func<System.Type, bool>(fun _ -> true),
                        jsonSerializerOptions = Orleans.FSharp.FSharpJson.serializerOptions)
                    |> ignore))
            |> ignore

/// <summary>
/// xUnit fixture that starts a TestCluster with JSON fallback serialization enabled.
/// </summary>
type JsonFallbackClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    /// <summary>Gets the running TestCluster instance.</summary>
    member _.Cluster = cluster

    /// <summary>Gets the GrainFactory for creating grain references.</summary>
    member _.GrainFactory = cluster.GrainFactory

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CodeGenAssemblyMarker>.Assembly
                let _ = codeGenAssembly.GetTypes()

                let builder = TestClusterBuilder()
                builder.Options.InitialSilosCount <- 1s
                builder.AddSiloBuilderConfigurator<JsonFallbackSiloConfigurator>() |> ignore
                cluster <- builder.Build()
                do! cluster.DeployAsync()
            }

        member _.DisposeAsync() =
            task {
                if not (isNull (box cluster)) then
                    do! cluster.StopAllSilosAsync()
                    cluster.Dispose()
            }

/// <summary>
/// xUnit collection for tests sharing the JSON fallback cluster.
/// </summary>
[<CollectionDefinition("JsonFallbackCluster")>]
type JsonFallbackClusterCollection() =
    interface ICollectionFixture<JsonFallbackClusterFixture>

// ---------------------------------------------------------------------------
// Mode 3 (Explicit) integration tests — [GenerateSerializer] + [Id]
// ---------------------------------------------------------------------------

[<Collection("ClusterCollection")>]
type ExplicitModeIntegrationTests(fixture: ClusterFixture) =

    /// <summary>
    /// Mode 3 (Explicit): [GenerateSerializer] + [Id] DU roundtrips through Orleans grain.
    /// CounterCommand is defined with explicit attributes.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode CounterCommand serializes through grain call`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(42L)
            let! result = grain.HandleMessage(Increment)
            let value = result :?> int
            test <@ value = 1 @>
        }

    /// <summary>
    /// Mode 3 (Explicit): multiple commands roundtrip through Orleans grain.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode multiple commands serialize correctly`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(43L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! result = grain.HandleMessage(GetValue)
            let value = result :?> int
            test <@ value = 3 @>
        }

// ---------------------------------------------------------------------------
// Mode 1 (Clean/JSON fallback) integration tests
// ---------------------------------------------------------------------------

[<Collection("JsonFallbackCluster")>]
type JsonFallbackIntegrationTests(fixture: JsonFallbackClusterFixture) =

    /// <summary>
    /// Mode 1 (Clean): explicitly attributed type still works with JSON fallback enabled.
    /// This verifies the fallback doesn't break the native serializer for attributed types.
    /// </summary>
    [<Fact>]
    member _.``JSON fallback does not break explicitly attributed grain types`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(100L)
            let! result = grain.HandleMessage(Increment)
            let value = result :?> int
            test <@ value = 1 @>
        }

    /// <summary>
    /// Mode 1 (Clean): verify the JSON fallback cluster starts successfully.
    /// </summary>
    [<Fact>]
    member _.``JSON fallback cluster starts successfully`` () =
        task {
            test <@ fixture.Cluster <> null @>
            test <@ fixture.GrainFactory <> null @>
        }

// ---------------------------------------------------------------------------
// Mode 3 (Explicit) — extended serialization integration tests
// ---------------------------------------------------------------------------

[<Collection("ClusterCollection")>]
type ExplicitModeExtendedTests(fixture: ClusterFixture) =

    /// <summary>
    /// Mode 3 (Explicit): Decrement command serializes through grain call when counter is zero.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode Decrement on zero counter stays zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(200L)
            let! result = grain.HandleMessage(Decrement)
            let value = result :?> int
            test <@ value = 0 @>
        }

    /// <summary>
    /// Mode 3 (Explicit): GetValue command roundtrips through grain call on fresh grain.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode GetValue on fresh grain returns zero`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(201L)
            let! result = grain.HandleMessage(GetValue)
            let value = result :?> int
            test <@ value = 0 @>
        }

    /// <summary>
    /// Mode 3 (Explicit): increment then decrement sequence maintains correct state.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode increment decrement sequence`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(202L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Decrement)
            let! result = grain.HandleMessage(GetValue)
            let value = result :?> int
            test <@ value = 2 @>
        }

    /// <summary>
    /// Mode 3 (Explicit): Order grain processes Place command with string payload.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode Order grain Place command serializes string payload`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("ser-order-place")
            let! result = grain.HandleMessage(Place "Widget order")
            let status = result :?> OrderStatus
            test <@ status = Processing "Widget order" @>
        }

    /// <summary>
    /// Mode 3 (Explicit): Order grain full lifecycle with multiple DU transitions.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode Order grain full lifecycle serialization`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("ser-order-lifecycle")
            let! r1 = grain.HandleMessage(GetStatus)
            test <@ (r1 :?> OrderStatus) = Idle @>

            let! r2 = grain.HandleMessage(Place "Test order")
            test <@ (r2 :?> OrderStatus) = Processing "Test order" @>

            let! r3 = grain.HandleMessage(Confirm)
            test <@ (r3 :?> OrderStatus) = Completed "Order confirmed: Test order" @>
        }

    /// <summary>
    /// Mode 3 (Explicit): Echo grain roundtrips string through DU command.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode Echo grain serializes string in DU`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IEchoGrain>("ser-echo-test")
            let! result = grain.HandleMessage(Echo "hello world")
            let msg = result :?> string
            test <@ msg.Contains("hello world") @>
        }

    /// <summary>
    /// Mode 3 (Explicit): Echo grain Greet command (fieldless DU case) serializes.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode Echo grain fieldless DU case serializes`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IEchoGrain>("ser-echo-greet")
            let! result = grain.HandleMessage(Greet)
            let msg = result :?> string
            test <@ msg.Contains("Hello") @>
        }

    /// <summary>
    /// Mode 3 (Explicit): Processor grain serializes DU with string payload through stateless worker.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode Processor grain DU with payload serializes`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IProcessorGrain>(0L)
            let! result = grain.HandleMessage(Process "test-data")
            let msg = result :?> string
            test <@ msg.Contains("test-data") @>
        }

    /// <summary>
    /// Mode 3 (Explicit): event-sourced BankAccount grain Deposit DU command serializes.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode BankAccount Deposit serializes through event sourcing`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("ser-bank-deposit")
            let! result = grain.HandleCommand(Deposit 250m)
            let balance = (result :?> BankAccountState).Balance
            test <@ balance = 250m @>
        }

    /// <summary>
    /// Mode 3 (Explicit): event-sourced BankAccount grain with mixed commands.
    /// </summary>
    [<Fact>]
    member _.``Explicit mode BankAccount mixed DU commands serialize correctly`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("ser-bank-mixed")
            let! _ = grain.HandleCommand(Deposit 1000m)
            let! _ = grain.HandleCommand(Withdraw 300m)
            let! result = grain.HandleCommand(GetBalance)
            let balance = (result :?> BankAccountState).Balance
            test <@ balance = 700m @>
        }

// ---------------------------------------------------------------------------
// Streaming with DU events — serialization through stream provider
// ---------------------------------------------------------------------------

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Orleans
open Orleans.Streams
open Orleans.FSharp.Streaming

[<Collection("ClusterCollection")>]
type StreamSerializationTests(fixture: ClusterFixture) =

    /// <summary>
    /// Stream serialization: DU events publish and subscribe through Orleans stream provider.
    /// </summary>
    [<Fact>]
    member _.``Stream serializes DU events through Orleans provider`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<CounterCommand> streamProvider "ser-stream-ns" (Guid.NewGuid().ToString())
            let received = ConcurrentBag<CounterCommand>()

            let! sub =
                Stream.subscribe streamRef (fun cmd ->
                    task { received.Add(cmd) })

            do! Stream.publish streamRef Increment
            do! Stream.publish streamRef Decrement
            do! Stream.publish streamRef GetValue

            do! Task.Delay(2000)

            let items = received |> Seq.toList
            test <@ items.Length = 3 @>
            test <@ items |> List.contains Increment @>
            test <@ items |> List.contains Decrement @>
            test <@ items |> List.contains GetValue @>

            do! Stream.unsubscribe sub
        }

    /// <summary>
    /// Stream serialization: DU events with data payloads roundtrip through streams.
    /// </summary>
    [<Fact>]
    member _.``Stream serializes DU events with data payloads`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<EchoCommand> streamProvider "ser-echo-stream" (Guid.NewGuid().ToString())
            let received = ConcurrentBag<EchoCommand>()

            let! sub =
                Stream.subscribe streamRef (fun cmd ->
                    task { received.Add(cmd) })

            do! Stream.publish streamRef (Echo "stream-test")
            do! Stream.publish streamRef Greet

            do! Task.Delay(2000)

            let items = received |> Seq.toList
            test <@ items.Length = 2 @>
            test <@ items |> List.exists (function Echo "stream-test" -> true | _ -> false) @>
            test <@ items |> List.exists (function Greet -> true | _ -> false) @>

            do! Stream.unsubscribe sub
        }
