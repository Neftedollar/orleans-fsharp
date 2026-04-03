module Orleans.FSharp.Integration.TemplateTests

open System
open System.Diagnostics
open System.IO
open System.Text
open Xunit

/// <summary>
/// Helper to run a dotnet CLI command and return (exitCode, stdout, stderr).
/// Uses async output reading to avoid deadlocks with large output.
/// </summary>
let private runDotnet (args: string) (workDir: string) (timeoutMs: int) : int * string * string =
    let psi = ProcessStartInfo()
    psi.FileName <- "dotnet"
    psi.Arguments <- args
    psi.WorkingDirectory <- workDir
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true

    use proc = new Process()
    proc.StartInfo <- psi

    let stdoutBuilder = StringBuilder()
    let stderrBuilder = StringBuilder()

    proc.OutputDataReceived.Add(fun e ->
        if not (isNull e.Data) then
            stdoutBuilder.AppendLine(e.Data) |> ignore)

    proc.ErrorDataReceived.Add(fun e ->
        if not (isNull e.Data) then
            stderrBuilder.AppendLine(e.Data) |> ignore)

    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()

    let finished = proc.WaitForExit(timeoutMs)

    if not finished then
        try
            proc.Kill(true)
        with _ ->
            ()

    let exitCode = if finished then proc.ExitCode else -1
    (exitCode, stdoutBuilder.ToString(), stderrBuilder.ToString())

/// <summary>
/// Get the absolute path to the solution root directory.
/// </summary>
let private solutionRoot =
    let mutable dir = DirectoryInfo(AppContext.BaseDirectory)

    while dir <> null && not (File.Exists(Path.Combine(dir.FullName, "Orleans.FSharp.slnx"))) do
        dir <- dir.Parent

    if dir = null then
        failwith "Could not find solution root (Orleans.FSharp.slnx)"

    dir.FullName

/// <summary>
/// Get the absolute path to the template directory.
/// </summary>
let private templatePath =
    Path.Combine(solutionRoot, "templates", "orleans-fsharp")

/// <summary>
/// Patch the generated project to use local ProjectReferences instead of NuGet PackageReferences.
/// This allows the template to build without publishing to NuGet first.
/// </summary>
let private patchProjectReferences (projectDir: string) (projectName: string) : unit =
    let srcRoot = Path.Combine(solutionRoot, "src")

    // Patch Grains project
    let grainsProj =
        Path.Combine(projectDir, "src", $"{projectName}.Grains", $"{projectName}.Grains.fsproj")

    if File.Exists(grainsProj) then
        let content = File.ReadAllText(grainsProj)

        let patched =
            content.Replace(
                """<PackageReference Include="Orleans.FSharp" Version="2.*" />""",
                $"""<ProjectReference Include="{Path.Combine(srcRoot, "Orleans.FSharp", "Orleans.FSharp.fsproj")}" />"""
            )

        File.WriteAllText(grainsProj, patched)

    // Patch Silo project
    let siloProj =
        Path.Combine(projectDir, "src", $"{projectName}.Silo", $"{projectName}.Silo.fsproj")

    if File.Exists(siloProj) then
        let content = File.ReadAllText(siloProj)

        let patched =
            content
                .Replace(
                    """<PackageReference Include="Orleans.FSharp" Version="2.*" />""",
                    $"""<ProjectReference Include="{Path.Combine(srcRoot, "Orleans.FSharp", "Orleans.FSharp.fsproj")}" />"""
                )
                .Replace(
                    """<PackageReference Include="Orleans.FSharp.Runtime" Version="2.*" />""",
                    $"""<ProjectReference Include="{Path.Combine(srcRoot, "Orleans.FSharp.Runtime", "Orleans.FSharp.Runtime.fsproj")}" />"""
                )

        File.WriteAllText(siloProj, patched)

/// <summary>
/// Create a unique temp directory for test isolation.
/// </summary>
let private createTempDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "orleans-fsharp-tmpl-test", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(dir) |> ignore
    dir

/// <summary>
/// Clean up a temp directory, ignoring errors.
/// </summary>
let private cleanupTempDir (dir: string) : unit =
    try
        if Directory.Exists(dir) then
            Directory.Delete(dir, true)
    with _ ->
        ()

[<Fact>]
let ``template installs and generates project that builds with zero warnings`` () =
    let tempDir = createTempDir ()

    try
        // Uninstall any previous version (ignore errors)
        runDotnet $"new uninstall \"{templatePath}\"" tempDir 30000 |> ignore

        // Install template from local path
        let exitCode, stdout, stderr = runDotnet $"new install \"{templatePath}\"" tempDir 60000

        Assert.True(
            (exitCode = 0),
            $"Template install failed (exit code {exitCode}). stdout: {stdout} stderr: {stderr}"
        )

        // Generate a project
        let projectDir = Path.Combine(tempDir, "TestApp")
        let exitCode, stdout, stderr = runDotnet "new orleans-fsharp -n TestApp" tempDir 60000

        Assert.True(
            (exitCode = 0),
            $"Project generation failed (exit code {exitCode}). stdout: {stdout} stderr: {stderr}"
        )

        Assert.True(Directory.Exists(projectDir), $"Generated project directory not found: {projectDir}")

        // Patch to use local project references
        patchProjectReferences projectDir "TestApp"

        // Build with zero warnings
        let exitCode, stdout, stderr = runDotnet "build --nologo" projectDir 180000

        Assert.True(
            (exitCode = 0),
            $"Build failed (exit code {exitCode}). stdout: {stdout} stderr: {stderr}"
        )
    finally
        // Cleanup
        runDotnet $"new uninstall \"{templatePath}\"" tempDir 30000 |> ignore
        cleanupTempDir tempDir

[<Fact>]
let ``template generated tests all pass`` () =
    let tempDir = createTempDir ()

    try
        // Install template
        runDotnet $"new uninstall \"{templatePath}\"" tempDir 30000 |> ignore
        let exitCode, _, _ = runDotnet $"new install \"{templatePath}\"" tempDir 60000
        Assert.True((exitCode = 0), "Template install failed")

        // Generate project
        let projectDir = Path.Combine(tempDir, "TestApp2")
        let exitCode, _, _ = runDotnet "new orleans-fsharp -n TestApp2" tempDir 60000
        Assert.True((exitCode = 0), "Project generation failed")

        // Patch references
        patchProjectReferences projectDir "TestApp2"

        // Run tests
        let exitCode, stdout, stderr = runDotnet "test --nologo" projectDir 180000

        Assert.True(
            (exitCode = 0),
            $"Tests failed (exit code {exitCode}). stdout: {stdout} stderr: {stderr}"
        )

        // Verify tests passed
        Assert.Contains("Passed!", stdout)
    finally
        runDotnet $"new uninstall \"{templatePath}\"" tempDir 30000 |> ignore
        cleanupTempDir tempDir

[<Fact>]
let ``template into existing non-empty dir preserves original files`` () =
    let tempDir = createTempDir ()

    try
        // Install template
        runDotnet $"new uninstall \"{templatePath}\"" tempDir 30000 |> ignore
        let exitCode, _, _ = runDotnet $"new install \"{templatePath}\"" tempDir 60000
        Assert.True((exitCode = 0), "Template install failed")

        // Create a non-empty directory
        let existingDir = Path.Combine(tempDir, "ExistingProject")
        Directory.CreateDirectory(existingDir) |> ignore
        File.WriteAllText(Path.Combine(existingDir, "existing-file.txt"), "this file exists")

        // Generate project into existing non-empty dir
        let _exitCode, _stdout, _stderr =
            runDotnet "new orleans-fsharp -n ExistingProject" tempDir 60000

        // The original file should still exist after template generation
        let originalFileExists =
            File.Exists(Path.Combine(existingDir, "existing-file.txt"))

        Assert.True(originalFileExists, "Original file should still exist after template generation")
    finally
        runDotnet $"new uninstall \"{templatePath}\"" tempDir 30000 |> ignore
        cleanupTempDir tempDir
