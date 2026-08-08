using System;
using MelonLoader.Hosting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BepInEx.MelonLoader.Loader.IL2CPP
{
    /// <summary>
    /// MonoBehaviour that drives MelonLoader's game-loop events from Unity directly.
    /// The native SupportModule does not deliver these events in the BepInEx-hosted
    /// context, so this loader creates its own driver component via Il2CppInterop.
    /// The type is registered at runtime through <c>ClassInjector.RegisterTypeInIl2Cpp</c>.
    /// Scene events are detected by polling ALL loaded scenes (games often load scenes
    /// additively, so watching only the active scene would miss them; subscribing to
    /// SceneManager's C# events fails on Il2Cpp because the interop UnityAction delegate
    /// lacks the managed (Object, IntPtr) constructor).
    /// </summary>
    public class GameLoopDriver : MonoBehaviour
    {
        private bool _lateStartFired;
        private readonly System.Collections.Generic.List<string> _seenScenes =
            new System.Collections.Generic.List<string>();

        private void Update()
        {
            // Defer mod loading until the game's initial scene is ready so mods that look
            // up scene objects on initialization work (Start is idempotent).
            if (!BepInExHost.HasStarted)
                BepInExHost.Start();

            if (!_lateStartFired)
            {
                _lateStartFired = true;
                BepInExHost.InvokeOnApplicationLateStart();
            }

            BepInExHost.InvokeUpdate();

            // The native SupportModule coroutine runner is unavailable when BepInEx hosts
            // MelonLoader, so advance MelonCoroutines ourselves (mods rely on e.g.
            // WaitForSeconds before re-binding on scene load).
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
    }
}
