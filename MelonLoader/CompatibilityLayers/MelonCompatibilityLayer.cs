using MelonLoader.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader.Utils;
using System.Linq;

namespace MelonLoader
{
    public static class MelonCompatibilityLayer
    {
        private static string _baseDirectory;
        private static List<MelonModule.Info> _allLayers = new List<MelonModule.Info>();

        internal static void LoadModules()
        {
            _baseDirectory = Path.Combine(MelonEnvironment.DependenciesDirectory, "CompatibilityLayers");
            if (!Directory.Exists(_baseDirectory))
                return;

            CheckLayer("IPA", MelonUtils.IsGameIl2Cpp);

            CheckLayer(InternalUtils.UnityInformationHandler.GameName);
            CheckLayer(InternalUtils.UnityInformationHandler.GameDeveloper);
            CheckLayer($"{InternalUtils.UnityInformationHandler.GameDeveloper}_{InternalUtils.UnityInformationHandler.GameName}");

            foreach (var m in _allLayers)
            {
                m.moduleGC = MelonModule.Load(m);
                MelonDebug.Msg($"Compatibility Layer Loaded: {m.fullPath}");
            }
        }

        private static void CheckLayer(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;

            CheckLayer(name, () => false);
            CheckLayer($"{name}_Mono", () => MelonUtils.IsGameIl2Cpp());
            CheckLayer($"{name}_Il2Cpp", () => !MelonUtils.IsGameIl2Cpp());

            if (name.StartsWith(" "))
            {
                name = name.TrimStart(' ');
                if (string.IsNullOrEmpty(name))
                    return;

                CheckLayer(name, () => false);
                CheckLayer($"{name}_Mono", () => MelonUtils.IsGameIl2Cpp());
                CheckLayer($"{name}_Il2Cpp", () => !MelonUtils.IsGameIl2Cpp());
            }
        }

        private static void CheckLayer(string name, Func<bool> shouldBeIgnored)
        {
            if (string.IsNullOrEmpty(name))
                return;

            name = name.Replace(' ', '_');
            string filePath = Path.Combine(_baseDirectory, $"{name}.dll");
            if (File.Exists(filePath)
                && (_allLayers.FirstOrDefault((x) =>
                {
                    string fileName = Path.GetFileNameWithoutExtension(x.fullPath);
                    return fileName == name;
                }) == null))
                _allLayers.Add(new MelonModule.Info(filePath, shouldBeIgnored));
        }
    }
}