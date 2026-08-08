#if NET6_0_OR_GREATER
using System;
using System.Reflection;
using AsmResolver;
using HarmonyLib;

namespace MelonLoader.Fixes.AsmResolver
{
    // Herp: This fixes an OOBE issue with AsmResolver Utf8String.Concat using the wrong Length variable for byte array allocation
    internal class AsmResolverUtf8StringConcatFix
    {
        private static MethodInfo _concat;
        private static MethodInfo _concatPrefix;

        internal static void Install()
        {
            try
            {
                Type thisType = typeof(AsmResolverUtf8StringConcatFix);
                Type utf8StringType = typeof(Utf8String);

                _concat = utf8StringType.GetMethod("Concat", BindingFlags.Public | BindingFlags.Instance, [typeof(byte[])]);
                if (_concat == null)
                    throw new Exception("Failed to get Utf8String.Concat(byte[])");

                _concatPrefix = thisType.GetMethod(nameof(ConcatPrefix), BindingFlags.NonPublic | BindingFlags.Static);
                if (_concatPrefix == null)
                    throw new Exception("Failed to get AsmResolverUtf8StringConcatFix.ConcatPrefix");

                MelonDebug.Msg($"Patching AsmResolver Utf8String.Concat(byte[])...");
                Core.HarmonyInstance.Patch(_concat,
                    new HarmonyMethod(_concatPrefix));
            }
            catch (Exception e)
            {
                MelonLogger.Warning(e);
            }
        }

        private static bool ConcatPrefix(Utf8String __instance, byte[] __0, ref Utf8String __result)
        {
            __result = ConcatFixed(__instance, __0);
            return false;
        }

        internal static Utf8String ConcatFixed(Utf8String a, Utf8String b) => !Utf8String.IsNullOrEmpty(b)
                ? ConcatFixed(a, b.GetBytes())
                : a;
        internal static Utf8String ConcatFixed(Utf8String a, byte[] b)
        {
            if (b is null || b.Length == 0)
                return a;

            var aBytes = a.GetBytes();
            byte[] result = new byte[aBytes.Length + b.Length];
            Buffer.BlockCopy(aBytes, 0, result, 0, aBytes.Length);
            Buffer.BlockCopy(b, 0, result, aBytes.Length, b.Length);
            return result;
        }
    }
}
#endif