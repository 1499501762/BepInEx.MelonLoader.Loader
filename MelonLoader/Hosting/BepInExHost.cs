using System;
using System.IO;
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

        private static bool _started;

        /// <summary>
        /// True once <see cref="Start"/> has run (i.e. mods have been loaded).
        /// </summary>
        public static bool HasStarted => _started;

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

            // Approach A: mods are loaded verbatim and never rewritten. The game interop
            // assembly must ship Il2Cpp.* alias types. They are maintained by the dedicated
            // BepInEx preloader patcher (BepInEx.MelonLoader.Loader.Patcher) which runs in
            // the preloader stage BEFORE BepInEx loads/memory-maps the interop assemblies -
            // rewriting them here (from a plugin) is impossible because BepInEx already
            // loaded every interop assembly by the time plugins run (access denied).

            PrintEarlyAccessBanner();

            Core.Initialize();
        }

        /// <summary>
        /// Prints a prominent watermelon-colored (pink text on a green frame) bilingual
        /// early-access warning banner when the loader initializes. MelonLoader's colored
        /// log calls are stripped by <see cref="LogMsg"/> (BepInEx logs have no colour), so
        /// the banner renders in colour straight to the console and also keeps a plain-text
        /// copy in the BepInEx log file for reference.
        /// </summary>
        private static void PrintEarlyAccessBanner()
        {
            const int width = 62;
            string[] texts =
            {
                " BepInEx MelonLoader Loader is an EARLY-ACCESS mod,",
                " BepInEx MelonLoader Loader 是一个仍处于早期版本的 mod，",
                " compatibility issues may occur. 可能存在兼容性问题。",
                "",
                " Do NOT report compatibility issues caused by THIS mod to the",
                " developers of MelonLoader mods - it is not their duty.",
                " 请不要把因使用本 mod 导致的兼容性问题提交给 MelonLoader",
                " 下的 mod 开发者，这不是他们的职责。",
                "",
                " Report issues to: # BepInEx MelonLoader Loader POST PAGE",
                " or the GitHub ISSUE PAGE.",
                " 请把兼容性问题提交到 # BepInEx MelonLoader Loader 的发布页",
                " 或 GitHub Issue 页。",
            };

            // Plain-text copy for the BepInEx log file.
            var sb = new System.Text.StringBuilder();
            sb.Append('+').Append(new string('-', width)).Append('+').AppendLine();
            foreach (var t in texts)
                sb.Append("| ").Append(t.PadRight(width - 1)).Append('|').AppendLine();
            sb.Append('+').Append(new string('-', width)).Append('+');
            MelonLogger.Msg(sb.ToString());

            // Watermelon-coloured render straight to the native console (green frame, pink
            // text). Console.WriteLine would be swallowed by BepInEx's Console.SetOut log
            // sink (colour lost), so write via the Win32 console API directly.
            try
            {
                var hOut = GetStdHandle(STD_OUTPUT_HANDLE);
                if (hOut != IntPtr.Zero && hOut != new IntPtr(-1))
                {
                    const ushort brightGreen = 2 | 8;      // FOREGROUND_GREEN | INTENSITY
                    const ushort pink = 1 | 4 | 8;         // FOREGROUND_RED | FOREGROUND_BLUE | INTENSITY
                    WriteNativeConsole(hOut, "+" + new string('-', width) + "+\n", brightGreen);
                    foreach (var t in texts)
                    {
                        WriteNativeConsole(hOut, "| ", brightGreen);
                        WriteNativeConsole(hOut, t.PadRight(width - 1), pink);
                        WriteNativeConsole(hOut, "|\n", brightGreen);
                    }
                    WriteNativeConsole(hOut, "+" + new string('-', width) + "+\n", brightGreen);
                }
            }
            catch
            {
                // No console (GUI-only) - the BepInEx log copy above is sufficient.
            }
        }

        private const int STD_OUTPUT_HANDLE = -11;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetConsoleTextAttribute(IntPtr hConsoleOutput, ushort wAttributes);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool WriteConsoleW(IntPtr hConsoleOutput, string lpBuffer, uint nNumberOfCharsToWrite,
            out uint lpNumberOfCharsWritten, IntPtr lpReserved);

        private static void WriteNativeConsole(IntPtr hOut, string text, ushort color)
        {
            SetConsoleTextAttribute(hOut, color);
            WriteConsoleW(hOut, text, (uint)text.Length, out _, IntPtr.Zero);
        }

        /// <summary>
        /// Starts MelonLoader (runs the Il2Cpp assembly generator when needed and loads mods).
        /// Should be deferred until the game's initial scene is ready so mods that look up
        /// scene objects (e.g. IronNestFCS finding "Player Turret Piece") work. Idempotent.
        /// </summary>
        public static void Start()
        {
            if (_started)
                return;
            _started = true;
            Core.Start();
        }

        // ---------------------------------------------------------------------
        // Game-loop event delivery.
        //
        // The official SupportModule drives MelonLoader's game-loop events (Update,
        // scene events, OnApplicationLateStart) through its own SM_Component / scene
        // hooks. In the BepInEx-hosted context that integration does not deliver
        // events (confirmed with diagnostics), which leaves MelonLoader mods inert.
        //
        // Instead, the BepInEx host plugins drive these events from Unity directly.
        // ---------------------------------------------------------------------

        private static bool _lateStartFired;

        /// <summary>
        /// True once <see cref="InvokeOnApplicationLateStart"/> has been called.
        /// </summary>
        public static bool HasOnApplicationLateStartFired => _lateStartFired;

        public static void InvokeOnApplicationLateStart()
        {
            if (_lateStartFired)
                return;
            _lateStartFired = true;
            MelonLogger.Msg("[BepInExHost] OnApplicationLateStart fired (host-driven)");
            MelonEvents.OnApplicationLateStart.Invoke();
        }




        public static void InvokeUpdate() => MelonEvents.OnUpdate.Invoke();

        /// <summary>
        /// ROOT-CAUSE FIX: The MelonLoader SupportModule (Il2Cpp.dll) re-creates
        /// Il2CppInteropRuntime with its own MelonDetourProvider, which relies on
        /// BootstrapInterop.NativeHookAttachDirect. Under BepInEx hosting the native
        /// BootstrapInterop host is not available, so those detours silently do nothing
        /// (method entry machine code is never patched) and Harmony's Il2CppDetourMethodPatcher
        /// never intercepts game calls. This re-creates Il2CppInteropRuntime using BepInEx's
        /// Il2CppInteropDetourProvider (Dobby-based, works under BepInEx) so Il2Cpp detours
        /// actually install. Called after SupportModule.Setup() and before MelonHarmonyInit.
        /// </summary>
        public static void ReconfigureDetourProvider()
        {
            try
            {
                var i2c = (System.Reflection.Assembly)null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Il2CppInterop.Runtime") { i2c = a; break; }
                if (i2c == null) return;

                var runtimeType = i2c.GetType("Il2CppInterop.Runtime.Startup.Il2CppInteropRuntime");
                var cfgType = i2c.GetType("Il2CppInterop.Runtime.Startup.RuntimeConfiguration");
                if (runtimeType == null || cfgType == null) return;

                // 当前 Instance 的 UnityVersion
                var instProp = runtimeType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var oldInst = instProp.GetValue(null, null);
                object unityVersion = null;
                if (oldInst != null)
                {
                    var uvProp = runtimeType.GetProperty("UnityVersion", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    unityVersion = uvProp.GetValue(oldInst, null);
                }

                // BepInEx 的 Il2CppInteropDetourProvider（Dobby）
                var bepAsm = (System.Reflection.Assembly)null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "BepInEx.Unity.IL2CPP") { bepAsm = a; break; }
                object detourProvider = null;
                if (bepAsm != null)
                {
                    var dpType = bepAsm.GetType("BepInEx.Unity.IL2CPP.Hook.Il2CppInteropDetourProvider");
                    if (dpType != null) detourProvider = Activator.CreateInstance(dpType, true);
                }
                if (detourProvider == null) return;

                var cfg = Activator.CreateInstance(cfgType, true);
                var dpProp = cfgType.GetProperty("DetourProvider");
                if (dpProp != null) dpProp.SetValue(cfg, detourProvider, null);
                if (unityVersion != null)
                {
                    var uvProp = cfgType.GetProperty("UnityVersion");
                    if (uvProp != null) uvProp.SetValue(cfg, unityVersion, null);
                }

                var create = runtimeType.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { cfgType }, null);
                if (create == null) return;
                create.Invoke(null, new object[] { cfg });
            }
            catch
            {
            }
        }

        public static void InvokeFixedUpdate() => MelonEvents.OnFixedUpdate.Invoke();
        public static void InvokeLateUpdate() => MelonEvents.OnLateUpdate.Invoke();
        public static void InvokeOnGUI() => MelonEvents.OnGUI.Invoke();

        public static void InvokeSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[BepInExHost] OnSceneWasLoaded: {sceneName} ({buildIndex})");
            MelonEvents.OnSceneWasLoaded.Invoke(buildIndex, sceneName);
        }

        public static void InvokeSceneWasInitialized(int buildIndex, string sceneName)
            => MelonEvents.OnSceneWasInitialized.Invoke(buildIndex, sceneName);

        public static void InvokeSceneWasUnloaded(int buildIndex, string sceneName)
            => MelonEvents.OnSceneWasUnloaded.Invoke(buildIndex, sceneName);

        /// <summary>
        /// Fired when the application is about to quit (the SupportModule's quit hook does not
        /// fire under BepInEx hosting). Drives <see cref="MelonEvents.OnApplicationQuit"/>.
        /// </summary>
        public static void InvokeOnApplicationQuit()
            => MelonEvents.OnApplicationQuit.Invoke();

        /// <summary>
        /// Fires <see cref="MelonEvents.OnApplicationDefiniteQuit"/> and runs MelonLoader's
        /// clean shutdown (<see cref="Core.Quit"/>).
        /// </summary>
        public static void InvokeOnApplicationDefiniteQuit()
        {
            MelonEvents.OnApplicationDefiniteQuit.Invoke();
            Core.Quit();
        }

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
