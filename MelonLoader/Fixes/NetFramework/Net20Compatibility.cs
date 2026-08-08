#if NET35
using HarmonyLib;
using System;
using System.Text.RegularExpressions;
using MelonLoader.Logging;

namespace MelonLoader.Fixes.NetFramework
{
    internal static class Net20Compatibility
    {
        public static void TryInstall()
        {
            if (Environment.Version.Major != 2)
                return;

            try
            {
                MelonEvents.OnPreInitialization.Subscribe(OnPreInit, unsubscribeOnFirstInvocation: true);

                Core.HarmonyInstance.Patch(AccessTools.Constructor(typeof(Regex), [typeof(string), typeof(RegexOptions)]), new(typeof(Net20Compatibility), nameof(RegexCtor)));
            }
            catch (Exception ex)
            {
                MelonLogger.Error(ex);
            }
        }

        private static void OnPreInit()
        {
            MelonLogger.MsgDirect(ColorARGB.Yellow, "The current game is running on .NET Framework 2.0, which is obsolete. Some universal Melons may run into unexpected errors.");
        }

        private static void RegexCtor([HarmonyArgument(1)] ref RegexOptions options)
        {
            // Compiled regex is not supported and results in an exception
            options &= ~RegexOptions.Compiled;
        }
    }
}
#endif