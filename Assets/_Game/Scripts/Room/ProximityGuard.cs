using System.Collections.Generic;
using HIS.UI;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Room
{
    /// <summary>
    /// M1a safety, step 3: keeps the player from being lured into the real room. Ten times a second (not per frame) the head
    /// is checked against the scanned room (<see cref="RoomGeometry"/>: wall segments and furniture footprints, flat):
    /// within <see cref="warnDistance"/> of a surface, or backing up faster than <see cref="backSpeed"/>, a warning note says
    /// what is where ("Wall behind you", "Couch to your left"); within <see cref="fadeDistance"/> of a WALL the live intruder also
    /// fades toward see-through, so there's nothing to keep backing into. Furniture and fast backing only warn (no fade). Clears at <see cref="clearDistance"/> (hysteresis) after a short
    /// minimum, and the intruder fades back. The safe-spot ring is ≥0.6 m clear, so standing in it never warns.
    /// Nothing is paused: the fight's timing stays the same.
    /// </summary>
    public class ProximityGuard : MonoBehaviour
    {
        public static ProximityGuard Instance { get; private set; }

        [SerializeField] float warnDistance = 0.35f, clearDistance = 0.5f;
        [SerializeField] float backSpeed = 1.2f;       // m/s backwards (flat, against where the head faces)
        [SerializeField] float sampleInterval = 0.1f;
        [SerializeField] float minShowSeconds = 0.8f, backingHoldSeconds = 1.2f;
        [SerializeField, Range(0f, 1f)] float fadeTo = 0.3f;
        // Fade only right at a WALL (or the scan's edge): near furniture or backing up it just warns. Fading him whenever
        // you duck behind the couch or backpedal from a knife man would hide the threat mid-fight (review 2026-09-24).
        [SerializeField] float fadeDistance = 0.3f;

        /// <summary>Tests: a fake head pose used instead of the camera (null = the real head).</summary>
        public static Pose? TestHead;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { TestHead = null; Enabled = false; } // domain reload off: an aborted test must not leave a fake head behind

        /// <summary>The warning shown now (plain words, e.g. "Wall behind you"), or null.</summary>
        public string Warning { get; private set; }
        /// <summary>Intruder opacity the guard is applying (1 = normal).</summary>
        public float Fade { get; private set; } = 1f;
        public float LastDistance { get; private set; } = float.PositiveInfinity;
        public float BackingSpeed => _back;

        const string NoticeKey = "safety";
        static readonly int FadeId = Shader.PropertyToID("_Fade"), BaseColorId = Shader.PropertyToID("_BaseColor");
        float _nextSample, _shownAt, _backUntil, _back, _prevT;
        Vector3 _prev;
        bool _hasPrev, _near, _fadeWanted;
        MaterialPropertyBlock _mpb;
        IntruderT _faded;
        Renderer[] _rends;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>
        /// Off by default — Brett 0.1.15: remove the "table in front of you" warnings, they're annoying. Scene-scan edges and
        /// the Quest's own boundary still protect the player. Tests switch it on.
        /// </summary>
        public static bool Enabled = false;

        void Update()
        {
            if (!Enabled) { if (Warning != null) SetWarning(null, Time.unscaledTime); _fadeWanted = false; if (Fade < 1f) { Fade = 1f; ApplyFade(1f); } return; }
            float now = Time.unscaledTime;
            if (Time.timeScale > 0f && now >= _nextSample) { _nextSample = now + sampleInterval; Sample(now); }
            float target = _fadeWanted ? fadeTo : 1f;
            if (!Mathf.Approximately(Fade, target) || (Fade < 1f && CurrentIntruder() != _faded))
            {
                Fade = Mathf.MoveTowards(Fade, target, Time.unscaledDeltaTime * (target < Fade ? 4f : 2f));
                ApplyFade(Fade);
            }
        }

        void Sample(float now)
        {
            var g = RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;
            if (g == null) { _fadeWanted = false; SetWarning(null, now); return; }
            Vector3 head, fwd;
            if (TestHead is Pose tp) { head = tp.position; fwd = tp.forward; }
            else
            {
                var cam = Camera.main;
                if (cam == null) return;
                head = cam.transform.position; fwd = cam.transform.forward;
            }
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;

            // Backing-up speed, smoothed over a couple of samples; a jump (tracking glitch, recentre, a test pose) is ignored.
            if (_hasPrev && now > _prevT)
            {
                var v = (head - _prev) / (now - _prevT); v.y = 0f;
                if (v.magnitude < 4f) _back = Mathf.Lerp(_back, -Vector3.Dot(v, fwd), 0.5f);
            }
            _prev = head; _prevT = now; _hasPrev = true;
            if (_back > backSpeed) _backUntil = now + backingHoldSeconds;

            float d = g.Nearest(head, out var hit);
            LastDistance = d;
            _near = _near ? d < clearDistance : d < warnDistance;
            _fadeWanted = _near && hit.Wall && d < (_fadeWanted ? clearDistance : fadeDistance);
            string msg = null;
            if (_near) msg = Describe(hit, head, fwd);
            else if (now < _backUntil)
            {
                // Backing up fast: name what's behind if it's close, else a general "slow down".
                float db = g.Nearest(head - fwd * 0.8f, out var behind);
                msg = db < 0.6f ? Describe(behind, head, fwd) : "Slow down, you're backing up fast";
            }
            if (msg == null && Warning != null && now - _shownAt < minShowSeconds) return; // no flicker
            SetWarning(msg, now);
        }

        static string Describe(in RoomGeometry.Hit hit, Vector3 head, Vector3 fwd)
        {
            var to = new Vector3(hit.Point.x - head.x, 0f, hit.Point.y - head.z);
            if (to.sqrMagnitude < 1e-4f) return $"{hit.Name} right here";
            to.Normalize();
            float ahead = Vector3.Dot(fwd, to);
            string where = ahead > 0.5f ? "in front of you" : ahead < -0.3f ? "behind you" :
                           Vector3.Cross(fwd, to).y > 0f ? "to your right" : "to your left";
            return $"{hit.Name} {where}";
        }

        void SetWarning(string msg, float now)
        {
            if (msg == Warning) return;
            if (msg != null && Warning == null) { _shownAt = now; _rends = null; } // re-read renderers: wounds added since
            Warning = msg;
            if (msg == null) { Notice.Hide(NoticeKey); Debug.Log("[HIS] Safety: clear"); return; }
            Notice.Show(NoticeKey, $"<color=#FFB23F><b>{msg}</b></color>\n<size=60%>Mind your real room</size>", 90);
            Debug.Log($"[HIS] Safety: {msg} ({LastDistance:F2} m, backing {_back:F1} m/s)");
        }

        HIS.Intruder.IntruderSpawner _spawner;
        bool _spawnerLooked;

        IntruderT CurrentIntruder()
        {
            var dir = HIS.Scenario.ScenarioDirector.Instance;
            var i = dir != null ? dir.Intruder : null;
            if (i == null)
            {
                if (!_spawnerLooked) { _spawner = FindAnyObjectByType<HIS.Intruder.IntruderSpawner>(); _spawnerLooked = true; }
                i = _spawner != null ? _spawner.Active : null; // Sandbox
            }
            return i != null && i.Current != IntruderT.State.Dead ? i : null;
        }

        /// <summary>
        /// Opacity on the live intruder through a property block: _Fade on HIS/OcclusionLitPBR (premultiplied, so he fades into
        /// the passthrough), base-colour alpha on his wound decals. A man who changed (killed, next scenario) is restored first.
        /// </summary>
        void ApplyFade(float f)
        {
            var who = CurrentIntruder();
            if (who != _faded) { if (_faded != null) SetOpacity(_faded, _rends, 1f); _faded = who; _rends = null; }
            if (who == null) return;
            _rends ??= who.GetComponentsInChildren<Renderer>(true);
            SetOpacity(who, _rends, f);
        }

        void SetOpacity(IntruderT who, Renderer[] rends, float f)
        {
            if (who == null) return;
            rends ??= who.GetComponentsInChildren<Renderer>(true);
            _mpb ??= new MaterialPropertyBlock();
            foreach (var r in rends)
            {
                if (r == null || !r.transform.IsChildOf(who.transform)) continue;
                var m = r.sharedMaterial;
                if (m == null) continue;
                r.GetPropertyBlock(_mpb); // keep what's there (the decals' atlas cell)
                if (m.HasProperty(FadeId)) _mpb.SetFloat(FadeId, f);
                else if (m.HasProperty(BaseColorId)) { var c = m.GetColor(BaseColorId); c.a *= f; _mpb.SetColor(BaseColorId, c); }
                else continue;
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Tests: forget motion history (a fake pose must not read as a fast step).</summary>
        public void ResetMotion() { _hasPrev = false; _back = 0f; _backUntil = 0f; }
    }
}
