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
            // assembly must ship Il2Cpp.* alias types, so ensure they exist (idempotent).
            // Note: BepInEx preloads interop before plugins run, so a freshly generated
            // alias interop takes effect on the next game launch (we log a restart hint).
            TryEnsureInteropAliases(baseDirectory);

            Core.Initialize();
        }

        /// <summary>
        /// Ensures the BepInEx game interop carries <c>Il2Cpp.*</c> alias types for the
        /// installed mods (MelonLoader mods reference Il2Cpp-prefixed game types and are
        /// loaded verbatim). Rewrites the interop assembly, never the mods. Idempotent.
        /// </summary>
        private static void TryEnsureInteropAliases(string baseDirectory)
        {
#if NET6_0_OR_GREATER
            try
            {
                var gameRoot = Path.GetDirectoryName(baseDirectory);
                if (string.IsNullOrEmpty(gameRoot))
                    return;
                var interopDir = Path.Combine(gameRoot, "BepInEx", "interop");
                if (!Directory.Exists(interopDir))
                    return;

                // Full alias pass: every game type of every interop assembly gets an
                // Il2Cpp-prefixed alias (Il2Cpp.LookAtTarget in Assembly-CSharp,
                // Il2CppTMPro.TMP_Text in Unity.TextMeshPro, ...). Any mod reference is
                // covered regardless of which interop assembly the type lives in, so new
                // mods never need another regeneration. Skipped when already fully aliased.
                if (Il2CppInteropAliasInjector.EnsureAliases(interopDir))
                    MelonLogger.Msg("[BepInExHost] Interop aliases installed - restart the game for them to take effect");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[BepInExHost] Interop alias check failed: {ex.Message}");
            }
#endif
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
