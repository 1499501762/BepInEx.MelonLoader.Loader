using System;
using BepInEx.Logging;
using MelonLoader.Bootstrap;
using MelonLoader.InternalUtils;
using MelonLoader.Logging;

namespace MelonLoader.Hosting
{
    /// <summary>
    /// Hosts MelonLoader 0.7.3 inside a BepInEx plugin process without the native
    /// MelonLoader bootstrap/native host. Replaces the native <see cref="BootstrapLibrary"/>
    /// (log sink, loader config, mono interop hooks) with managed equivalents backed by BepInEx.
    /// </summary>
    public static class BepInExHost
    {
        /// <summary>
        /// True once MelonLoader has been initialized in BepInEx-hosted mode.
        /// </summary>
        public static bool IsActive { get; private set; }

        private static ManualLogSource _logSource;

        /// <summary>
        /// Initializes MelonLoader for BepInEx hosting.
        /// </summary>
        /// <param name="baseDirectory">MelonLoader base directory (e.g. the <c>MLLoader</c> folder).</param>
        public static unsafe void Initialize(string baseDirectory)
        {
            if (IsActive)
                return;
            IsActive = true;

            _logSource = Logger.CreateLogSource("MelonLoader");

            // Config must be set before any static ctor touches LoaderConfig.
            var config = new LoaderConfig();
            config.Loader.BaseDirectory = baseDirectory;
            LoaderConfig.Current = config;

            // Install a managed BootstrapLibrary backed by BepInEx.
            var lib = new BootstrapLibrary();
            lib.LogMsg = LogMsg;
            lib.LogError = LogError;
            lib.LogMelonInfo = LogMelonInfo;
            lib.IsConsoleOpen = () => true;
            lib.GetLoaderConfig = (ref LoaderConfig c) => c = LoaderConfig.Current;
            lib.MonoInstallHooks = () => { };
            lib.MonoGetDomainPtr = () => IntPtr.Zero;
            lib.MonoGetRuntimeHandle = () => IntPtr.Zero;
            lib.NativeHookAttach = NativeHookAttach;
            lib.NativeHookDetach = NativeHookDetach;
            BootstrapInterop.InitializeManaged(lib);

            Core.Initialize();
        }

        /// <summary>
        /// Starts MelonLoader (runs the Il2Cpp assembly generator when needed and loads mods).
        /// </summary>
        public static void Start() => Core.Start();

        // Native hooks are not required when hosted by BepInEx (MonoMod / BepInEx handles hooking).
        private static unsafe void NativeHookAttach(nint* target, nint detour) { }
        private static unsafe void NativeHookDetach(nint* target, nint detour) { }

        private static unsafe void LogMsg(ColorARGB* msgColor, string msg, int msgLength,
            ColorARGB* sectionColor, string section, int sectionLength,
            string strippedMsg, int strippedMsgLength)
        {
            if (string.IsNullOrEmpty(msg))
                return;
            _logSource?.Log(LogLevel.Message, msg);
        }

        private static void LogError(string msg, int msgLength, string section, int sectionLength, bool warning)
        {
            if (string.IsNullOrEmpty(msg))
                return;
            if (warning)
                _logSource?.LogWarning(msg);
            else
                _logSource?.LogError(msg);
        }

        private static unsafe void LogMelonInfo(ColorARGB* nameColor, string name, int nameLength,
            string info, int infoLength)
        {
            if (string.IsNullOrEmpty(info))
                return;
            _logSource?.Log(LogLevel.Message, string.IsNullOrEmpty(name) ? info : $"[{name}] {info}");
        }
    }
}
