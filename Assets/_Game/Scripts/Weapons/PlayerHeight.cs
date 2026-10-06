using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// The player's height for the body frame (Brett 2026-09-25: "adjust height in settings so if a shorter person plays or
    /// taller holster and stuff doesnt get messed up"). Auto (0, the default): PlayerBody measures the standing eye height each
    /// time the headset goes on. A set height (cm) places the belt, the hip holsters and the sling from it instead, so a
    /// shorter or taller player (or one who put the headset on sitting or bent over) gets the slots at their own hips.
    /// Kept in PlayerPrefs; edited from the pause menu (GamePause).
    /// </summary>
    public static class PlayerHeight
    {
        public const int MinCm = 120, MaxCm = 215;
        /// <summary>Standing eye height over stature (adult anthropometry: ~0.936; 175 cm → eyes at 1.64 m, the old default).</summary>
        public const float EyeRatio = 0.936f;
        const string Key = "his.height.cm";
        static int s_cm = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_cm = -1; // domain reload off: re-read the pref each Play session

        /// <summary>Raised after a change.</summary>
        public static event System.Action Changed;

        /// <summary>The set height in cm, or 0 for Auto.</summary>
        public static int Cm
        {
            get { if (s_cm < 0) s_cm = Sanitize(PlayerPrefs.GetInt(Key, 0)); return s_cm; }
        }

        public static bool Auto => Cm == 0;

        /// <summary>Standing eye height (m) for the set height (meaningless in Auto).</summary>
        public static float EyeHeight => Cm * 0.01f * EyeRatio;

        /// <summary>The stature (cm) that goes with a standing eye height (m).</summary>
        public static int StatureCm(float eye) => Mathf.RoundToInt(eye / EyeRatio * 100f);

        /// <summary>Set the height in cm (0 or less = Auto; otherwise clamped to <see cref="MinCm"/>–<see cref="MaxCm"/>).</summary>
        public static void Set(int cm)
        {
            cm = Sanitize(cm);
            if (cm == Cm) return;
            s_cm = cm;
            PlayerPrefs.SetInt(Key, cm);
            PlayerPrefs.Save();
            Debug.Log(cm == 0 ? "[HIS] Height: auto (measured)" : $"[HIS] Height: {cm} cm (eyes at {EyeHeight:F2} m)");
            Changed?.Invoke();
        }

        public static void Reset() => Set(0);

        static int Sanitize(int cm) => cm <= 0 ? 0 : Mathf.Clamp(cm, MinCm, MaxCm);
    }
}
