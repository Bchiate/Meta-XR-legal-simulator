using System;
using UnityEngine;

namespace HIS.Outcome
{
    /// <summary>
    /// Tints the player's real room through the one passthrough layer: red/blue police lights for a bad call, a dim for
    /// "you didn't survive". Follows research/11 §1 and its verification corrections, which take precedence over the body:
    /// <list type="bullet">
    /// <item>Two 16³ RGB LUTs (red, blue) with the night dimming baked in, built once and only after passthrough is initialised.
    ///   A LUT made earlier stays Pending with a null native handle, and an early capability query caches a fallback forever.</item>
    /// <item>Fade in with the single-LUT overload, because only that one blends with the real colours. Alternate with the
    ///   two-LUT overload, which only changes a weight (no LUT upload, no GC). Fade out with the single-LUT overload, then DisableColorMap.</item>
    /// <item>The phase is a cosine, so the alternation starts on pure red (no pop from the fade-in). It runs N + ½ cycles
    ///   so it ends on pure blue, which is exactly where the blue fade-out starts.</item>
    /// <item>LUTs and brightness/contrast/saturation are mutually exclusive styles (XrPassthroughStyleFB), so the dim is its own mode.</item>
    /// <item>Camera flash (the mugshot finale): a third, near-white LUT through the single-LUT overload, weight up in 60 ms, back to 0 in
    ///   about 0.35 s. One flash at a time, well under 3 per second; it cancels anything else running.</item>
    /// </list>
    /// If the LUTs can't be made (no support, Editor without passthrough), the per-layer colour scale does a cruder version.
    /// That path is untested on this OpenXR build. Driven by <see cref="OutcomeShow"/> through <see cref="Tick"/>.
    /// </summary>
    public class PoliceLights : MonoBehaviour
    {
        const int LutResolution = 16;          // Meta: start at 16; 16³ × RGB = 12 KB each
        const int DimSteps = 8;                // BCS updates allocate inside the SDK, so the dim fades in a few steps, not per frame

        // Fallback per-layer tints (research/11 §1). Roughly equal luminance, so each swap changes hue, not brightness.
        static readonly Vector4 RedScale = new(1f, 0.35f, 0.35f, 1f);
        static readonly Vector4 BlueScale = new(0.35f, 0.45f, 1f, 1f);

        enum Mode { None, Police, Dim, Flash }

        OVRPassthroughLayer _layer;
        OVRPassthroughColorLut _red, _blue, _white;
        bool _lutFailed;
        Mode _mode;
        bool _usingLut;
        bool _touched;          // we changed the layer's style and still owe it a restore
        bool _scaleOverridden;  // fallback path: we turned on the per-layer colour scale

        float _t;
        float _freq, _fadeIn, _altEnd, _fadeOut, _end;
        bool _gentle;

        float _dimBrightness, _dimSaturation, _dimSeconds;
        int _dimStep;
        float _flashPeak, _flashSeconds;
        const float FlashRise = 0.06f;

        public bool LutsReady => _red != null && _red.IsValid && _blue != null && _blue.IsValid && _white != null && _white.IsValid;
        public bool PoliceActive => _mode == Mode.Police;
        public bool FlashActive => _mode == Mode.Flash;
        /// <summary>Tests: flashes fired since start-up.</summary>
        public int Flashes { get; private set; }
        /// <summary>Tests: nothing is applied to the passthrough layer (every show restores it).</summary>
        public bool Idle => _mode == Mode.None && !_touched;

        public void Init(OVRPassthroughLayer layer) => _layer = layer;

        // A failed build is retried once per passthrough (re)start, not on every poll.
        void OnEnable() => OVRManager.OnPassthroughInitializedStateChange += OnPassthroughState;
        void OnDisable() => OVRManager.OnPassthroughInitializedStateChange -= OnPassthroughState;
        void OnPassthroughState(bool up) { if (up) _lutFailed = false; }

        /// <summary>
        /// Builds the LUTs once passthrough is up. Cheap to call often: returns at once when they're built, when building
        /// failed before, or when passthrough isn't initialised yet.
        /// </summary>
        public void TryPrepare()
        {
            if (_lutFailed || LutsReady || _layer == null) return;
            if (!OVRManager.IsInsightPassthroughInitialized()) return;
            try
            {
                DisposeLuts();
                _red = BuildLut(new Color(1f, 0.12f, 0.10f));
                _blue = BuildLut(new Color(0.12f, 0.28f, 1f));
                _white = BuildFlashLut();
                // v205: a runtime without LUT support doesn't throw, it logs an error and leaves the LUT Invalid.
                if (!LutsReady) throw new InvalidOperationException("the runtime could not create them");
                var caps = OVRManager.GetPassthroughCapabilities();
                Debug.Log($"[HIS] Police LUTs ready ({LutResolution}³ RGB; device max {(caps != null ? caps.MaxColorLutResolution : 0u)})");
            }
            catch (Exception e)
            {
                _lutFailed = true;
                DisposeLuts();
                Debug.LogWarning($"[HIS] Police LUTs unavailable ({e.Message}); falling back to the passthrough layer colour scale");
            }
        }

        /// <summary>
        /// Start the police lights. Returns the total length in seconds: fade-in, then N + ½ red/blue cycles at
        /// <paramref name="hz"/> (about <paramref name="alternateSeconds"/> long), then fade-out.
        /// <paramref name="gentle"/> ("Reduce flashing") uses a plain sine blend instead of the snappier hold-and-swap curve.
        /// </summary>
        public float BeginPolice(float hz, float fadeIn, float alternateSeconds, float fadeOut, bool gentle)
        {
            Restore();
            if (_layer == null) return 0f;
            TryPrepare();
            _usingLut = LutsReady;
            _freq = Mathf.Max(0.1f, hz);
            int cycles = Mathf.Max(1, Mathf.RoundToInt(_freq * alternateSeconds - 0.5f));
            _fadeIn = Mathf.Max(0.05f, fadeIn);
            _altEnd = _fadeIn + (cycles + 0.5f) / _freq;
            _fadeOut = Mathf.Max(0.05f, fadeOut);
            _end = _altEnd + _fadeOut;
            _gentle = gentle;
            _t = 0f;
            _mode = Mode.Police;
            _touched = true;
            ApplyPolice(0f);
            Debug.Log($"[HIS] Police lights: {(_usingLut ? "LUT" : "colour-scale fallback")}, {_freq:F2} Hz × {cycles + 0.5f} cycles, {_end:F1} s");
            return _end;
        }

        /// <summary>Dim the real room (brightness/saturation down) over <paramref name="seconds"/>; it stays dim until <see cref="Restore"/>.</summary>
        public void BeginDim(float brightness, float saturation, float seconds)
        {
            Restore();
            if (_layer == null) return;
            _dimBrightness = Mathf.Clamp(brightness, -1f, 1f);
            _dimSaturation = Mathf.Clamp(saturation, -1f, 1f);
            _dimSeconds = Mathf.Max(0.05f, seconds);
            _dimStep = 0;
            _t = 0f;
            _mode = Mode.Dim;
            _touched = true;
        }

        /// <summary>
        /// A camera flash: the real room blown out to near-white (<paramref name="peak"/> 0..1) over 60 ms, fading back over
        /// <paramref name="seconds"/> (ease-out, so it reads as one pop, not a pulse). Stops police lights or a dim first.
        /// </summary>
        public void Flash(float peak, float seconds)
        {
            Restore();
            Flashes++;
            if (_layer == null) return;
            TryPrepare();
            _usingLut = LutsReady;
            _flashPeak = Mathf.Clamp01(peak);
            _flashSeconds = Mathf.Max(0.05f, seconds);
            _t = 0f;
            _mode = Mode.Flash;
            _touched = true;
            ApplyFlash(0f);
        }

        public void Tick(float dt)
        {
            if (_mode == Mode.None || _layer == null) return;
            _t += dt;
            if (_mode == Mode.Police) ApplyPolice(_t);
            else if (_mode == Mode.Flash) ApplyFlash(_t);
            else ApplyDim(_t);
        }

        void ApplyFlash(float t)
        {
            // A 60 ms rise (no hard step from the dark room to white: Meta's comfort guidance), then an ease-out decay.
            if (t >= FlashRise + _flashSeconds) { Restore(); return; }
            float k = t < FlashRise ? Smooth01(t / FlashRise) : 1f - (t - FlashRise) / _flashSeconds;
            k = _flashPeak * (t < FlashRise ? k : k * k);
            if (UseLut()) { _layer.SetColorLut(_white, k); return; }
            SetScale(Vector4.Lerp(Vector4.one, new Vector4(2.5f, 2.5f, 2.6f, 1f), k)); // fallback: over-bright colour scale
        }

        /// <summary>End the police lights now if they're still running (normally they finish on their own).</summary>
        public void FinishPolice()
        {
            if (_mode != Mode.Police) return;
            Restore();
        }

        /// <summary>Put the passthrough back the way we found it. Safe to call any time, as often as you like.</summary>
        public void Restore()
        {
            _mode = Mode.None;
            if (!_touched) return;
            _touched = false;
            if (_layer == null) return;
            _layer.DisableColorMap();
            if (_scaleOverridden)
            {
                _layer.colorScale = Vector4.one;
                _layer.overridePerLayerColorScaleAndOffset = false;
                _scaleOverridden = false;
            }
        }

        void ApplyPolice(float t)
        {
            if (t < _fadeIn)
            {
                SetSingle(true, Smooth01(t / _fadeIn));
            }
            else if (t < _altEnd)
            {
                // Cosine phase: w = 0 (pure red) when the alternation starts, w = 1 (pure blue) after N + ½ cycles.
                float s = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * _freq * (t - _fadeIn));
                float w = _gentle ? s : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.7f, s));
                SetPair(w);
            }
            else if (t < _end)
            {
                SetSingle(false, 1f - Smooth01((t - _altEnd) / _fadeOut));
            }
            else
            {
                Restore();
            }
        }

        void ApplyDim(float t)
        {
            int step = Mathf.Clamp(Mathf.CeilToInt(t / _dimSeconds * DimSteps), 0, DimSteps);
            if (step == _dimStep) return;
            _dimStep = step;
            float k = Smooth01(step / (float)DimSteps);
            _layer.SetBrightnessContrastSaturation(_dimBrightness * k, 0f, _dimSaturation * k);
        }

        // The single-LUT weight blends the real colours with the LUT (0 = untouched room).
        void SetSingle(bool red, float k)
        {
            k = Mathf.Clamp01(k);
            if (UseLut())
            {
                _layer.SetColorLut(red ? _red : _blue, k); // re-sent every frame so a LUT re-created after headset sleep is picked up
                return;
            }
            SetScale(Vector4.Lerp(Vector4.one, red ? RedScale : BlueScale, k));
        }

        void SetPair(float w)
        {
            w = Mathf.Clamp01(w); // the SDK logs a warning every call for weights outside [0, 1]
            if (UseLut())
            {
                _layer.SetColorLut(_red, _blue, w);
                return;
            }
            SetScale(Vector4.Lerp(RedScale, BlueScale, w));
        }

        bool UseLut()
        {
            if (!_usingLut) return false;
            if (LutsReady) return true;
            // Lost the LUTs mid-show (disposed or invalidated): drop to the colour scale for the rest of it.
            _usingLut = false;
            _layer.DisableColorMap();
            return false;
        }

        void SetScale(Vector4 scale)
        {
            _layer.overridePerLayerColorScaleAndOffset = true;
            _layer.colorScale = scale;
            _scaleOverridden = true;
        }

        static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        /// <summary>
        /// A night-time police tint: the room is dimmed to 55% and then 80% pulled toward <paramref name="tint"/>, scaled
        /// by the input's luminance so shapes stay readable. The tint is normalised to a shared luminance, so swapping
        /// red and blue changes hue rather than strobing brightness (photosensitivity, research/11 pitfalls).
        /// OpenXR LUT layout: index r + g·N + b·N² (red varies fastest).
        /// </summary>
        static OVRPassthroughColorLut BuildLut(Color tint)
        {
            const int n = LutResolution;
            const float targetLuma = 0.5f; // 0.35 read as dim on device (0.1.7)
            float tl = Luma(tint);
            if (tl > 1e-4f) tint *= targetLuma / tl;
            var data = new Color32[n * n * n];
            float inv = 1f / (n - 1);
            for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
            for (int r = 0; r < n; r++)
            {
                var c = new Color(r * inv, g * inv, b * inv);
                var o = Color.Lerp(c * 0.55f, tint * (0.3f + 1.3f * Luma(c)), 0.8f);
                data[r + g * n + b * n * n] = new Color32(
                    (byte)(Mathf.Clamp01(o.r) * 255f + 0.5f),
                    (byte)(Mathf.Clamp01(o.g) * 255f + 0.5f),
                    (byte)(Mathf.Clamp01(o.b) * 255f + 0.5f), 255);
            }
            return new OVRPassthroughColorLut(data, OVRPassthroughColorLut.ColorChannels.Rgb);
        }

        static float Luma(Color c) => 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;

        /// <summary>Camera flash: everything lifted to 80 % white with a cool tint, a trace of the room left in it.</summary>
        static OVRPassthroughColorLut BuildFlashLut()
        {
            const int n = LutResolution;
            var data = new Color32[n * n * n];
            float inv = 1f / (n - 1);
            for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
            for (int r = 0; r < n; r++)
            {
                float l = Luma(new Color(r * inv, g * inv, b * inv));
                data[r + g * n + b * n * n] = new Color32((byte)(Mathf.Clamp01(0.80f + 0.18f * l) * 255f), (byte)(Mathf.Clamp01(0.82f + 0.18f * l) * 255f),
                    (byte)(Mathf.Clamp01(0.86f + 0.14f * l) * 255f), 255);
            }
            return new OVRPassthroughColorLut(data, OVRPassthroughColorLut.ColorChannels.Rgb);
        }

        void DisposeLuts()
        {
            if (_red != null) { _red.Dispose(); _red = null; }
            if (_blue != null) { _blue.Dispose(); _blue = null; }
            if (_white != null) { _white.Dispose(); _white = null; }
        }

        void OnDestroy()
        {
            Restore();
            DisposeLuts();
        }
    }
}
