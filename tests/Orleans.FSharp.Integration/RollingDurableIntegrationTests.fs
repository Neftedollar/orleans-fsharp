module Orleans.FSharp.Integration.RollingDurableIntegrationTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open Xunit

// Commands travel over stdin only for deterministic test coordination. Every operation inside
// the binary uses a genuine Orleans grain reference; persistence is bytes in this test's directory.
type private Silo(childProcess: Process, lines: Channel<string>, diagnostics: ConcurrentQueue<string>) =
    member _.Wait(prefix: string) = task {
        let deadline = DateTime.UtcNow.AddSeconds 60.
        let mutable answer = None
        while answer.IsNone do
            let remaining = deadline - DateTime.UtcNow
            if remaining <= TimeSpan.Zero then raise (TimeoutException(String.concat "\n" diagnostics))
            let! line = lines.Reader.ReadAsync().AsTask().WaitAsync remaining
            if line.StartsWith("ERROR ", StringComparison.Ordinal) then
                invalidOp (String.concat "\n" diagnostics)
            if line.StartsWith(prefix, StringComparison.Ordinal) then answer <- Some(line.Substring prefix.Length)
        return answer.Value
    }
    member this.Command(line: string) = task {
        do! childProcess.StandardInput.WriteLineAsync line
        do! childProcess.StandardInput.FlushAsync()
        return! this.Wait "RESULT "
    }
    member _.Stop() = task {
        if not childProcess.HasExited then
            do! childProcess.StandardInput.WriteLineAsync "stop"
            do! childProcess.StandardInput.FlushAsync()
            do! childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 30.)
        Assert.Equal(0, childProcess.ExitCode)
    }
    interface IDisposable with
        member _.Dispose() =
            if not childProcess.HasExited then
                childProcess.Kill(entireProcessTree = true)
                childProcess.WaitForExit(10000) |> ignore
            childProcess.Dispose()

let private freePort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port

let private binary version =
    let output = DirectoryInfo(AppContext.BaseDirectory)
    Path.Combine(output.Parent.Parent.Parent.Parent.Parent.FullName,
        $"Orleans.FSharp.Rolling.{version}", "bin", output.Parent.Parent.Name,
        output.Parent.Name, output.Name, $"Orleans.FSharp.Rolling.{version}.dll")

let private start version root primary deployment = task {
    let port = freePort ()
    let gateway = freePort ()
    let primaryPort = primary |> Option.defaultValue port
    let info = ProcessStartInfo("dotnet")
    info.UseShellExecute <- false
    info.RedirectStandardInput <- true
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.ArgumentList.Add(binary version)
    for name, value in
        [ "--silo-port", string port; "--gateway-port", string gateway
          "--primary-port", string primaryPort; "--service-id", deployment
          "--cluster-id", deployment; "--silo-name", $"durable-{version}-{port}"
          "--store", root ] do
        info.ArgumentList.Add name
        info.ArgumentList.Add value
    let childProcess = new Process(StartInfo = info)
    let lines = Channel.CreateUnbounded<string>()
    let diagnostics = ConcurrentQueue<string>()
    childProcess.OutputDataReceived.Add(fun event ->
        if not (isNull event.Data) then
            diagnostics.Enqueue event.Data
            lines.Writer.TryWrite event.Data |> ignore)
    childProcess.ErrorDataReceived.Add(fun event ->
        if not (isNull event.Data) then diagnostics.Enqueue event.Data)
    childProcess.EnableRaisingEvents <- true
    childProcess.Exited.Add(fun _ -> lines.Writer.TryComplete() |> ignore)
    Assert.True(childProcess.Start())
    childProcess.BeginOutputReadLine()
    childProcess.BeginErrorReadLine()
    let silo = new Silo(childProcess, lines, diagnostics)
    try
        let! _ = silo.Wait "READY "
        return silo, port
    with error ->
        (silo :> IDisposable).Dispose()
        return raise (InvalidOperationException(String.concat "\n" diagnostics, error))
}

let private expected value =
    [ "ordinary"; "view"; "log"; "custom" ]
    |> List.map (fun lane -> $"{lane}={value}")
    |> String.concat ";"

let private assertCommand (silo: Silo) command value = task {
    let! result = silo.Command command
    Assert.Equal(expected value, result)
}

let private assertCustomBytes root version snapshotVersion snapshotSchema (schemas: int[]) =
    let journals =
        Directory.GetFiles(root, "*.json")
        |> Array.choose (fun path ->
            use document = JsonDocument.Parse(File.ReadAllBytes path)
            let element = document.RootElement
            match element.TryGetProperty("SnapshotVersion") with
            | true, value when value.GetInt32() > 0 -> Some(element.Clone())
            | _ -> None)
    // The account journal is the only one with a snapshot: the overlap bonus identity has none.
    let journal = journals |> Array.exactlyOne
    Assert.Equal(version, journal.GetProperty("Version").GetInt32())
    Assert.Equal(snapshotVersion, journal.GetProperty("SnapshotVersion").GetInt32())
    Assert.Equal(snapshotSchema, journal.GetProperty("Snapshot").GetProperty("Schema").GetInt32())
    let tailSchemas = journal.GetProperty("Tail").EnumerateArray() |> Seq.map (fun e -> e.GetProperty("Schema").GetInt32()) |> Seq.toArray
    Assert.Equal<int>(schemas, tailSchemas)

let private durableHashes root =
    Directory.GetFiles(root, "*.json")
    |> Array.sort
    |> Array.map (fun path -> Path.GetFileName path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)))

[<Fact(Timeout = 420000)>]
let ``durable changed schemas replay across binaries and permit only reader-compatible rollback`` () = task {
    let root = Path.Combine(Path.GetTempPath(), $"orleans-durable-rolling-{Guid.NewGuid():N}")
    Directory.CreateDirectory root |> ignore
    let deployment () = $"durable-{Guid.NewGuid():N}"
    try
        // N creates a view and ordinary state at 11/version 2; LogStorage retains both events;
        // CustomStorage has snapshot 4/version 1 and retained tail Added 7, not a full snapshot.
        let cluster = deployment ()
        let! old, primary = start "V1" root None cluster
        use old = old
        do! assertCommand old "seed account" "11|0|2"
        Assert.Equal(4, Directory.GetFiles(root, "*.json").Length)
        assertCustomBytes root 2 1 1 [| 1 |]

        let! newer, _ = start "V2" root (Some primary) cluster
        use newer = newer
        // N is still alive and its activated old-contract identities remain callable.
        do! assertCommand old "read account" "11|0|2"
        // A genuinely added API field must route to N+1 in the mixed cluster.
        do! assertCommand newer "bonus extended-contract" "6|6|1"
        // N's old API also calls the identities already activated on N+1.
        do! assertCommand old "read extended-contract" "6|6|1"
        // Upgrade the already-persisted account while N remains alive. The V2 request must
        // replace its old activation, upcast its data, and preserve access through N's old API.
        do! assertCommand newer "bonus account" "17|6|3"
        do! assertCommand old "read account" "17|6|3"
        assertCustomBytes root 3 1 1 [| 1; 2 |]
        do! newer.Stop()
        do! old.Stop()

        // No process from the old deployment remains. Replay the mixed-phase writes without
        // adding another bonus: ordinary state + native view + historical events + snapshot/tail.
        let! migrated, _ = start "V2" root None (deployment ())
        use migrated = migrated
        do! assertCommand migrated "read account" "17|6|3"
        do! migrated.Stop()

        let! restarted, _ = start "V2" root None (deployment ())
        use restarted = restarted
        do! assertCommand restarted "read account" "17|6|3"
        do! restarted.Stop()

        // Bridge deliberately exposes the old user API but retains the V2 persistence readers
        // and writer. It preserves bonus information; it does not downcast new state/events.
        let! bridge, _ = start "Bridge" root None (deployment ())
        use bridge = bridge
        do! assertCommand bridge "read account" "17|6|3"
        do! assertCommand bridge "add account" "19|6|4"
        assertCustomBytes root 4 1 1 [| 1; 2; 2 |]
        // Persist the changed custom snapshot shape too: subsequent rollback refusal and restart
        // now exercise its schema-2 state reader, not only the schema-2 retained event reader.
        do! assertCommand bridge "snapshot account" "19|6|4"
        do! bridge.Stop()
        assertCustomBytes root 4 4 2 [||]

        // Negative control: original N, same exact durable bytes, no bridge readers. Timeouts,
        // network errors and unrelated activation failures are NOT accepted as schema refusal.
        let beforeRefusal = durableHashes root
        let! refused, _ = start "V1" root None (deployment ())
        use refused = refused
        do! assertCommand refused "reject account" "REFUSED"
        do! refused.Stop()
        Assert.Equal<(string * string)[]>(beforeRefusal, durableHashes root)

        let! final, _ = start "V2" root None (deployment ())
        use final = final
        do! assertCommand final "read account" "19|6|4"
        do! final.Stop()
    finally
        // Only the uniquely created fixture directory is removed, after all child handles dispose.
        if Directory.Exists root then Directory.Delete(root, true)
}
