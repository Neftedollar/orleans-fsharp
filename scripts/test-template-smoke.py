#!/usr/bin/env python3

import importlib.util
import unittest
from pathlib import Path


CHECKER_PATH = Path(__file__).with_name("check-template-smoke.py")
SPEC = importlib.util.spec_from_file_location("check_template_smoke", CHECKER_PATH)
assert SPEC is not None and SPEC.loader is not None
CHECKER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CHECKER)

EXPECTED_LINES = (
    "--- Functional Counter Grain Demo ---",
    "After increment: 1",
    "After increment: 2",
    "Current value: 2",
    "After decrement: 1",
    "Sample complete. Shutting down...",
)


class TemplateSmokeTranscriptTests(unittest.TestCase):
    def test_accepts_expected_lines_with_interspersed_logs(self) -> None:
        output = "\n".join(
            line
            for expected in EXPECTED_LINES
            for line in ("unrelated runtime log", expected)
        )

        CHECKER.check_transcript(output)

    def test_rejects_missing_line(self) -> None:
        output = "\n".join(
            line
            for line in EXPECTED_LINES
            if line != "Current value: 2"
        )

        with self.assertRaises(ValueError):
            CHECKER.check_transcript(output)

    def test_rejects_wrong_line(self) -> None:
        output = "\n".join(EXPECTED_LINES).replace(
            "After decrement: 1", "After decrement: 2"
        )

        with self.assertRaises(ValueError):
            CHECKER.check_transcript(output)

    def test_rejects_out_of_order_lines(self) -> None:
        lines = list(EXPECTED_LINES)
        lines[1], lines[2] = lines[2], lines[1]

        with self.assertRaises(ValueError):
            CHECKER.check_transcript("\n".join(lines))


if __name__ == "__main__":
    unittest.main()
