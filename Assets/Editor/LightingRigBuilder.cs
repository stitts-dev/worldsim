using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WorldSim.EditorTools
{
    /// <summary>
    /// Lighting rig and security-camera composition for the CatBasement scene.
    /// Called by CatBasementSceneBuilder; owns nothing else in the scene.
    ///
    ///   BuildLighting(environment, roomX, roomZ, wallHeight)
    ///   FrameCamera(streamCam, displayCam, roomX, roomZ)
    ///
    /// Everything is procedural: Unity primitives, a generated Texture2D, URP Lit
    /// materials. Assets are written under Assets/WorldSim/Scenarios/CatBasement
    /// and reused in place on a rebuild.
    ///
    /// Two problems this replaces:
    ///   1. A bare point light 0.6 m from the +Z wall. URP point attenuation is
    ///      1/d^2, so at d = 0.6 the multiplier is 2.78 - a 0.55-albedo wall goes
    ///      past 1.0 linear and clips to white. Fixed by pointing SPOT lights
    ///      straight down: the wall sits 90 deg off the cone axis and gets nothing
    ///      but the soft outer falloff (see BuildShopLight for the numbers).
    ///   2. A 60 deg FOV aimed near-horizontally, which spent the top third of the
    ///      frame on blank wall/void and cut cats off at the bottom edge. Fixed by
    ///      pitching the camera 51.6 deg down so the top of frame lands on floor,
    ///      and widening just enough to reach the floor under the mount.
    /// </summary>
    public static class LightingRigBuilder
    {
        const string ScenarioDir = "Assets/WorldSim/Scenarios/CatBasement";
        const string MaterialDir = ScenarioDir + "/Materials";
        const string TextureDir = ScenarioDir + "/Textures";

        // The room this rig and the FOV solve were computed against.
        const float DesignRoomX = 6f, DesignRoomZ = 4f, DesignWallH = 2.5f;

        public const string RigRootName = "Lighting";

        // ------------------------------------------------------------------
        //  Camera
        // ------------------------------------------------------------------

        // ---- geometry of the solve (all angles derived, none guessed) ----
        //
        // Room: floor top y = 0, x in [-3,3], z in [-2,2], walls 2.5 m.
        // Litterboxes: x = -0.7/0/+0.7, z = 1.75, footprint 0.40 x 0.30 -> the box
        // strip spans x in [-0.9,0.9], z in [1.6,1.9].
        //
        // REGION THAT MUST BE IN FRAME. WanderAgent roams x in [-2.2,2.2],
        // z in [-1.6,1.0]; CatAgent's flee target is (-2.4,0,-1.5), outside that
        // box. A cat is a 0.45 x 0.25 capsule, so grow every point by its 0.25 m
        // half-length and take the top at y = 0.3. The frustum's intersection with
        // any horizontal plane is convex, so covering the convex hull's vertices
        // covers everything inside:
        //     (-2.65,-1.75) (-2.45,-1.85) (2.45,-1.85) (2.45,1.25)
        //     ( 1.17, 1.90) (-1.17, 1.90) (-2.45,1.25) (-2.65,-1.25)
        // each tested at y = 0 (floor contact) and y = 0.3 (cat top).
        //
        // WHY THE +X/-Z CORNER. With camera height h and bottom-edge depression
        // b = pitch + vFOV/2, the near cut-off is a line perpendicular to the
        // heading at forward distance f = h / tan(b)  [derived: for a floor point
        // at forward distance f, y_cam/z_cam = -tan(v) reduces to f = h/tan(p+v)].
        // f is strictly positive, so a corner camera can never see the floor
        // directly under itself. Whichever -Z corner is chosen loses a wedge in
        // that corner. Putting the camera at -X would bury the flee target
        // (-2.4,0,-1.5) - the payoff shot of the whole demo - inside that wedge,
        // so the camera goes to +X and the flee corner sits 5.6 m away, mid-frame.
        //
        // POSITION. (roomX/2 - 0.25, 2.45, -roomZ/2 + 0.15) = (2.75, 2.45, -1.85).
        // Height is wallHeight - 0.05: as high as possible without poking over the
        // wall (see the top-edge argument below). 0.25/0.15 m of wall clearance
        // keeps the nearest thing the frame border touches at 0.27 m, clear of the
        // 0.05 near plane set below.
        //
        // PITCH AND FOV. Two opposing constraints, solved simultaneously. Write
        // the pitch as p and the half-FOV as v, so the bottom edge sits at
        // depression b = p + v and the top edge at p - v below horizontal:
        //   near: the binding hull vertex is (2.45, 0, -1.85), only 0.247 m
        //         forward of the mount, so f <= 0.247 forces
        //         b >= atan(2.45 / 0.247) = 84.2 deg.
        //   far:  the binding hull vertex is (-2.45, 0.30, 1.25) - a cat's back in
        //         the opposite corner - 6.04 m forward, so the top edge may
        //         descend no more than atan((2.45 - 0.30) / 6.04) = 19.6 deg.
        //   2v >= 84.2 - 19.6 = 64.6 deg is therefore the hard minimum.
        // vFOV = 68 deg is that plus a working margin. Aiming at floor point
        // (1.15, 0, -0.75), 3.13 m down the ray, yields pitch 51.6 deg and yaw
        // -55.5 deg, hence b = 85.6 deg (near cut-off 0.188 m, inside the needed
        // 0.247) and a top edge 17.6 deg down (the plane passes 0.533 m up at
        // 6.04 m, clearing the 0.30 m cat). Verified over all 16 hull points:
        // worst |u|,|w| = 0.949, i.e. nothing closer than 5% to any frame edge.
        //
        // TOP OF FRAME NEVER SHOWS VOID. The top edge is a plane containing the
        // camera's (horizontal) right vector, so every ray in it shares one
        // y-component while its horizontal length grows toward the frame corners:
        // the top-centre ray is the steepest and the top corners approach
        // horizontal from below. With top-centre already 17.6 deg BELOW horizontal
        // the entire top edge points downward, so it can never rise above the
        // 2.45 m mount, let alone the 2.5 m wall line. Traced: the highest point
        // the whole frame border touches is 2.29 m (top-left, on the -Z wall
        // 0.76 m away); top-centre lands on the +Z wall 0.29 m up and top-right on
        // the +Z wall at 1.59 m. Every border ray hits wall or floor - no void.
        //
        // HORIZONTAL. hFOV = 2*atan(tan(34 deg) * 16/9) = 100.35 deg at the
        // stream's 1280x720. Coverage survives down to a 5:4 viewport (worst 0.97)
        // and clips only at 1:1, so a Game view at any normal aspect matches.
        //
        // DETECTOR CHECK. 1280/100.35 = 12.8 px/deg across, 720/68 = 10.6 px/deg
        // down. A cat at the litterboxes (4.9 m) is ~67 x 31 px, at the problem
        // zone (4.3 m) ~76 x 35 px, worst case in the far corner (6.4 m)
        // ~51 x 24 px - all above the ~32 px that COCO-trained YOLO needs.

        const float CamHeight = 2.45f;               // wallHeight (2.5) - 0.05
        const float CamInsetX = 0.25f, CamInsetZ = 0.15f;
        const float LookAtXFrac = 1.15f / 6f;        // 0.191667 of roomX
        const float LookAtZFrac = 0.75f / 4f;        // 0.1875 of roomZ (negated)
        const float VerticalFov = 68f;
        const float NearClip = 0.05f;                // frame border touches wall at 0.27 m
        const float FarClip = 30f;

        /// <summary>
        /// Places both cameras at one identical pose - high in the corner opposite
        /// the litterboxes - and sets the field of view solved above. Either camera
        /// may be null; the other is still configured.
        /// </summary>
        public static void FrameCamera(Camera streamCam, Camera displayCam, float roomX, float roomZ)
        {
            if (streamCam == null && displayCam == null)
            {
                Debug.LogWarning("LightingRigBuilder.FrameCamera: both cameras are null, nothing to frame.");
                return;
            }

            WarnIfOffDesign(roomX, roomZ, DesignWallH);

            Vector3 pos = new Vector3(roomX * 0.5f - CamInsetX, CamHeight, -roomZ * 0.5f + CamInsetZ);
            Vector3 lookAt = new Vector3(roomX * LookAtXFrac, 0f, -roomZ * LookAtZFrac);

            ApplyCameraPose(streamCam, pos, lookAt);
            ApplyCameraPose(displayCam, pos, lookAt);

            Debug.Log(string.Format(
                "LightingRigBuilder: camera at {0} looking at {1}, vFOV {2} deg (hFOV {3:0.0} deg at 16:9).",
                pos, lookAt, VerticalFov,
                2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(VerticalFov * 0.5f * Mathf.Deg2Rad) * 16f / 9f)));
        }

        static void ApplyCameraPose(Camera cam, Vector3 pos, Vector3 lookAt)
        {
            if (cam == null) return;

            cam.transform.position = pos;
            cam.transform.LookAt(lookAt, Vector3.up);

            // fieldOfView is vertical only while the camera is non-physical; a
            // physical camera would reinterpret it through the sensor height.
            cam.usePhysicalProperties = false;
            cam.fieldOfView = VerticalFov;

            // The mount sits 0.25 m off one wall and 0.15 m off another, and the
            // frame border grazes wall 0.27 m out. The 0.3 default would clip that
            // strip away and leave clear-colour void in the frame corner.
            cam.nearClipPlane = NearClip;
            cam.farClipPlane = FarClip;

            // clearFlags, backgroundColor, tag and any attached FrameStreamer /
            // AudioListener belong to the scene builder - left untouched.
        }

        // ------------------------------------------------------------------
        //  Lighting
        // ------------------------------------------------------------------

        // ---- exposure budget ----
        //
        // URP punctual attenuation is  1/d^2 * saturate((1 - (d^2/r^2)^2)^2), and a
        // URP Lit diffuse surface returns  albedo * intensity * atten * NdotL, all
        // in LINEAR space: the sRGB colours written into materials and lights are
        // converted first, so the floor's 0.42 albedo shades as 0.42^2.2 = 0.147
        // and a 0.08 black cat as 0.0038. That factor is why the old rig needed
        // intensities near 1 to blow out a nearby wall yet still left the floor
        // muddy. Ranges are set well past the working distance (8 m for a 2.3 m
        // throw) so the range window stays >= 0.986 rather than quietly eating 10%.
        //
        // Every surface in the room was swept against the two 2.1 spots, the 2.0
        // bulb and this ambient. Result, back in sRGB as the JPEG encoder sees it:
        //   litter under the fixture ......... 0.71  <- brightest thing in frame
        //   floor at the boxes / problem zone  0.40 - 0.41
        //   gray cat at the problem zone ..... 0.53
        //   black cat there .................. 0.08  <- silhouette against 0.41
        //   orange cat, peak (red only) ...... 0.90  green 0.42, blue 0.09
        //   +Z wall behind the boxes, peak ... 0.39  (was clipping to white)
        //   -Z wall nearest the bulb, peak ... 0.59
        //   unlit corners: floor 0.17 / cat .. 0.20 - 0.22
        // Nothing in the room reaches 1.0 on any channel, so there is no clipped
        // highlight anywhere, while the detection area sits at a solid mid-tone
        // and everything outside it stays basement-dark.

        static readonly Color AmbientColor = new Color(0.21f, 0.193f, 0.171f);

        /// <summary>
        /// Builds the basement lighting: a two-lamp shop light over the litterbox
        /// run and a bare bulb mid-room, plus a low warm flat ambient. Also turns
        /// on the URP additional-light and soft-shadow support the fixtures need.
        /// </summary>
        /// <param name="environment">
        /// The scene builder's Environment root. Used only for reporting - the rig
        /// is deliberately NOT parented to it, see below.
        /// </param>
        public static void BuildLighting(Transform environment, float roomX, float roomZ, float wallHeight)
        {
            WarnIfOffDesign(roomX, roomZ, wallHeight);
            EnableUrpPunctualShadows();
            ApplyAmbient();
            WarnAboutExistingLights(environment);

            var existing = GameObject.Find("/" + RigRootName);
            if (existing != null) Object.DestroyImmediate(existing);

            // Scene root, NOT a child of Environment. NavMeshSurface is baked with
            // collectObjects = Children and useGeometry = RenderMeshes, so a
            // 2.2 x 0.28 m horizontal housing parented under Environment would be
            // voxelized into a walkable island at ceiling height that WanderAgent
            // could sample. Colliders are stripped from every primitive too, which
            // covers the PhysicsColliders setting as well.
            var root = new GameObject(RigRootName);
            root.transform.position = Vector3.zero;

            BuildShopLight(root.transform, roomZ, wallHeight);
            BuildBareBulb(root.transform, roomZ, wallHeight);

            AssetDatabase.SaveAssets();
            Debug.Log("LightingRigBuilder: built '" + RigRootName + "' (2 fixtures, 3 lights, ambient "
                      + AmbientColor + ").");
        }

        // ---- fixture 1: shop light over the litterbox run ----

        static void BuildShopLight(Transform root, float roomZ, float wallHeight)
        {
            // The litterboxes are at z = roomZ/2 - 0.25 = 1.75. Hanging the fixture
            // at 1.15 (0.60 m in front of them) centres its pool between the boxes
            // and the problem zone at z = 0.6, instead of over the boxes alone.
            float z = roomZ * 0.5f - 0.85f;
            float housingY = wallHeight - 0.08f;   // 2.42
            float tubeY = wallHeight - 0.135f;     // 2.365
            float lampY = wallHeight - 0.18f;      // 2.32

            var fixture = new GameObject("ShopLight");
            fixture.transform.SetParent(root, false);

            Material metal = LitMaterial("Lighting_Fixture", new Color(0.26f, 0.26f, 0.28f), Color.black, null, 0.35f, 0.6f);
            Material tubeMat = LitMaterial("Lighting_Tube", new Color(0.85f, 0.85f, 0.85f),
                new Color(1.00f, 0.92f, 0.80f) * 1.35f, TubeGradient(), 0.2f, 0f);

            Prim(PrimitiveType.Cube, "Housing", fixture.transform,
                new Vector3(0f, housingY, z), new Vector3(2.20f, 0.07f, 0.30f), Vector3.zero, metal);
            Prim(PrimitiveType.Cube, "EndCap_L", fixture.transform,
                new Vector3(-1.08f, housingY - 0.015f, z), new Vector3(0.08f, 0.11f, 0.32f), Vector3.zero, metal);
            Prim(PrimitiveType.Cube, "EndCap_R", fixture.transform,
                new Vector3(1.08f, housingY - 0.015f, z), new Vector3(0.08f, 0.11f, 0.32f), Vector3.zero, metal);
            Prim(PrimitiveType.Cube, "Tube", fixture.transform,
                new Vector3(0f, tubeY, z), new Vector3(2.00f, 0.05f, 0.12f), Vector3.zero, tubeMat);

            // Short hanger rods up to the (implied) ceiling at wallHeight. Unity's
            // cylinder is 2 units tall, so localScale.y is the half-length.
            float rodTop = wallHeight;
            float rodBottom = housingY + 0.035f;
            float rodHalf = Mathf.Max(0.005f, (rodTop - rodBottom) * 0.5f);
            foreach (float rx in new[] { -0.85f, 0.85f })
                Prim(PrimitiveType.Cylinder, "Hanger", fixture.transform,
                    new Vector3(rx, rodBottom + rodHalf, z), new Vector3(0.02f, rodHalf, 0.02f), Vector3.zero, metal);

            // Two lamps rather than one keeps the pool wide and flat instead of a
            // hotspot: the floor reads 0.39 sRGB directly under a lamp, 0.40 in
            // front of the boxes and 0.41 at the problem zone - a 0.02 spread over
            // the whole detection area, with the peak deliberately landing on the
            // problem zone rather than under the fixture.
            //
            // Straight down (Euler 90,0,0 aims a light's +Z at -Y) is what kills
            // the old blowout. The +Z wall is 0.85 m away but 90 deg off axis at
            // lamp height, so it falls outside the 50 deg half-cone entirely; the
            // cone only reaches the wall below y ~ 1.75, peaking at 0.39 sRGB
            // around y = 0.8. No wall in the room exceeds 0.59.
            foreach (float lx in new[] { -0.55f, 0.55f })
                MakeLamp("Lamp", fixture.transform, LightType.Spot,
                    new Vector3(lx, lampY, z), new Vector3(90f, 0f, 0f),
                    new Color(1.00f, 0.90f, 0.78f),   // ~3400 K warm fluorescent
                    2.1f, 8f, 100f, 45f, LightShadows.Soft, 0.85f);
        }

        // ---- fixture 2: bare bulb on a cord, mid-room ----

        static void BuildBareBulb(Transform root, float roomZ, float wallHeight)
        {
            // Kept 1.3 m off the -Z wall and 2.75 m off the -X wall. A point light
            // is omnidirectional, so wall distance is the only thing standing
            // between it and the blowout the old rig had: at 1.3 m the brightest
            // patch on the -Z wall reads 0.59 sRGB, a warm falloff rather than a
            // blob. Inside ~0.9 m a 0.55-albedo wall starts clipping.
            //
            // This lamp exists to keep the half of the room the shop light cannot
            // reach off the floor of the histogram: without it the near corners sit
            // at ambient alone and a cat there is invisible. With it they read 0.17
            // floor / 0.20-0.22 cat - dark, but a detectable difference.
            float x = -0.25f;
            float z = -roomZ * 0.175f;             // -0.70
            float bulbY = wallHeight - 0.40f;      // 2.10

            var fixture = new GameObject("BareBulb");
            fixture.transform.SetParent(root, false);

            Material metal = LitMaterial("Lighting_Fixture", new Color(0.26f, 0.26f, 0.28f), Color.black, null, 0.35f, 0.6f);
            Material glass = LitMaterial("Lighting_Bulb", new Color(0.90f, 0.86f, 0.76f),
                new Color(1.00f, 0.80f, 0.58f) * 1.6f, null, 0.85f, 0f);

            float cordTop = wallHeight;
            float cordBottom = bulbY + 0.06f;
            float cordHalf = Mathf.Max(0.005f, (cordTop - cordBottom) * 0.5f);
            Prim(PrimitiveType.Cylinder, "Cord", fixture.transform,
                new Vector3(x, cordBottom + cordHalf, z), new Vector3(0.012f, cordHalf, 0.012f), Vector3.zero, metal);
            Prim(PrimitiveType.Cylinder, "Socket", fixture.transform,
                new Vector3(x, bulbY + 0.055f, z), new Vector3(0.05f, 0.035f, 0.05f), Vector3.zero, metal);
            Prim(PrimitiveType.Sphere, "Bulb", fixture.transform,
                new Vector3(x, bulbY, z), new Vector3(0.085f, 0.11f, 0.085f), Vector3.zero, glass);

            // Shadows off on purpose. URP gives a point light six atlas faces; with
            // shadows on it would take most of the 2048 additional-light atlas away
            // from the two spots that actually light the detection area, and every
            // cat would carry a second, contradictory shadow. This lamp is the room
            // fill - the spots do the modelling.
            MakeLamp("Lamp", fixture.transform, LightType.Point,
                new Vector3(x, bulbY, z), Vector3.zero,
                new Color(1.00f, 0.82f, 0.62f),       // ~2900 K tungsten
                2.0f, 7f, 0f, 0f, LightShadows.None, 1f);
        }

        // ------------------------------------------------------------------
        //  Scene / pipeline state
        // ------------------------------------------------------------------

        static void ApplyAmbient()
        {
            // Flat warm ambient, no skybox. 0.21 sRGB is 0.030 linear, so ambient
            // alone puts an unlit 0.42 floor at 0.12 sRGB - a floor under the dark
            // corners without lifting the whole room out of "basement".
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = AmbientColor;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = false;
        }

        /// <summary>
        /// URP asset defaults ship with additional-light shadows and soft shadows
        /// OFF (m_AdditionalLightShadowsSupported / m_SoftShadowsSupported are
        /// false in UniversalRenderPipelineAsset, and Create() does not change
        /// them). Without this the spot lights below cast nothing at all. Only
        /// supportsAdditionalLightShadows has a public setter, so both go through
        /// SerializedObject. Idempotent: a second run finds them already set.
        /// </summary>
        static void EnableUrpPunctualShadows()
        {
            EnableOn(GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset);
            var perQuality = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            if (perQuality != null && perQuality != GraphicsSettings.defaultRenderPipeline)
                EnableOn(perQuality);
        }

        static void EnableOn(UniversalRenderPipelineAsset urp)
        {
            if (urp == null)
            {
                Debug.LogWarning("LightingRigBuilder: no UniversalRenderPipelineAsset is active - "
                                 + "spot/point shadows cannot be enabled. Run EnsureUrp first.");
                return;
            }

            var so = new SerializedObject(urp);
            bool changed = false;
            changed |= SetSerializedBool(so, urp, "m_AdditionalLightShadowsSupported", true);
            changed |= SetSerializedBool(so, urp, "m_SoftShadowsSupported", true);
            changed |= SetSerializedBool(so, urp, "m_AnyShadowsSupported", true);
            if (!changed) return;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(urp);
            AssetDatabase.SaveAssets();
            Debug.Log("LightingRigBuilder: enabled additional-light + soft shadows on " + urp.name + ".");
        }

        static bool SetSerializedBool(SerializedObject so, Object owner, string property, bool value)
        {
            SerializedProperty p = so.FindProperty(property);
            if (p == null)
            {
                Debug.LogWarning("LightingRigBuilder: '" + property + "' not found on " + owner.name
                                 + " - enable it by hand in the URP asset inspector (Lighting > Shadows).");
                return false;
            }
            if (p.boolValue == value) return false;
            p.boolValue = value;
            return true;
        }

        /// <summary>
        /// Reports lights that are not part of this rig instead of deleting them -
        /// the scene builder owns its own hierarchy and this runs at an unknown
        /// point in its sequence.
        /// </summary>
        static void WarnAboutExistingLights(Transform environment)
        {
            Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light light in lights)
            {
                if (light == null) continue;
                Transform t = light.transform;
                if (t.root != null && t.root.name == RigRootName) continue;

                string where = environment != null && t.IsChildOf(environment) ? " (under Environment)" : "";
                Debug.LogWarning("LightingRigBuilder: pre-existing light '" + t.name + "'" + where
                                 + " is outside the '" + RigRootName + "' rig and will add to its exposure. "
                                 + "Delete it so the rig's levels hold.");
            }
        }

        static void WarnIfOffDesign(float roomX, float roomZ, float wallHeight)
        {
            if (Mathf.Abs(roomX - DesignRoomX) < 0.01f
                && Mathf.Abs(roomZ - DesignRoomZ) < 0.01f
                && Mathf.Abs(wallHeight - DesignWallH) < 0.01f) return;

            Debug.LogWarning(string.Format(
                "LightingRigBuilder: room is {0} x {1} x {2}, but the FOV and exposure numbers were solved "
                + "for {3} x {4} x {5}. Positions scale with the room; the {6} deg field of view and the "
                + "light intensities do not - re-derive them if this drifts far.",
                roomX, roomZ, wallHeight, DesignRoomX, DesignRoomZ, DesignWallH, VerticalFov));
        }

        // ------------------------------------------------------------------
        //  Primitive / light / asset helpers
        // ------------------------------------------------------------------

        static GameObject Prim(PrimitiveType type, string name, Transform parent,
            Vector3 localPos, Vector3 localScale, Vector3 euler, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;

            // No colliders on fixtures: they must not reach the NavMesh bake, and
            // nothing in the scene needs to collide with the ceiling.
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = localScale;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            // The housing sits above its own lamps; casting would only ever produce
            // self-shadow acne on the tube.
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        static Light MakeLamp(string name, Transform parent, LightType type,
            Vector3 localPos, Vector3 euler, Color color, float intensity, float range,
            float spotAngle, float innerSpotAngle, LightShadows shadows, float shadowStrength)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(euler);

            var light = go.AddComponent<Light>();
            light.type = type;

            // Colour is set directly rather than via useColorTemperature so the warm
            // cast is identical no matter how the pipeline handles CCT. The values
            // are the blackbody colours noted at each call site.
            light.useColorTemperature = false;
            light.color = color;

            light.intensity = intensity;
            // Range well past the 2.3 m throw: URP's range window
            // saturate((1 - (d^2/r^2)^2)^2) stays >= 0.986 across the whole
            // detection area, so the exposure budget above is the real exposure.
            light.range = range;

            if (type == LightType.Spot)
            {
                light.spotAngle = spotAngle;
                light.innerSpotAngle = innerSpotAngle;   // soft edge, no hard cookie line
            }

            light.shadows = shadows;
            light.shadowStrength = shadowStrength;       // < 1 lifts shadow interiors
            light.bounceIntensity = 0f;                  // no realtime GI in this scene

            // URP takes shadow bias from the pipeline asset unless a light opts out
            // via UniversalAdditionalLightData.usePipelineSettings. These are the
            // fallback values for that case; the asset's 1/1 is correct at this scale.
            light.shadowBias = 0.05f;
            light.shadowNormalBias = 0.2f;
            light.shadowNearPlane = 0.1f;

            return light;
        }

        static Material LitMaterial(string name, Color baseColor, Color emission,
            Texture2D emissionMap, float smoothness, float metallic)
        {
            EnsureFolder(MaterialDir);
            string path = MaterialDir + "/" + name + ".mat";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = false;
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                mat = new Material(shader);
                created = true;
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);
            mat.color = baseColor;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);

            bool emissive = emission.maxColorComponent > 0.001f;
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", emissive ? emission : Color.black);
            if (emissionMap != null && mat.HasProperty("_EmissionMap")) mat.SetTexture("_EmissionMap", emissionMap);
            if (emissive) mat.EnableKeyword("_EMISSION"); else mat.DisableKeyword("_EMISSION");
            // Nothing is lightmapped or realtime-GI lit here, so keep the emission
            // out of GI entirely rather than leaving a stale bake flag on the asset.
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;

            if (created) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Procedural emission map for the tube: bright along the middle, dimmed
        /// toward the end caps and the tube's edges, so the fixture reads as a lamp
        /// instead of a uniform white slab in the Scene view. Reused if present.
        /// </summary>
        static Texture2D TubeGradient()
        {
            EnsureFolder(TextureDir);
            string path = TextureDir + "/Lighting_TubeGradient.asset";

            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int w = 64, h = 8;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false)
            {
                name = "Lighting_TubeGradient",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float ty = (y + 0.5f) / h;
                float across = Mathf.SmoothStep(0.55f, 1f, 1f - Mathf.Abs(ty * 2f - 1f));
                for (int x = 0; x < w; x++)
                {
                    float tx = (x + 0.5f) / w;
                    float along = Mathf.SmoothStep(0.45f, 1f, Mathf.Min(tx, 1f - tx) * 5f);
                    float v = Mathf.Clamp01(along * across);
                    pixels[y * w + x] = new Color(v, v, v);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false);

            AssetDatabase.CreateAsset(tex, path);
            return tex;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            string[] parts = folder.Split('/');
            string path = parts[0];                       // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = path + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(path, parts[i]);
                path = next;
            }
        }
    }
}
