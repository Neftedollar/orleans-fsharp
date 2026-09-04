#!/usr/bin/env python3
"""Validate and fingerprint the exact production NuGet package set."""

import argparse
import hashlib
import json
import sys
import zipfile
from pathlib import Path
from xml.etree import ElementTree


PACKAGE_IDS = (
    "Orleans.FSharp",
    "Orleans.FSharp.Abstractions",
    "Orleans.FSharp.Analyzers",
    "Orleans.FSharp.Runtime",
    "Orleans.FSharp.Templates",
    "Orleans.FSharp.Testing",
)

SYMBOL_PACKAGE_IDS = tuple(
    package_id for package_id in PACKAGE_IDS if package_id != "Orleans.FSharp.Templates"
)

REQUIRED_TEMPLATE_PACKAGE_IDS = {
    "Orleans.FSharp",
    "Orleans.FSharp.Runtime",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def nuspec_version(package: Path) -> str:
    with zipfile.ZipFile(package) as archive:
        nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError(f"{package.name} contains {len(nuspecs)} nuspec files")
        root = ElementTree.fromstring(archive.read(nuspecs[0]))
        version = root.findtext("{*}metadata/{*}version")
        if not version:
            raise ValueError(f"{package.name} has no nuspec version")
        return version


def validate_template(package: Path, version: str) -> None:
    with zipfile.ZipFile(package) as archive:
        project_files = [
            name
            for name in archive.namelist()
            if name.endswith((".fsproj", ".props", ".targets"))
        ]
        package_ids: set[str] = set()

        for name in project_files:
            root = ElementTree.fromstring(archive.read(name))
            for element in root.iter():
                if not isinstance(element.tag, str) or element.tag.rsplit("}", 1)[-1] != "PackageReference":
                    continue

                package_id = element.get("Include", "")
                if package_id != "Orleans.FSharp" and not package_id.startswith("Orleans.FSharp."):
                    continue

                package_ids.add(package_id)
                declared_version = element.get("Version")
                if declared_version is None:
                    version_element = next(
                        (
                            child
                            for child in element
                            if child.tag.rsplit("}", 1)[-1] == "Version"
                        ),
                        None,
                    )
                    declared_version = version_element.text if version_element is not None else None

                if declared_version != f"[{version}]":
                    raise ValueError(
                        f"{package.name}:{name} references {package_id} with "
                        f"Version={declared_version!r}, expected exact [{version}]"
                    )

    missing_required = sorted(REQUIRED_TEMPLATE_PACKAGE_IDS - package_ids)
    if missing_required:
        raise ValueError(
            f"{package.name} is missing required template references: {missing_required}"
        )


def current_manifest(directory: Path, version: str) -> dict:
    expected_names = {
        *(f"{package_id}.{version}.nupkg" for package_id in PACKAGE_IDS),
        *(f"{package_id}.{version}.snupkg" for package_id in SYMBOL_PACKAGE_IDS),
    }
    artifacts = sorted((*directory.glob("*.nupkg"), *directory.glob("*.snupkg")))
    actual_names = {artifact.name for artifact in artifacts}

    if actual_names != expected_names:
        missing = sorted(expected_names - actual_names)
        extra = sorted(actual_names - expected_names)
        raise ValueError(f"package allowlist mismatch; missing={missing}, extra={extra}")

    entries = []
    for artifact in artifacts:
        actual_version = nuspec_version(artifact)
        if actual_version != version:
            raise ValueError(
                f"{artifact.name} contains nuspec version {actual_version}, expected {version}"
            )
        entries.append(
            {
                "file": artifact.name,
                "sha256": sha256(artifact),
                "size": artifact.stat().st_size,
            }
        )

    validate_template(directory / f"Orleans.FSharp.Templates.{version}.nupkg", version)
    return {"version": version, "artifacts": entries}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--version", required=True)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--write-manifest", type=Path)
    mode.add_argument("--check-manifest", type=Path)
    args = parser.parse_args()

    try:
        manifest = current_manifest(args.directory.resolve(), args.version)
        if args.write_manifest is not None:
            args.write_manifest.write_text(
                json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8"
            )
        else:
            recorded = json.loads(args.check_manifest.read_text(encoding="utf-8"))
            if recorded != manifest:
                raise ValueError("package bytes do not match the release-candidate manifest")
    except (OSError, ValueError, zipfile.BadZipFile, ElementTree.ParseError, json.JSONDecodeError) as error:
        print(f"release package check failed: {error}", file=sys.stderr)
        return 1

    print(
        f"validated {len(PACKAGE_IDS)} packages and {len(SYMBOL_PACKAGE_IDS)} symbol packages "
        f"for exact version {args.version}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
