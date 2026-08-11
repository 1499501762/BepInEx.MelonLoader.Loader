using System;
using System.IO;
using Mono.Cecil;
using Mono.Collections.Generic;

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

            // typeof(GameType) used inside a custom attribute (e.g. Harmony's
            // [HarmonyPatch(typeof(Il2Cpp.EntityLocation), "Method")]) is stored in the
            // attribute blob, not as a metadata TypeRef, so GetTypeReferences() never
            // sees it. Without this, Harmony throws TypeLoadException when it reads the
            // patch annotations and the mod's patches silently never apply. Harmony
            // annotations can live on a type, a method, a field, a property or an event,
            // so all of those attribute collections are visited.
            foreach (var type in module.GetTypes())
            {
                changed |= RewriteMemberAttributes(type.CustomAttributes);
                foreach (var m in type.Methods)
                    changed |= RewriteMemberAttributes(m.CustomAttributes);
                foreach (var f in type.Fields)
                    changed |= RewriteMemberAttributes(f.CustomAttributes);
                foreach (var p in type.Properties)
                    changed |= RewriteMemberAttributes(p.CustomAttributes);
                foreach (var e in type.Events)
                    changed |= RewriteMemberAttributes(e.CustomAttributes);
            }

            return changed;
        }

        private static bool RewriteMemberAttributes(Collection<CustomAttribute> attributes)
        {
            bool changed = false;
            foreach (var attr in attributes)
            {
                try
                {
                    if (RewriteCustomAttribute(attr))
                        changed = true;
                }
                catch
                {
                    // Skip attributes we can't decode; member-reference rewrites still apply.
                }
            }

            return changed;
        }

        private static bool RewriteCustomAttribute(CustomAttribute attr)
        {
            bool changed = false;

            // The attribute type itself may be a game type in rare cases.
            if (RewriteTypeReference(attr.AttributeType))
                changed = true;

            // Constructor arguments - e.g. [HarmonyPatch(typeof(Il2Cpp.EntityLocation), "X")].
            foreach (var arg in attr.ConstructorArguments)
            {
                if (RewriteAttributeArgument(arg))
                    changed = true;
            }

            // Named arguments - e.g. [X(GameType = typeof(Il2Cpp.Y))].
            foreach (var named in attr.Properties)
            {
                if (RewriteAttributeArgument(named.Argument))
                    changed = true;
            }

            foreach (var named in attr.Fields)
            {
                if (RewriteAttributeArgument(named.Argument))
                    changed = true;
            }

            return changed;
        }

        private static bool RewriteAttributeArgument(CustomAttributeArgument arg)
        {
            bool changed = false;

            if (arg.Value is TypeReference typeRef)
            {
                if (RewriteTypeReference(typeRef))
                    changed = true;
            }
            else if (arg.Value is CustomAttributeArgument nested)
            {
                if (RewriteAttributeArgument(nested))
                    changed = true;
            }
            else if (arg.Value is CustomAttributeArgument[] array)
            {
                foreach (var element in array)
                {
                    if (RewriteAttributeArgument(element))
                        changed = true;
                }
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

            // Attribute-blob references (fallback-decoded by Mono.Cecil) can use a
            // ModuleReference scope instead of an AssemblyNameReference; accept both.
            // Assembly-scope references also get their "Il2Cpp" assembly-name prefix
            // stripped (Il2CppFMODUnity -> FMODUnity) to match BepInEx's interop naming.
            string scopeName;
            if (tr.Scope is AssemblyNameReference)
                scopeName = ((AssemblyNameReference)tr.Scope).Name;
            else if (tr.Scope is ModuleReference)
                scopeName = ((ModuleReference)tr.Scope).Name;
            else
                return false;

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
            if (tr.Scope is AssemblyNameReference asm && scopeName.StartsWith("Il2Cpp", StringComparison.Ordinal))
            {
                asm.Name = scopeName.Substring(6);
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
