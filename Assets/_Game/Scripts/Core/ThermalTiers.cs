using System.Globalization;

namespace HIS.Core
{
    public enum ThermalTier { Normal, Warm, Hot }

    /// <summary>One poll of the headset's heat signals. Unknown values are NaN / -1 (Editor, API missing, call failed).</summary>
    public struct ThermalSample
    {
        public float BatteryC;   // battery temperature (same value as VrApi's Temp=)
        public bool Plugged;     // charging adds battery heat: logged so it isn't mistaken for game load
        public int Status;       // Android PowerManager thermal status: 0 none, 1 light, 2 moderate, 3 severe .. 6 shutdown
        public float Headroom;   // PowerManager.getThermalHeadroom(10): 1.0 = SEVERE; Quest's skin sensor hits LIGHT near 0.87
        public int XrLevel;      // worst XR_EXT_performance_settings thermal notification: 0 normal, 25 warning, 75 impaired
        public float Fps;        // average over the poll, NaN while loading/paused (those dips aren't heat)

        public static ThermalSample Unknown => new() { BatteryC = float.NaN, Status = -1, Headroom = float.NaN, Fps = float.NaN };

        public override string ToString()
        {
            var c = CultureInfo.InvariantCulture;
            string F(float v, string fmt) => float.IsNaN(v) ? "?" : v.ToString(fmt, c);
            return $"batt={F(BatteryC, "0.0")}C{(Plugged ? " (plugged in)" : "")} status={(Status < 0 ? "?" : Status.ToString(c))} " +
                   $"headroom={F(Headroom, "0.00")} xr={XrLevel} fps={F(Fps, "0")}";
        }
    }

    /// <summary>
    /// Normal → Warm → Hot from the heat signals, with hysteresis: a step up needs the enter threshold (hard signals — the
    /// OS thermal status or an XR thermal notification — at once, noisy ones on two polls in a row); a step down needs every
    /// signal below the lower exit threshold for <see cref="StepDownSeconds"/>, one tier at a time. Pure logic (no Unity
    /// calls) so the headless tests can drive it with a fake clock and fake temperatures.
    /// </summary>
    public sealed class ThermalTiers
    {
        // Headroom: Android normalises each sensor so 1.0 = its SEVERE threshold, 30 °C per unit. Quest 3's skin sensor
        // (surf-virt-usr) throttles LIGHT at 48 °C / SEVERE 52 °C → 0.80 ≈ 46 °C skin, 0.90 ≈ 49 °C.
        public float WarmHeadroom = 0.80f, HotHeadroom = 0.90f, WarmHeadroomExit = 0.72f, HotHeadroomExit = 0.85f;
        // Battery (the only temperature VrApi logs) is a backstop only: the OS throttles it at 58 °C (LIGHT) and Brett's 29-min
        // 0.1.15 session peaked at 50 without throttling. The headset's own signals (status/headroom/XR warning/fps) lead.
        public float WarmBatteryC = 54f, HotBatteryC = 57f, WarmBatteryExitC = 52f, HotBatteryExitC = 55f;
        // Frame rate held under this for LowFpsPolls polls in a row (not loading, not paused) = the OS is already clocking down.
        public float LowFps = 64f;
        public int LowFpsPolls = 3;
        public int ConfirmPolls = 2;
        public float StepDownSeconds = 60f;

        public ThermalTier Tier { get; private set; }
        public string Reason { get; private set; } = "start";

        int _upCount, _lowFpsCount;
        float _coolSince = -1f;

        public void Reset() { Tier = ThermalTier.Normal; Reason = "reset"; _upCount = _lowFpsCount = 0; _coolSince = -1f; }

        /// <summary>Feed one poll. True when the tier changed (see <see cref="Tier"/>, <see cref="Reason"/>).</summary>
        public bool Update(in ThermalSample s, float now)
        {
            _lowFpsCount = !float.IsNaN(s.Fps) && s.Fps < LowFps ? _lowFpsCount + 1 : 0;
            var want = Wanted(s, out bool hard, out string why);
            if (want > Tier)
            {
                _coolSince = -1f;
                if (++_upCount >= (hard ? 1 : ConfirmPolls)) return Set(want, why);
                return false;
            }
            _upCount = 0;
            if (Tier == ThermalTier.Normal || Holds(s, Tier)) { _coolSince = -1f; return false; }
            if (_coolSince < 0f) _coolSince = now;
            if (now - _coolSince < StepDownSeconds) return false;
            _coolSince = -1f;
            return Set(Tier - 1, $"cooled below the {Tier} exit for {StepDownSeconds:0} s");
        }

        bool Set(ThermalTier t, string why) { Tier = t; Reason = why; _upCount = 0; return true; }

        ThermalTier Wanted(in ThermalSample s, out bool hard, out string why)
        {
            hard = true;
            if (s.Status >= 2) { why = $"OS thermal status {s.Status}"; return ThermalTier.Hot; }
            if (s.XrLevel >= 75) { why = "XR thermal impaired"; return ThermalTier.Hot; }
            hard = false;
            if (s.Headroom >= HotHeadroom) { why = $"headroom {s.Headroom:0.00} >= {HotHeadroom:0.00}"; return ThermalTier.Hot; }
            if (s.BatteryC >= HotBatteryC) { why = $"battery {s.BatteryC:0.0} C >= {HotBatteryC:0}"; return ThermalTier.Hot; }
            hard = true;
            if (s.Status >= 1) { why = $"OS thermal status {s.Status}"; return ThermalTier.Warm; }
            if (s.XrLevel >= 25) { why = "XR thermal warning"; return ThermalTier.Warm; }
            hard = false;
            if (s.Headroom >= WarmHeadroom) { why = $"headroom {s.Headroom:0.00} >= {WarmHeadroom:0.00}"; return ThermalTier.Warm; }
            if (s.BatteryC >= WarmBatteryC) { why = $"battery {s.BatteryC:0.0} C >= {WarmBatteryC:0}"; return ThermalTier.Warm; }
            if (_lowFpsCount >= LowFpsPolls) { hard = true; why = $"fps {s.Fps:0} < {LowFps:0} for {_lowFpsCount} polls"; return ThermalTier.Warm; }
            why = "cool";
            return ThermalTier.Normal;
        }

        // Still above the exit threshold of tier t? (NaN compares false, so an unknown signal never holds a tier.)
        bool Holds(in ThermalSample s, ThermalTier t) => t == ThermalTier.Hot
            ? s.Status >= 2 || s.XrLevel >= 75 || s.Headroom >= HotHeadroomExit || s.BatteryC >= HotBatteryExitC
            : s.Status >= 1 || s.XrLevel >= 25 || s.Headroom >= WarmHeadroomExit || s.BatteryC >= WarmBatteryExitC || _lowFpsCount > 0;
    }
}
