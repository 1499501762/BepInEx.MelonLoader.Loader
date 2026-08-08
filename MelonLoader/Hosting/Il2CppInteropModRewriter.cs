using System;
using System.IO;
using Mono.Cecil;

namespace MelonLoader.Hosting
{
    /// <summary>
    /// Rewrites MelonLoader mod assemblies so game-type references resolve against
    /// BepInEx's Il2CppInterop interop assemblies.
    /// <para>
    /// MelonLoader's Il2CppInterop generator namespaces every game type with an
    /// "Il2Cpp." prefix (e.g. <c>Il2Cpp.RecordItem</c>, <c>Il2CppSleepyNodes.X</c>).
    /// BepInEx 6's Il2CppInterop keeps the original namespaces. A mod compiled against
    /// MelonLoader's interop therefore fails with a TypeLoadException the first time it
    /// touches a game type. This rewriter strips the "Il2Cpp" prefix so the mod binds to
    /// BepInEx's interop types instead (and keeps BepInEx plugins like Coop untouched).
    /// </para>
    /// </summary>
    public static class Il2CppInteropModRewriter
    {
        /// <summary>
        /// Returns rewritten assembly bytes if any game-type references needed fixing,
        /// or <c>null</c> if the assembly can be loaded unchanged.
        /// </summary>
        public static byte[] RewriteIfNeeded(string path)
        {
            try
            {
                using var module = ModuleDefinition.ReadModule(path, new ReaderParameters
                {
                    InMemory = true,
                    ReadSymbols = false
                });

                if (!RewriteModule(module))
                    return null;

                using (var ms = new MemoryStream())
                {
                    module.Write(ms);
                    return ms.ToArray();
                }
            }
            catch
            {
                // If we can't read/rewrite the module, fall back to loading it unchanged.
                return null;
            }
        }

        /// <summary>
        /// Rewrites every assembly under <paramref name="baseDirectory"/> that references
        /// MelonLoader-style "Il2Cpp.*" game types, in place. This covers assemblies that a
        /// mod loads at runtime on its own (e.g. hot-reloaded logic DLLs), which bypass the
        /// in-memory rewrite done at MelonAssembly load time.
        /// </summary>
        public static void RewriteAllOnDisk(string baseDirectory)
        {
            try
            {
                if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
                    return;

                foreach (var file in Directory.GetFiles(baseDirectory, "*.dll", SearchOption.AllDirectories))
                {
                    try
                    {
                        var rewritten = RewriteIfNeeded(file);
                        if (rewritten == null)
                            continue;

                        File.WriteAllBytes(file, rewritten);
                        MelonLogger.Msg($"[BepInExHost] Rewrote '{file}' for BepInEx interop compatibility");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[BepInExHost] Failed to rewrite '{file}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[BepInExHost] Failed to scan for interop rewrites: {ex}");
            }
        }

        private static bool RewriteModule(ModuleDefinition module)
        {
            bool changed = false;
            foreach (var tr in module.GetTypeReferences())
            {
                if (RewriteTypeReference(tr))
                    changed = true;
            }

            return changed;
        }

        private static bool RewriteTypeReference(TypeReference tr)
        {
            if (tr == null)
                return false;

            // Nested types: their scope is the declaring type; walk up the chain.
            if (tr.DeclaringType != null)
                return RewriteTypeReference(tr.DeclaringType);

            if (!(tr.Scope is AssemblyNameReference scope))
                return false;

            string scopeName = scope.Name;

            // Framework assemblies legitimately start with "Il2Cpp" and are identical in
            // both interop sets - never touch them.
            if (IsFrameworkAssembly(scopeName))
                return false;

            bool changed = false;

            // Strip the "Il2Cpp." namespace prefix (MelonLoader convention) so the type
            // resolves against BepInEx's original-namespace interop.
            string ns = tr.Namespace;
            if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Il2Cpp", StringComparison.Ordinal))
            {
                tr.Namespace = ns.Substring(6); // "Il2Cpp" is 6 characters
                changed = true;
            }

            // Strip the "Il2Cpp" prefix from the referenced assembly name
            // (e.g. Il2CppFMODUnity -> FMODUnity) to match BepInEx's interop naming.
            if (scopeName.StartsWith("Il2Cpp", StringComparison.Ordinal))
            {
                scope.Name = scopeName.Substring(6);
                changed = true;
            }

            return changed;
        }

        private static bool IsFrameworkAssembly(string name)
        {
            if (name == "Il2Cppmscorlib")
                return true;
            if (name.StartsWith("Il2CppSystem", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("Il2CppInterop", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("UnityEngine", StringComparison.Ordinal))
                return true;
            return false;
        }
    }
}
