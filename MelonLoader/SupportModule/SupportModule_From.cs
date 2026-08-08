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
            if (!_updateSeen)
            {
                _updateSeen = true;
                LogEventOnce(nameof(Update));
            }
            MelonEvents.OnUpdate.Invoke();
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