#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using MelonLoader.Utils;

namespace MelonLoader.Hosting
{
    /// <summary>
    /// Compatibility self-check (roadmap item 7.5). Before mods load, statically scans each mod
    /// assembly with dnlib for calls to native-MelonLoader APIs that are not truly available
    /// under the BepInEx bridge (e.g. <c>MelonUtils.NativeHookAttach</c> is a no-op here), and
    /// prints an explicit warning so users know such a mod may misbehave and don't file invalid
    /// issues downstream. Metadata-only scan - never loads or rewrites the mod (Approach A safe).
    /// </summary>
    internal static class ModCompatScanner
    {
        // Fully-qualified call targets that are no-ops / unavailable under the BepInEx bridge,
        // mapped to the reason users should see. Kept deliberately small and focused on APIs
        // mods can actually call directly.
        private static readonly Dictionary<string, string> UnsupportedApi = new(StringComparer.Ordinal)
        {
            ["MelonLoader.MelonUtils::NativeHookAttach"] =
                "native hook attach is a NO-OP under the BepInEx bridge (the hook will NOT be installed)",
            ["MelonLoader.MelonUtils::NativeHookDetach"] =
                "native hook detach is a NO-OP under the BepInEx bridge",
            ["MelonLoader.Imports::Hook"] =
                "obsolete compatibility API - hook is a NO-OP under the BepInEx bridge, use Harmony patches instead",
            ["MelonLoader.Imports::Unhook"] =
                "obsolete compatibility API - unhook is a NO-OP under the BepInEx bridge, use Harmony patches instead",
        };

        /// <summary>Scans every mod assembly under <paramref name="modsDir"/> and warns on hits.</summary>
        public static void Scan(string modsDir)
        {
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
                return;

            int scanned = 0, warned = 0;
            foreach (var file in Directory.GetFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                scanned++;
                var hits = ScanAssembly(file);
                if (hits.Count == 0)
                    continue;
                warned++;
                MelonLogger.Warning($"[CompatScan] '{Path.GetFileName(file)}' 调用了在 BepInEx 桥接下未完整实现的 MelonLoader API：");
                foreach (var h in hits)
                    MelonLogger.Warning($"  - {h.Key}: {h.Value}");
                MelonLogger.Warning($"  -> 该 mod 依赖原生 MelonLoader 能力，在此加载器下相关功能可能异常；如遇问题请反馈给 BepInEx.MelonLoader.Loader 作者，而不是 mod 作者。");
            }

            // Always log a summary so the scan is observable and its coverage verifiable.
            MelonLogger.Msg($"[CompatScan] 扫描 {scanned} 个 mod 程序集，{warned} 个含未实现 API 调用。");
        }

        private static List<KeyValuePair<string, string>> ScanAssembly(string file)
        {
            var hits = new List<KeyValuePair<string, string>>();
            ModuleDefMD mod = null;
            try
            {
                mod = ModuleDefMD.Load(file);
                foreach (var type in mod.GetTypes())
                {
                    if (type == null)
                        continue;
                    foreach (var method in type.Methods)
                    {
                        if (method == null || !method.HasBody)
                            continue;
                        foreach (var instr in method.Body.Instructions)
                        {
                            if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt && instr.OpCode != OpCodes.Newobj)
                                continue;

                            string decl = null, name = null;
                            if (instr.Operand is MemberRef mr)
                            {
                                decl = mr.DeclaringType?.FullName;
                                name = mr.Name;
                            }
                            else if (instr.Operand is MethodSpec ms && ms.Method is MemberRef mr2)
                            {
                                decl = mr2.DeclaringType?.FullName;
                                name = mr2.Name;
                            }
                            if (decl == null || name == null)
                                continue;

                            var key = decl + "::" + name;
                            if (UnsupportedApi.TryGetValue(key, out var reason) && !hits.Exists(k => k.Key == key))
                                hits.Add(new KeyValuePair<string, string>(key, reason));
                        }
                    }
                }
            }
            catch
            {
                // Cannot read the assembly - skip silently (it would fail to load anyway).
            }
            finally
            {
                // Release the memory-mapped handle so the file is not left locked for mod loading.
                mod?.Dispose();
            }
            return hits;
        }
    }
}
#endif
