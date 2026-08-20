# Contract: catsentry-v1

Frozen interface between any **frame source** (Unity worldsim, real camera), the
**detection service** (cat-sentry), and any **actuator** (worldsim virtual turret,
ESP32 hardware). Both sides build against this doc only. Changes require a version
bump (`catsentry-v2`) — never edit v1 in place once both tracks are building.

## 1. Video: source → detection

- Transport: **MJPEG over HTTP** (`multipart/x-mixed-replace; boundary=frame`).
- Endpoint: `GET http://<host>:8089/stream`
- Also required: `GET http://<host>:8089/snapshot` → single JPEG (used by tests/alerts).
- Frame spec: **1280x720, 10–15 fps, JPEG quality ~70**. Fixed resolution — zone
  polygons are defined in normalized coords but detector assumes 16:9.
- Detection side consumes via `cv2.VideoCapture(<url>)`. Real deployment swaps the
  URL to an RTSP camera. Nothing else changes.

## 2. Commands: MQTT

- Broker: mosquitto, `localhost:1883`, no auth (LAN/tailnet only). QoS 1 on
  `deterrent/*`, QoS 0 elsewhere.
- All payloads JSON UTF-8. Timestamps ISO 8601 UTC (`2026-08-19T21:04:00Z`).
- Coordinates normalized `[0..1]` in frame space, origin top-left.

### Topics

`catsentry/event` — published by detection service.
```json
{
  "ts": "2026-08-19T21:04:00Z",
  "type": "cat_detected | zone_enter | zone_exit | squat_suspected | deterrent_fired | all_clear",
  "cat_id": null,
  "zone": "floor_left",
  "confidence": 0.87,
  "bbox": [0.41, 0.62, 0.11, 0.09],
  "snapshot_path": "events/2026-08-19/210400.jpg"
}
```
`cat_id` is `null` in v1 (per-cat ID is phase 2). `bbox` = `[x, y, w, h]` normalized.

`catsentry/deterrent/fire` — published by detection service, consumed by actuator.
```json
{
  "ts": "2026-08-19T21:04:03Z",
  "level": "sound",
  "target": [0.46, 0.66],
  "duration_ms": 1500,
  "reason": "squat_suspected dwell=4.2s zone=left_of_boxes"
}
```
`level`: `"sound"` (first warning) or `"air"` (escalation).

`catsentry/deterrent/ack` — published by actuator after executing.
```json
{ "ts": "2026-08-19T21:04:04Z", "level": "sound", "ok": true, "detail": "played chirp 1500ms" }
```

`catsentry/telemetry` — actuator heartbeat, every 10 s.
```json
{ "ts": "2026-08-19T21:04:10Z", "source": "sim", "status": "ok" }
```
`source`: `"sim"` or `"esp32"`.

## 3. Swap rule

Going from simulation to hardware changes exactly two things:
1. Video source URL (MJPEG sim URL → RTSP camera URL) — config line.
2. Actuator implementation (Unity turret → ESP32 firmware) — same topics, same payloads.

Detection service code does not change.
