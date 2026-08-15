using System;
using System.IO;
using BepInEx.Preloader.Core.Patching;
using MelonLoader.Hosting;

namespace MelonLoader.Patcher
{
    /// <summary>
    /// BepInEx 6 preloader patcher that maintains the Il2Cpp.* alias types in the game
    /// interop assemblies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The aliases MUST be written from the patcher's CONSTRUCTOR, not Initialize(). BepInEx's
    /// IL2CPP preloader flow is:
    /// </para>
    /// <code>
    ///   Il2CppInteropManager.Initialize()              // generates interop files, starts runtime
    ///   assemblyPatcher.AddPatchersFromDirectory(...)  // Loads this patcher - Activator.CreateInstance
    ///                                                  //   runs the CONSTRUCTOR here.
    ///   assemblyPatcher.LoadAssemblyDirectories(interop)
    ///                                                  // Mono.Cecil ReadAssembly (InMemory=false)
    ///                                                  //   LOCKS every interop file.
    ///   assemblyPatcher.PatchAndLoad()                 // calls patcher.Initialize() here - the
    ///                                                  //   interop is ALREADY locked = access denied.
    /// </code>
    /// So Initialize() is too late to rewrite the interop. From the constructor the files are
    /// still writable, and BepInEx's Cecil pass then reads OUR aliased version and loads it, so
    /// the aliases take effect on the same launch.
    /// </remarks>
    [PatcherPluginInfo("MelonLoader.InteropAliases", "BepInEx.MelonLoader.Loader.Patcher", "2.3.8")]
    public class InteropAliasPatcher : BasePatcher
    {
        public InteropAliasPatcher()
        {
            try
            {
                EnsureAliases();
            }
            catch (Exception ex)
            {
                Log?.LogError($"[MelonLoader.Patcher] interop alias injection failed in ctor: {ex}");
            }
        }

        public override void Initialize()
        {
            // Aliases are maintained in the constructor (see class remarks); by the time
            // Initialize() runs BepInEx has already Cecil-loaded/locked the interop files.
            Log?.LogInfo("[MelonLoader.Patcher] interop aliases handled during preloader load");
        }

        public override void Finalizer()
        {
        }

        private void EnsureAliases()
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
            // EnsureAliases returns true when it (re)wrote aliases, false when already up to
            // date OR when the write failed (partial). Distinguish by checking the marker.
            var marker = Path.Combine(interopDir, ".melonloader-aliased");
            if (changed)
                Log?.LogInfo("[MelonLoader.Patcher] interop aliases (re)generated for this launch");
            else if (File.Exists(marker))
                Log?.LogInfo("[MelonLoader.Patcher] interop aliases up to date");
            else
                Log?.LogWarning("[MelonLoader.Patcher] interop aliases NOT generated (files were locked or failed)");
        }
    }
}
