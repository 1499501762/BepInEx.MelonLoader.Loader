using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Unity.IL2CPP;
using MelonLoader.Hosting;


namespace BepInEx.MelonLoader.Loader.IL2CPP
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        public override void Load()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                if (args.Name.Contains("MelonLoader"))
                    return typeof(BepInExHost).Assembly;
                return null;
            };

            // Initialize only here. Core.Start (which loads mods) is deferred to the first
            // frame update, so mods that look up scene objects (e.g. IronNestFCS finding
            // "Player Turret Piece") run once the game's initial scene is ready.
            BepInExHost.Initialize(GetMelonLoaderBaseDirectory());

            CreateGameLoopDriver();
        }

        /// <summary>
        /// BasePlugin is not a MonoBehaviour, so create our own driver component to
        /// drive MelonLoader's game-loop events (the native SupportModule doesn't
        /// deliver these in the BepInEx-hosted context).
        /// </summary>
        private static void CreateGameLoopDriver()
        {
            try
            {
                // BepInEx's official helper registers the managed type with
                // Il2CppInterop and adds it to a persistent (DontDestroyOnLoad)
                // GameObject, mirroring how other BepInEx IL2CPP mods add components.
                // (Non-generic overload: our driver is compiled against reference
                // UnityEngine assemblies, so it doesn't satisfy the T:Il2CppObjectBase
                // generic constraint at build time.)
                IL2CPPChainloader.AddUnityComponent(typeof(GameLoopDriver));
                global::MelonLoader.MelonLogger.Msg("[BepInExHost] GameLoopDriver created");
            }
            catch (Exception e)
            {
                global::MelonLoader.MelonLogger.Error($"[BepInExHost] Failed to create GameLoopDriver: {e}");
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
}