#!/usr/bin/env python3
"""Stage the template package with exact references to one release candidate.

The source template intentionally uses the stable 5.* range for checkout-based development.
A published template must instead reference the exact package version shipped beside it,
including a prerelease suffix, so NuGet cannot resolve an older stable package.
"""

import argparse
import re
import shutil
import sys
from pathlib import Path
from xml.etree import ElementTree


VERSION_RE = re.compile(
    r"^(?P<major>0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)

def validate_version(value: str) -> str:
    match = VERSION_RE.fullmatch(value)
    if match is None:
        raise ValueError(f"'{value}' is not a supported NuGet SemVer release candidate")
    if match.group("major") != "5":
        raise ValueError(f"release template expects a 5.x candidate, got '{value}'")
    return value


def stage(source: Path, output: Path, version: str) -> None:
    if not source.is_dir():
        raise ValueError(f"template source does not exist: {source}")
    if output.exists():
        raise ValueError(f"staging output already exists: {output}")

    shutil.copytree(source, output, ignore=shutil.ignore_patterns("bin", "obj"))
    exact_version = f"[{version}]"
    replacement_count = 0

    project_files = sorted(
        path
        for path in output.rglob("*")
        if path.suffix in {".fsproj", ".props", ".targets"}
    )

    for path in project_files:
        parser = ElementTree.XMLParser(target=ElementTree.TreeBuilder(insert_comments=True))
        tree = ElementTree.parse(path, parser=parser)
        changed = False

        for element in tree.getroot().iter():
            if not isinstance(element.tag, str) or element.tag.rsplit("}", 1)[-1] != "PackageReference":
                continue

            package_id = element.get("Include", "")
            if package_id != "Orleans.FSharp" and not package_id.startswith("Orleans.FSharp."):
                continue

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
                if version_element is None:
                    raise ValueError(
                        f"{path.relative_to(output)}:{package_id} has no explicit source version"
                    )
                declared_version = version_element.text
                if declared_version != "5.*":
                    raise ValueError(
                        f"{path.relative_to(output)}:{package_id} must use source range 5.*, "
                        f"found {declared_version!r}"
                    )
                version_element.text = exact_version
            else:
                if declared_version != "5.*":
                    raise ValueError(
                        f"{path.relative_to(output)}:{package_id} must use source range 5.*, "
                        f"found {declared_version!r}"
                    )
                element.set("Version", exact_version)

            replacement_count += 1
            changed = True

        if changed:
            tree.write(path, encoding="utf-8", xml_declaration=False)

    if replacement_count == 0:
        raise ValueError("template contains no Orleans.FSharp package references to pin")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()

    try:
        version = validate_version(args.version)
        stage(args.source.resolve(), args.output.resolve(), version)
    except (OSError, ValueError, ElementTree.ParseError) as error:
        print(f"release template staging failed: {error}", file=sys.stderr)
        return 1

    print(f"staged release template {version} at {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
