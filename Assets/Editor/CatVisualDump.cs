using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace WorldSim.EditorTools
{
    /// <summary>
    /// Prints where every piece of a cat actually ends up in world space.
    ///
    /// This exists for one job: CatVisualBuilder's rig has to be restructured
    /// (legs and head reparented under animation pivots) without moving a single
    /// visible part, and there is no Play mode available to eyeball that. Dumping
    /// leaf world positions before and after the restructure and diffing the two
    /// turns "the cat probably didn't fall apart" into a measurement.
    ///
    /// Keyed on leaf name, never on hierarchy path, precisely because the
    /// hierarchy is what changes. Every leaf name in the rig is unique, so the
    /// key survives reparenting.
    ///
    ///   Unity -batchmode -projectPath . -quit \
    ///     -executeMethod WorldSim.EditorTools.CatVisualDump.DumpFromBatch \
    ///     -logFile dump.log
    ///
    /// Writes CatVisualDump.txt at the project root (gitignored build output; the
    /// interesting artifact is the diff, not the file).
    /// </summary>
    public static class CatVisualDump
    {
        const string OutputPath = "CatVisualDump.txt";

        // The exact cast CatBasementSceneBuilder spawns. Per-cat size jitter is
        // derived from the fur colour, so these three colours are the only inputs
        // that reproduce the three cats that actually appear on the feed.
        static readonly (string Name, Color Fur)[] Cast =
        {
            ("Cat_Black", new Color(0.08f, 0.08f, 0.08f)),
            ("Cat_Orange", new Color(0.85f, 0.45f, 0.12f)),
            ("Cat_Gray", new Color(0.5f, 0.5f, 0.52f))
        };

        [MenuItem("WorldSim/Dump Cat Visual Rest Pose")]
        public static void DumpFromBatch()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# leaf world pose of every cat part, at the rest pose the builder produces");
            sb.AppendLine("# columns: cat  leaf  posX posY posZ  scaleX scaleY scaleZ");

            foreach ((string name, Color fur) in Cast)
            {
                var host = new GameObject("__dump_" + name);
                host.transform.position = Vector3.zero;
                host.transform.rotation = Quaternion.identity;
                host.transform.localScale = Vector3.one;

                GameObject visual = CatVisualBuilder.BuildCatVisual(host.transform, fur, name);

                var rows = new List<string>();
                foreach (Renderer r in visual.GetComponentsInChildren<Renderer>(true))
                    rows.Add(Row(name, r.transform));
                rows.Sort(System.StringComparer.Ordinal);

                sb.AppendLine();
                sb.AppendLine("## " + name + "  (" + rows.Count + " parts)");
                foreach (string row in rows) sb.AppendLine(row);

                Object.DestroyImmediate(host);
            }

            System.IO.File.WriteAllText(OutputPath, sb.ToString());
            Debug.Log("CatVisualDump: wrote " + OutputPath);
        }

        /// <summary>
        /// Six decimals is a micron. Anything that moves less than that is float
        /// noise from a different multiplication order, not a rig change.
        /// </summary>
        static string Row(string cat, Transform t)
        {
            Vector3 p = t.position;
            Vector3 s = t.lossyScale;
            return string.Format(CultureInfo.InvariantCulture,
                "{0}  {1,-12}  {2,10:F6} {3,10:F6} {4,10:F6}   {5,9:F6} {6,9:F6} {7,9:F6}",
                cat, t.name, p.x, p.y, p.z, s.x, s.y, s.z);
        }
    }
}
