using System;
using System.Collections.Generic;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// Key → clip variants plus how that key is played. A key with several clips picks one at random (no repeats
    /// back-to-back). Filled by HIS > Audio > Build Sfx Library from Assets/_Game/Audio/Clips/&lt;key&gt;/ (clips cut by
    /// scripts/prepare_sfx.py); per-key mix defaults live in the builder.
    /// </summary>
    [CreateAssetMenu(menuName = "HIS/Sfx Library")]
    public class SfxLibrary : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string key;
            public AudioClip[] clips;
            [Tooltip("Mix level applied on top of the caller's volume.")] [Range(0f, 2f)] public float volume = 1f;
            [Tooltip("Off = in the player's head (UI, heartbeat, stings).")] public bool spatial = true;
            public float minDistance = 0.6f;
            public float maxDistance = 25f;
            [Tooltip("Ignore a new play of this key sooner than this (s).")] public float cooldown = 0f;
            [Tooltip("Most simultaneous voices of this key (0 = no limit).")] public int maxVoices = 0;
            [Tooltip("Unity AudioSource priority: 0 = never stolen … 256 = first to go.")] [Range(0, 256)] public int priority = 128;
            [Tooltip("Low-pass cutoff (Hz) for sounds heard through walls / far away; 0 = off.")] public float lowpassHz = 0f;
            [Tooltip("Random pitch ± (0 for voices).")] public float pitchJitter = 0.05f;
            [NonSerialized] public int last = -1;
            [NonSerialized] public float lastPlayed = -100f;
        }

        public List<Entry> entries = new();

        Dictionary<string, Entry> _map;

        public Entry Find(string key)
        {
            _map ??= Build();
            return _map.TryGetValue(key, out var e) && e.clips != null && e.clips.Length > 0 ? e : null;
        }

        public AudioClip Pick(Entry e)
        {
            if (e.clips.Length == 1) return e.clips[0];
            int i; do { i = UnityEngine.Random.Range(0, e.clips.Length); } while (i == e.last);
            e.last = i; return e.clips[i];
        }

        public AudioClip Get(string key) { var e = Find(key); return e != null ? Pick(e) : null; }

        Dictionary<string, Entry> Build()
        {
            var m = new Dictionary<string, Entry>();
            foreach (var e in entries) if (!string.IsNullOrEmpty(e.key)) m[e.key] = e;
            return m;
        }

        /// <summary>Forget per-play state and the key map. Domain reload is off in the Editor, so the loaded asset keeps
        /// lastPlayed from the previous Play session (unscaledTime restarts near 0 → every cooldown key goes silent) and a
        /// map that predates a Build Sfx Library. Called by Sfx.Awake and the builder.</summary>
        public void ResetRuntime()
        {
            _map = null;
            foreach (var e in entries) { e.lastPlayed = -100f; e.last = -1; }
        }

        void OnValidate() { _map = null; }
        void OnEnable() { _map = null; }
    }
}
