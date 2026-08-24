#!/usr/bin/env python3
"""Live contract check for the Pi Unity scenario runner."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from urllib.request import urlopen

from pi_unity import ScenarioError, expect, load_scenario


TOOLS = Path(__file__).resolve().parent
SCENE = "Assets/WorldSim/Scenarios/CatBasement/CatBasement.unity"


class PiUnityHarnessTest(unittest.TestCase):
    def assert_clean_failure(self, scenario, output, because):
        """Assert the CLI failed for the stated reason, not merely that it failed.

        `because` is not decoration. Every kind of breakage this tool has -
        a bad scenario file, an unreachable sim, an unwritable output path -
        exits 1 with a "FAIL:" line, so asserting only on those two things
        passes even when the code under test never ran. These tests were doing
        exactly that: six of eight passed with nothing listening on port 8090.
        """
        result = subprocess.run(
            [sys.executable, str(TOOLS / "pi_unity.py"), str(scenario), "--output", str(output)],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertTrue(result.stderr.startswith("FAIL:"), result.stderr)
        self.assertIn(because, result.stderr)
        self.assertNotIn("Traceback", result.stderr)

    def test_expected_fields_require_presence_and_json_type(self):
        for actual, expected in (({}, {"value": None}), ({"value": True}, {"value": 1})):
            with self.subTest(actual=actual, expected=expected):
                with self.assertRaises(ScenarioError):
                    expect("state", actual, expected)

    def test_unity_integer_inputs_fit_signed_32_bit(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "scenario.json"
            for field in ("seed", "advance"):
                with self.subTest(field=field):
                    scenario = {
                        "name": "bounds",
                        "scene": SCENE,
                        "url": "http://localhost:8090",
                        "seed": 1,
                        "runs": 2,
                        "doctor": {},
                        "checkpoints": [{"advance": 0, "expect": {}}],
                    }
                    if field == "seed":
                        scenario["seed"] = 2147483648
                    else:
                        scenario["checkpoints"][0]["advance"] = 2147483648
                    path.write_text(json.dumps(scenario))
                    with self.assertRaises(ScenarioError):
                        load_scenario(path)

    def test_saved_scenario_declares_a_safe_unity_scene(self):
        scenario = load_scenario(TOOLS / "scenarios" / "cat-basement-smoke.json")
        self.assertEqual(scenario.get("scene"), SCENE)

        with tempfile.TemporaryDirectory() as directory:
            unsafe = dict(scenario, scene="../Other.unity")
            path = Path(directory) / "unsafe.json"
            path.write_text(json.dumps(unsafe))
            with self.assertRaises(ScenarioError):
                load_scenario(path)

    def test_pi_unity_cli_shows_scene_without_contacting_unity(self):
        result = subprocess.run(
            [
                sys.executable,
                str(TOOLS / "pi_unity.py"),
                str(TOOLS / "scenarios" / "cat-basement-smoke.json"),
                "--show",
                "scene",
            ],
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.stdout.strip(),
            SCENE,
        )

    def test_malformed_scenario_fails_cleanly(self):
        with tempfile.TemporaryDirectory() as directory:
            scenario = Path(directory) / "bad.json"
            scenario.write_text(
                json.dumps(
                    {
                        "name": [],
                        "scene": SCENE,
                        "url": "http://localhost:8090",
                        "seed": 1,
                        "runs": 2,
                        "doctor": {},
                        "checkpoints": [{"advance": 0, "expect": {}}],
                    }
                )
            )
            self.assert_clean_failure(
                scenario, Path(directory) / "output", "name must be filename-safe"
            )

    def test_invalid_encoding_and_url_fail_cleanly(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            invalid_encoding = directory / "encoding.json"
            invalid_encoding.write_bytes(b"\xff")

            invalid_url = directory / "url.json"
            invalid_url.write_text(
                json.dumps(
                    {
                        "name": "bad-url",
                        "scene": SCENE,
                        "url": "http://localhost:not-a-port",
                        "seed": 1,
                        "runs": 2,
                        "doctor": {},
                        "checkpoints": [{"advance": 0, "expect": {}}],
                    }
                )
            )
            with self.assertRaises(ScenarioError):
                load_scenario(invalid_url)
            for scenario, because in (
                (invalid_encoding, "codec can't decode"),
                (invalid_url, "invalid control URL"),
            ):
                with self.subTest(scenario=scenario.name):
                    self.assert_clean_failure(scenario, directory / "output", because)

    def test_output_path_failure_is_clean(self):
        with tempfile.TemporaryDirectory() as directory:
            with urlopen("http://localhost:8090/doctor", timeout=5):
                pass
            output = Path(directory) / "not-a-directory"
            output.write_text("occupied")
            self.assert_clean_failure(
                TOOLS / "scenarios" / "cat-basement-smoke.json",
                output,
                "File exists",
            )

    def test_saved_scenario_repeats_deterministically(self):
        with tempfile.TemporaryDirectory() as output:
            with urlopen("http://localhost:8090/play", timeout=5):
                pass
            result = subprocess.run(
                [
                    sys.executable,
                    str(TOOLS / "pi_unity.py"),
                    str(TOOLS / "scenarios" / "cat-basement-smoke.json"),
                    "--output",
                    output,
                ],
                capture_output=True,
                text=True,
                timeout=90,
            )

            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            report = json.loads((Path(output) / "report.json").read_text())
            self.assertEqual(report["scene"], SCENE)
            self.assertEqual(report["seed"], 12345)
            self.assertEqual(report["runs"], 2)
            self.assertTrue(report["deterministic"])
            self.assertEqual(report["final"]["frame"], 120)
            self.assertEqual(report["final"]["simTime"], 2.0)
            self.assertTrue(report["final"]["paused"])
            self.assertEqual(report["final"]["entityCount"], 3)
            self.assertEqual(
                [entity["name"] for entity in report["final"]["entities"]],
                ["Cat_Black", "Cat_Gray", "Cat_Orange"],
            )
            self.assertTrue((Path(output) / "cat-basement-smoke.png").is_file())
            with urlopen("http://localhost:8090/inspect", timeout=5) as response:
                self.assertFalse(json.load(response)["paused"])


if __name__ == "__main__":
    unittest.main()
