namespace MelonLoader
{
    internal class SupportModule_From : ISupportModule_From
    {
        // One-shot diagnostics (BepInEx hosting) to confirm the game-loop events are delivered.
        private static bool _updateSeen;
        private static bool _lateStartLogged;
        private static void LogEventOnce(string name)
        {
            if (_lateStartLogged)
                return;
            _lateStartLogged = true;
            MelonLogger.Msg($"[BepInExHost] SupportModule event delivered: {name}");
        }

        public void OnApplicationLateStart()
        {
            LogEventOnce(nameof(OnApplicationLateStart));
            MelonEvents.OnApplicationLateStart.Invoke();
        }

        public void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"[BepInExHost] OnSceneWasLoaded: {sceneName} ({buildIndex})");
            MelonEvents.OnSceneWasLoaded.Invoke(buildIndex, sceneName);
        }

        public void OnSceneWasInitialized(int buildIndex, string sceneName)
            => MelonEvents.OnSceneWasInitialized.Invoke(buildIndex, sceneName);

        public void OnSceneWasUnloaded(int buildIndex, string sceneName)
            => MelonEvents.OnSceneWasUnloaded.Invoke(buildIndex, sceneName);

        public void Update()
        {
            // BepInEx hosting drives MelonEvents.OnUpdate via the plugin's GameLoopDriver.
            // The native SupportModule must NOT also drive it: doing so fires OnUpdate several
            // times per frame, and mods that consume frame-edge input (e.g. InputSystem
            // wasPressedThisFrame) end up toggling multiple times per key press - the
            // IronNestFreecam rig is opened then immediately closed. FixedUpdate/LateUpdate
            // and the scene/late-start events remain SupportModule-driven.
        }

        public void FixedUpdate()
            => MelonEvents.OnFixedUpdate.Invoke();

        public void LateUpdate()
            => MelonEvents.OnLateUpdate.Invoke();

        public void OnGUI()
            => MelonEvents.OnGUI.Invoke();

        public void Quit()
            => MelonEvents.OnApplicationQuit.Invoke();

        public void DefiniteQuit()
        {
            MelonEvents.OnApplicationDefiniteQuit.Invoke();
            Core.Quit();
        }

        public void SetInteropSupportInterface(InteropSupport.Interface interop)
        {
            if (InteropSupport.SMInterface == null)
                InteropSupport.SMInterface = interop;
        }
    }
}