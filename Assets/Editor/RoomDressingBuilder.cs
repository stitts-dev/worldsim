using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;

namespace WorldSim.EditorTools
{
    /// <summary>
    /// Room dressing for the CatBasement scene: procedural surface textures
    /// (concrete floor with control joints, painted cinder block walls), a
    /// skirting band at the wall/floor junction, a cast-iron floor drain built
    /// from a code-generated mesh, and a handful of props shoved against the
    /// north and east walls.
    ///
    /// Called by CatBasementSceneBuilder AFTER the walls and litterboxes exist
    /// and BEFORE the NavMesh bake:
    ///
    ///     RoomDressingBuilder.Dress(environment.transform, RoomX, RoomZ, WallH);
    ///
    /// Pathing rules this file obeys, so the bake stays usable:
    ///  - Nothing standing on the floor reaches further into the room than the
    ///    litterbox front face (z = roomZ/2 - 0.4) or further than 0.42 m off
    ///    the east wall, so props never carve navmesh the litterboxes and walls
    ///    had not already carved.
    ///  - Nothing enters the problem zone (circle of radius 1.5 m around
    ///    (0, 0, 0.6)) or the flee corner at (-2.4, 0, -1.5).
    ///  - Every floor-standing / wall-mounted prop group carries a
    ///    NavMeshModifier with overrideArea + area 1 (Not Walkable), same as
    ///    the litterboxes. NavMeshBuildMarkup is hierarchical, so one modifier
    ///    on the group root covers all of its children.
    ///  - The floor drain is deliberately left walkable: it is a 5 mm flush
    ///    disc, not an obstacle, and marking it would punch a hole in the
    ///    middle of the navmesh for nothing.
    ///
    /// Generated assets live under Assets/WorldSim/Scenarios/CatBasement/
    /// (Textures/, Meshes/, Materials/). Textures and meshes are reused when
    /// they already exist so rebuilds do not churn GUIDs; materials are
    /// rewritten in place. To pick up edits to the generators, run
    /// WorldSim > Regenerate Room Dressing Textures first.
    /// </summary>
    public static class RoomDressingBuilder
    {
        const string ScenarioDir = "Assets/WorldSim/Scenarios/CatBasement";
        const string MatDir = ScenarioDir + "/Materials";
        const string TexDir = ScenarioDir + "/Textures";
        const string MeshDir = ScenarioDir + "/Meshes";

        const int NotWalkable = 1; // NavMesh built-in area index

        // Everything in this room is lit by flat ambient 0.22 plus two weak warm
        // point lights and gets no bounce. Albedo values are therefore pushed
        // well above what they would be for a normally lit scene - a bright
        // basement still reads as a basement, a black one reads as nothing.
        static readonly Color TextureTint = new Color(0.97f, 0.96f, 0.94f); // near-white; textures carry the color
        static readonly Color SkirtingColor = new Color(0.40f, 0.38f, 0.36f);
        static readonly Color ToteColor = new Color(0.34f, 0.40f, 0.50f);
        static readonly Color ToteLidColor = new Color(0.26f, 0.31f, 0.40f);
        static readonly Color MetalColor = new Color(0.68f, 0.69f, 0.71f);
        static readonly Color DarkMetalColor = new Color(0.30f, 0.30f, 0.32f);
        static readonly Color PanelColor = new Color(0.74f, 0.73f, 0.70f);
        static readonly Color TankColor = new Color(0.80f, 0.78f, 0.74f);
        static readonly Color PaintCanColor = new Color(0.72f, 0.70f, 0.66f);
        static readonly Color BucketColor = new Color(0.62f, 0.58f, 0.34f);

        // ---------------------------------------------------------------
        // entry points
        // ---------------------------------------------------------------

        /// <summary>
        /// Re-skins the existing Floor / Wall_* children with procedural
        /// materials and adds the dressing props under a "Dressing" child of
        /// <paramref name="environment"/>. Idempotent: an existing "Dressing"
        /// subtree is replaced.
        /// </summary>
        public static void Dress(Transform environment, float roomX, float roomZ, float wallHeight)
        {
            if (environment == null)
            {
                Debug.LogError("RoomDressingBuilder.Dress: environment is null - nothing dressed.");
                return;
            }

            EnsureFolders();
            ApplySurfaceMaterials(environment, roomX, roomZ, wallHeight);
            BuildProps(environment, roomX, roomZ, wallHeight);
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Assigns the concrete floor / cinder-block wall materials to the
        /// children named Floor, Wall_North, Wall_South, Wall_East, Wall_West.
        /// Missing children are warned about and skipped - only the material is
        /// touched, never the transform (VirtualTurret reads Floor.localScale).
        /// </summary>
        public static void ApplySurfaceMaterials(Transform environment, float roomX, float roomZ, float wallHeight)
        {
            if (environment == null) return;
            EnsureFolders();

            Texture2D concrete = ConcreteTexture(out Texture2D concreteNormal);
            Texture2D block = BlockTexture(out Texture2D blockNormal);

            // Floor texture covers 2 x 2 m and carries half a control joint on
            // each edge, so tiling at 1 tile / 2 m produces a continuous sawcut
            // grid across the slab.
            Vector2 floorTiling = new Vector2(
                Mathf.Max(1f, Mathf.Floor(roomX * 0.5f + 0.5f)),
                Mathf.Max(1f, Mathf.Floor(roomZ * 0.5f + 0.5f)));

            // Block texture is 4 blocks wide x 8 courses tall over 1.6 x 1.6 m,
            // running bond. Tiling snapped to halves keeps the bond aligned
            // across the wrap seam.
            Vector2 nsTiling = new Vector2(SnapHalf(roomX / 1.6f), SnapHalf(wallHeight / 1.6f));
            Vector2 ewTiling = new Vector2(SnapHalf(roomZ / 1.6f), SnapHalf(wallHeight / 1.6f));

            Material floorMat = TexturedMaterial("Dress_FloorConcrete", concrete, concreteNormal, floorTiling, 0.14f, 0f, 0.9f);
            Material wallNs = TexturedMaterial("Dress_WallBlock_NS", block, blockNormal, nsTiling, 0.08f, 0f, 1.0f);
            Material wallEw = TexturedMaterial("Dress_WallBlock_EW", block, blockNormal, ewTiling, 0.08f, 0f, 1.0f);

            Skin(environment, "Floor", floorMat);
            Skin(environment, "Wall_North", wallNs);
            Skin(environment, "Wall_South", wallNs);
            Skin(environment, "Wall_East", wallEw);
            Skin(environment, "Wall_West", wallEw);
        }

        /// <summary>
        /// Rebuilds the "Dressing" subtree: skirting, floor drain, and the
        /// north/east wall props.
        /// </summary>
        public static void BuildProps(Transform environment, float roomX, float roomZ, float wallHeight)
        {
            if (environment == null) return;
            EnsureFolders();

            Transform stale = environment.Find("Dressing");
            if (stale != null) UnityEngine.Object.DestroyImmediate(stale.gameObject);

            var root = new GameObject("Dressing");
            root.transform.SetParent(environment, false);

            float hx = roomX * 0.5f; // inner face of the east/west walls
            float hz = roomZ * 0.5f; // inner face of the north/south walls

            BuildSkirting(root.transform, hx, hz);
            BuildFloorDrain(root.transform);
            BuildNorthWallClutter(root.transform, hx, hz);
            BuildEastWallShelf(root.transform, hx);
            BuildWaterHeater(root.transform, hx, hz);
            BuildServiceRun(root.transform, hx, hz);
        }

        [MenuItem("WorldSim/Regenerate Room Dressing Textures")]
        public static void RegenerateTextures()
        {
            if (AssetDatabase.IsValidFolder(TexDir)) AssetDatabase.DeleteAsset(TexDir);
            if (AssetDatabase.IsValidFolder(MeshDir)) AssetDatabase.DeleteAsset(MeshDir);
            AssetDatabase.Refresh();
            Debug.Log("RoomDressingBuilder: generated textures/meshes cleared - rebuild the scene to regenerate.");
        }

        // ---------------------------------------------------------------
        // props
        // ---------------------------------------------------------------

        static void BuildSkirting(Transform parent, float hx, float hz)
        {
            // A painted band around the base of the walls. Sunk 5 mm so its
            // bottom face is not coplanar with the floor top at y = 0.
            const float h = 0.11f, proud = 0.035f, sink = 0.005f;
            Material mat = PlainMaterial("Dress_Skirting", SkirtingColor, 0.18f, 0f);

            var group = new GameObject("Skirting");
            group.transform.SetParent(parent, false);

            Box("Skirting_North", group.transform, new Vector3(0f, h * 0.5f - sink, hz - proud * 0.5f), new Vector3(hx * 2f, h, proud), mat);
            Box("Skirting_South", group.transform, new Vector3(0f, h * 0.5f - sink, -hz + proud * 0.5f), new Vector3(hx * 2f, h, proud), mat);
            Box("Skirting_East", group.transform, new Vector3(hx - proud * 0.5f, h * 0.5f - sink, 0f), new Vector3(proud, h, hz * 2f), mat);
            Box("Skirting_West", group.transform, new Vector3(-hx + proud * 0.5f, h * 0.5f - sink, 0f), new Vector3(proud, h, hz * 2f), mat);

            MarkNotWalkable(group);
        }

        static void BuildFloorDrain(Transform parent)
        {
            const float radius = 0.135f;
            Mesh mesh = DiscMesh("FloorDrain", 48, radius);
            Texture2D tex = DrainTexture(out Texture2D normal);
            Material mat = TexturedMaterial("Dress_Drain", tex, normal, Vector2.one, 0.26f, 0.45f, 1.0f);

            // Open floor, roughly frame-center from the security camera. Left
            // walkable on purpose: 5 mm proud of the slab, not an obstacle.
            var go = new GameObject("FloorDrain");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(1.1f, 0.005f, 0.2f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static void BuildNorthWallClutter(Transform parent, float hx, float hz)
        {
            Texture2D card = CardboardTexture(out Texture2D cardNormal);
            Material cardboard = TexturedMaterial("Dress_Cardboard", card, cardNormal, Vector2.one, 0.05f, 0f, 0.7f);
            Material tote = PlainMaterial("Dress_Tote", ToteColor, 0.45f, 0f);
            Material lid = PlainMaterial("Dress_ToteLid", ToteLidColor, 0.45f, 0f);

            // Left flank of the litterbox row: stacked cartons.
            var boxes = new GameObject("StoredBoxes");
            boxes.transform.SetParent(parent, false);
            Box("Carton_Large", boxes.transform, new Vector3(-hx + 0.85f, 0.19f, hz - 0.21f), new Vector3(0.62f, 0.38f, 0.34f), cardboard, -4f);
            Box("Carton_Top", boxes.transform, new Vector3(-hx + 0.78f, 0.53f, hz - 0.19f), new Vector3(0.50f, 0.30f, 0.30f), cardboard, 9f);
            Box("Carton_Small", boxes.transform, new Vector3(-hx + 1.38f, 0.145f, hz - 0.17f), new Vector3(0.42f, 0.29f, 0.28f), cardboard, 3f);
            MarkNotWalkable(boxes);

            // Right flank: a pair of plastic storage totes.
            var totes = new GameObject("StorageTotes");
            totes.transform.SetParent(parent, false);
            Box("Tote_Lower", totes.transform, new Vector3(hx - 1.08f, 0.17f, hz - 0.20f), new Vector3(0.66f, 0.34f, 0.34f), tote, 2f);
            Box("Tote_Lid", totes.transform, new Vector3(hx - 1.08f, 0.345f, hz - 0.20f), new Vector3(0.68f, 0.03f, 0.36f), lid, 2f);
            Box("Tote_Upper", totes.transform, new Vector3(hx - 1.12f, 0.50f, hz - 0.19f), new Vector3(0.60f, 0.32f, 0.32f), tote, -5f);
            MarkNotWalkable(totes);
        }

        static void BuildEastWallShelf(Transform parent, float hx)
        {
            Texture2D wood = WoodTexture(out Texture2D woodNormal);
            Material shelfMat = TexturedMaterial("Dress_Wood", wood, woodNormal, new Vector2(2f, 1f), 0.16f, 0f, 0.8f);
            Material postMat = PlainMaterial("Dress_ShelfPost", DarkMetalColor, 0.30f, 0.55f);
            Texture2D card = CardboardTexture(out Texture2D cardNormal);
            Material cardboard = TexturedMaterial("Dress_Cardboard", card, cardNormal, Vector2.one, 0.05f, 0f, 0.7f);
            Material canMat = PlainMaterial("Dress_PaintCan", PaintCanColor, 0.40f, 0.45f);
            Material bucketMat = PlainMaterial("Dress_Bucket", BucketColor, 0.35f, 0f);

            // 0.32 m deep, hard against the east wall: near face sits at
            // hx - 0.34, so the navmesh loses 0.34 m of the east margin and no
            // more.
            const float zMin = -0.45f, zMax = 0.85f;
            float zc = (zMin + zMax) * 0.5f;
            float zl = zMax - zMin;
            float xc = hx - 0.18f;

            var shelf = new GameObject("WallShelf");
            shelf.transform.SetParent(parent, false);

            foreach (float px in new[] { hx - 0.32f, hx - 0.04f })
            foreach (float pz in new[] { zMin + 0.02f, zMax - 0.02f })
                Box("Post", shelf.transform, new Vector3(px, 0.775f, pz), new Vector3(0.04f, 1.55f, 0.04f), postMat);

            float[] levels = { 0.065f, 0.50f, 0.95f, 1.40f };
            for (int i = 0; i < levels.Length; i++)
                Box("Shelf_" + i, shelf.transform, new Vector3(xc, levels[i], zc), new Vector3(0.32f, 0.03f, zl), shelfMat);

            Box("Shelf_Carton", shelf.transform, new Vector3(xc, levels[1] + 0.125f, zc + 0.15f), new Vector3(0.24f, 0.22f, 0.30f), cardboard, 7f);
            Cylinder("PaintCan_A", shelf.transform, new Vector3(xc, levels[2] + 0.11f, zc - 0.20f), 0.075f, 0.19f, canMat);
            Cylinder("PaintCan_B", shelf.transform, new Vector3(xc + 0.03f, levels[2] + 0.11f, zc - 0.02f), 0.075f, 0.19f, canMat);
            Cylinder("Bucket", shelf.transform, new Vector3(xc, levels[0] + 0.135f, zc - 0.40f), 0.11f, 0.24f, bucketMat);

            MarkNotWalkable(shelf);
        }

        static void BuildWaterHeater(Transform parent, float hx, float hz)
        {
            Material tank = PlainMaterial("Dress_Tank", TankColor, 0.48f, 0.10f);
            Material metal = PlainMaterial("Dress_Metal", MetalColor, 0.42f, 0.75f);

            float cx = hx - 0.42f;
            float cz = hz - 0.45f;

            var heater = new GameObject("WaterHeater");
            heater.transform.SetParent(parent, false);
            Cylinder("Base", heater.transform, new Vector3(cx, 0.045f, cz), 0.275f, 0.09f, metal);
            Cylinder("Tank", heater.transform, new Vector3(cx, 0.80f, cz), 0.26f, 1.42f, tank);
            Cylinder("Flue", heater.transform, new Vector3(cx, 1.79f, cz), 0.055f, 0.72f, metal);
            MarkNotWalkable(heater);
        }

        static void BuildServiceRun(Transform parent, float hx, float hz)
        {
            Material metal = PlainMaterial("Dress_Metal", MetalColor, 0.42f, 0.75f);
            Material panelMat = PlainMaterial("Dress_Panel", PanelColor, 0.35f, 0.25f);
            Material ventMat = PlainMaterial("Dress_Vent", DarkMetalColor, 0.25f, 0.40f);

            float panelX = -hx + 1.1f;
            float zPipe = hz - 0.07f;   // pipe centerline, 7 cm off the wall
            float zFace = hz - 0.045f;  // wall-mounted plate centers

            var run = new GameObject("ServiceRun");
            run.transform.SetParent(parent, false);

            // Breaker panel, up out of the litterbox region and left of frame center.
            Box("BreakerPanel", run.transform, new Vector3(panelX, 1.35f, zFace), new Vector3(0.34f, 0.44f, 0.09f), panelMat);
            Box("PanelDoorSeam", run.transform, new Vector3(panelX, 1.35f, zFace - 0.048f), new Vector3(0.28f, 0.38f, 0.01f), ventMat);

            // Conduit: up out of the panel, along the wall, then down and over
            // to the heater.
            Pipe("Conduit_Riser", run.transform, new Vector3(panelX, 1.57f, zPipe), new Vector3(panelX, 1.72f, zPipe), 0.022f, metal);
            Pipe("Conduit_Run", run.transform, new Vector3(panelX, 1.72f, zPipe), new Vector3(hx - 0.42f, 1.72f, zPipe), 0.022f, metal);

            // Main water line running the length of the north wall.
            Pipe("WaterLine", run.transform, new Vector3(-hx + 0.2f, 1.60f, zPipe - 0.02f), new Vector3(hx - 0.42f, 1.60f, zPipe - 0.02f), 0.034f, metal);
            Pipe("WaterLine_Elbow", run.transform, new Vector3(hx - 0.42f, 1.60f, zPipe - 0.02f), new Vector3(hx - 0.42f, 1.60f, hz - 0.45f), 0.034f, metal);
            Pipe("WaterLine_Drop", run.transform, new Vector3(hx - 0.42f, 1.60f, hz - 0.45f), new Vector3(hx - 0.42f, 1.46f, hz - 0.45f), 0.034f, metal);

            // Wall vent high in the north-east corner of frame.
            Box("WallVent", run.transform, new Vector3(hx - 0.6f, 1.90f, zFace), new Vector3(0.30f, 0.20f, 0.06f), ventMat);

            MarkNotWalkable(run);
        }

        // ---------------------------------------------------------------
        // geometry helpers
        // ---------------------------------------------------------------

        static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Material mat, float yawDeg = 0f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            if (!Mathf.Approximately(yawDeg, 0f)) go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Cylinder(string name, Transform parent, Vector3 pos, float radius, float height, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static GameObject Pipe(string name, Transform parent, Vector3 a, Vector3 b, float radius, Material mat)
        {
            Vector3 delta = b - a;
            float len = delta.magnitude;
            if (len < 0.001f) len = 0.001f;

            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (a + b) * 0.5f;
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, delta / len);
            go.transform.localScale = new Vector3(radius * 2f, len * 0.5f, radius * 2f);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static void MarkNotWalkable(GameObject group)
        {
            var mod = group.GetComponent<NavMeshModifier>();
            if (mod == null) mod = group.AddComponent<NavMeshModifier>();
            mod.overrideArea = true;
            mod.area = NotWalkable; // markup is hierarchical - covers all children
        }

        static void Skin(Transform environment, string childName, Material mat)
        {
            Transform t = environment.Find(childName);
            if (t == null)
            {
                Debug.LogWarning("RoomDressingBuilder: no child named '" + childName + "' under " + environment.name +
                                 "; call Dress() after the room geometry is built.");
                return;
            }

            var r = t.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterial = mat;
        }

        static float SnapHalf(float v)
        {
            return Mathf.Max(0.5f, Mathf.Floor(v * 2f + 0.5f) * 0.5f);
        }

        // ---------------------------------------------------------------
        // procedural mesh
        // ---------------------------------------------------------------

        static Mesh DiscMesh(string name, int segments, float radius)
        {
            string path = MeshDir + "/" + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) return existing;

            var verts = new Vector3[segments + 1];
            var norms = new Vector3[segments + 1];
            var uvs = new Vector2[segments + 1];
            var tris = new int[segments * 3];

            verts[0] = Vector3.zero;
            norms[0] = Vector3.up;
            uvs[0] = new Vector2(0.5f, 0.5f);

            for (int i = 0; i < segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                verts[i + 1] = new Vector3(cx * radius, 0f, cz * radius);
                norms[i + 1] = Vector3.up;
                uvs[i + 1] = new Vector2(0.5f + cx * 0.5f, 0.5f + cz * 0.5f);

                int next = (i + 1) % segments;
                // Unity front face = cross(v1 - v0, v2 - v0); this winding gives +Y.
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = next + 1;
                tris[i * 3 + 2] = i + 1;
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        // ---------------------------------------------------------------
        // materials
        // ---------------------------------------------------------------

        static Material TexturedMaterial(string name, Texture2D albedo, Texture2D normal, Vector2 tiling,
                                         float smoothness, float metallic, float bumpScale)
        {
            Material mat = LoadOrCreateMaterial(name);

            // The texture carries all the color; tinting it again would square
            // the albedo and sink the room to mud.
            SetColorIfPresent(mat, "_BaseColor", TextureTint);
            mat.color = TextureTint;

            if (albedo != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", albedo);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);
                mat.mainTexture = albedo;
                mat.mainTextureScale = tiling;
                if (mat.HasProperty("_BaseMap")) mat.SetTextureScale("_BaseMap", tiling);
                if (mat.HasProperty("_MainTex")) mat.SetTextureScale("_MainTex", tiling);
            }

            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetTextureScale("_BumpMap", tiling);
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", bumpScale);
                mat.EnableKeyword("_NORMALMAP");
            }

            SetFloatIfPresent(mat, "_Smoothness", smoothness);
            SetFloatIfPresent(mat, "_Glossiness", smoothness);
            SetFloatIfPresent(mat, "_Metallic", metallic);

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material PlainMaterial(string name, Color color, float smoothness, float metallic)
        {
            Material mat = LoadOrCreateMaterial(name);
            SetColorIfPresent(mat, "_BaseColor", color);
            mat.color = color;
            SetFloatIfPresent(mat, "_Smoothness", smoothness);
            SetFloatIfPresent(mat, "_Glossiness", smoothness);
            SetFloatIfPresent(mat, "_Metallic", metallic);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material LoadOrCreateMaterial(string name)
        {
            string path = MatDir + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void SetColorIfPresent(Material mat, string prop, Color value)
        {
            if (mat.HasProperty(prop)) mat.SetColor(prop, value);
        }

        static void SetFloatIfPresent(Material mat, string prop, float value)
        {
            if (mat.HasProperty(prop)) mat.SetFloat(prop, value);
        }

        // ---------------------------------------------------------------
        // procedural textures
        // ---------------------------------------------------------------

        /// <summary>Poured concrete slab, 512 px over 2 x 2 m, with half a
        /// control joint on each edge so tiling makes a continuous sawcut grid.</summary>
        static Texture2D ConcreteTexture(out Texture2D normal)
        {
            const int size = 512;
            string albedoPath = TexDir + "/Concrete_Floor.asset";
            string normalPath = TexDir + "/Concrete_Floor_N.asset";

            var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var cachedN = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            if (cached != null && cachedN != null) { normal = cachedN; return cached; }

            var rng = new System.Random(0x0CAA7);
            float[] l4 = Lattice(4, rng), l8 = Lattice(8, rng), l16 = Lattice(16, rng), l64 = Lattice(64, rng);

            var px = new Color[size * size];
            var h = new float[size * size];
            float jointHalf = size * 0.006f; // ~3 px -> ~1.2 cm sawcut over 2 m

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;

                    float broad = Sample(l4, 4, u, v);
                    float mid = Sample(l8, 8, u, v);
                    float fine = Sample(l16, 16, u, v);
                    float grain = Sample(l64, 64, u, v);
                    float n = broad * 0.46f + mid * 0.27f + fine * 0.17f + grain * 0.10f;

                    // Bright: flat ambient, no bounce. Concrete lives 0.45-0.60.
                    float shade = 0.455f + n * 0.135f;

                    // Damp patches - low frequency, subtle.
                    float damp = Mathf.InverseLerp(0.66f, 0.94f, broad);
                    shade -= damp * 0.055f;

                    // Aggregate speckle.
                    float sp = (float)rng.NextDouble();
                    shade += (sp - 0.5f) * 0.035f;
                    if (sp > 0.9885f) shade += 0.085f;
                    else if (sp < 0.0125f) shade -= 0.075f;

                    float height = n * 0.55f + grain * 0.2f + (sp - 0.5f) * 0.12f;

                    // Control joint on both edges (half each side of the wrap).
                    float du = Mathf.Min(u, 1f - u) * size;
                    float dv = Mathf.Min(v, 1f - v) * size;
                    float d = Mathf.Min(du, dv);
                    float joint = Mathf.Clamp01(1f - (d - jointHalf) / 2.5f);
                    if (joint > 0f)
                    {
                        shade = Mathf.Lerp(shade, shade * 0.58f, joint);
                        height -= joint * 0.85f;
                    }

                    shade = Mathf.Clamp(shade, 0.16f, 0.95f);
                    h[y * size + x] = height;
                    px[y * size + x] = new Color(shade * 1.03f, shade, shade * 0.955f, 1f);
                }
            }

            normal = SaveTexture(normalPath, size, HeightToNormalPixels(size, h, 1.5f), true, TextureWrapMode.Repeat);
            return SaveTexture(albedoPath, size, px, false, TextureWrapMode.Repeat);
        }

        /// <summary>Painted cinder block in running bond: 4 blocks wide x 8
        /// courses tall over 1.6 x 1.6 m.</summary>
        static Texture2D BlockTexture(out Texture2D normal)
        {
            const int size = 512;
            const int cellW = 128, cellH = 64;
            string albedoPath = TexDir + "/Block_Wall.asset";
            string normalPath = TexDir + "/Block_Wall_N.asset";

            var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var cachedN = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            if (cached != null && cachedN != null) { normal = cachedN; return cached; }

            var rng = new System.Random(0xB10C);
            float[] l8 = Lattice(8, rng), l32 = Lattice(32, rng), l128 = Lattice(128, rng);

            // Per-block value jitter so the wall is not a flat sheet.
            int rows = size / cellH, cols = size / cellW;
            var blockTone = new float[rows * cols];
            for (int i = 0; i < blockTone.Length; i++) blockTone[i] = (float)rng.NextDouble();

            var px = new Color[size * size];
            var h = new float[size * size];
            const float mortarHalf = 2.5f; // ~8 mm joint at 3.1 mm/px

            for (int y = 0; y < size; y++)
            {
                int row = y / cellH;
                float shift = (row % 2 == 0) ? 0f : cellW * 0.5f;

                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;

                    float sx = Mathf.Repeat(x + shift, cellW);
                    float sy = y - row * cellH;
                    int col = Mathf.Clamp((int)(Mathf.Repeat(x + shift, size) / cellW), 0, cols - 1);

                    float dEdge = Mathf.Min(Mathf.Min(sx, cellW - sx), Mathf.Min(sy, cellH - sy));
                    float mortar = Mathf.Clamp01(1f - (dEdge - mortarHalf) / 2.0f);

                    float blot = Sample(l8, 8, u, v);
                    float paint = Sample(l32, 32, u, v);
                    float tooth = Sample(l128, 128, u, v);

                    // Painted block: bright, chalky, faintly warm.
                    float shade = 0.585f + (blockTone[row * cols + col] - 0.5f) * 0.055f;
                    shade += (blot - 0.5f) * 0.05f;
                    shade += (paint - 0.5f) * 0.045f;
                    shade += (tooth - 0.5f) * 0.05f;

                    float height = 0.72f + (tooth - 0.5f) * 0.25f + (paint - 0.5f) * 0.1f;

                    if (mortar > 0f)
                    {
                        shade = Mathf.Lerp(shade, 0.455f + (tooth - 0.5f) * 0.05f, mortar);
                        height = Mathf.Lerp(height, 0.18f, mortar);
                    }

                    // Grime creeping up from the bottom of the tile - low
                    // amplitude so the repeat does not band.
                    shade -= Mathf.Clamp01(1f - v * 3.2f) * 0.035f;

                    shade = Mathf.Clamp(shade, 0.18f, 0.95f);
                    h[y * size + x] = height;
                    px[y * size + x] = new Color(shade * 1.015f, shade, shade * 0.97f, 1f);
                }
            }

            normal = SaveTexture(normalPath, size, HeightToNormalPixels(size, h, 2.6f), true, TextureWrapMode.Repeat);
            return SaveTexture(albedoPath, size, px, false, TextureWrapMode.Repeat);
        }

        /// <summary>Kraft carton: fiber noise, faint flutes, one tape band.</summary>
        static Texture2D CardboardTexture(out Texture2D normal)
        {
            const int size = 256;
            string albedoPath = TexDir + "/Cardboard.asset";
            string normalPath = TexDir + "/Cardboard_N.asset";

            var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var cachedN = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            if (cached != null && cachedN != null) { normal = cachedN; return cached; }

            var rng = new System.Random(0xCA4D);
            float[] l8 = Lattice(8, rng), l48 = Lattice(48, rng);

            var px = new Color[size * size];
            var h = new float[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    float blot = Sample(l8, 8, u, v);
                    float fiber = Sample(l48, 48, u, v);

                    float flute = Mathf.Sin(u * Mathf.PI * 2f * 24f) * 0.5f + 0.5f;

                    float k = 0.70f + (blot - 0.5f) * 0.10f + (fiber - 0.5f) * 0.09f + (flute - 0.5f) * 0.035f;
                    float height = 0.5f + (fiber - 0.5f) * 0.6f + (flute - 0.5f) * 0.25f;

                    Color c = new Color(k * 0.98f, k * 0.80f, k * 0.57f, 1f);

                    // Packing tape across the middle.
                    float tape = Mathf.Clamp01(1f - Mathf.Abs(v - 0.5f) / 0.055f);
                    if (tape > 0f)
                    {
                        Color tapeCol = new Color(0.80f, 0.76f, 0.66f, 1f);
                        c = Color.Lerp(c, tapeCol, tape * 0.85f);
                        height = Mathf.Lerp(height, 0.62f, tape);
                    }

                    h[y * size + x] = height;
                    px[y * size + x] = c;
                }
            }

            normal = SaveTexture(normalPath, size, HeightToNormalPixels(size, h, 1.2f), true, TextureWrapMode.Repeat);
            return SaveTexture(albedoPath, size, px, false, TextureWrapMode.Repeat);
        }

        /// <summary>Rough shelf board: warped grain lines along U.</summary>
        static Texture2D WoodTexture(out Texture2D normal)
        {
            const int size = 256;
            string albedoPath = TexDir + "/Wood_Shelf.asset";
            string normalPath = TexDir + "/Wood_Shelf_N.asset";

            var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var cachedN = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            if (cached != null && cachedN != null) { normal = cachedN; return cached; }

            var rng = new System.Random(0x0D00);
            float[] l6 = Lattice(6, rng), l24 = Lattice(24, rng);

            var px = new Color[size * size];
            var h = new float[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    float warp = Sample(l6, 6, u, v);
                    float speck = Sample(l24, 24, u, v);

                    float grain = Mathf.Sin((v * 9f + warp * 1.4f) * Mathf.PI * 2f) * 0.5f + 0.5f;
                    float k = 0.62f + (grain - 0.5f) * 0.14f + (speck - 0.5f) * 0.08f;
                    k = Mathf.Clamp(k, 0.2f, 0.92f);

                    h[y * size + x] = grain * 0.6f + speck * 0.4f;
                    px[y * size + x] = new Color(k * 0.98f, k * 0.79f, k * 0.56f, 1f);
                }
            }

            normal = SaveTexture(normalPath, size, HeightToNormalPixels(size, h, 1.0f), true, TextureWrapMode.Repeat);
            return SaveTexture(albedoPath, size, px, false, TextureWrapMode.Repeat);
        }

        /// <summary>Cast-iron floor drain: rusty rim, slotted grate, clamped.</summary>
        static Texture2D DrainTexture(out Texture2D normal)
        {
            const int size = 256;
            string albedoPath = TexDir + "/Floor_Drain.asset";
            string normalPath = TexDir + "/Floor_Drain_N.asset";

            var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var cachedN = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
            if (cached != null && cachedN != null) { normal = cachedN; return cached; }

            var rng = new System.Random(0xD3A1);
            float[] l10 = Lattice(10, rng), l40 = Lattice(40, rng);

            var px = new Color[size * size];
            var h = new float[size * size];

            const int slots = 10;
            float slotSpan = Mathf.PI * 2f / slots;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                    float dx = (u - 0.5f) * 2f, dy = (v - 0.5f) * 2f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ang = Mathf.Atan2(dy, dx);

                    float rust = Sample(l10, 10, u, v);
                    float grit = Sample(l40, 40, u, v);

                    float k;
                    float height;
                    if (r > 0.80f)
                    {
                        k = 0.44f + (rust - 0.5f) * 0.14f + (grit - 0.5f) * 0.07f; // rim
                        height = 0.85f;
                    }
                    else
                    {
                        k = 0.38f + (rust - 0.5f) * 0.10f + (grit - 0.5f) * 0.06f; // grate plate
                        height = 0.62f;

                        float a = Mathf.Repeat(ang + Mathf.PI, slotSpan) / slotSpan; // 0..1 within a sector
                        bool inSlot = a > 0.28f && a < 0.72f && r > 0.24f && r < 0.72f;
                        if (inSlot)
                        {
                            k = 0.055f + grit * 0.03f;
                            height = 0.05f;
                        }
                        else if (r < 0.17f)
                        {
                            k = 0.42f + (grit - 0.5f) * 0.06f; // hub
                            height = 0.70f;
                        }
                    }

                    // Rust bloom on the rim and around the slots.
                    float rusty = Mathf.InverseLerp(0.58f, 0.95f, rust);
                    Color c = new Color(k, k * 0.985f, k * 0.96f, 1f);
                    c = Color.Lerp(c, new Color(k * 1.25f, k * 0.82f, k * 0.55f, 1f), rusty * 0.55f);

                    h[y * size + x] = height;
                    px[y * size + x] = c;
                }
            }

            normal = SaveTexture(normalPath, size, HeightToNormalPixels(size, h, 2.2f), true, TextureWrapMode.Clamp);
            return SaveTexture(albedoPath, size, px, false, TextureWrapMode.Clamp);
        }

        // ---------------------------------------------------------------
        // noise + texture plumbing
        // ---------------------------------------------------------------

        static float[] Lattice(int cells, System.Random rng)
        {
            var a = new float[cells * cells];
            for (int i = 0; i < a.Length; i++) a[i] = (float)rng.NextDouble();
            return a;
        }

        /// <summary>Bilinear value noise over a wrapping lattice - seamless in both axes.</summary>
        static float Sample(float[] lat, int cells, float u, float v)
        {
            float fx = u * cells, fy = v * cells;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);

            int xa = ((x0 % cells) + cells) % cells;
            int ya = ((y0 % cells) + cells) % cells;
            int xb = (xa + 1) % cells;
            int yb = (ya + 1) % cells;

            float v00 = lat[ya * cells + xa], v10 = lat[ya * cells + xb];
            float v01 = lat[yb * cells + xa], v11 = lat[yb * cells + xb];
            return Mathf.Lerp(Mathf.Lerp(v00, v10, tx), Mathf.Lerp(v01, v11, tx), ty);
        }

        /// <summary>
        /// Sobel-lite height to tangent-space normal. Stored RGBA with A = 1, which
        /// is what URP's UnpackNormalmapRGorAG expects from an unimported texture
        /// (it multiplies x by w, then rebuilds z from xy).
        /// </summary>
        static Color[] HeightToNormalPixels(int size, float[] height, float strength)
        {
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                int yd = (y - 1 + size) % size, yu = (y + 1) % size;
                for (int x = 0; x < size; x++)
                {
                    int xl = (x - 1 + size) % size, xr = (x + 1) % size;
                    float gx = (height[y * size + xr] - height[y * size + xl]) * strength;
                    float gy = (height[yu * size + x] - height[yd * size + x]) * strength;
                    Vector3 n = new Vector3(-gx, -gy, 1f).normalized;
                    px[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }
            return px;
        }

        static Texture2D SaveTexture(string path, int size, Color[] pixels, bool linear, TextureWrapMode wrap)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, linear);
            tex.name = System.IO.Path.GetFileNameWithoutExtension(path);
            tex.wrapMode = wrap;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 4;
            tex.SetPixels(pixels);
            tex.Apply(true, false);
            AssetDatabase.CreateAsset(tex, path);
            return tex;
        }

        // ---------------------------------------------------------------
        // folders
        // ---------------------------------------------------------------

        static void EnsureFolders()
        {
            EnsureFolder(ScenarioDir);
            EnsureFolder(MatDir);
            EnsureFolder(TexDir);
            EnsureFolder(MeshDir);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            string[] parts = path.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
