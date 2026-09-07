#!/usr/bin/env python3

import argparse
import sys
from pathlib import Path


EXPECTED_LINES = (
    "--- Functional Counter Grain Demo ---",
    "After increment: 1",
    "After increment: 2",
    "Current value: 2",
    "After decrement: 1",
    "Sample complete. Shutting down...",
)


def check_transcript(output: str) -> None:
    lines = output.splitlines()
    next_line = 0

    for expected in EXPECTED_LINES:
        try:
            next_line = lines.index(expected, next_line) + 1
        except ValueError as error:
            raise ValueError(
                f"missing or out-of-order transcript line: {expected!r}"
            ) from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("log", type=Path)
    args = parser.parse_args()

    try:
        check_transcript(args.log.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        print(f"Template smoke check failed: {error}", file=sys.stderr)
        return 1

    print("Template smoke transcript verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
