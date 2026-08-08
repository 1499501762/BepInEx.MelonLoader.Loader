using System;
using System.Diagnostics;
using System.IO;
using BepInEx.Unity.Mono;
using MelonLoader.Hosting;

namespace BepInEx.MelonLoader.Loader.UnityMono;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    private void Awake()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            if (args.Name.Contains("MelonLoader"))
                return typeof(BepInExHost).Assembly;
            return null;
        };

        BepInExHost.Initialize(GetMelonLoaderBaseDirectory());
        BepInExHost.Start();
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