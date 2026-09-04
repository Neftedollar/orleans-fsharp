module Orleans.FSharp.Integration.RollingUpdateIntegrationTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Orleans.Runtime
open Orleans.FSharp
open Xunit

type RollingClientActor = RollingClientActor of unit

[<NoEquality; NoComparison>]
type RollingClientApi =
    { identify: unit -> Task<string> }

let private contract contractVersion =
    grainContract<RollingClientActor, string, RollingClientApi> {
        grainType "rolling.functional.probe"
        version contractVersion
        stringKey
        readOnly (_.identify)
    }

let private v1Ref = FunctionalGrain.ref (contract 1)
let private v2Ref = FunctionalGrain.ref (contract 2)

let private freePort () =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> IPEndPoint).Port
    listener.Stop()
    port

type private RollingSiloProcess(childProcess: Process, output: ConcurrentQueue<string>) =

    member _.Output = String.concat Environment.NewLine output

    member _.StopAsync() =
        task {
            if not childProcess.HasExited then
                do! childProcess.StandardInput.WriteLineAsync "stop"
                do! childProcess.StandardInput.FlushAsync()

                try
                    do! childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 30.0)
                with :? TimeoutException ->
                    childProcess.Kill(entireProcessTree = true)
                    do! childProcess.WaitForExitAsync()
        }

    interface IDisposable with
        member _.Dispose() =
            if not childProcess.HasExited then
                childProcess.Kill(entireProcessTree = true)

            childProcess.Dispose()

module private RollingSiloProcess =

    let start dllPath siloPort gatewayPort primaryPort serviceId clusterId siloName =
        task {
            let startInfo = new ProcessStartInfo("dotnet")
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardInput <- true
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.ArgumentList.Add dllPath

            for name, value in
                [ "--silo-port", string siloPort
                  "--gateway-port", string gatewayPort
                  "--primary-port", string primaryPort
                  "--service-id", serviceId
                  "--cluster-id", clusterId
                  "--silo-name", siloName ] do
                startInfo.ArgumentList.Add name
                startInfo.ArgumentList.Add value

            let childProcess = new Process()
            childProcess.StartInfo <- startInfo
            childProcess.EnableRaisingEvents <- true
            let output = ConcurrentQueue<string>()
            let ready = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            childProcess.OutputDataReceived.Add(fun event ->
                if not (isNull event.Data) then
                    output.Enqueue($"stdout: {event.Data}")

                    if event.Data.StartsWith("READY ", StringComparison.Ordinal) then
                        ready.TrySetResult() |> ignore)

            childProcess.ErrorDataReceived.Add(fun event ->
                if not (isNull event.Data) then
                    output.Enqueue($"stderr: {event.Data}"))

            childProcess.Exited.Add(fun _ ->
                if not ready.Task.IsCompleted then
                    ready.TrySetException(
                        InvalidOperationException(
                            $"rolling silo '{siloName}' exited before becoming ready with code {childProcess.ExitCode}"
                        )
                    )
                    |> ignore)

            if not (childProcess.Start()) then
                invalidOp $"failed to start rolling silo '{siloName}'"

            childProcess.BeginOutputReadLine()
            childProcess.BeginErrorReadLine()

            try
                do! ready.Task.WaitAsync(TimeSpan.FromSeconds 60.0)
                return new RollingSiloProcess(childProcess, output)
            with cause ->
                if not childProcess.HasExited then
                    childProcess.Kill(entireProcessTree = true)

                let diagnostics = String.concat Environment.NewLine output
                childProcess.Dispose()
                return raise (InvalidOperationException($"rolling silo '{siloName}' did not start:{Environment.NewLine}{diagnostics}", cause))
        }

let private rollingBinary projectName =
    let baseDirectory = new DirectoryInfo(AppContext.BaseDirectory)
    let targetFramework = baseDirectory.Name
    let configuration = baseDirectory.Parent.Name
    let orleansVersion = baseDirectory.Parent.Parent.Name
    let testsDirectory = baseDirectory.Parent.Parent.Parent.Parent.Parent.FullName

    Path.Combine(
        testsDirectory,
        projectName,
        "bin",
        orleansVersion,
        configuration,
        targetFramework,
        projectName + ".dll"
    )

let private waitFor expected (call: unit -> Task<string>) =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 45.0
        let mutable result = None
        let mutable lastFailure: exn option = None

        while result.IsNone && DateTime.UtcNow < deadline do
            try
                let! value = call ()

                if String.Equals(value, expected, StringComparison.Ordinal) then
                    result <- Some value
                else
                    lastFailure <- Some(InvalidOperationException($"expected '{expected}', received '{value}'"))
            with cause ->
                lastFailure <- Some cause

            if result.IsNone then
                do! Task.Delay 200

        match result with
        | Some value -> return value
        | None ->
            let details = lastFailure |> Option.map string |> Option.defaultValue "no reply was received"
            return raise (TimeoutException($"timed out waiting for rolling host '{expected}': {details}"))
    }

let rec private exceptionTree (cause: exn) =
    seq {
        yield cause

        match cause with
        | :? AggregateException as aggregate ->
            for inner in aggregate.InnerExceptions do
                yield! exceptionTree inner
        | _ when not (isNull cause.InnerException) ->
            yield! exceptionTree cause.InnerException
        | _ -> ()
    }

let private nativeVersionRoutingRejection grainType requestedVersion (cause: exn) =
    let prefix = $"No active nodes are compatible with grain {grainType} and interface "
    let requestedVersionText = $" version {requestedVersion}."

    exceptionTree cause
    |> Seq.tryPick (fun error ->
        match error with
        | :? OrleansException as native
            when native.Message.StartsWith(prefix, StringComparison.Ordinal)
                 && native.Message.Contains(requestedVersionText, StringComparison.Ordinal)
                 && native.Message.Contains("Known nodes with grain type:", StringComparison.Ordinal)
                 && native.Message.Contains("All known nodes compatible with interface version:", StringComparison.Ordinal) ->
            Some native
        | _ -> None)

let private waitForNoCompatibleSilo grainType requestedVersion (call: unit -> Task<string>) =
    task {
        let deadline = DateTime.UtcNow.AddSeconds 45.0
        let mutable rejection: OrleansException option = None
        let mutable lastOutcome = "no call was attempted"

        while rejection.IsNone && DateTime.UtcNow < deadline do
            try
                let! value = call ()
                lastOutcome <- $"the incompatible call unexpectedly reached '{value}'"
            with cause ->
                lastOutcome <- $"{cause.GetType().FullName}: {cause.Message}"
                rejection <- nativeVersionRoutingRejection grainType requestedVersion cause

            if rejection.IsNone then
                do! Task.Delay 200

        match rejection with
        | Some native -> return native
        | None ->
            return
                raise (
                    TimeoutException(
                        $"timed out waiting for native version routing to report no compatible silo: {lastOutcome}"
                    )
                )
    }

[<Fact(Timeout = 180000)>]
let ``native routing supports a mixed-binary rolling update and rollback`` () =
    task {
        let siloV1Port = freePort ()
        let gatewayV1Port = freePort ()
        let siloV2Port = freePort ()
        let gatewayV2Port = freePort ()
        let deployment = Guid.NewGuid().ToString "N"
        let serviceId = $"rolling-service-{deployment}"
        let clusterId = $"rolling-cluster-{deployment}"

        let v1Binary = rollingBinary "Orleans.FSharp.Rolling.V1"
        let v2Binary = rollingBinary "Orleans.FSharp.Rolling.V2"
        Assert.True(File.Exists v1Binary, $"missing rolling fixture binary: {v1Binary}")
        Assert.True(File.Exists v2Binary, $"missing rolling fixture binary: {v2Binary}")

        use! siloV1 =
            RollingSiloProcess.start
                v1Binary
                siloV1Port
                gatewayV1Port
                siloV1Port
                serviceId
                clusterId
                "rolling-v1"

        let clientBuilder = Host.CreateApplicationBuilder()
        clientBuilder.Logging.ClearProviders() |> ignore

        clientBuilder.UseOrleansClient(fun client ->
            client.UseStaticClustering([| IPEndPoint(IPAddress.Loopback, gatewayV1Port) |])
            |> ignore

            client.Services.Configure<ClusterOptions>(fun (options: ClusterOptions) ->
                options.ServiceId <- serviceId
                options.ClusterId <- clusterId)
            |> ignore

            client.AddFunctionalGrainClient() |> ignore)
        |> ignore

        use clientHost = clientBuilder.Build()
        do! clientHost.StartAsync()

        let! outcome =
            task {
                try
                    let client = clientHost.Services.GetRequiredService<IClusterClient>()
                    let keyBefore = $"before-{Guid.NewGuid():N}"
                    let! before = waitFor "v1" (fun () -> (v1Ref client keyBefore).identify ())
                    Assert.Equal("v1", before)

                    use! siloV2 =
                        RollingSiloProcess.start
                            v2Binary
                            siloV2Port
                            gatewayV2Port
                            siloV1Port
                            serviceId
                            clusterId
                            "rolling-v2"

                    let keyDuringV1 = $"during-v1-{Guid.NewGuid():N}"
                    let! duringV1 = (v1Ref client keyDuringV1).identify ()
                    Assert.Contains(duringV1, [| "v1"; "v2" |])

                    // Several independent grain identities make this a routing assertion, not a
                    // lucky one-off placement on the V2 silo.
                    for index in 1..8 do
                        let keyDuringV2 = $"during-v2-{index}-{Guid.NewGuid():N}"
                        let! duringV2 = waitFor "v2" (fun () -> (v2Ref client keyDuringV2).identify ())
                        Assert.Equal("v2", duringV2)

                    do! siloV2.StopAsync()

                    let keyAfterRollback = $"rollback-{Guid.NewGuid():N}"
                    let! afterRollback = waitFor "v1" (fun () -> (v1Ref client keyAfterRollback).identify ())
                    Assert.Equal("v1", afterRollback)

                    let incompatibleKey = $"rollback-v2-{Guid.NewGuid():N}"

                    let! incompatibility =
                        waitForNoCompatibleSilo "rolling.functional.probe" 2 (fun () ->
                            (v2Ref client incompatibleKey).identify ())

                    Assert.Equal(typeof<OrleansException>, incompatibility.GetType())
                    Assert.Contains(" version 2.", incompatibility.Message)
                    Assert.DoesNotContain("hosts contract version 1", incompatibility.Message)
                    return Ok()
                with cause ->
                    return Error cause
            }

        do! clientHost.StopAsync()
        do! siloV1.StopAsync()

        match outcome with
        | Ok() -> return ()
        | Error cause -> return raise cause
    }
