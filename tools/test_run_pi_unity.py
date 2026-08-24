#!/usr/bin/env python3
"""Clean-process integration check for the generic Pi Unity launcher."""

import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import unittest


TOOLS = Path(__file__).resolve().parent


class PiUnityLauncherTest(unittest.TestCase):
    def test_early_unity_exit_code_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            unity = Path(directory) / "Unity"
            unity.write_text("#!/bin/sh\nexit 7\n")
            unity.chmod(0o755)
            result = subprocess.run(
                [
                    "bash",
                    str(TOOLS / "run-pi-unity.sh"),
                    str(TOOLS / "scenarios" / "cat-basement-smoke.json"),
                    str(Path(directory) / "output"),
                ],
                capture_output=True,
                text=True,
                # PI_UNITY_LOG too, not just UNITY: the launcher truncates its
                # log on startup, so leaving it at the default would have this
                # test destroy the real run log sitting in the repo.
                env={
                    **os.environ,
                    "UNITY": str(unity),
                    "PI_UNITY_LOG": str(Path(directory) / "pi-unity.log"),
                },
                timeout=10,
            )
            self.assertEqual(result.returncode, 7, result.stdout + result.stderr)

    def test_saved_scenario_launches_its_unity_scene(self):
        with tempfile.TemporaryDirectory() as output:
            process = subprocess.Popen(
                [
                    "bash",
                    str(TOOLS / "run-pi-unity.sh"),
                    str(TOOLS / "scenarios" / "cat-basement-smoke.json"),
                    output,
                ],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                env={
                    **os.environ,
                    "PI_UNITY_BOOT_TIMEOUT_SEC": "150",
                    "PI_UNITY_LOG": str(Path(output) / "pi-unity.log"),
                },
                start_new_session=True,
            )
            try:
                stdout, stderr = process.communicate(timeout=240)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    stdout, stderr = process.communicate(timeout=25)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    stdout, stderr = process.communicate()
                self.fail("launcher timed out\n" + stdout + stderr)

            self.assertEqual(process.returncode, 0, stdout + stderr)
            report = json.loads((Path(output) / "report.json").read_text())
            self.assertEqual(report["doctor"]["scene"], "CatBasement")
            self.assertEqual(
                report["scene"],
                "Assets/WorldSim/Scenarios/CatBasement/CatBasement.unity",
            )
            self.assertEqual(report["seed"], 12345)
            self.assertTrue((Path(output) / "cat-basement-smoke.png").is_file())


if __name__ == "__main__":
    unittest.main()
