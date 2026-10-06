using System.Collections.Generic;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// Pooled sound player. Clips are looked up by key from a <see cref="SfxLibrary"/> so scripts stay asset-agnostic:
    /// Sfx.Play("pistol.shot", pos). Missing keys are silent (logged once) so the game runs before audio lands.
    /// Per key (from the library): mix volume, 3D/2D, distances, cooldown, voice cap, priority, low-pass.
    /// Voices: a free source first; otherwise the least important playing one-shot is stolen (never a gunshot for a
    /// footstep, never a loop). Loops (siren, ambience) return a handle for fades and Stop.
    /// </summary>
    public class Sfx : MonoBehaviour
    {
        public static Sfx Instance { get; private set; }

        [SerializeField] SfxLibrary library;
        [SerializeField] int poolSize = 24;

        class Voice
        {
            public AudioSource Src;
            public AudioLowPassFilter Lowpass;
            public string Key;
            public int Priority, Id;
            public bool Loop;
            public Transform Follow;
            public float BaseVolume, FadeFrom, FadeTo, FadeStart, FadeSeconds;
            public float BaseLowpass;   // the entry's own low-pass (0 = none); the head shadow darkens it further from behind
            public bool Spatial;
            public bool StopAfterFade;
            public float PendingUntil;  // a delayed start (Play2D's delay): held until then even if the source doesn't say it's playing yet
        }

        readonly List<Voice> _voices = new();
        readonly HashSet<string> _warned = new();
        int _nextId = 1;
        // Fade clock: unscaled time that stands still while GamePause holds AudioListener.pause, so a fade doesn't
        // finish silently during a pause (the faint siren would then snap to full on resume).
        float _clock;

        public SfxLibrary Library => library;

        void Awake()
        {
            Instance = this;
            if (library != null) library.ResetRuntime(); // domain reload is off in the Editor: drop last session's cooldowns/map
            for (int i = 0; i < poolSize; i++)
            {
                var go = new GameObject($"Sfx{i}");
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false; src.spatialBlend = 1f; src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.dopplerLevel = 0f; src.spread = 0f;
                var lp = go.AddComponent<AudioLowPassFilter>(); lp.enabled = false;
                _voices.Add(new Voice { Src = src, Lowpass = lp });
            }
        }

        public static bool Has(string key) => Instance != null && Instance.library != null && Instance.library.Find(key) != null;

        /// <summary>One-shot at a world position. Returns a handle (0 = not played: missing key, cooldown or voice cap).</summary>
        public static int Play(string key, Vector3 position, float volume = 1f, float pitchJitter = -1f) =>
            Instance == null ? 0 : Instance.Start(key, position, null, volume, pitchJitter, loop: false, fadeIn: 0f, force2D: false);

        /// <summary>One-shot that rides along with a transform (a voice from a moving head).</summary>
        public static int PlayFollow(string key, Transform follow, float volume = 1f) =>
            Instance == null || follow == null ? 0 : Instance.Start(key, follow.position, follow, volume, -1f, loop: false, fadeIn: 0f, force2D: false);

        /// <summary>Non-spatial (in the player's head): heartbeat, UI, hit thud, stings. <paramref name="delay"/> &gt; 0 starts it
        /// that many seconds from now (AudioSource.PlayDelayed: its voice is held meanwhile; StopAll cancels it).</summary>
        public static int Play2D(string key, float volume = 1f, float delay = 0f) =>
            Instance == null ? 0 : Instance.Start(key, Vector3.zero, null, volume, -1f, loop: false, fadeIn: 0f, force2D: true, delay);

        /// <summary>A looping sound (siren, ambience) that can follow a transform; fade in, then Stop(handle, fade).</summary>
        public static int PlayLoop(string key, Vector3 position, float volume = 1f, float fadeIn = 0f, Transform follow = null) =>
            Instance == null ? 0 : Instance.Start(key, position, follow, volume, 0f, loop: true, fadeIn, force2D: false);

        public static void Stop(int handle, float fadeOut = 0f)
        {
            if (Instance == null || handle == 0) return;
            foreach (var v in Instance._voices)
            {
                if (v.Id != handle) continue;
                if (!v.Src.isPlaying && !AudioListener.pause && Time.unscaledTime >= v.PendingUntil) return;
                if (fadeOut <= 0f) { v.Src.Stop(); v.Id = 0; v.PendingUntil = 0f; return; }
                v.FadeFrom = v.Src.volume; v.FadeTo = 0f; v.FadeStart = Instance._clock; v.FadeSeconds = fadeOut; v.StopAfterFade = true;
                return;
            }
        }

        /// <summary>True while the sound behind this handle is still playing (or frozen by the pause).</summary>
        public static bool IsPlaying(int handle) => SecondsLeft(handle) > 0f;

        /// <summary>Seconds until this handle's one-shot ends at its current pitch (0 = done, stopped or unknown; loops: +inf).</summary>
        public static float SecondsLeft(int handle)
        {
            if (Instance == null || handle == 0) return 0f;
            foreach (var v in Instance._voices)
            {
                if (v.Id != handle) continue;
                var src = v.Src;
                if (src.clip == null || (!src.isPlaying && (!AudioListener.pause || src.time <= 0f))) return 0f;
                if (v.Loop) return float.PositiveInfinity;
                return Mathf.Max(0f, (src.clip.length - src.time) / Mathf.Max(0.01f, Mathf.Abs(src.pitch)));
            }
            return 0f;
        }

        /// <summary>Multiply a playing sound's pitch (a gun's own voice on shared clips: the M9 on the pistol.* keys).</summary>
        public static void ScalePitch(int handle, float factor)
        {
            if (Instance == null || handle == 0) return;
            foreach (var v in Instance._voices) if (v.Id == handle) { v.Src.pitch *= factor; return; }
        }

        /// <summary>Longest clip behind a key in seconds at pitch 1 (0 = no clip yet). For scheduling lines before they play.</summary>
        public static float Length(string key)
        {
            var e = Instance != null && Instance.library != null ? Instance.library.Find(key) : null;
            if (e == null) return 0f;
            float m = 0f;
            foreach (var c in e.clips) if (c != null) m = Mathf.Max(m, c.length);
            return m;
        }

        /// <summary>Tests: voices playing (or frozen by the pause) whose key starts with <paramref name="prefix"/>.</summary>
        public static int CountPlaying(string prefix)
        {
            if (Instance == null) return 0;
            int n = 0;
            foreach (var v in Instance._voices)
                if (v.Id != 0 && v.Key != null && v.Key.StartsWith(prefix, System.StringComparison.Ordinal) && (v.Src.isPlaying || (AudioListener.pause && v.Src.time > 0f))) n++;
            return n;
        }

        /// <summary>Tests: the keys <see cref="CountPlaying"/> counts, with each voice's play time, for failure messages.</summary>
        public static string PlayingKeys(string prefix)
        {
            if (Instance == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var v in Instance._voices)
                if (v.Id != 0 && v.Key != null && v.Key.StartsWith(prefix, System.StringComparison.Ordinal) && (v.Src.isPlaying || (AudioListener.pause && v.Src.time > 0f)))
                    sb.Append(sb.Length > 0 ? ", " : "").Append(v.Key).Append(" at ").Append(v.Src.time.ToString("F1")).Append(v.Src.loop ? " s (loop)" : " s");
            return sb.ToString();
        }

        /// <summary>Stop everything (session reset).</summary>
        public static void StopAll()
        {
            if (Instance == null) return;
            foreach (var v in Instance._voices) { v.Src.Stop(); v.Id = 0; v.Follow = null; v.PendingUntil = 0f; }
        }

        int Start(string key, Vector3 position, Transform follow, float volume, float pitchJitter, bool loop, float fadeIn, bool force2D, float delay = 0f)
        {
            var e = library != null ? library.Find(key) : null;
            if (e == null)
            {
                if (_warned.Add(key)) Debug.Log($"[HIS] Sfx: no clip for '{key}' yet");
                return 0;
            }
            float now = Time.unscaledTime;
            if (e.cooldown > 0f && now - e.lastPlayed < e.cooldown) return 0;
            if (e.maxVoices > 0)
            {
                int n = 0;
                foreach (var v in _voices) if (v.Key == key && v.Src.isPlaying) n++;
                if (n >= e.maxVoices) return 0;
            }
            var voice = Acquire(e.priority);
            if (voice == null) return 0;
            e.lastPlayed = now;

            var src = voice.Src;
            src.Stop();
            src.clip = library.Pick(e);
            src.loop = loop;
            src.priority = e.priority;
            bool spatial = e.spatial && !force2D;
            src.spatialBlend = spatial ? 1f : 0f;
            src.minDistance = e.minDistance; src.maxDistance = e.maxDistance;
            float jitter = pitchJitter >= 0f ? pitchJitter : e.pitchJitter;
            src.pitch = 1f + Random.Range(-jitter, jitter);
            voice.Lowpass.enabled = e.lowpassHz > 0f;
            if (e.lowpassHz > 0f) voice.Lowpass.cutoffFrequency = e.lowpassHz;
            voice.BaseLowpass = e.lowpassHz; voice.Spatial = spatial;
            voice.Key = key; voice.Priority = e.priority; voice.Loop = loop; voice.Id = _nextId++;
            voice.Follow = follow; voice.StopAfterFade = false;
            voice.BaseVolume = Mathf.Clamp01(volume * e.volume);
            src.transform.position = follow != null ? follow.position : position;
            if (fadeIn > 0f)
            {
                src.volume = 0f;
                voice.FadeFrom = 0f; voice.FadeTo = voice.BaseVolume; voice.FadeStart = _clock; voice.FadeSeconds = fadeIn;
            }
            else { src.volume = voice.BaseVolume; voice.FadeSeconds = 0f; }
            voice.PendingUntil = delay > 0f ? Time.unscaledTime + delay : 0f;
            if (delay > 0f) src.PlayDelayed(delay); else src.Play();
            return voice.Id;
        }

        Transform _head;

        /// <summary>
        /// A cheap front/back cue (research/21 §5.2): with no spatializer on this stack, Unity's panning can't tell a sound
        /// behind you from one in front. Behind the head it loses its top end (down to ~5 kHz), as the head's shadow does,
        /// so a kick at a door at your back sounds like it's at your back.
        /// </summary>
        void HeadShadow(Voice v)
        {
            var to = v.Src.transform.position - _head.position; to.y = 0f;
            var f = _head.forward; f.y = 0f;
            float behind = to.sqrMagnitude > 1e-4f && f.sqrMagnitude > 1e-4f ? Mathf.Clamp01(-Vector3.Dot(f.normalized, to.normalized) * 1.5f) : 0f;
            float open = v.BaseLowpass > 0f ? v.BaseLowpass : 22000f;
            bool on = v.BaseLowpass > 0f || behind > 0.01f;
            if (v.Lowpass.enabled != on) v.Lowpass.enabled = on;
            if (on) v.Lowpass.cutoffFrequency = Mathf.Lerp(open, Mathf.Min(open, 5000f), behind);
        }

        Voice Acquire(int priority)
        {
            Voice steal = null;
            foreach (var v in _voices)
            {
                if (!v.Src.isPlaying && Time.unscaledTime >= v.PendingUntil) return v;
                if (v.Loop) continue;
                // Steal the least important (highest number) that isn't more important than the new sound; oldest on ties.
                if (v.Priority < priority) continue;
                if (steal == null || v.Priority > steal.Priority || (v.Priority == steal.Priority && v.Src.time > steal.Src.time)) steal = v;
            }
            return steal;
        }

        void Update()
        {
            if (AudioListener.pause)
            {
                // Frozen: hold fades and keep followers (isPlaying may read false while paused, so don't drop them).
                foreach (var v in _voices) if (v.Follow != null) v.Src.transform.position = v.Follow.position;
                return;
            }
            _clock += Time.unscaledDeltaTime;
            float now = _clock;
            if (_head == null && Camera.main != null) _head = Camera.main.transform;
            foreach (var v in _voices)
            {
                if (!v.Src.isPlaying) { v.Follow = null; continue; }
                if (v.Follow != null) v.Src.transform.position = v.Follow.position;
                if (v.Spatial && _head != null) HeadShadow(v);
                if (v.FadeSeconds > 0f)
                {
                    float k = Mathf.Clamp01((now - v.FadeStart) / v.FadeSeconds);
                    v.Src.volume = Mathf.Lerp(v.FadeFrom, v.FadeTo, k);
                    if (k >= 1f)
                    {
                        v.FadeSeconds = 0f;
                        if (v.StopAfterFade) { v.Src.Stop(); v.Id = 0; }
                    }
                }
            }
        }
    }
}
