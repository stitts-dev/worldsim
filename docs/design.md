# worldsim — Design Spec (standalone, hand to any agent)

Date: 2026-08-19
Status: approved design, pre-implementation
Repo: `C:\Users\jaden\homelab\worldsim` (own git repo, public)

This document is self-contained: an agent with no other context (e.g. a Cursor
session) can implement worldsim from this file alone.

## What worldsim is

A reusable Unity simulation harness for homelab robotics/AI experiments. It
renders virtual camera feeds as **real MJPEG HTTP streams** and connects virtual
actuators to a **real MQTT broker**, so external AI services can be developed and
tested against the sim exactly as they would run against physical hardware
(digital-twin pattern).

Scenario #1: **CatBasement** — a basement with litterboxes and wandering cat
agents, used to close the loop for the `cat-sentry` detection service (a separate
Python project that consumes the stream and publishes deterrent commands).
Future scenarios (not now, but the Core/Scenario split exists for them): other
home-robotics tests, a DnD-universe world experiment.

## Stack

- **Unity 6 LTS** (6000.0.x), **URP**, 3D. Windows dev machine.
- MQTT client: **M2Mqtt** (via NuGetForUnity) or vendored MQTTnet — implementer's
  choice, smallest working option wins.
- No other external services. MJPEG server is hand-rolled C# (`HttpListener`),
  ~100–150 lines — do not add a web framework.

## Project structure

```
worldsim/
  Assets/
    WorldSim/
      Core/                      # reusable across scenarios — own asmdef "WorldSim.Core"
        FrameStreamer.cs         # Camera → RenderTexture → JPEG → MJPEG HTTP server
        MqttBridge.cs            # connect, publish, subscribe; JSON helpers
        ScenarioRunner.cs        # scenario lifecycle, config loading
        DatasetCapture.cs        # phase 2 — labeled frame dumps
        Agents/
          WanderAgent.cs         # NavMesh wander behavior, reusable
      Scenarios/
        CatBasement/             # asmdef "WorldSim.CatBasement", refs Core
          CatBasement.unity      # the scene
          CatAgent.cs            # wander + scripted misbehavior
          VirtualTurret.cs       # MQTT-driven deterrent actuator
          config (ScriptableObject or JSON in StreamingAssets)
  README.md
  .gitignore                     # standard Unity gitignore
```

Rule: nothing scenario-specific in `Core`. Anything used by a second scenario
later gets promoted into `Core`.

## Core components

### FrameStreamer (Core)
- Attach to any Unity `Camera`. Renders to a `RenderTexture` (**1280x720**),
  encodes JPEG (`ImageConversion.EncodeToJPG`, quality 70), serves:
  - `GET http://0.0.0.0:8089/stream` — MJPEG, `multipart/x-mixed-replace; boundary=frame`, **10–15 fps** (throttled, independent of Unity framerate)
  - `GET http://0.0.0.0:8089/snapshot` — single JPEG
- `HttpListener` on a background thread; frame handoff via a locked latest-frame
  buffer (drop frames, never queue). Multiple simultaneous clients supported.
- Port configurable, default 8089.
- Note: `AsyncGPUReadback` preferred over `Texture2D.ReadPixels` for the
  RenderTexture readback (avoids GPU stall); fall back to ReadPixels if it fights.

### MqttBridge (Core)
- Connects to broker (default `localhost:1883`, configurable). Reconnect with
  backoff. Publish/subscribe with plain string topics + JSON string payloads
  (use `JsonUtility` or vendored minimal JSON — no heavy deps).
- Publishes heartbeat every 10 s to a configurable telemetry topic.

### WanderAgent (Core)
- `NavMeshAgent` picks random reachable points within a bounds volume, idles
  1–4 s between moves. Speed/bounds configurable.

## Scenario: CatBasement

### Scene
- Rectangular basement room ~6x4 m: floor plane, 4 walls, dim warm lighting
  (basement feel). Primitive geometry + simple materials is fine.
- **3 litterboxes**: open-top boxes (scaled cubes with rim, sand-colored top
  surface) along one wall, ~40x30 cm each.
- **Camera**: mounted high in a corner opposite the boxes, angled down covering
  boxes + adjacent floor — like a real security cam. `FrameStreamer` attached.
- **Virtual turret**: small mount near ceiling with `VirtualTurret` attached.
- NavMesh baked over the floor.

### CatAgent
- Visual: a free low-poly cat model if trivially available; otherwise a
  cat-sized capsule (~45 cm long, ~25 cm tall) with legs implied — MUST read as a
  distinct blob from above, exact realism not required for v1. Three instances
  with different albedo colors (black, orange, gray) — future per-cat ID work.
- Behavior loop: `WanderAgent` wandering, plus **misbehavior routine**:
  - Trigger: every N seconds (config, default 60–120 s random) OR hotkey **P**
    (targets nearest cat) for on-demand testing.
  - Sequence: walk to a point on the floor *beside* the litterboxes (1–2 m away,
    in the region a config marks as the "problem zone") → stop → **squat pose**:
    scale Y to ~60% and hold still for `squat_duration` (default 8 s) → resume
    wandering.
  - React to deterrent: if turret fires while squatting → immediately cancel
    squat and flee (sprint to far corner). This makes the closed-loop demo
    visually obvious.

### VirtualTurret
- Subscribes `catsentry/deterrent/fire`. On message:
  - `level: "sound"` → play a chirp `AudioSource` + expanding ring gizmo/particle
    at `target` (normalized frame coords are informational; the turret just
    reacts visibly — precise aiming is phase 2).
  - `level: "air"` → particle burst (white puff) aimed at the floor near target.
  - Publish ack to `catsentry/deterrent/ack`.
  - Notify any squatting `CatAgent` within the room to trigger the flee reaction.
- On-screen debug overlay (OnGUI is fine): last command received, ack sent,
  fires this session.

## Interface contract (catsentry-v1) — FROZEN, build exactly this

Video out: MJPEG per FrameStreamer above (1280x720, 10–15 fps, q≈70, port 8089).

MQTT: broker `localhost:1883`, no auth, QoS 1 on `deterrent/*`. JSON UTF-8,
timestamps ISO 8601 UTC, coordinates normalized [0..1], origin top-left.

Subscribe — `catsentry/deterrent/fire`:
```json
{ "ts": "2026-08-19T21:04:03Z", "level": "sound", "target": [0.46, 0.66], "duration_ms": 1500, "reason": "squat_suspected dwell=4.2s zone=floor_left" }
```
`level` ∈ `"sound" | "air"`.

Publish — `catsentry/deterrent/ack`:
```json
{ "ts": "2026-08-19T21:04:04Z", "level": "sound", "ok": true, "detail": "played chirp 1500ms" }
```

Publish — `catsentry/telemetry` every 10 s:
```json
{ "ts": "2026-08-19T21:04:10Z", "source": "sim", "status": "ok" }
```

Do not rename topics or fields. Contract changes require agreement with the
cat-sentry side and a v2 doc.

## Phase 2 (spec now, build after v1 acceptance)

**DatasetCapture (Core):** offline mode that generates labeled training images
for the per-cat classifier: randomize lighting (intensity/color/angle), cat
albedo/patterns, camera pose jitter, floor/wall textures; for each frame compute
each cat's 2D bbox from its renderer bounds projected to screen; dump
`images/*.jpg` + COCO-format `annotations.json`. Target: 5–10k frames batch run.

**Turret aiming:** map normalized frame coords → world ray via the streaming
camera, orient turret before firing. (Groundwork for later ML-Agents RL.)

## Acceptance criteria (v1 done =  all true)

1. Open project in Unity 6, press Play: no errors, basement scene runs.
2. `http://localhost:8089/stream` plays live in VLC/browser; `/snapshot` returns a JPEG.
3. 3 cats wander; pressing **P** makes one walk beside the boxes and squat ~8 s.
4. With mosquitto running: `catsentry/telemetry` heartbeats visible
   (`mosquitto_sub -t 'catsentry/#' -v`).
5. `mosquitto_pub -t catsentry/deterrent/fire -m '{"ts":"2026-08-19T00:00:00Z","level":"sound","target":[0.5,0.5],"duration_ms":1000,"reason":"manual test"}'`
   → chirp + ring in sim, ack published, squatting cat flees.
6. Same with `"level":"air"` → puff particle + ack.
7. Streaming + MQTT survive 10+ minutes without leak/disconnect (watch memory,
   reconnect works after broker restart).

## Milestones for implementation

- **W1** — Unity project, repo, gitignore, URP setup, basement scene geometry, NavMesh.
- **W2** — FrameStreamer working (VLC shows stream). *Highest-risk item — do early.*
- **W3** — cats wandering + misbehavior routine + hotkey.
- **W4** — MqttBridge + VirtualTurret + acks + flee reaction. Acceptance run.
- **W5 (phase 2)** — DatasetCapture, turret aiming.
