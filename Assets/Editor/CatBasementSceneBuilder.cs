using System;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using WorldSim.CatBasement;
using WorldSim.Core;
using WorldSim.Core.Agents;

namespace WorldSim.EditorTools
{
    /// <summary>
    /// Programmatic assembly of the CatBasement scene per SETUP.md sections 3-5:
    /// URP setup, room geometry, litterboxes, security camera + FrameStreamer,
    /// turret + VirtualTurret, MqttBridge, three cats, NavMesh bake. Idempotent:
    /// rebuilding overwrites the scene file and regenerates its assets.
    ///
    /// Menu: WorldSim > Build CatBasement Scene
    /// Batch: Unity -batchmode -executeMethod WorldSim.EditorTools.CatBasementSceneBuilder.BuildFromBatch -quit
    /// </summary>
    public static class CatBasementSceneBuilder
    {
        const string ScenarioDir = "Assets/WorldSim/Scenarios/CatBasement";
        const string ScenePath = ScenarioDir + "/CatBasement.unity";
        const string SettingsDir = "Assets/Settings";
        const string ChirpPath = ScenarioDir + "/Audio/chirp.wav";

        // Room footprint (meters). Floor is a flattened cube so its localScale
        // spans the room exactly - VirtualTurret.NormalizedToWorld reads it.
        const float RoomX = 6f, RoomZ = 4f, WallH = 2.5f, WallT = 0.1f;

        [MenuItem("WorldSim/Build CatBasement Scene")]
        public static void BuildAll()
        {
            EnsureProjectSettings();
            EnsureUrp();
            BuildScene();
            Debug.Log("CatBasementSceneBuilder: scene built and saved at " + ScenePath);
        }

        public static void BuildFromBatch()
        {
            try
            {
                BuildAll();
            }
            catch (Exception e)
            {
                Debug.LogError("CatBasementSceneBuilder: FAILED - " + e);
                EditorApplication.Exit(1);
            }
        }

        // ---- project settings ----

        static void EnsureProjectSettings()
        {
            // Without this the player loop is throttled whenever the editor loses
            // focus, and FrameStreamer's Update-driven capture collapses to ~1 fps
            // - far under the contract's 10-15. The whole point is to keep serving
            // while you are looking at VLC or the detector, not at Unity.
            if (!PlayerSettings.runInBackground)
            {
                PlayerSettings.runInBackground = true;
                AssetDatabase.SaveAssets();
            }
        }

        // ---- URP (SETUP.md step 3) ----

        static void EnsureUrp()
        {
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset) return;

            if (!AssetDatabase.IsValidFolder(SettingsDir))
                AssetDatabase.CreateFolder("Assets", "Settings");

            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, SettingsDir + "/URP-Renderer.asset");

            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(pipeline, SettingsDir + "/URP-Pipeline.asset");

            GraphicsSettings.defaultRenderPipeline = pipeline;
            AssetDatabase.SaveAssets();
        }

        // ---- scene (SETUP.md step 5) ----

        static void BuildScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Basement mood: no skybox, dim warm flat ambient.
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.22f, 0.20f, 0.18f);

            var environment = new GameObject("Environment");

            GameObject floor = MakeBox("Floor", environment.transform,
                new Vector3(0f, -0.05f, 0f), new Vector3(RoomX, 0.1f, RoomZ),
                Mat("Floor", new Color(0.42f, 0.40f, 0.38f)));

            // Walls stand on the floor edges.
            Material wallMat = Mat("Wall", new Color(0.55f, 0.52f, 0.48f));
            MakeBox("Wall_North", environment.transform, new Vector3(0f, WallH / 2f, RoomZ / 2f + WallT / 2f), new Vector3(RoomX + WallT * 2f, WallH, WallT), wallMat);
            MakeBox("Wall_South", environment.transform, new Vector3(0f, WallH / 2f, -RoomZ / 2f - WallT / 2f), new Vector3(RoomX + WallT * 2f, WallH, WallT), wallMat);
            MakeBox("Wall_East", environment.transform, new Vector3(RoomX / 2f + WallT / 2f, WallH / 2f, 0f), new Vector3(WallT, WallH, RoomZ), wallMat);
            MakeBox("Wall_West", environment.transform, new Vector3(-RoomX / 2f - WallT / 2f, WallH / 2f, 0f), new Vector3(WallT, WallH, RoomZ), wallMat);

            // Litterboxes along the north wall: shallow dark box + sand-colored fill.
            Material boxMat = Mat("Litterbox", new Color(0.25f, 0.27f, 0.30f));
            Material sandMat = Mat("Litter", new Color(0.76f, 0.70f, 0.55f));
            float boxZ = RoomZ / 2f - 0.25f;
            var litterboxes = new Transform[3];
            for (int i = 0; i < 3; i++)
            {
                float x = (i - 1) * 0.7f;
                var box = new GameObject("Litterbox_" + (char)('A' + i));
                box.transform.SetParent(environment.transform);
                box.transform.position = new Vector3(x, 0f, boxZ);
                litterboxes[i] = box.transform;
                MakeBox("Shell", box.transform, new Vector3(x, 0.075f, boxZ), new Vector3(0.4f, 0.15f, 0.3f), boxMat);
                MakeBox("Litter", box.transform, new Vector3(x, 0.155f, boxZ), new Vector3(0.36f, 0.02f, 0.26f), sandMat);
                var mod = box.AddComponent<NavMeshModifier>();
                mod.overrideArea = true;
                mod.area = 1; // Not Walkable - cats route around, never over
            }

            LightingRigBuilder.BuildLighting(environment.transform, RoomX, RoomZ, WallH);

            // Security camera: corner opposite the boxes, high, angled down.
            // FrameStreamer takes this camera's targetTexture for the MJPEG feed,
            // which stops it drawing to the screen - so a second camera at the same
            // pose renders the Game view. Without it, Play mode is a black screen
            // reading "No cameras rendering" even though the stream is fine.
            // Aimed between the boxes and the problem zone in front of them, with
            // the FOV tight enough that the top of frame stays below the wall line
            // - a wide angle here wastes a third of every frame on blank wall and
            // background, which is exactly the region the detector's zones need.
            // Framed on the litterboxes and the floor immediately in front of them
            // - the region the detector's zones actually cover - not on the whole
            // room. Covering every walkable metre from a corner needs a wide angle,
            // which fills half the frame with wall and shrinks the cats to a few
            // pixels. A real litterbox camera frames the boxes.
            var camPos = new Vector3(-1.9f, 2.3f, -1.8f);
            var camLookAt = new Vector3(0.15f, 0.1f, 1.05f);

            var camGo = new GameObject("SecurityCamera");
            camGo.transform.position = camPos;
            camGo.transform.LookAt(camLookAt);
            Camera streamCam = camGo.AddComponent<Camera>();
            ConfigureCamera(streamCam);
            camGo.AddComponent<FrameStreamer>(); // defaults are the contract values

            // Display-only twin. Tagged MainCamera because CatAgent's hotkey-P
            // "nearest cat" logic reads Camera.main; identical pose, so it means
            // the same thing as the security camera the design intends.
            var viewGo = new GameObject("DisplayCamera") { tag = "MainCamera" };
            viewGo.transform.position = camPos;
            viewGo.transform.LookAt(camLookAt);
            Camera displayCam = viewGo.AddComponent<Camera>();
            ConfigureCamera(displayCam);
            viewGo.AddComponent<AudioListener>();

            // LightingRigBuilder.FrameCamera is deliberately not used: it solves for
            // full walkable-floor coverage and puts the camera on the far side of
            // the room from the boxes, which frames mostly wall. The tuned pose
            // above is verified against captured frames instead.

            // Managers: MQTT bridge. It marshals broker callbacks onto the main
            // thread itself via the SynchronizationContext it captures in Awake,
            // so there is no dispatcher component to place.
            var managers = new GameObject("Managers");
            var bridge = managers.AddComponent<MqttBridge>();

            // Control surface for an external test harness (pigame's unity engine):
            // step/reset/inspect/capture on its own port. Its own root object, not a
            // component on Managers: it survives the scene reload that /reset performs,
            // and the scene's copy arriving as a duplicate has to be destroyable
            // without taking the MQTT bridge down with it.
            var controlGo = new GameObject("SimControl");
            var control = controlGo.AddComponent<SimControl>();
            control.captureCamera = streamCam;

            // Decision feed, rendered as world-space text parented to the streaming
            // camera so it lands in the MJPEG frames themselves. IMGUI and
            // screen-space canvases do not appear in a RenderTexture, which is why
            // VirtualTurret's OnGUI readout is invisible to anyone watching the feed.
            var feed = managers.AddComponent<EventFeed>();
            feed.bridge = bridge;
            feed.floorReference = floor.transform;
            feed.maxLines = 7;
            feed.panelWidthFraction = 0.30f;
            // CatAgent already publishes every transition to catsentry/groundtruth,
            // so the local reflection-based poll would print each behaviour a second
            // time. The MQTT path is the one worth keeping: it also proves the topic
            // is actually being published.
            feed.watchCats = false;

            // Reference points for the cats.
            var problemZone = new GameObject("ProblemZoneCenter").transform;
            problemZone.position = new Vector3(0f, 0f, 0.6f); // ~1.2 m in front of the boxes
            var farCorner = new GameObject("FarCorner").transform;
            farCorner.position = new Vector3(-2.4f, 0f, -1.5f);

            BuildTurret(bridge, floor.transform);

            BuildCat("Cat_Black", new Color(0.08f, 0.08f, 0.08f), new Vector3(-1.5f, 0f, -0.8f), problemZone, farCorner, litterboxes, bridge);
            BuildCat("Cat_Orange", new Color(0.85f, 0.45f, 0.12f), new Vector3(0.5f, 0f, -1.0f), problemZone, farCorner, litterboxes, bridge);
            BuildCat("Cat_Gray", new Color(0.5f, 0.5f, 0.52f), new Vector3(1.8f, 0f, 0.2f), problemZone, farCorner, litterboxes, bridge);

            // Props and surfaces go in before the bake so anything standing on the
            // floor is carved out of the walkable mesh rather than walked through.
            RoomDressingBuilder.Dress(environment.transform, RoomX, RoomZ, WallH);

            BakeNavMesh(environment);

            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        static void BuildTurret(MqttBridge bridge, Transform floor)
        {
            var turret = new GameObject("VirtualTurret");
            // Wall-mounted above the litterboxes, low enough to sit inside the
            // camera's frame - the deterrent device should be visible in the feed,
            // not hidden above the top edge where nobody watching can see it fire.
            turret.transform.position = new Vector3(0f, 1.62f, 1.88f);

            Material turretMat = Mat("Turret", new Color(0.18f, 0.18f, 0.20f));
            MakeBox("Bracket", turret.transform, turret.transform.position + new Vector3(0f, 0.07f, 0.06f),
                new Vector3(0.06f, 0.14f, 0.06f), turretMat);
            MakeBox("Housing", turret.transform, turret.transform.position, new Vector3(0.22f, 0.12f, 0.14f), turretMat);
            // Nozzle points down-forward at the floor in front of the boxes.
            var nozzle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            nozzle.name = "Nozzle";
            UnityEngine.Object.DestroyImmediate(nozzle.GetComponent<Collider>());
            nozzle.transform.SetParent(turret.transform);
            nozzle.transform.position = turret.transform.position + new Vector3(0f, -0.04f, -0.10f);
            nozzle.transform.rotation = Quaternion.Euler(58f, 0f, 0f);
            nozzle.transform.localScale = new Vector3(0.045f, 0.07f, 0.045f);
            nozzle.GetComponent<MeshRenderer>().sharedMaterial = turretMat;
            // Status lamp so the device is findable in frame at a glance.
            var lamp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lamp.name = "StatusLamp";
            UnityEngine.Object.DestroyImmediate(lamp.GetComponent<Collider>());
            lamp.transform.SetParent(turret.transform);
            lamp.transform.position = turret.transform.position + new Vector3(0.08f, 0.035f, -0.07f);
            lamp.transform.localScale = new Vector3(0.028f, 0.028f, 0.028f);
            lamp.GetComponent<MeshRenderer>().sharedMaterial = Mat("TurretLamp", new Color(0.9f, 0.25f, 0.15f));

            var audio = turret.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 0f; // audible regardless of listener distance

            ParticleSystem ring = MakeParticles("RingEffect", turret.transform, ringShape: true,
                new Color(1f, 0.75f, 0.2f, 0.9f));
            ParticleSystem puff = MakeParticles("PuffEffect", turret.transform, ringShape: false,
                new Color(1f, 1f, 1f, 0.9f));

            var vt = turret.AddComponent<VirtualTurret>();
            vt.bridge = bridge;
            vt.chirpClip = AssetDatabase.LoadAssetAtPath<AudioClip>(ChirpPath);
            vt.ringEffect = ring;
            vt.puffEffect = puff;
            vt.floorReference = floor;
            if (vt.chirpClip == null)
                Debug.LogWarning("CatBasementSceneBuilder: " + ChirpPath + " not found - turret will fire silently.");
        }

        static void BuildCat(string name, Color color, Vector3 pos, Transform problemZone, Transform farCorner,
            Transform[] litterboxes, MqttBridge bridge)
        {
            var cat = new GameObject(name);
            cat.transform.position = pos;

            // The visual hierarchy hangs off the root so CatAgent's squat, which
            // scales the root's localScale.y, compresses the cat's height rather
            // than its body length.
            CatVisualBuilder.BuildCatVisual(cat.transform, color, name);

            var nav = cat.AddComponent<NavMeshAgent>();
            nav.radius = 0.15f;
            nav.height = 0.3f;
            nav.stoppingDistance = 0.1f;

            var wander = cat.AddComponent<WanderAgent>();
            wander.boundsCenter = new Vector3(0f, 0f, -0.3f);
            wander.boundsExtents = new Vector3(2.2f, 0f, 1.3f);

            var agent = cat.AddComponent<CatAgent>();
            agent.problemZoneCenter = problemZone;
            agent.farCorner = farCorner;
            // Correct litterbox use is what the detector must NOT fire on, so the
            // cats need to know where the boxes actually are.
            agent.litterboxes = litterboxes;
            agent.bridge = bridge;
        }

        static void BakeNavMesh(GameObject environment)
        {
            var surface = environment.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.BuildNavMesh();

            // CreateAsset overwrites in place and keeps the existing GUID, so
            // rebuilds don't churn the .meta or break references to the bake.
            string navPath = ScenarioDir + "/CatBasement-NavMesh.asset";
            AssetDatabase.CreateAsset(surface.navMeshData, navPath);
            AssetDatabase.SaveAssets();
        }

        // ---- helpers ----

        static void ConfigureCamera(Camera cam)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.06f);
            cam.fieldOfView = 52f;
        }

        static GameObject MakeBox(string name, Transform parent, Vector3 pos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent);
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static void MakeLight(string name, LightType type, Vector3 pos, Color color, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.position = pos;
            var light = go.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.Soft;
        }

        static Material Mat(string name, Color color)
        {
            string dir = ScenarioDir + "/Materials";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder(ScenarioDir, "Materials");

            string path = dir + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static ParticleSystem MakeParticles(string name, Transform parent, bool ringShape, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.localPosition = Vector3.zero;
            // Circle shape emits in its local XY plane; tilt so the ring expands
            // horizontally across the floor.
            if (ringShape) go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.duration = 1f;
            main.startLifetime = ringShape ? 0.6f : 0.8f;
            main.startSpeed = ringShape ? 3f : 1.2f;
            main.startSize = ringShape ? 0.07f : 0.14f;
            main.startColor = color;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, ringShape ? (short)60 : (short)40) });

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ringShape ? ParticleSystemShapeType.Circle : ParticleSystemShapeType.Sphere;
            shape.radius = 0.05f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            Shader particleShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (particleShader == null) particleShader = Shader.Find("Sprites/Default");
            string dir = ScenarioDir + "/Materials";
            if (!AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder(ScenarioDir, "Materials");
            string matPath = dir + "/Particles_" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(particleShader);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            renderer.sharedMaterial = mat;

            return ps;
        }
    }
}
