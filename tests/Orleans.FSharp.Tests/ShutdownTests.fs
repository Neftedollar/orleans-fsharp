module Orleans.FSharp.Tests.ShutdownTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open Swensen.Unquote
open FsCheck
open FsCheck.Xunit
open Orleans.FSharp

// ---------------------------------------------------------------------------
// configureGracefulShutdown tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``configureGracefulShutdown sets HostOptions ShutdownTimeout`` () =
    let timeout = TimeSpan.FromSeconds(30.0)

    let builder =
        HostBuilder()
        |> Shutdown.configureGracefulShutdown timeout

    let host = builder.Build()
    let options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>()
    test <@ options.Value.ShutdownTimeout = timeout @>
    host.Dispose()

[<Fact>]
let ``configureGracefulShutdown returns builder for chaining`` () =
    let builder = HostBuilder()
    let result = Shutdown.configureGracefulShutdown (TimeSpan.FromSeconds(5.0)) builder
    test <@ not (isNull (box result)) @>

[<Fact>]
let ``configureGracefulShutdown with zero timeout`` () =
    let timeout = TimeSpan.Zero

    let builder =
        HostBuilder()
        |> Shutdown.configureGracefulShutdown timeout

    let host = builder.Build()
    let options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>()
    test <@ options.Value.ShutdownTimeout = TimeSpan.Zero @>
    host.Dispose()

// ---------------------------------------------------------------------------
// stopHost tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``stopHost stops a running host`` () =
    task {
        let host = HostBuilder().Build()
        // Start and immediately stop
        do! host.StartAsync()
        do! Shutdown.stopHost host
        // Verify no exception - host stopped successfully
        host.Dispose()
    }

[<Fact>]
let ``stopHost completes without throwing`` () =
    task {
        let host = HostBuilder().Build()
        do! host.StartAsync()
        do! Shutdown.stopHost host
        // If we reach here without exception, the stop succeeded
        test <@ true @>
        host.Dispose()
    }

// ---------------------------------------------------------------------------
// onShutdown tests
// ---------------------------------------------------------------------------

[<Fact>]
let ``onShutdown registers a hosted service`` () =
    let handlerCalled = ref false

    let builder =
        HostBuilder()
        |> Shutdown.onShutdown (fun _ct ->
            task { handlerCalled.Value <- true })

    let host = builder.Build()
    let services = host.Services.GetServices<IHostedService>()

    let hasShutdownHandler =
        services
        |> Seq.exists (fun s -> s.GetType().Name = "ShutdownHandlerService")

    test <@ hasShutdownHandler @>
    host.Dispose()

[<Fact>]
let ``onShutdown returns builder for chaining`` () =
    let builder = HostBuilder()

    let result =
        Shutdown.onShutdown (fun _ct -> Task.FromResult()) builder

    test <@ not (isNull (box result)) @>

[<Fact>]
let ``onShutdown receives a usable token when graceful shutdown starts`` () =
    task {
        let tokenWasAlreadyCancelled = TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

        use host =
            HostBuilder()
            |> Shutdown.configureGracefulShutdown (TimeSpan.FromSeconds 5.0)
            |> Shutdown.onShutdown (fun token ->
                tokenWasAlreadyCancelled.TrySetResult token.IsCancellationRequested |> ignore
                Task.FromResult())
            |> _.Build()

        do! host.StartAsync()
        do! host.StopAsync()

        let! wasCancelled = tokenWasAlreadyCancelled.Task
        test <@ not wasCancelled @>
    }

[<Fact>]
let ``multiple onShutdown handlers run in registration order`` () =
    task {
        let observed = ResizeArray<int>()

        use host =
            HostBuilder()
            |> Shutdown.onShutdown (fun _ ->
                observed.Add 1
                Task.FromResult())
            |> Shutdown.onShutdown (fun _ ->
                observed.Add 2
                Task.FromResult())
            |> _.Build()

        do! host.StartAsync()
        do! host.StopAsync()

        test <@ observed |> Seq.toList = [ 1; 2 ] @>
    }

[<Fact>]
let ``a failing onShutdown handler does not prevent later handlers`` () =
    task {
        let observed = ResizeArray<int>()

        use host =
            HostBuilder()
            |> Shutdown.onShutdown (fun _ ->
                observed.Add 1
                Task.FromException<unit>(InvalidOperationException("first failed")))
            |> Shutdown.onShutdown (fun _ ->
                observed.Add 2
                Task.FromResult())
            |> _.Build()

        do! host.StartAsync()

        let! error =
            Assert.ThrowsAnyAsync<Exception>(Func<Task>(fun () -> host.StopAsync()))

        let messages =
            match error with
            | :? AggregateException as aggregate ->
                aggregate.Flatten().InnerExceptions |> Seq.map _.Message |> Seq.toList
            | other -> [ other.Message ]

        test <@ observed |> Seq.toList = [ 1; 2 ] @>
        test <@ messages |> List.exists (fun message -> message.Contains "first failed") @>
    }

[<Fact>]
let ``shutdown timeout bounds a handler which ignores cancellation`` () =
    task {
        let handlerRelease =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        use host =
            HostBuilder()
            |> Shutdown.configureGracefulShutdown (TimeSpan.FromMilliseconds 100.0)
            |> Shutdown.onShutdown (fun _ignoredToken -> handlerRelease.Task)
            |> _.Build()

        do! host.StartAsync()

        try
            let stopTask = host.StopAsync()

            let! _ =
                Assert.ThrowsAnyAsync<OperationCanceledException>(
                    Func<Task>(fun () -> stopTask.WaitAsync(TimeSpan.FromSeconds 2.0))
                )

            test <@ not handlerRelease.Task.IsCompleted @>
        finally
            // Let the detached callback finish so the test does not leave background work behind.
            handlerRelease.TrySetResult() |> ignore
    }

// ---------------------------------------------------------------------------
// Shutdown module exists in assembly
// ---------------------------------------------------------------------------

[<Fact>]
let ``Shutdown module exists in the assembly`` () =
    let shutdownModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.tryFind (fun t -> t.Name = "Shutdown" && t.IsAbstract && t.IsSealed)

    test <@ shutdownModule.IsSome @>

[<Fact>]
let ``configureGracefulShutdown method exists`` () =
    let shutdownModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Shutdown" && t.IsAbstract && t.IsSealed)

    let method =
        shutdownModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "configureGracefulShutdown")

    test <@ method.IsSome @>

[<Fact>]
let ``stopHost method exists`` () =
    let shutdownModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Shutdown" && t.IsAbstract && t.IsSealed)

    let method =
        shutdownModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "stopHost")

    test <@ method.IsSome @>

[<Fact>]
let ``onShutdown method exists`` () =
    let shutdownModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Shutdown" && t.IsAbstract && t.IsSealed)

    let method =
        shutdownModule.GetMethods()
        |> Array.tryFind (fun m -> m.Name = "onShutdown")

    test <@ method.IsSome @>

// ---------------------------------------------------------------------------
// FsCheck property tests
// ---------------------------------------------------------------------------

[<Property>]
let ``configureGracefulShutdown stores any positive seconds timeout`` (seconds: PositiveInt) =
    let timeout = TimeSpan.FromSeconds(float seconds.Get)
    let builder = HostBuilder() |> Shutdown.configureGracefulShutdown timeout
    let host = builder.Build()
    use _ = host
    let options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>()
    options.Value.ShutdownTimeout = timeout

[<Property>]
let ``Shutdown module methods all have non-empty names`` () =
    let shutdownModule =
        typeof<AssemblyMarker>.Assembly.GetTypes()
        |> Array.find (fun t -> t.Name = "Shutdown" && t.IsAbstract && t.IsSealed)
    shutdownModule.GetMethods()
    |> Array.forall (fun m -> m.Name.Length > 0)
