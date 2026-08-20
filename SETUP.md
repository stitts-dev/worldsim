# SETUP — bringing the sim up

The whole project is authored as text, including the scene. Scene assembly, URP
setup and the NavMesh bake are done by an editor script
(`Assets/Editor/CatBasementSceneBuilder.cs`) rather than by hand, so a fresh
clone reaches a runnable scene with one command.

Read `docs/design.md` and `docs/contract-catsentry-v1.md` first if anything
here is ambiguous — both are self-contained specs.

## 1. Unity 6 LTS

```bash
brew install --cask unity-hub
```

Then install the editor this project is pinned to
(`ProjectSettings/ProjectVersion.txt` — currently **6000.0.82f1**):

```bash
"/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" -- --headless install --version 6000.0.82f1 --architecture arm64
```

## 2. Activate a Unity licence

**Required before anything below runs.** Batch mode fails with `No valid Unity
Editor license found. Please activate your license.` until one exists. Personal
is free and sufficient here.

Open Unity Hub, sign in to a Unity account, and accept the Personal licence
under `Preferences → Licenses`. Hub writes it to
`/Library/Application Support/Unity/licenses/`, and every batch-mode command
below then works. Verify:

```bash
ls /Library/Application\ Support/Unity/licenses/
```

## 3. Build the scene

One batch-mode call resolves packages (URP 17.0.4 from the editor's own bundle,
AI Navigation 2.0.14), compiles, creates the URP pipeline assets, assembles
`CatBasement.unity`, bakes the NavMesh, and saves.

```bash
/Applications/Unity/Hub/Editor/6000.0.82f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath "$PWD" -executeMethod WorldSim.EditorTools.CatBasementSceneBuilder.BuildFromBatch -quit -logFile build.log
```

Look for `CatBasementSceneBuilder: scene built and saved` in `build.log`. The
script is idempotent — re-run it any time to rebuild the scene from source.
Inside the editor the same thing is on the menu: **WorldSim → Build CatBasement
Scene**.

### What it builds

- **Room** — 6 x 4 m floor, four walls, dim warm point lights, no skybox.
- **Litterboxes** — three along the north wall, each a shell plus sand-colored
  fill, marked non-walkable so cats route around them.
- **Security camera** — corner opposite the boxes, ~2.15 m up, angled down over
  the boxes and the floor in front of them. Tagged `MainCamera`, carries
  `FrameStreamer` at the contract defaults (port 8089, 1280x720, q70, 12 fps).
- **Turret** — near the ceiling overlooking the boxes, with an `AudioSource`
  (procedural `Audio/chirp.wav`), a ring particle system for `sound`, a puff for
  `air`, and `VirtualTurret` wired to the bridge and the floor.
- **Managers** — `MqttBridge`, pointed at localhost:1883.
- **Cats** — three capsules (black, orange, gray) with `NavMeshAgent`,
  `WanderAgent` bounded to the walkable floor, and `CatAgent` pointed at a
  problem-zone marker in front of the boxes and a far-corner flee target.

## 4. mosquitto

```bash
brew install mosquitto
```

Run it in the foreground — no config needed, the contract is explicitly "no
auth (LAN/tailnet only)":

```bash
/opt/homebrew/opt/mosquitto/sbin/mosquitto -p 1883 -v
```

## 5. Press Play, then run the acceptance harness

Open the project, open
`Assets/WorldSim/Scenarios/CatBasement/CatBasement.unity`, press Play.

`tools/acceptance.py` checks design.md criteria 2, 4, 5 and 6 from outside
Unity, over exactly the surface the real cat-sentry detector uses. No
third-party packages — MJPEG over `http.client`, MQTT over a small built-in
client.

```bash
python3 tools/acceptance.py
```

Add the criterion-7 soak for the long run:

```bash
python3 tools/acceptance.py --soak 1800
```

The remaining criteria are visual and stay manual:

1. [ ] Play with no console errors, basement scene runs (criterion 1).
2. [ ] Three cats wander; pressing **P** sends the nearest one to squat beside
       the boxes for ~8 s (criterion 3).
3. [ ] Fire while a cat is mid-squat — hotkey **P**, then run the harness or
       `mosquitto_pub` — and confirm it cancels the squat and flees (part of
       criteria 5 and 6).

To poke by hand instead:

```bash
mosquitto_sub -t 'catsentry/#' -v
```

```bash
mosquitto_pub -t catsentry/deterrent/fire -m '{"ts":"2026-08-19T00:00:00Z","level":"sound","target":[0.5,0.5],"duration_ms":1000,"reason":"manual test"}'
```

## Notes on the build

- **No NuGet, no MQTT package.** `MqttClientLite.cs` is a ~200-line MQTT 3.1.1
  client (connect, subscribe, publish QoS 0/1, ping). M2Mqtt was dropped
  because it ships no netstandard target: its net45 assembly fails to compile
  under Unity's default .NET Standard 2.1 profile (CS0012 on mscorlib 4.0.0.0)
  and only builds when the project's API Compatibility Level is switched to
  .NET Framework — a setting that cannot be applied before Unity's first script
  compile, which leaves a fresh clone unbuildable without hand intervention.
  `docs/design.md` leaves the client to the implementer, "smallest working
  option wins". Same reasoning as the hand-rolled MJPEG server.
- **`http://+:8089/` binding** can be refused without elevation or a URL ACL.
  `FrameStreamer` catches that and falls back to `http://localhost:8089/` with a
  warning — the stream still works locally, but another machine (the real
  detector) will not reach it. macOS generally binds fine; on Windows reserve
  the ACL once:
  ```
  netsh http add urlacl url=http://+:8089/ user=Everyone
  ```
  Check the firewall allows inbound TCP 8089 when testing across the tailnet.
- **`AsyncGPUReadback` warnings on Play** are not a bug to chase.
  `FrameStreamer` logs one and switches to `Texture2D.ReadPixels` — the designed
  fallback working.
- **NavMesh bake agent size** is the default Humanoid (radius 0.5), which erodes
  the walkable area to roughly 5 x 3 m inside the 6 x 4 m room. `WanderAgent`
  bounds sit inside that on purpose, and the cats' squat spot in front of the
  boxes lands within it.
