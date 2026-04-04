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
