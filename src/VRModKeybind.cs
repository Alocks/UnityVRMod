using UnityVRMod.Config;
using UniverseLib.Input; // Using UniverseLib's InputManager for universal input

#pragma warning disable IDE0130
namespace UnityVRMod.Core
#pragma warning restore IDE0130
{
    public static class VRModKeybind
    {
        private static readonly Dictionary<KeyCode, float> _nextAllowedKeyTime = [];

        internal static bool IsTrustedKeyPress(KeyCode key, float debounceSeconds = 0.2f)
        {
            // Some input backends can report noisy GetKeyDown events; require current held state too.
            if (!InputManager.GetKeyDown(key) || !InputManager.GetKey(key))
            {
                return false;
            }

            // Prevent accidental double-triggers caused by rapid or duplicated backend events.
            float now = Time.unscaledTime;
            if (_nextAllowedKeyTime.TryGetValue(key, out float nextAllowedTime) && now < nextAllowedTime)
            {
                return false;
            }

            _nextAllowedKeyTime[key] = now + debounceSeconds;
            return true;
        }

        public static void Update()
        {
            if (ConfigManager.ToggleSafeModeKey == null)
            {
                return;
            }

            KeyCode toggleKey = ConfigManager.ToggleSafeModeKey.Value;

            if (IsTrustedKeyPress(toggleKey))
            {
                VRModCore.LogRuntimeDebug("Toggle Safe Mode key pressed!");
                if (VRModCore.VrVisualizationFeature != null)
                {
                    VRModCore.VrVisualizationFeature.ToggleUserSafeMode();
                }
                else
                {
                    VRModCore.LogWarning("VrVisualizationManager (VrVisFeature) is null. Cannot toggle safe mode.");
                }
            }

            // REMINDER: Add other mod-specific keybind checks here if needed in the future.
        }
    }
}