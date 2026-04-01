using UnityEngine;

namespace SpatialDrawing
{
    /// <summary>
    /// Provides a simple static API for triggering haptic feedback (vibration) on the device.
    /// On Android, uses a precise duration Java call for fine-grained control.
    /// Falls back to Handheld.Vibrate() on other platforms (iOS, etc.).
    /// The IsEnabled flag is controlled by the Settings toggle.
    /// </summary>
    public static class HapticService
    {
        // ── State ──
        /// <summary>Whether haptic feedback is currently enabled. Controlled by the Settings toggle.</summary>
        public static bool IsEnabled { get; private set; } = true;

        // ── Duration Constants (milliseconds) ──
        private const long DURATION_LIGHT  = 15;
        private const long DURATION_MEDIUM = 35;
        private const long DURATION_STRONG = 60;

        // ── Public API ──

        /// <summary>
        /// Enable or disable all haptic feedback. Called from the Settings toggle.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
#if UNITY_EDITOR
            Debug.Log($"[HapticService] Haptics {(enabled ? "ENABLED" : "DISABLED")}");
#endif
        }

        /// <summary>
        /// Light buzz — for UI button clicks, minor state changes, alert cues.
        /// </summary>
        public static void Light()  => Vibrate(DURATION_LIGHT);

        /// <summary>
        /// Medium buzz — for primary interaction gestures like pinch/cycle.
        /// </summary>
        public static void Medium() => Vibrate(DURATION_MEDIUM);

        /// <summary>
        /// Strong buzz — for significant events like menu open, confirm, or item selected.
        /// </summary>
        public static void Strong() => Vibrate(DURATION_STRONG);

        // ── Internal Implementation ──

        private static void Vibrate(long milliseconds)
        {
            if (!IsEnabled) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                AndroidJavaObject activity   = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                AndroidJavaObject vibrator   = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                vibrator?.Call("vibrate", milliseconds);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HapticService] Android vibration failed: {e.Message}. Falling back.");
                Handheld.Vibrate();
            }
#elif UNITY_IOS && !UNITY_EDITOR
            Handheld.Vibrate();
#else
            // In-Editor simulation: just log.
            Debug.Log($"[HapticService] Simulated vibrate: {milliseconds}ms");
#endif
        }
    }
}
