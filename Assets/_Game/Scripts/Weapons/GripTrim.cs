using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// Player grip-angle trims on top of the tuned aim pitch (research/16 §3.1: pitch ±15°, yaw ±10°, roll ±10°, default 0),
    /// kept in PlayerPrefs and applied to both hands' AimAnchor, so they carry over to every weapon. Yaw/roll are "inward"
    /// (toward the body's midline), mirrored per hand. Edited from the pause menu (GamePause).
    /// </summary>
    public static class GripTrim
    {
        public static readonly string[] Names = { "Pitch", "Yaw", "Roll" };
        public static readonly float[] Limits = { 15f, 10f, 10f };
        static readonly string[] Keys = { "his.grip.pitch", "his.grip.yaw", "his.grip.roll" };
        static readonly float[] Values = new float[3];
        static bool _loaded;

        /// <summary>Raised after a change (hands re-aim their anchors).</summary>
        public static event System.Action Changed;

        public static float Get(int axis) { Load(); return Values[axis]; }

        /// <summary>(pitch, yaw inward, roll inward) in degrees.</summary>
        public static Vector3 Current { get { Load(); return new Vector3(Values[0], Values[1], Values[2]); } }

        public static void Set(int axis, float degrees)
        {
            Load();
            degrees = Mathf.Clamp(Mathf.Round(degrees), -Limits[axis], Limits[axis]);
            if (Mathf.Approximately(degrees, Values[axis])) return;
            Values[axis] = degrees;
            PlayerPrefs.SetFloat(Keys[axis], degrees);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        public static void Reset(int axis) => Set(axis, 0f);

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            for (int i = 0; i < 3; i++) Values[i] = Mathf.Clamp(PlayerPrefs.GetFloat(Keys[i], 0f), -Limits[i], Limits[i]);
        }
    }
}
