using MelonLoader.Bootstrap;

namespace MelonLoader.InternalUtils;

internal class BootstrapLibrary
{
    internal NativeHookFn NativeHookAttach { get; set; }
    internal NativeHookFn NativeHookDetach { get; set; }
    internal LogMsgFn LogMsg { get; set; }
    internal LogErrorFn LogError { get; set; }
    internal LogMelonInfoFn LogMelonInfo { get; set; }
    internal ActionFn MonoInstallHooks { get; set; }
    internal PtrRetFn MonoGetDomainPtr { get; set; }
    internal PtrRetFn MonoGetRuntimeHandle { get; set; }
    internal BoolRetFn IsConsoleOpen { get; set; }
    internal GetLoaderConfigFn GetLoaderConfig { get; set; }
}
