using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace WorldSim.EditorTools
{
    /// <summary>
    /// Opens CatBasement and enters Play mode when the editor is launched with
    /// <c>-autoplay</c>. Lets an unattended run bring the sim up so the external
    /// acceptance harness (tools/acceptance.py) can drive it — press-Play by
    /// script rather than by hand.
    ///
    /// Does nothing without the flag, so a normal editor session is unaffected.
    /// </summary>
    [InitializeOnLoad]
    public static class AutoPlay
    {
        const string Flag = "-autoplay";
        const string ScenePath = "Assets/WorldSim/Scenarios/CatBasement/CatBasement.unity";

        static AutoPlay()
        {
            if (!HasFlag()) return;

            // Entering Play mode reloads the domain and re-runs this constructor.
            // Without this guard we would ask to enter Play mode again from inside
            // it, which Unity logs as an error.
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            // delayCall so the asset database and scene are ready to touch.
            EditorApplication.delayCall += Begin;
        }

        static bool HasFlag()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (string.Equals(arg, Flag, StringComparison.Ordinal))
                    return true;
            return false;
        }

        static void Begin()
        {
            try
            {
                if (EditorSceneManager.GetActiveScene().path != ScenePath)
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                Debug.Log("AutoPlay: entering Play mode for " + ScenePath);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception e)
            {
                Debug.LogError("AutoPlay: could not enter Play mode - " + e);
            }
        }
    }
}
