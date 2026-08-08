#if NET6_0_OR_GREATER
using System;
using System.Linq;
using System.Reflection;
using AsmResolver;
using AsmResolver.DotNet;
using HarmonyLib;
using Il2CppInterop.Generator.Contexts;
using MelonLoader.Fixes.AsmResolver;

namespace MelonLoader.Fixes.Il2CppInterop
{
    // Herp: This fixes a string validation issue with Il2CppInterop's MethodRewriteContext.UnmangleMethodNameWithSignature
    internal class Il2CppInteropUnmangleMethodNameFix
    {
        private static MethodInfo _produceMethodSignatureBase;
        private static MethodInfo _parameterSignatureMatchesThis;
        
        private static MethodInfo _unmangleMethodNameWithSignature;
        private static MethodInfo _unmangleMethodNameWithSignaturePrefix;

        internal static void Install()
        {
            try
            {
                Type thisType = typeof(Il2CppInteropUnmangleMethodNameFix);
                Type contextType = typeof(MethodRewriteContext);

                _produceMethodSignatureBase = contextType.GetMethod("ProduceMethodSignatureBase", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_produceMethodSignatureBase == null)
                    throw new Exception("Failed to get MethodRewriteContext.ProduceMethodSignatureBase");

                _parameterSignatureMatchesThis = contextType.GetMethod("ParameterSignatureMatchesThis", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_parameterSignatureMatchesThis == null)
                    throw new Exception("Failed to get MethodRewriteContext.ParameterSignatureMatchesThis");

                _unmangleMethodNameWithSignature = contextType.GetMethod("UnmangleMethodNameWithSignature", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_unmangleMethodNameWithSignature == null)
                    throw new Exception("Failed to get MethodRewriteContext.UnmangleMethodNameWithSignature");

                _unmangleMethodNameWithSignaturePrefix = thisType.GetMethod(nameof(UnmangleMethodNameWithSignaturePrefix), BindingFlags.NonPublic | BindingFlags.Static);
                if (_unmangleMethodNameWithSignaturePrefix == null)
                    throw new Exception("Failed to get Il2CppInteropUnmangleMethodNameFix.UnmangleMethodNameWithSignaturePrefix");

                MelonDebug.Msg($"Patching Il2CppInterop MethodRewriteContext.UnmangleMethodNameWithSignature...");
                Core.HarmonyInstance.Patch(_unmangleMethodNameWithSignature,
                    new HarmonyMethod(_unmangleMethodNameWithSignaturePrefix));
            }
            catch (Exception e)
            {
                MelonLogger.Warning(e);
            }
        }

        private static bool UnmangleMethodNameWithSignaturePrefix(MethodRewriteContext __instance, ref string __result)
        {
            string baseSig = (string)_produceMethodSignatureBase.Invoke(__instance, []);

            int methodCount = 0;
            var allMethods = __instance.DeclaringType.Methods;
            if (allMethods.Count() > 0)
            {
                var matchesFound = allMethods.Where((x) => (bool)_parameterSignatureMatchesThis.Invoke(__instance, [x]));
                if (matchesFound.Count() > 0)
                {
                    matchesFound = matchesFound.TakeWhile(it => it != __instance);
                    methodCount = matchesFound.Count();
                }
            }

            var unmangleMethodNameWithSignature = $"{baseSig}_{methodCount}";

            if (__instance.DeclaringType.AssemblyContext.GlobalContext.Options.RenameMap.TryGetValue(
                    CombineStringsFixed($"{GetNamespacePrefixFixed(__instance.DeclaringType.NewType)}.", 
                    CombineStringsFixed(__instance.DeclaringType.NewType.Name, "::")) + unmangleMethodNameWithSignature, out var newNameByType))
            {
                unmangleMethodNameWithSignature = newNameByType;
            }
            else if (__instance.DeclaringType.AssemblyContext.GlobalContext.Options.RenameMap.TryGetValue(
                    GetNamespacePrefixFixed(__instance.DeclaringType.NewType) + "::" + unmangleMethodNameWithSignature, out var newName))
            {
                unmangleMethodNameWithSignature = newName;
            }

            __result = unmangleMethodNameWithSignature;
            return false;
        }

        private static string GetNamespacePrefixFixed(ITypeDefOrRef type)
        {
            if (type.DeclaringType is not null)
                return CombineStringsFixed($"{GetNamespacePrefixFixed(type.DeclaringType)}.", type.DeclaringType.Name);

            return type.Namespace;
        }

        private static string CombineStringsFixed(string a, Utf8String b)
        {
            if (string.IsNullOrEmpty(a))
                return string.Empty;

            if (Utf8String.IsNullOrEmpty(b))
                return a!;

            return AsmResolverUtf8StringConcatFix.ConcatFixed(a, b);
        }

        private static Utf8String CombineStringsFixed(Utf8String a, string b)
        {
            if (string.IsNullOrEmpty(b))
                return Utf8String.Empty;

            if (Utf8String.IsNullOrEmpty(a))
                return b!;

            return AsmResolverUtf8StringConcatFix.ConcatFixed(a, b);
        }
    }
}
#endif