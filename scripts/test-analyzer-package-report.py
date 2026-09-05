#!/usr/bin/env python3
"""Negative controls for the analyzer package smoke's SARIF acceptance check."""

import copy
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("analyzer_smoke", Path(__file__).with_name("check-analyzer-package.py"))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)

RESULT = {
    "ruleId": "OF0001",
    "locations": [{"physicalLocation": {
        "artifactLocation": {"uri": "tests/Orleans.FSharp.Analyzers.Consumer/Consumer.fs"},
        "region": {"startLine": 5},
    }}],
}


class ReportTests(unittest.TestCase):
    def check_results(self, results, *, success=True, default_level=None):
        rule = {"id": "OF0001"}
        if default_level is not None:
            rule["defaultConfiguration"] = {"level": default_level}
        run = {
            "results": results,
            "invocations": [{"executionSuccessful": success}],
            "tool": {"driver": {"rules": [rule]}},
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.sarif"
            path.write_text(json.dumps({"runs": [run]}), encoding="utf-8")
            smoke.check_report(path)

    def test_expected_warning_with_sarif_default_level(self):
        self.check_results([RESULT])

    def test_no_loaded_analyzer_cannot_pass(self):
        with self.assertRaises(ValueError):
            self.check_results([])

    def test_duplicate_or_unsuppressed_control_cannot_pass(self):
        with self.assertRaises(ValueError):
            self.check_results([RESULT, RESULT])

    def test_wrong_rule_cannot_pass(self):
        with self.assertRaises(ValueError):
            self.check_results([dict(RESULT, ruleId="OTHER")])

    def test_error_cannot_pass_as_warning(self):
        with self.assertRaises(ValueError):
            self.check_results([dict(RESULT, level="error")])

    def test_warning_on_negative_control_cannot_pass(self):
        result = copy.deepcopy(RESULT)
        result["locations"][0]["physicalLocation"]["region"]["startLine"] = 10
        with self.assertRaises(ValueError):
            self.check_results([result])

    def test_similarly_named_file_cannot_pass(self):
        result = copy.deepcopy(RESULT)
        result["locations"][0]["physicalLocation"]["artifactLocation"]["uri"] = "tests/NotConsumer.fs"
        with self.assertRaises(ValueError):
            self.check_results([result])

    def test_same_filename_in_another_directory_cannot_pass(self):
        result = copy.deepcopy(RESULT)
        result["locations"][0]["physicalLocation"]["artifactLocation"]["uri"] = "wrong/Consumer.fs"
        with self.assertRaises(ValueError):
            self.check_results([result])

    def test_failed_invocation_cannot_pass(self):
        with self.assertRaises(ValueError):
            self.check_results([RESULT], success=False)

    def test_inherited_error_severity_cannot_pass(self):
        with self.assertRaises(ValueError):
            self.check_results([RESULT], default_level="error")


if __name__ == "__main__":
    unittest.main()
