# Unity Project Context

<!-- unity-onboarding:generated:start -->

## Project Summary

- Project root: repository root
- Purpose: reusable Unity simulation harness for external robotics and AI services
- Last analyzed: 2026-08-20
- Last analyzed commit: `57dc140` (context updated with the subsequent Pi Unity harness change)

## Confirmed Environment

- Unity version: 6000.0.82f1, revision `2fb0dae735e1`
- Render pipeline: Universal Render Pipeline 17.0.4
- Input system: legacy Input Manager (`Input.GetKeyDown`); Input System package absent
- Confirmed host: macOS with Metal; other target platforms are not configured

## Important Packages And Frameworks

| Area | Finding | Confidence | Evidence |
| --- | --- | --- | --- |
| Navigation | AI Navigation 2.0.14 with a baked CatBasement NavMesh | Confirmed | `Packages/manifest.json`, `CatBasementSceneBuilder.cs` |
| Rendering | URP 17.0.4 | Confirmed | `Packages/manifest.json`, `Assets/Settings/` |
| Messaging | Project-owned MQTT 3.1.1 client; no third-party MQTT package | Confirmed | `MqttClientLite.cs`, `MqttBridge.cs` |
| Tests | Unity Test Framework is transitive only; no first-party test assembly | Confirmed | `Packages/packages-lock.json`, `Assets/**/*.asmdef` |

## Directory Structure

| Path | Purpose | Confidence | Evidence |
| --- | --- | --- | --- |
| `Assets/WorldSim/Core/` | Scenario-independent streaming, MQTT, agents, event feed, and sim control | Confirmed | Runtime scripts and `WorldSim.Core.asmdef` |
| `Assets/WorldSim/Scenarios/CatBasement/` | CatBasement behavior, scene, NavMesh, materials, meshes, textures, and audio | Confirmed | Scenario assets and `WorldSim.CatBasement.asmdef` |
| `Assets/Editor/` | Idempotent scene and generated-asset builders plus unattended Play startup | Confirmed | Editor scripts |
| `tools/` | External Pi Unity, MJPEG, MQTT, and unattended acceptance harnesses | Confirmed | Python and shell tools |

## Assembly Boundaries

| Assembly | Responsibility | Key references | Notes |
| --- | --- | --- | --- |
| `WorldSim.Core` | Reusable simulation transport and control | Unity modules only | Scenario-agnostic runtime assembly |
| `WorldSim.CatBasement` | Cat and turret scenario behavior | `WorldSim.Core` | Runtime scenario assembly |
| Assembly-CSharp-Editor | Scene and visual builders | Core, CatBasement, UnityEditor | Editor-only by folder convention |

## Scenes And Startup Flow

- Build scenes: none enabled in `ProjectSettings/EditorBuildSettings.asset`
- Saved scenario: `tools/scenarios/cat-basement-smoke.json` selects `Assets/WorldSim/Scenarios/CatBasement/CatBasement.unity`
- Scene loading: `run-pi-unity.sh` reads the saved scene and passes it as `-scene`; `AutoPlay` opens that asset with `-autoplay`; `SimControl /reset` reloads the active scene
- Authoring flow: `CatBasementSceneBuilder` rebuilds and saves the scene from source

## Architecture

| Pattern | Finding | Confidence | Evidence |
| --- | --- | --- | --- |
| Runtime composition | MonoBehaviours are wired by the editor scene builder | Confirmed | `CatBasementSceneBuilder.cs` |
| External engine contract | HTTP reset/step/inspect/capture is separate from detector-facing MJPEG/MQTT | Confirmed | `SimControl.cs`, `FrameStreamer.cs`, `MqttBridge.cs` |
| Scenario inspection | Scenario entities implement `ISimInspectable`; Core emits stable ordered JSON | Confirmed | `SimControl.cs`, `CatAgent.cs` |
| Deterministic replay | Seeded scene reload plus fixed 1/60 simulation stepping | Confirmed | `SimControl.cs`, `cat-basement-smoke.json` |

## Coding Conventions

- Namespace style: `WorldSim.Core`, `WorldSim.Core.Agents`, `WorldSim.CatBasement`, `WorldSim.EditorTools`
- Serialized fields: public fields are used for Inspector wiring in existing runtime code
- Async/threading: background HTTP and MQTT work is marshalled to Unity's main thread
- Comments/docs: XML summaries and comments explain lifecycle and protocol constraints

## Testing And Validation

- EditMode tests: none
- PlayMode tests: no Unity Test Framework suite; external live integration test in `tools/test_pi_unity.py`
- Prototype replay: `tools/run-pi-unity.sh <scenario.json>` launches the scenario's scene, runs deterministic Pi Unity, captures evidence, and closes Unity
- Acceptance: `tools/run-acceptance.sh` launches the saved CatBasement scene and runs deterministic Pi Unity plus MJPEG/MQTT checks
- Long run: `tools/acceptance.py --soak <seconds>` checks stream and heartbeat survival

## Available Unity Tooling

| Capability | Status | Evidence |
| --- | --- | --- |
| Repository and package inspection | available | Local Codex workspace |
| Unity MCP Editor tools | unavailable | No MCP package/configuration or callable Unity MCP tools detected |
| Live engine health/state/capture | available | `SimControl` on port 8090 |
| Live MJPEG/MQTT acceptance | available | `FrameStreamer`, mosquitto, external harness |

## Important Constraints

- Keep scenario-specific behavior out of `WorldSim.Core`.
- Preserve the frozen catsentry-v1 topics and payload fields.
- Do not add a Unity MCP or package unless Editor-only automation is needed and explicitly approved.
- `SimControl` must survive scene reload and perform all Unity API work on the main thread.
- Captured scene pixels can include network-timed `EventFeed` text; state JSON is the deterministic gate.
- **Never time the sim against the wall clock.** `Time.captureDeltaTime` is pinned, so the
  sim advances at whatever rate the editor renders - 5.08x real time on one machine, and
  machine-dependent. Measure anything about entity motion (speed, duration) against
  `simTime` from `/inspect`. The one exception is frame delivery: `FrameStreamer` throttles
  on unscaled time and the contract counts real frames per real second, so the acceptance
  harness's fps check is right to use the wall clock.
- **A capture cannot tell you whether the cats are moving.** Render-gate calibration measured
  40 frames of cat movement as scoring 0 against a frozen frame - identical. Assert behaviour
  against `/inspect` JSON; captures guard rendering only.
- **A deterrent only makes a cat flee if `IsSquatting` is true** (`VirtualTurret.NotifySquattingCats`).
  Any test of the flee path must wait for a cat to be squatting before publishing the fire,
  or it gets a green that proves nothing.
- **Looking at a capture proves a cue is PRESENT, not that it is the right SIZE.** At this
  camera's framing a cat is roughly 100 px, and a visual check passed a sniff whose nose-down
  was a third of its intended amplitude - 3.2 cm of drop from head pitch alone where head plus
  body gives ~8.3 cm. It read as plausibly nose-down. If a pose has a target magnitude, check
  it against the geometry or against `/inspect`, and treat "I looked at it and it seemed fine"
  as evidence of presence only.

### The pattern behind all of these

Every trap above is the same shape: **the instrument was broken and the system looked fine.**
In one session, acceptance tests passed with nothing listening on the port, an fps check passed
numbers outside the band it printed, a wall-clock measurement reported cats at twice their
maximum speed, and a visual check passed a third-strength animation cue. In all four the code
under test was healthy and the thing measuring it was not.

When a measurement says something surprising, suspect the measurement first. Before acting on
a number, be able to say what would make it wrong - and prefer a check you have seen FAIL on a
known-bad input over one you have only ever seen pass.

## Known Flakes

### The editor sometimes never enters Play mode (~5 occurrences, no root cause)

`tools/run-acceptance.sh` and manual launches intermittently sit until the 300 s
boot timeout with nothing serving on 8089 or 8090. It has happened both in
isolation and twice consecutively. It clears on retry with no code change, so it
is not a compile or scene failure.

What is ruled out:

- **The AI-package gotcha is NOT the cause.** `com.unity.ai.assistant` and
  `com.unity.ai.inference` were absent from `Packages/manifest.json` on every
  observed occurrence. (That gotcha is real and separate: Unity re-adds those
  packages on its own, they need entitlements this account lacks, and their retry
  loop starves AutoPlay. Strip them if they reappear.)
- Not a compile error - `-quit` batchmode compiles clean immediately before and
  after a hang.

Evidence captured from one occurrence, which is the useful part:

- The editor loads the project normally (`Loading completed in 6.1 seconds`),
  opens the scene, launches `UnityShaderCompiler`, creates the GICache - and then
  logs `Application is shutting down...` and `ALC ILPP context 1 is unloading`.
- **`AutoPlay` never logs at all** - zero matches for "AutoPlay" in the whole
  log, where a healthy boot has 3. So the editor is dying (or being told to quit)
  before `AutoPlay`'s static constructor runs, rather than AutoPlay failing.
- A stale `Temp/UnityLockfile` was present. Deleting it and relaunching worked
  first time. **One instance only - not established as causal**, and the lockfile
  may simply be normal. Worth checking next time before deleting it.

What would settle it, and is still missing: the tail of the editor log at the
moment of a hang, compared against a healthy boot's log at the same phase,
plus whether `Temp/UnityLockfile` is present on a hang but absent on a good
boot. If you hit this, capture that before retrying - a retry destroys the
evidence, and it has now cost five boots' worth of time precisely because
everyone (including me) retried first and looked second.

Do not treat a single 300 s timeout as a product failure. `run-acceptance.sh`
reports it as `INFRA:` and exits 3 for exactly this reason.

## Unknowns And Confidence

- No Unity MCP is available for direct Console or hierarchy inspection; Editor log and runtime endpoints are the current evidence sources.
- Build/player readiness is unknown because Build Settings contains no enabled scenes.
- A second Unity scenario has not yet proved cross-scenario scene selection or asset reuse.

## Source Files Inspected

- `README.md`, `SETUP.md`, `docs/design.md`, `docs/contract-catsentry-v1.md`
- `ProjectSettings/ProjectVersion.txt`, `ProjectSettings/EditorBuildSettings.asset`
- `Packages/manifest.json`, `Packages/packages-lock.json`
- Core, CatBasement, editor-builder, and external harness sources
- Live `/doctor`, `/inspect`, `/capture`, MJPEG snapshot, Unity log, and process state

<!-- unity-onboarding:generated:end -->
