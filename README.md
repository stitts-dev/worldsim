# worldsim

Reusable Unity simulation harness for homelab robotics/AI experiments — the
digital-twin side of the toolbelt. Virtual cameras stream **real MJPEG over
HTTP**; virtual actuators speak **real MQTT**. External AI services run against
the sim exactly as they would against physical hardware, so pipelines get built
and validated before any parts are bought.

```
Assets/WorldSim/
  Core/              # reusable: FrameStreamer (MJPEG), MqttBridge, agents, dataset capture
  Scenarios/
    CatBasement/     # scenario #1 — litterboxes, wandering cats, virtual deterrent turret
```

Scenario #1 drives [cat-sentry](https://github.com/stitts-dev/cat-sentry): cat
agents wander a basement, occasionally squat beside the litterboxes; the real
detection service watches the stream and fires the virtual turret over MQTT.
`Core` is scenario-agnostic — future experiments add scenarios, not plumbing.

## Status

CatBasement is assembled and runnable. Unity 6 LTS, URP. The external control
surface supports deterministic reset/step/inspect/capture on port 8090.

## Pi Unity saved scenario

Launch the scene named by a saved scenario, replay it, save the evidence, and
close Unity:

```bash
./tools/run-pi-unity.sh tools/scenarios/cat-basement-smoke.json
```

To target an editor that is already in Play mode instead:

```bash
python3 tools/pi_unity.py tools/scenarios/cat-basement-smoke.json --output /tmp/cat-basement-smoke
```

The saved scenario replays the same seed twice, checks frame-zero and frame-120
state, rejects nondeterministic runs, and writes `report.json` plus a PNG capture.
Copy the JSON and change its `name`, `scene`, seed, and checkpoints to save a new
prototype. The launcher and harness need no scenario-specific code.
`tools/run-acceptance.sh` runs this gate before the existing MJPEG/MQTT checks.

## Docs

- [Design spec](docs/design.md) — self-contained, an agent can build from this alone
- [Interface contract (catsentry-v1)](docs/contract-catsentry-v1.md) — frozen
