using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace WorldSim.EditorTools
{
    /// <summary>
    /// Opens the scene supplied after <c>-scene</c> and enters Play mode when the
    /// editor is launched with <c>-autoplay</c>. This lets a saved Pi Unity scenario
    /// select its own Unity scene instead of baking one scenario into the harness.
    ///
    /// Does nothing without the flag, so a normal editor session is unaffected.
    /// </summary>
    [InitializeOnLoad]
    public static class AutoPlay
    {
        const string Flag = "-autoplay";
        const string SceneFlag = "-scene";
        static string _scenePath;

        static AutoPlay()
        {
            if (!HasFlag()) return;

            _scenePath = ArgumentValue(SceneFlag);
            if (string.IsNullOrEmpty(_scenePath))
            {
                Debug.LogError("AutoPlay: -scene Assets/.../*.unity is required with -autoplay");
                EditorApplication.Exit(2);
                return;
            }

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

        static string ArgumentValue(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.Ordinal))
                    return args[i + 1];
            return null;
        }

        static void Begin()
        {
            try
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(_scenePath) == null)
                    throw new InvalidOperationException("scene asset not found: " + _scenePath);

                if (EditorSceneManager.GetActiveScene().path != _scenePath)
                    EditorSceneManager.OpenScene(_scenePath, OpenSceneMode.Single);

                Debug.Log("AutoPlay: entering Play mode for " + _scenePath);
                EditorApplication.EnterPlaymode();
            }
            catch (Exception e)
            {
                Debug.LogError("AutoPlay: could not enter Play mode - " + e);
                EditorApplication.Exit(2);
            }
        }
    }
}
