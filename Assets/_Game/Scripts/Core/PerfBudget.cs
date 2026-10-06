using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// Heat budget knobs that effect systems read at spawn time. <see cref="ThermalGovernor"/> sets them from the thermal
    /// tier; at Normal they equal the shipped values, so nothing looks different until the headset is actually warm.
    /// </summary>
    public static class PerfBudget
    {
        public struct Levels
        {
            public int BloodDecals;            // world blood quads alive at once (each is a draw call)
            public float Spray, Confetti;      // particle count multipliers
            public int MaxCorpses;             // bodies left in the room (min with the mode's own limit)
            public OVRManager.FoveatedRenderingLevel Foveation;
            public bool DynamicFoveation;      // true: the system picks Off..Foveation by GPU load; false: always Foveation
            // (No eye-buffer scale or MSAA level since 0.1.51: changing either at runtime rebuilt the eye swapchains while a frame
            // was still drawing into the old one — a kernel GPU page fault in 0.1.50. The render asset's 0.8 / 4x always applies.)
            public bool HardOcclusion;         // soft → hard depth occlusion (cheaper edges)
            public bool Wireframe;             // the room scan wireframe (a tester aid) shows; off at every level since 0.1.21
            public bool BreakNotice;           // "take a short break" between rounds
        }

        public static readonly Levels Normal = new()
        {
            BloodDecals = 120, Spray = 1f, Confetti = 1f, MaxCorpses = 99,
            Foveation = OVRManager.FoveatedRenderingLevel.Medium, DynamicFoveation = true,
            HardOcclusion = false, Wireframe = false, // off (Brett 0.1.20: edges shimmered black on his tables)
        };
        public static readonly Levels Warm = new()
        {
            BloodDecals = 60, Spray = 0.6f, Confetti = 0.6f, MaxCorpses = 1,
            Foveation = OVRManager.FoveatedRenderingLevel.High, DynamicFoveation = true,
            HardOcclusion = false, Wireframe = false,
        };
        public static readonly Levels Hot = new()
        {
            BloodDecals = 30, Spray = 0.35f, Confetti = 0.4f, MaxCorpses = 1,
            Foveation = OVRManager.FoveatedRenderingLevel.High, DynamicFoveation = false,
            HardOcclusion = true, Wireframe = false,
            BreakNotice = true,
        };

        public static Levels For(ThermalTier t) => t switch { ThermalTier.Hot => Hot, ThermalTier.Warm => Warm, _ => Normal };

        public static Levels Current { get; private set; } = Normal;
        public static void Apply(ThermalTier t) => Current = For(t);

        public static int BloodDecalCap => Current.BloodDecals;
        public static int MaxCorpses => Current.MaxCorpses;
        public static int Spray(int n) => Scale(n, Current.Spray);
        public static int Confetti(int n) => Scale(n, Current.Confetti);
        static int Scale(int n, float k) => k >= 1f ? n : Mathf.RoundToInt(n * k);
    }
}
