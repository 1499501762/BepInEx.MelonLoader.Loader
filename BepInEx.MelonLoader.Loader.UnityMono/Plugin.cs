using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Unity.Mono;
using MelonLoader.Hosting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BepInEx.MelonLoader.Loader.UnityMono;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private bool _lateStartFired;
    private readonly System.Collections.Generic.List<string> _seenScenes =
        new System.Collections.Generic.List<string>();

    private void Awake()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            if (args.Name.Contains("MelonLoader"))
                return typeof(BepInExHost).Assembly;
            return null;
        };

        // Initialize only here. Core.Start (which loads mods) is deferred to the first
        // frame update, so mods that look up scene objects run once the scene is ready.
        BepInExHost.Initialize(GetMelonLoaderBaseDirectory());
    }

    // BaseUnityPlugin is a MonoBehaviour, so drive MelonLoader's game-loop events
    // directly from Unity's lifecycle (the native SupportModule doesn't deliver
    // these in the BepInEx-hosted context). Scene changes are detected by polling
    // ALL loaded scenes - games often load scenes additively, so watching only the
    // active scene would miss them.
    private void Update()
    {
        if (!BepInExHost.HasStarted)
            BepInExHost.Start();

        if (!_lateStartFired)
        {
            _lateStartFired = true;
            BepInExHost.InvokeOnApplicationLateStart();
        }

        BepInExHost.InvokeUpdate();

        // The native SupportModule coroutine runner is unavailable when BepInEx hosts
        // MelonLoader, so advance MelonCoroutines ourselves.
        global::MelonLoader.MelonCoroutines.ProcessQueue(GetWaitSeconds, Time.time);

        DetectSceneChanges();
    }

    /// <summary>
    /// Resolves Unity yield instructions to a wait duration in seconds.
    /// </summary>
    private static float? GetWaitSeconds(object yield)
    {
        if (yield == null)
            return null;

        var type = yield.GetType();
        if (type.Name == "WaitForSeconds")
        {
            try
            {
                var field = type.GetField("m_Seconds",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                    return (float)field.GetValue(yield);
            }
            catch
            {
                // fall through
            }

            return 1f;
        }

        return null;
    }

    private void FixedUpdate() => BepInExHost.InvokeFixedUpdate();
    private void LateUpdate() => BepInExHost.InvokeLateUpdate();
    private void OnGUI() => BepInExHost.InvokeOnGUI();

    private void DetectSceneChanges()
    {
        try
        {
            int count = SceneManager.sceneCount;
            var current = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                string key = scene.buildIndex + "|" + scene.name;
                current.Add(key);
                if (!_seenScenes.Contains(key))
                {
                    _seenScenes.Add(key);
                    BepInExHost.InvokeSceneWasLoaded(scene.buildIndex, scene.name);
                }
            }

            // Drop scenes that have been unloaded so a later reload is reported again.
            for (int i = _seenScenes.Count - 1; i >= 0; i--)
            {
                if (!current.Contains(_seenScenes[i]))
                    _seenScenes.RemoveAt(i);
            }
        }
        catch
        {
            // Scene detection is best-effort; never break the frame loop.
        }
    }

    private static string GetMelonLoaderBaseDirectory()
    {
        var gameDir = ".";
        try
        {
            gameDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) ?? ".";
        }
        catch
        {
            // Fall back to the working directory if the executable path can't be resolved.
        }

        return Path.Combine(gameDir, "MLLoader");
    }
}