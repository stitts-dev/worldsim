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

Spec complete, implementation starting. Unity 6 LTS, URP.

## Docs

- [Design spec](docs/design.md) — self-contained, an agent can build from this alone
- [Interface contract (catsentry-v1)](docs/contract-catsentry-v1.md) — frozen
