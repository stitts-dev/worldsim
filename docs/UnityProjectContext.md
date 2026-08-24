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
