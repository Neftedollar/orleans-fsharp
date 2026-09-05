#!/usr/bin/env python3
"""Exercise the packed F# analyzer through its real CLI host and public suppression API."""

import argparse
import json
import subprocess
from pathlib import Path
from urllib.parse import unquote, urlparse
from urllib.request import url2pathname

ROOT = Path(__file__).resolve().parent.parent
PROJECT = ROOT / "tests/Orleans.FSharp.Analyzers.Consumer/Consumer.fsproj"


def check_report(path: Path) -> None:
    report = json.loads(path.read_text(encoding="utf-8"))
    runs = report.get("runs", [])
    if len(runs) != 1:
        raise ValueError("expected exactly one analyzer invocation report")
    run = runs[0]
    invocations = run.get("invocations", [])
    if not invocations or any(invocation.get("executionSuccessful") is not True for invocation in invocations):
        raise ValueError("analyzer invocation was unsuccessful or did not report success")
    results = run.get("results", [])
    if len(results) != 1:
        raise ValueError(f"expected exactly one diagnostic, got {len(results)}")
    result = results[0]
    location = result["locations"][0]["physicalLocation"]
    rules = run.get("tool", {}).get("driver", {}).get("rules", [])
    rule = next((rule for rule in rules if rule.get("id") == result.get("ruleId")), {})
    # SARIF inherits an omitted result level from its rule before falling back to warning.
    level = result.get("level", rule.get("defaultConfiguration", {}).get("level", "warning"))
    uri = urlparse(location["artifactLocation"]["uri"])
    if uri.scheme not in ("", "file") or uri.netloc not in ("", "localhost"):
        raise ValueError("expected a local consumer source path")
    source_path = Path(url2pathname(uri.path) if uri.scheme == "file" else unquote(uri.path))
    candidates = [source_path] if source_path.is_absolute() else [ROOT / source_path, ROOT.parent / source_path]
    expected_source = PROJECT.with_suffix(".fs").resolve()
    if (
        result.get("ruleId") != "OF0001"
        or level != "warning"
        or not any(candidate.resolve() == expected_source for candidate in candidates)
        or location["region"]["startLine"] != 5
    ):
        raise ValueError(f"unexpected diagnostic: {result}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    version_property = f"-p:AnalyzerPackageVersion={args.version}"

    def run(*command: str) -> None:
        subprocess.run(command, cwd=ROOT, check=True, timeout=180)

    run("dotnet", "restore", str(PROJECT), version_property,
        "--source", str(args.directory.resolve()), "--source", "https://api.nuget.org/v3/index.json")
    run("dotnet", "build", str(PROJECT), version_property, "--no-restore", "--verbosity", "minimal")
    package_path = subprocess.check_output(
        ["dotnet", "msbuild", str(PROJECT), "-nologo", version_property,
         "-getProperty:PkgOrleans_FSharp_Analyzers"], cwd=ROOT, text=True, timeout=60
    ).strip()
    if not package_path:
        raise ValueError("NuGet did not expose the analyzer package path")
    analyzer_path = Path(package_path) / "lib/net8.0"
    if not (analyzer_path / "Orleans.FSharp.Analyzers.dll").is_file():
        raise ValueError(f"analyzer assembly absent from {analyzer_path}")

    # Never let a stale successful report conceal a host that stopped producing output.
    args.report.unlink(missing_ok=True)
    run(str(args.cli.resolve()), "--project", str(PROJECT),
        "--property", f"AnalyzerPackageVersion={args.version}",
        "--analyzers-path", str(analyzer_path), "--report", str(args.report.resolve()))
    check_report(args.report)
    print("analyzer package smoke passed: OF0001 emitted; task and AllowAsync controls silent")


if __name__ == "__main__":
    main()
