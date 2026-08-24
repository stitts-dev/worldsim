#!/usr/bin/env python3
"""Replay a saved Pi Unity scenario against a live simulation control surface."""

import argparse
import hashlib
from http.client import InvalidURL
import json
from pathlib import Path
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import urlopen


class ScenarioError(Exception):
    pass


def request(url, path, query=None, binary=False):
    target = url.rstrip("/") + path
    if query:
        target += "?" + urlencode(query)
    try:
        with urlopen(target, timeout=60) as response:
            body = response.read()
    except (HTTPError, URLError, TimeoutError, ValueError, InvalidURL) as error:
        raise ScenarioError(f"{target}: {error}") from error
    if binary:
        return body
    try:
        return json.loads(body)
    except json.JSONDecodeError as error:
        raise ScenarioError(f"{target}: invalid JSON") from error


def load_scenario(path):
    try:
        scenario = json.loads(path.read_text())
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ScenarioError(f"{path}: {error}") from error

    if not isinstance(scenario, dict):
        raise ScenarioError(f"{path}: scenario must be an object")
    required = {"name", "scene", "url", "seed", "runs", "doctor", "checkpoints"}
    missing = required - scenario.keys()
    if missing:
        raise ScenarioError(f"{path}: missing {', '.join(sorted(missing))}")
    if not isinstance(scenario["name"], str) or not re.fullmatch(
        r"[A-Za-z0-9][A-Za-z0-9._-]*", scenario["name"]
    ):
        raise ScenarioError(f"{path}: name must be filename-safe")
    scene = scenario["scene"]
    if (
        not isinstance(scene, str)
        or not scene.startswith("Assets/")
        or not scene.endswith(".unity")
        or "\\" in scene
        or any(part in ("", ".", "..") for part in scene.split("/"))
    ):
        raise ScenarioError(f"{path}: scene must be a safe Assets/.../*.unity path")
    url = scenario["url"]
    try:
        parsed_url = urlsplit(url) if isinstance(url, str) else None
        if parsed_url is not None:
            parsed_url.port
    except ValueError as error:
        raise ScenarioError(f"{path}: invalid control URL: {error}") from error
    if parsed_url is None or parsed_url.scheme not in ("http", "https") or not parsed_url.hostname:
        raise ScenarioError(f"{path}: url must be HTTP or HTTPS")
    if type(scenario["seed"]) is not int or not -2147483648 <= scenario["seed"] <= 2147483647:
        raise ScenarioError(f"{path}: seed must be a signed 32-bit integer")
    if type(scenario["runs"]) is not int or scenario["runs"] < 2:
        raise ScenarioError(f"{path}: runs must be at least 2")
    if not isinstance(scenario["doctor"], dict):
        raise ScenarioError(f"{path}: doctor must be an object")
    if not isinstance(scenario["checkpoints"], list) or not scenario["checkpoints"]:
        raise ScenarioError(f"{path}: checkpoints must be a non-empty array")
    for index, checkpoint in enumerate(scenario["checkpoints"]):
        if not isinstance(checkpoint, dict):
            raise ScenarioError(f"{path}: checkpoint {index} must be an object")
        advance = checkpoint.get("advance")
        if type(advance) is not int or not 0 <= advance <= 2147483647:
            raise ScenarioError(f"{path}: checkpoint {index} advance must fit signed 32-bit")
        if not isinstance(checkpoint.get("expect"), dict):
            raise ScenarioError(f"{path}: checkpoint {index} expect must be an object")
    return scenario


def expect(label, actual, expected):
    if not isinstance(actual, dict):
        raise ScenarioError(f"{label}: expected an object, got {type(actual).__name__}")
    for key, wanted in expected.items():
        if key not in actual:
            raise ScenarioError(f"{label}.{key}: field is missing")
        got = actual[key]
        if got != wanted or isinstance(got, bool) != isinstance(wanted, bool):
            raise ScenarioError(f"{label}.{key}: expected {wanted!r}, got {got!r}")


def capture_png(url, label):
    """Fetch /capture and return (bytes, sha256). Rejects anything but a PNG."""
    image = request(url, "/capture", binary=True)
    if not image.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ScenarioError(f"{label}: /capture did not return a PNG")
    return image, hashlib.sha256(image).hexdigest()


def run(scenario, output, url_override=None):
    url = url_override or scenario["url"]
    doctor = request(url, "/doctor")
    expect("doctor", doctor, scenario["doctor"])

    baseline = None
    baseline_digest = None
    capture = None
    digest = None
    final = None
    for run_number in range(1, scenario["runs"] + 1):
        request(url, "/reset", {"seed": scenario["seed"]})
        states = []
        for checkpoint_number, checkpoint in enumerate(scenario["checkpoints"]):
            request(url, "/step", {"n": checkpoint["advance"]})
            state = request(url, "/inspect")
            expect(
                f"run {run_number} checkpoint {checkpoint_number}",
                state,
                checkpoint["expect"],
            )
            states.append(state)

        # Inside the loop, not after it. The report claims the runs were
        # deterministic; a capture taken once at the end is a picture whose
        # reproducibility was never tested, sitting next to that claim.
        capture, digest = capture_png(url, f"run {run_number}")

        if baseline is None:
            baseline, baseline_digest = states, digest
        else:
            if states != baseline:
                raise ScenarioError(f"run {run_number}: state differs from run 1")
            if digest != baseline_digest:
                raise ScenarioError(
                    f"run {run_number}: capture differs from run 1 "
                    f"({digest[:16]} vs {baseline_digest[:16]})"
                )
        final = states[-1]

    request(url, "/play")

    output.mkdir(parents=True, exist_ok=True)
    (output / f"{scenario['name']}.png").write_bytes(capture)
    report = {
        "scenario": scenario["name"],
        "scene": scenario["scene"],
        "seed": scenario["seed"],
        "runs": scenario["runs"],
        "deterministic": True,
        "captureSha256": digest,
        "doctor": doctor,
        "checkpoints": baseline,
        "final": final,
    }
    (output / "report.json").write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scenario", type=Path, help="saved scenario JSON")
    parser.add_argument("--output", type=Path, help="report and capture directory")
    parser.add_argument("--url", help="override the scenario control URL")
    parser.add_argument("--show", choices=("scene", "url"), help="print one launch field and exit")
    args = parser.parse_args()

    try:
        scenario = load_scenario(args.scenario)
        if args.show:
            print(scenario[args.show])
            return 0
        if args.output is None:
            parser.error("--output is required unless --show is used")
        report = run(scenario, args.output, args.url)
    except (ScenarioError, OSError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    print(
        f"PASS {report['scenario']}: {report['runs']} deterministic runs, "
        f"frame {report['final']['frame']}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
