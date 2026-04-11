module Orleans.FSharp.Integration.FSharpBinaryCodecIntegrationTests

open System
open System.Threading.Tasks
open Xunit
open Swensen.Unquote
open Orleans
open Orleans.Hosting
open Orleans.TestingHost
open Orleans.FSharp.Sample
open Orleans.FSharp.EventSourcing

// ===========================================================================
// Binary codec TestCluster fixture
// ===========================================================================

/// <summary>
/// Silo configurator that enables F# binary serialization for clean F# types.
/// Types without [GenerateSerializer] will be serialized using FSharpBinaryCodec.
/// </summary>
type BinaryCodecSiloConfigurator() =
    interface ISiloConfigurator with
        member _.Configure(siloBuilder: ISiloBuilder) =
            siloBuilder.AddMemoryGrainStorageAsDefault() |> ignore
            siloBuilder.AddMemoryGrainStorage("Default") |> ignore
            siloBuilder.AddMemoryStreams("StreamProvider") |> ignore
            siloBuilder.AddMemoryGrainStorage("PubSubStore") |> ignore
            siloBuilder.UseInMemoryReminderService() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProviderAsDefault() |> ignore
            siloBuilder.AddLogStorageBasedLogConsistencyProvider("LogStorage") |> ignore

            // Register the BankAccount event-sourced grain for IFSharpEventSourcedGrain dispatch.
            siloBuilder.Services.AddFSharpEventSourcedGrain<Orleans.FSharp.Sample.BankAccountState, Orleans.FSharp.Sample.BankAccountEvent, Orleans.FSharp.Sample.BankAccountCommand>(Orleans.FSharp.Sample.BankAccountGrainDef.bankAccount) |> ignore

            // Enable F# binary serialization
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                siloBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun serializerBuilder ->
                    Orleans.FSharp.FSharpBinaryCodecRegistration.addToSerializerBuilder serializerBuilder
                    |> ignore))
            |> ignore

/// <summary>
/// Client configurator for the binary codec test cluster.
/// </summary>
type BinaryCodecClientConfigurator() =
    interface IClientBuilderConfigurator with
        member _.Configure(_configuration, clientBuilder: IClientBuilder) =
            clientBuilder.AddMemoryStreams("StreamProvider") |> ignore

            // Register F# binary serialization on the client so the proxy can deep-copy
            // F# types passed as `object` to IFSharpGrain.HandleMessage / IFSharpEventSourcedGrain.HandleCommand.
            Orleans.Serialization.ServiceCollectionExtensions.AddSerializer(
                clientBuilder.Services,
                System.Action<Orleans.Serialization.ISerializerBuilder>(fun b ->
                    Orleans.FSharp.FSharpBinaryCodecRegistration.addToSerializerBuilder b |> ignore))
            |> ignore

/// <summary>
/// xUnit fixture that starts a TestCluster with F# binary codec serialization enabled.
/// </summary>
type BinaryCodecClusterFixture() =
    let mutable cluster: TestCluster = Unchecked.defaultof<TestCluster>

    /// <summary>Gets the running TestCluster instance.</summary>
    member _.Cluster = cluster

    /// <summary>Gets the GrainFactory for creating grain references.</summary>
    member _.GrainFactory = cluster.GrainFactory

    /// <summary>Gets the cluster client for advanced operations like streaming.</summary>
    member _.Client = cluster.Client

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            task {
                let codeGenAssembly = typeof<Orleans.FSharp.CodeGen.CodeGenAssemblyMarker>.Assembly
                let _ = codeGenAssembly.GetTypes()

                let builder = TestClusterBuilder()
                builder.Options.InitialSilosCount <- 1s
                builder.AddSiloBuilderConfigurator<BinaryCodecSiloConfigurator>() |> ignore
                builder.AddClientBuilderConfigurator<BinaryCodecClientConfigurator>() |> ignore
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
/// xUnit collection for tests sharing the binary codec cluster.
/// </summary>
[<CollectionDefinition("BinaryCodecCluster")>]
type BinaryCodecClusterCollection() =
    interface ICollectionFixture<BinaryCodecClusterFixture>

// ===========================================================================
// Integration tests — grain calls with binary codec
// ===========================================================================

[<Collection("BinaryCodecCluster")>]
type BinaryCodecIntegrationTests(fixture: BinaryCodecClusterFixture) =

    /// <summary>
    /// Binary codec: cluster starts successfully with FSharpBinaryCodec enabled.
    /// </summary>
    [<Fact>]
    member _.``Binary codec cluster starts successfully`` () =
        task {
            test <@ fixture.Cluster <> null @>
            test <@ fixture.GrainFactory <> null @>
        }

    /// <summary>
    /// Binary codec: explicitly attributed DU type still works when binary codec is registered.
    /// This verifies the codec does not break the native serializer for attributed types.
    /// </summary>
    [<Fact>]
    member _.``Binary codec does not break explicitly attributed grain types`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(300L)
            let! result = grain.HandleMessage(Increment)
            let value = result :?> int
            test <@ value = 1 @>
        }

    /// <summary>
    /// Binary codec: multiple DU commands roundtrip through grain with binary serialization.
    /// </summary>
    [<Fact>]
    member _.``Binary codec multiple commands serialize correctly`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<ICounterGrain>(301L)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Increment)
            let! _ = grain.HandleMessage(Decrement)
            let! result = grain.HandleMessage(GetValue)
            let value = result :?> int
            test <@ value = 2 @>
        }

    /// <summary>
    /// Binary codec: Order grain processes Place command with string payload.
    /// </summary>
    [<Fact>]
    member _.``Binary codec Order grain Place command serializes`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("bin-order-place")
            let! result = grain.HandleMessage(Place "Binary widget")
            let status = result :?> OrderStatus
            test <@ status = Processing "Binary widget" @>
        }

    /// <summary>
    /// Binary codec: Order grain full lifecycle with multiple DU transitions.
    /// </summary>
    [<Fact>]
    member _.``Binary codec Order grain full lifecycle`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IOrderGrain>("bin-order-lifecycle")
            let! r1 = grain.HandleMessage(GetStatus)
            test <@ (r1 :?> OrderStatus) = Idle @>

            let! r2 = grain.HandleMessage(Place "Test order")
            test <@ (r2 :?> OrderStatus) = Processing "Test order" @>

            let! r3 = grain.HandleMessage(Confirm)
            test <@ (r3 :?> OrderStatus) = Completed "Order confirmed: Test order" @>
        }

    /// <summary>
    /// Binary codec: Echo grain roundtrips string through DU command.
    /// </summary>
    [<Fact>]
    member _.``Binary codec Echo grain serializes string in DU`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IEchoGrain>("bin-echo-test")
            let! result = grain.HandleMessage(Echo "binary hello")
            let msg = result :?> string
            test <@ msg.Contains("binary hello") @>
        }

    /// <summary>
    /// Binary codec: Echo grain Greet command (fieldless DU case) serializes.
    /// </summary>
    [<Fact>]
    member _.``Binary codec Echo grain fieldless DU case serializes`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IEchoGrain>("bin-echo-greet")
            let! result = grain.HandleMessage(Greet)
            let msg = result :?> string
            test <@ msg.Contains("Hello") @>
        }

    /// <summary>
    /// Binary codec: Processor grain serializes DU with string payload.
    /// </summary>
    [<Fact>]
    member _.``Binary codec Processor grain DU with payload serializes`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IProcessorGrain>(0L)
            let! result = grain.HandleMessage(Process "binary-data")
            let msg = result :?> string
            test <@ msg.Contains("binary-data") @>
        }

    /// <summary>
    /// Binary codec: event-sourced BankAccount grain Deposit DU command serializes.
    /// </summary>
    [<Fact>]
    member _.``Binary codec BankAccount Deposit serializes`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bin-bank-deposit")
            let! result = grain.HandleCommand(Deposit 500m)
            let balance = (result :?> BankAccountState).Balance
            test <@ balance = 500m @>
        }

    /// <summary>
    /// Binary codec: event-sourced BankAccount grain with mixed commands.
    /// </summary>
    [<Fact>]
    member _.``Binary codec BankAccount mixed DU commands serialize`` () =
        task {
            let grain = fixture.GrainFactory.GetGrain<IBankAccountGrain>("bin-bank-mixed")
            let! _ = grain.HandleCommand(Deposit 2000m)
            let! _ = grain.HandleCommand(Withdraw 500m)
            let! result = grain.HandleCommand(GetBalance)
            let balance = (result :?> BankAccountState).Balance
            test <@ balance = 1500m @>
        }

// ===========================================================================
// Stream events with binary codec
// ===========================================================================

open System.Collections.Concurrent
open Orleans.Streams
open Orleans.FSharp.Streaming

[<Collection("BinaryCodecCluster")>]
type BinaryCodecStreamTests(fixture: BinaryCodecClusterFixture) =

    /// <summary>
    /// Binary codec: DU events publish and subscribe through Orleans stream provider.
    /// </summary>
    [<Fact>]
    member _.``Binary codec stream serializes DU events`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<CounterCommand> streamProvider "bin-stream-ns" (Guid.NewGuid().ToString())
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
    /// Binary codec: DU events with data payloads roundtrip through streams.
    /// </summary>
    [<Fact>]
    member _.``Binary codec stream serializes DU events with data payloads`` () =
        task {
            let streamProvider = fixture.Client.GetStreamProvider("StreamProvider")
            let streamRef = Stream.getStream<EchoCommand> streamProvider "bin-echo-stream" (Guid.NewGuid().ToString())
            let received = ConcurrentBag<EchoCommand>()

            let! sub =
                Stream.subscribe streamRef (fun cmd ->
                    task { received.Add(cmd) })

            do! Stream.publish streamRef (Echo "stream-binary-test")
            do! Stream.publish streamRef Greet

            do! Task.Delay(2000)

            let items = received |> Seq.toList
            test <@ items.Length = 2 @>
            test <@ items |> List.exists (function Echo "stream-binary-test" -> true | _ -> false) @>
            test <@ items |> List.exists (function Greet -> true | _ -> false) @>

            do! Stream.unsubscribe sub
        }
