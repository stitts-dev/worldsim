using UnityEditor;
using UnityEngine;

namespace WorldSim.EditorTools
{
    /// <summary>
    /// Builds a recognisable cat silhouette out of Unity primitives plus one
    /// procedurally generated cone mesh (the ears). Replaces the single bare
    /// capsule that read as a pill from the security camera.
    ///
    /// Contract with CatBasementSceneBuilder.BuildCat:
    ///   - The returned root is a CHILD of <paramref name="parent"/> at local
    ///     origin. Parent at y=0 puts the cat's feet on the floor.
    ///   - Local +Z is the cat's forward (nose), matching NavMeshAgent's
    ///     transform.forward steering.
    ///   - Every primitive's Collider is destroyed. NavMeshAgent does the
    ///     moving; stray colliders break pathing and would be picked up by a
    ///     NavMeshSurface bake set to PhysicsColliders.
    ///   - CatAgent squats by scaling the PARENT root's localScale.y to 60%.
    ///     Feet sit at y~0.002 and every offset is baked into child positions,
    ///     so the whole cat compresses toward the floor: legs shorten, the
    ///     torso capsule's circular cross-section flattens to an ellipse, and
    ///     the tilted ears shear outward (which reads as ears flattening -
    ///     exactly what a crouching cat does).
    ///
    /// Assets are written under Assets/WorldSim/Scenarios/CatBasement/ and are
    /// idempotent: existing assets at the same path are refilled in place, so
    /// GUIDs and inbound references survive a rebuild.
    /// </summary>
    public static class CatVisualBuilder
    {
        /// <summary>Nose tip to tail tip, in meters, before per-cat size jitter.</summary>
        public const float ApproxLength = 0.50f;

        /// <summary>Floor to ear tips, in meters, before per-cat size jitter.</summary>
        public const float ApproxHeight = 0.264f;

        const string ScenarioDir = "Assets/WorldSim/Scenarios/CatBasement";
        const string MaterialDir = ScenarioDir + "/Materials";
        const string MeshDir = ScenarioDir + "/Meshes";
        const string EarMeshPath = MeshDir + "/CatEar.asset";

        const int EarSegments = 14;

        // Torso capsule: rotated 90 deg about X so its length axis lies along Z.
        // A 90 deg (axis-aligned) rotation composes with the root's squat scale
        // without shear, so the body flattens cleanly instead of skewing.
        const float TorsoY = 0.145f;
        const float TorsoZ = -0.010f;
        const float TorsoDiameter = 0.125f;

        static Mesh _earMesh;

        // ---- entry point ----

        /// <summary>
        /// Creates the cat's visual hierarchy under <paramref name="parent"/> and
        /// returns its root. Call once per cat, in place of the old Body capsule.
        /// </summary>
        public static GameObject BuildCatVisual(Transform parent, Color furColor, string name)
        {
            if (string.IsNullOrEmpty(name)) name = "Cat";
            string key = Sanitize(name);

            // Deterministic per-cat variation: same fur color always produces the
            // same cat, so rebuilding the scene doesn't reshuffle the cast.
            // UnityEngine.Random is left alone on purpose - the scene builder and
            // WanderAgent share that stream.
            var rng = new System.Random(SeedFromColor(furColor));

            Palette pal = MakePalette(furColor, rng);

            // Uniform, never per-axis. Per-axis jitter would scale the torso's
            // vertical radius (root X/Z, because the capsule is rotated) and the
            // stripe rings' vertical radius (root X/Y) by different factors, and
            // the rings would sink inside the body.
            float sizeMul = 0.95f + (float)rng.NextDouble() * 0.11f;
            float earMul = 0.88f + (float)rng.NextDouble() * 0.24f;
            int tailSide = rng.Next(3) - 1; // -1 curls left, 0 straight, +1 right

            Material furMat = EnsureMaterial("CatFur_" + key, pal.Fur, pal.FurSmoothness);
            Material accentMat = EnsureMaterial("CatAccent_" + key, pal.Accent, 0.16f);
            Material stripeMat = EnsureMaterial("CatStripe_" + key, pal.Stripe, pal.FurSmoothness);
            Material innerMat = EnsureMaterial("CatInner_" + key, pal.InnerEar, 0.22f);
            Material eyeMat = EnsureMaterial("CatEye_" + key, pal.Eye, 0.85f);

            var root = new GameObject("Visual");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale = new Vector3(sizeMul, sizeMul, sizeMul);
            Transform t = root.transform;

            BuildLegs(t, furMat, accentMat);
            BuildTorso(t, furMat, accentMat);
            BuildStripes(t, stripeMat, pal.StripeCount);
            BuildHead(t, furMat, accentMat, innerMat, eyeMat, earMul);
            BuildTail(t, furMat, pal.TailTipIsAccent ? accentMat : stripeMat, tailSide);

            // Belt and braces: CreatePrimitive attaches a collider every time and
            // one survivor is enough to make the NavMeshAgent shove itself around.
            foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(col);

            AssetDatabase.SaveAssets();
            return root;
        }

        // ---- body parts ----

        static void BuildLegs(Transform t, Material fur, Material accent)
        {
            // Cylinder primitive is 2 units tall and 1 wide, so localScale.y is
            // half the leg length. Bottoms stop 3 mm above y=0 so the flat cap
            // never z-fights the floor, and the paws end 1 mm lower still so the
            // paw is what visibly touches down.
            var frontLeg = new Vector3(0.040f, 0.0475f, 0.040f);
            var rearLeg = new Vector3(0.046f, 0.0475f, 0.046f);
            const float legY = 0.0505f;

            Cylinder("Leg_FL", t, new Vector3(-0.040f, legY, 0.078f), frontLeg, fur);
            Cylinder("Leg_FR", t, new Vector3(0.040f, legY, 0.078f), frontLeg, fur);
            Cylinder("Leg_RL", t, new Vector3(-0.050f, legY, -0.100f), rearLeg, fur);
            Cylinder("Leg_RR", t, new Vector3(0.050f, legY, -0.100f), rearLeg, fur);

            var frontPaw = new Vector3(0.048f, 0.030f, 0.058f);
            var rearPaw = new Vector3(0.052f, 0.030f, 0.060f);
            Sphere("Paw_FL", t, new Vector3(-0.040f, 0.017f, 0.086f), frontPaw, accent);
            Sphere("Paw_FR", t, new Vector3(0.040f, 0.017f, 0.086f), frontPaw, accent);
            Sphere("Paw_RL", t, new Vector3(-0.050f, 0.017f, -0.093f), rearPaw, accent);
            Sphere("Paw_RR", t, new Vector3(0.050f, 0.017f, -0.093f), rearPaw, accent);
        }

        static void BuildTorso(Transform t, Material fur, Material accent)
        {
            // Overhead read comes from the plan-view outline: narrow chest, round
            // haunches. Three overlapping volumes give that taper; a lone capsule
            // gives a pill.
            Prim(PrimitiveType.Capsule, "Torso", t,
                new Vector3(0f, TorsoY, TorsoZ),
                new Vector3(TorsoDiameter, 0.135f, TorsoDiameter),
                Quaternion.Euler(90f, 0f, 0f), fur);

            Sphere("Haunches", t, new Vector3(0f, 0.142f, -0.118f), new Vector3(0.152f, 0.146f, 0.155f), fur);
            Sphere("Chest", t, new Vector3(0f, 0.148f, 0.070f), new Vector3(0.128f, 0.126f, 0.115f), fur);
            Sphere("Bib", t, new Vector3(0f, 0.122f, 0.095f), new Vector3(0.080f, 0.070f, 0.075f), accent);
            Sphere("Neck", t, new Vector3(0f, 0.166f, 0.106f), new Vector3(0.088f, 0.086f, 0.088f), fur);
        }

        static void BuildStripes(Transform t, Material stripe, int count)
        {
            if (count <= 0) return;

            // Flattened spheres centred on the torso axis: they wrap the body as
            // rings, so there are no corners to poke through and no z-fighting.
            // Radius 0.068 against the torso's 0.0625 leaves a 5.5 mm band. The
            // colour is what reads from the camera, not the 5.5 mm.
            // Nearly flush with the torso. At 5.5 mm proud these read as raised
            // ridges from the overhead camera and the cat looks like a caterpillar;
            // ~1.5 mm keeps them as colour bands, which is all they are for.
            var scale = new Vector3(0.128f, 0.128f, 0.016f);
            for (int i = 0; i < count; i++)
            {
                float f = count == 1 ? 0.5f : i / (float)(count - 1);
                float z = Mathf.Lerp(-0.075f, 0.055f, f);
                Sphere("Stripe_" + i, t, new Vector3(0f, TorsoY, z), scale, stripe);
            }
        }

        static void BuildHead(Transform t, Material fur, Material accent, Material inner, Material eye, float earMul)
        {
            Sphere("Head", t, new Vector3(0f, 0.186f, 0.152f), new Vector3(0.112f, 0.107f, 0.108f), fur);
            Sphere("Muzzle", t, new Vector3(0f, 0.170f, 0.192f), new Vector3(0.062f, 0.048f, 0.055f), accent);
            Sphere("Eye_L", t, new Vector3(-0.027f, 0.194f, 0.196f), new Vector3(0.014f, 0.014f, 0.014f), eye);
            Sphere("Eye_R", t, new Vector3(0.027f, 0.194f, 0.196f), new Vector3(0.014f, 0.014f, 0.014f), eye);

            BuildEar("Ear_L", t, -1f, inner, fur, earMul);
            BuildEar("Ear_R", t, 1f, inner, fur, earMul);
        }

        static void BuildEar(string name, Transform t, float side, Material inner, Material fur, float earMul)
        {
            // Pivot empty carries the pose at unit scale so the two cones can be
            // scaled independently without their offsets picking up the parent's
            // squash.
            var pivot = new GameObject(name);
            pivot.transform.SetParent(t, false);
            pivot.transform.localPosition = new Vector3(side * 0.036f, 0.216f, 0.143f);
            pivot.transform.localRotation = Quaternion.Euler(-8f, 0f, side * -14f);
            pivot.transform.localScale = Vector3.one;

            // Cone mesh is centred: 1 unit tall, 1 wide, apex up. Base is buried
            // in the skull so the ear never floats.
            float h = 0.048f * earMul;
            // Leaf names are unique across the whole rig on purpose: CatVisualDump
            // keys on leaf name so its before/after diff survives reparenting, and
            // "Outer" appearing under both ears would collide.
            Cone(name + "_Outer", pivot.transform,
                new Vector3(0f, h * 0.5f, 0f),
                new Vector3(0.056f * earMul, h, 0.030f * earMul), fur);

            float ih = 0.036f * earMul;
            Cone(name + "_Inner", pivot.transform,
                new Vector3(0f, ih * 0.5f, 0.008f * earMul),
                new Vector3(0.030f * earMul, ih, 0.014f * earMul), inner);
        }

        static void BuildTail(Transform t, Material fur, Material tip, int side)
        {
            // Seven overlapping spheres. Kept low and mostly horizontal because a
            // long thin line trailing the body is the strongest cat cue in the
            // security camera's oblique view; a vertical tail foreshortens away.
            // Consecutive radii overlap even at side = +-1, so the tail never
            // breaks into beads.
            var pos = new[]
            {
                new Vector3(0.000f, 0.172f, -0.180f),
                new Vector3(0.006f, 0.176f, -0.206f),
                new Vector3(0.017f, 0.180f, -0.229f),
                new Vector3(0.030f, 0.185f, -0.247f),
                new Vector3(0.044f, 0.191f, -0.260f),
                new Vector3(0.058f, 0.199f, -0.268f),
                new Vector3(0.070f, 0.209f, -0.270f)
            };
            var dia = new[] { 0.032f, 0.029f, 0.026f, 0.023f, 0.021f, 0.018f, 0.016f };

            for (int i = 0; i < pos.Length; i++)
            {
                Vector3 p = pos[i];
                p.x *= side;
                Material m = i >= pos.Length - 2 ? tip : fur;
                Sphere("Tail_" + i, t, p, new Vector3(dia[i], dia[i], dia[i]), m);
            }
        }

        // ---- palette ----

        struct Palette
        {
            public Color Fur;
            public Color Accent;
            public Color Stripe;
            public Color InnerEar;
            public Color Eye;
            public float FurSmoothness;
            public int StripeCount;
            public bool TailTipIsAccent;
        }

        /// <summary>
        /// Derives markings from the fur colour so black / orange / gray each get
        /// a different pattern on top of a different hue. Colour alone is weak in
        /// a dim warm basement; the pattern is what separates them on the feed.
        /// </summary>
        static Palette MakePalette(Color fur, System.Random rng)
        {
            Color.RGBToHSV(fur, out float h, out float s, out float v);

            bool isDark = v < 0.25f;
            bool isWarm = s > 0.35f;

            var pal = new Palette();

            // A pure-black cat is a hole in the frame under 0.22 ambient. Lift the
            // floor slightly and raise smoothness so the two point lights leave a
            // rim highlight - that, plus the white markings, is what makes a black
            // cat visible on a real security camera.
            pal.Fur = isDark ? Color.HSVToRGB(h, s, Mathf.Max(v, 0.12f)) : fur;
            pal.FurSmoothness = isDark ? 0.36f : 0.18f;

            if (isDark)
            {
                // Tuxedo: white bib, paws and tail tip, no stripes.
                pal.Accent = new Color(0.90f, 0.89f, 0.86f);
                pal.Stripe = pal.Accent;
                pal.StripeCount = 0;
                pal.TailTipIsAccent = true;
            }
            else if (isWarm)
            {
                // Tabby: four dark rings across the spine, cream underside.
                pal.Accent = Color.HSVToRGB(h, s * 0.45f, Mathf.Clamp01(v * 1.35f + 0.12f));
                pal.Stripe = Color.HSVToRGB(h, Mathf.Clamp01(s * 1.05f), Mathf.Clamp01(v * 0.52f));
                pal.StripeCount = 4;
                pal.TailTipIsAccent = false;
            }
            else
            {
                // Gray: light underside, two soft rings, dark tail tip.
                pal.Accent = Color.HSVToRGB(h, s * 0.60f, Mathf.Clamp01(v * 1.45f + 0.10f));
                pal.Stripe = Color.HSVToRGB(h, s, Mathf.Clamp01(v * 0.55f));
                pal.StripeCount = 2;
                pal.TailTipIsAccent = false;
            }

            pal.InnerEar = Color.Lerp(pal.Accent, new Color(0.85f, 0.52f, 0.52f), 0.55f);
            pal.Eye = Color.HSVToRGB(Mathf.Lerp(0.10f, 0.22f, (float)rng.NextDouble()), 0.85f, 0.95f);
            return pal;
        }

        static int SeedFromColor(Color c)
        {
            unchecked
            {
                int seed = Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f) * 73856093
                           ^ Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f) * 19349663
                           ^ Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f) * 83492791;
                return seed & 0x7fffffff;
            }
        }

        // ---- primitive helpers ----

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Quaternion rot, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;

            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);

            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = mat;
            return go;
        }

        static GameObject Sphere(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            return Prim(PrimitiveType.Sphere, name, parent, pos, scale, Quaternion.identity, mat);
        }

        static GameObject Cylinder(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            return Prim(PrimitiveType.Cylinder, name, parent, pos, scale, Quaternion.identity, mat);
        }

        static GameObject Cone(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = EarMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // ---- procedural cone mesh (there is no PrimitiveType.Cone) ----

        static Mesh EarMesh()
        {
            if (_earMesh != null) return _earMesh;

            EnsureFolderPath(MeshDir);

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(EarMeshPath);
            bool isNew = mesh == null;
            if (isNew) mesh = new Mesh();

            mesh.Clear();
            mesh.name = "CatEar";
            FillCone(mesh, EarSegments);

            if (isNew) AssetDatabase.CreateAsset(mesh, EarMeshPath);
            else EditorUtility.SetDirty(mesh);

            _earMesh = mesh;
            return mesh;
        }

        /// <summary>
        /// Flat-shaded cone: apex at +0.5 Y, base circle of radius 0.5 at -0.5 Y,
        /// so it occupies the same 1x1x1 box convention as the sphere primitive.
        /// Winding follows Unity's rule that a triangle (a,b,c) faces along
        /// Cross(b-a, c-a).
        /// </summary>
        static void FillCone(Mesh mesh, int segments)
        {
            int sideCount = segments * 3;
            int total = sideCount * 2;

            var verts = new Vector3[total];
            var norms = new Vector3[total];
            var uvs = new Vector2[total];
            var tris = new int[total];

            var apex = new Vector3(0f, 0.5f, 0f);
            var baseCenter = new Vector3(0f, -0.5f, 0f);
            const float r = 0.5f;

            for (int i = 0; i < segments; i++)
            {
                float f0 = i / (float)segments;
                float f1 = (i + 1) / (float)segments;
                float a0 = f0 * Mathf.PI * 2f;
                float a1 = f1 * Mathf.PI * 2f;

                var p0 = new Vector3(Mathf.Cos(a0) * r, -0.5f, Mathf.Sin(a0) * r);
                var p1 = new Vector3(Mathf.Cos(a1) * r, -0.5f, Mathf.Sin(a1) * r);

                int s = i * 3;
                verts[s] = apex;
                verts[s + 1] = p1;
                verts[s + 2] = p0;
                Vector3 n = Vector3.Cross(p1 - apex, p0 - apex).normalized;
                norms[s] = n;
                norms[s + 1] = n;
                norms[s + 2] = n;
                uvs[s] = new Vector2(0.5f, 1f);
                uvs[s + 1] = new Vector2(f1, 0f);
                uvs[s + 2] = new Vector2(f0, 0f);
                tris[s] = s;
                tris[s + 1] = s + 1;
                tris[s + 2] = s + 2;

                int b = sideCount + i * 3;
                verts[b] = baseCenter;
                verts[b + 1] = p0;
                verts[b + 2] = p1;
                norms[b] = Vector3.down;
                norms[b + 1] = Vector3.down;
                norms[b + 2] = Vector3.down;
                uvs[b] = new Vector2(0.5f, 0.5f);
                uvs[b + 1] = new Vector2(0.5f + Mathf.Cos(a0) * 0.5f, 0.5f + Mathf.Sin(a0) * 0.5f);
                uvs[b + 2] = new Vector2(0.5f + Mathf.Cos(a1) * 0.5f, 0.5f + Mathf.Sin(a1) * 0.5f);
                tris[b] = b;
                tris[b + 1] = b + 1;
                tris[b + 2] = b + 2;
            }

            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
        }

        // ---- assets ----

        static Material EnsureMaterial(string assetName, Color color, float smoothness)
        {
            EnsureFolderPath(MaterialDir);

            string path = MaterialDir + "/" + assetName + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool isNew = mat == null;
            if (isNew)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                mat = new Material(shader);
            }

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            mat.color = color;
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", smoothness); // Standard fallback
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            if (isNew) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);

            return mat;
        }

        static void EnsureFolderPath(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;

            int cut = path.LastIndexOf('/');
            if (cut <= 0) return; // "Assets" always exists, so recursion stops here

            string parent = path.Substring(0, cut);
            string leaf = path.Substring(cut + 1);
            EnsureFolderPath(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static string Sanitize(string s)
        {
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!ok) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}
