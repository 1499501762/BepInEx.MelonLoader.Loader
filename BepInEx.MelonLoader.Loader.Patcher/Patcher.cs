using System;
using System.IO;
using BepInEx.Preloader.Core.Patching;
using MelonLoader.Hosting;

namespace MelonLoader.Patcher
{
    /// <summary>
    /// BepInEx 6 preloader patcher that maintains the Il2Cpp.* alias types in the game
    /// interop assemblies. It runs in the preloader stage - BEFORE BepInEx generates /
    /// loads / memory-maps the interop assemblies - so the files are writable here (a
    /// plugin can never rewrite them because BepInEx already locked them by then).
    /// </summary>
    [PatcherPluginInfo("MelonLoader.InteropAliases", "BepInEx.MelonLoader.Loader.Patcher", "2.3.7")]
    public class InteropAliasPatcher : BasePatcher
    {
        public override void Initialize()
        {
            try
            {
                Il2CppInteropAliasInjector.LogWarning = msg => Log?.LogWarning(msg);

                var root = BepInEx.Paths.BepInExRootPath;
                if (string.IsNullOrEmpty(root))
                    root = Path.GetDirectoryName(AppContext.BaseDirectory);
                var interopDir = Path.Combine(root ?? "", "interop");
                Log?.LogInfo($"[MelonLoader.Patcher] interop dir: {interopDir}");

                if (!Directory.Exists(interopDir))
                {
                    Log?.LogInfo("[MelonLoader.Patcher] interop not present yet (first launch) - BepInEx will generate it");
                    return;
                }

                var changed = Il2CppInteropAliasInjector.EnsureAliases(interopDir);
                Log?.LogInfo(changed
                    ? "[MelonLoader.Patcher] interop aliases (re)generated for this launch"
                    : "[MelonLoader.Patcher] interop aliases up to date");
            }
            catch (Exception ex)
            {
                Log?.LogError($"[MelonLoader.Patcher] interop alias injection failed: {ex}");
            }
        }

        public override void Finalizer()
        {
        }
    }
}
