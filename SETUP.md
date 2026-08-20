# SETUP — editor-side assembly

Everything that can be authored as text has been (scripts, asmdefs). What's
left needs the Unity editor: package install, scene assembly, NavMesh bake,
wiring components together in the inspector. Read `docs/design.md` and
`docs/contract-catsentry-v1.md` first if anything here is ambiguous — both are
self-contained specs.

## 1. Install Unity 6 LTS

- Unity Hub → Installs → Install Editor → **Unity 6000.0.x LTS**.
- Modules: just the default Windows Build Support is fine (no mobile/console
  targets needed for this project).

## 2. Open the project

- Unity Hub → Open → point at the repo root (`worldsim/`, the folder with
  `Assets/`).
- First open will be slow — Unity imports every script, generates `.meta`
  files for everything under `Assets/WorldSim/` (none exist yet, this repo was
  authored without the editor), and does an initial domain reload.
- **Expect compile errors on first open** until step 3 (M2Mqtt) is done — the
  `using uPLibrary.Networking.M2Mqtt;` lines in `MqttBridge.cs` and
  `VirtualTurret.cs` won't resolve until the package is installed. That's
  expected, not a sign anything else is broken.

## 3. Confirm URP

If the project wasn't created from a URP template:
- `Window → Package Manager` → install **Universal RP**.
- `Edit → Project Settings → Graphics` → set the Scriptable Render Pipeline
  Asset to a URP asset (create one via `Assets → Create → Rendering → URP
  Asset (with Universal Renderer)` if none exists).
- `Edit → Render Pipeline → Universal Render Pipeline → Upgrade Project
  Materials to UniversalRP Materials` if any default/legacy materials get used
  for the room geometry.

## 4. Install NuGetForUnity + M2Mqtt

1. `Window → Package Manager → + → Add package from git URL…` and add:
   `https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity`
   (or import the latest NuGetForUnity `.unitypackage` release if you prefer —
   either way you end up with a `NuGet → Manage NuGet Packages` menu).
2. `NuGet → Manage NuGet Packages` → search **M2Mqtt** → Install.
   This drops the M2Mqtt DLL under `Assets/Packages/M2Mqtt.<version>/lib/...`
   (already covered by `.gitignore` — the DLL itself isn't committed, only its
   `.meta`).
3. Let Unity recompile. If `MqttBridge.cs` / `VirtualTurret.cs` still can't
   find `uPLibrary.Networking.M2Mqtt`:
   - Check the DLL's importer (`Assets/Packages/M2Mqtt.../M2Mqtt.dll` → click
     it in the Project window → Inspector) has **Any Platform** ticked.
   - If NuGetForUnity generated its own `.asmdef` for the DLL (some versions
     do, to avoid duplicate-reference warnings), open
     `Assets/WorldSim/Core/WorldSim.Core.asmdef` and add that assembly's name
     to its `references` array (Inspector → Assembly Definition References →
     `+` is easier than hand-editing the JSON).

## 5. Scene assembly — `CatBasement.unity`

Create the scene at `Assets/WorldSim/Scenarios/CatBasement/CatBasement.unity`
(new scene, URP template).

**Room** — floor plane + 4 walls, ~6 m x 4 m footprint:
- Floor: a `Plane` (10x10 units default → scale to ~0.6 x 1 x 0.4, or a Cube
  flattened to Y≈0.1, whichever is easier to eyeball at 6x4 m) tagged/named
  `Floor`.
- 4 walls: scaled cubes standing on the floor edges, ~2.5 m tall. Dim warm
  lighting (a couple of point/spot lights, low intensity, warm color temp —
  "basement", not "showroom"). Primitive geometry + simple materials, no need
  for anything fancier.

**Litterboxes** — 3x, along one wall:
- Open-top box look: a shallow cube (~40x30 cm footprint, ~15 cm tall) with a
  slightly larger flat "rim" cube around the top edge, sand-colored material
  on the top face/fill. Space them along the wall with a bit of gap.
- These define the "problem zone" the cats squat near — see CatAgent wiring
  below.

**Camera mount**:
- Empty GameObject in the corner opposite the litterboxes, ~2-2.2 m up,
  angled down to frame the boxes + the floor area in front of them (like a
  real security cam).
- Add a `Camera` component. Tag it `MainCamera` (CatAgent's hotkey-P "nearest
  cat" logic uses `Camera.main` as its reference point — if nothing is tagged
  MainCamera it falls back to world origin, which still works for a 6x4 m
  room but tagging the security cam is the correct match to the design intent).
- Add `FrameStreamer` (from `Assets/WorldSim/Core/FrameStreamer.cs`). Leave
  defaults (port 8089, q70, 12 fps) unless you have a reason not to — they're
  the contract values. Resolution (1280x720) is fixed in code, not an
  Inspector field, so there's nothing to set there.

**Turret mount**:
- Empty GameObject near the ceiling, roughly overlooking the litterboxes.
- Add an `AudioSource` (required component) — set a chirp `AudioClip` if you
  have one handy, otherwise leave it null (VirtualTurret null-checks it and
  just skips the sound).
- Add two `ParticleSystem` children (or reuse one for both, your call):
  one for the "sound" ring effect, one for the "air" puff. Simple built-in
  particle shapes are fine — a flat expanding ring for sound, a white burst
  for air.
- Add `VirtualTurret` (from `Assets/WorldSim/Scenarios/CatBasement/VirtualTurret.cs`).
  Wire `ringEffect` / `puffEffect` / `chirpClip` in the inspector. Set
  `floorReference` to the Floor object (its `localScale` is used to map
  normalized `target` coords onto the room footprint — rough, phase-2 does
  real aiming, see issue #7).

**MQTT bridge**:
- One GameObject (e.g. "Managers" or directly on the turret mount) with
  `MqttBridge` (from `Assets/WorldSim/Core/MqttBridge.cs`). Defaults
  (`localhost:1883`) match the contract. `VirtualTurret.bridge` auto-finds it
  via `FindFirstObjectByType` if left unassigned, but wiring it explicitly in
  the inspector is one click and avoids relying on that fallback.

**Cats** — 3x capsule (or a free low-poly cat model if you grab one trivially;
don't go hunting for one):
- Capsule scaled to roughly 45 cm long x 25 cm tall, one each in black,
  orange, gray albedo (just different `Material` colors — future per-cat-ID
  work reads these, nothing more is needed now).
- Each needs: `NavMeshAgent`, `WanderAgent` (from
  `Assets/WorldSim/Core/Agents/WanderAgent.cs`), `CatAgent` (from
  `Assets/WorldSim/Scenarios/CatBasement/CatAgent.cs`).
- `WanderAgent.boundsCenter` / `boundsExtents` → roughly the room's walkable
  floor area (so cats don't try to wander into walls — NavMesh will clip them
  back onto the mesh regardless, but tighter bounds means less time spent
  pathing at edges).
- `CatAgent.problemZoneCenter` → an empty Transform placed just in front of
  the litterboxes (1-2 m off, per design.md). `CatAgent.farCorner` → an empty
  Transform in the corner diagonally opposite the boxes (flee target).
- Defaults for trigger/squat timing (60-120 s random trigger, 8 s squat) match
  the spec — no need to touch unless testing faster cycles.

**NavMesh**:
- `Window → AI → Navigation` (or the Unity 6 equivalent under `AI Navigation`
  package — install it from Package Manager if the menu isn't there).
- Mark the floor as `Navigation Static`, leave walls/litterboxes as obstacles
  (or also static, so the mesh routes around them).
- Bake. Confirm the walkable area covers the room floor including in front of
  the litterboxes.

## 6. mosquitto (test machine)

```
# Windows (choco)
choco install mosquitto

# or just run the default config, no auth needed (matches the contract):
mosquitto -v
```
Confirm it's listening on `1883` (default). No config file changes needed —
contract is explicitly "no auth (LAN/tailnet only)".

## 7. Press-Play acceptance checklist

Straight from `docs/design.md` criteria 1-7:

1. [ ] Open project in Unity 6, press Play — no console errors, basement scene runs.
2. [ ] `http://localhost:8089/stream` plays live in VLC/browser; `/snapshot` returns a JPEG.
3. [ ] 3 cats wander; pressing **P** makes one walk beside the boxes and squat ~8 s.
4. [ ] With mosquitto running: `mosquitto_sub -t 'catsentry/#' -v` shows `catsentry/telemetry` heartbeats every ~10 s.
5. [ ] `mosquitto_pub -t catsentry/deterrent/fire -m '{"ts":"2026-08-19T00:00:00Z","level":"sound","target":[0.5,0.5],"duration_ms":1000,"reason":"manual test"}'`
       → chirp + ring in sim, `catsentry/deterrent/ack` published, a squatting cat flees (fire while one is mid-squat, or use hotkey P then fire quickly).
6. [ ] Same with `"level":"air"` → puff particle + ack.
7. [ ] Streaming + MQTT survive 10+ minutes without leak/disconnect; stop mosquitto and restart it mid-run to confirm MqttBridge reconnects and heartbeats resume.

## Known first-import friction (see also PR/commit notes)

- `http://+:8089/` binding can throw `HttpListenerException` (access denied)
  if the editor isn't running elevated and no URL ACL is reserved.
  `FrameStreamer` catches this and falls back to `http://localhost:8089/`
  automatically (logged as a warning) — stream still works locally, but
  something on another machine (e.g. the real cat-sentry detector) won't
  reach it until you either run Unity as Administrator once, or reserve the
  ACL yourself:
  ```
  netsh http add urlacl url=http://+:8089/ user=Everyone
  ```
  Also check Windows Firewall allows inbound TCP 8089 if testing from another
  device on the tailnet/LAN.
- `AsyncGPUReadback` failing on a given GPU/driver combo isn't a compile
  error, it's a runtime fallback — `FrameStreamer` will log a warning and
  switch to `Texture2D.ReadPixels` automatically. If you see that warning
  immediately on Play, it's not a bug to chase, it's the designed fallback
  path working.
