using System.Collections.Generic;
using HIS.Core;
using HIS.Scenario;
using TMPro;
using UnityEngine;

namespace HIS.Outcome
{
    /// <summary>Which funny bad-call show plays (pause menu row "Bad-call show", saved in PlayerPrefs).</summary>
    public enum BadShowMode { All, Bullhorn, Sirens, Helicopter, Mugshot }

    /// <summary>
    /// The sound-and-props half of the BAD CALL show (PLAN.md M4, Brett: "make it funny"); <see cref="OutcomeShow"/> keeps
    /// the police-light tint and the banner and asks this for the timing. Four bits; a bad call features ONE of them: the
    /// one picked in the pause menu, or with "All" the next in <see cref="Rotation"/> (helicopter, mugshot, bullhorn, sirens,
    /// saved across sessions; Brett 0.1.17: "I miss the helicopter thing maybe have it cycle between them"):
    /// <list type="bullet">
    /// <item><b>Bullhorn</b>: the deadpan cop outside the door escalates, one line after another (never over each other),
    ///   ending on the verdict's charge ("You're under arrest for voluntary manslaughter."). Subtitled.</item>
    /// <item><b>Sirens</b>: the one distant siren, then more pile in from other directions outside the door, each on its
    ///   own spot, pitch and approach, the last one a silly high little one. Low-passed like the first; their summed
    ///   loudness is capped at about +4.6 dB over the single siren (Brett: not aggressive).</item>
    /// <item><b>Helicopter</b>: a rotor thump fades in overhead and a soft searchlight disc sweeps the real walls and floor
    ///   (<see cref="HeliSpotlight"/>); the cop says one of several deadpan helicopter lines (<see cref="VoiceLines.PoliceHeli"/>:
    ///   random, never the same twice running, only lines that have audio, else "we can see you through the window").</item>
    /// <item><b>Mugshot</b>: "Look at the camera" as a big booking board (height-chart backdrop, a silhouette, the placard
    ///   with the charge; <see cref="MugshotProps"/>) pops up ONCE where the player is looking and then stays put (Brett
    ///   0.1.17: it followed him left on "turn to your left"); a camera flash with the shutter (the passthrough blown white
    ///   for a third of a second through <see cref="PoliceLights.Flash"/>, the board with it; one pop, not a strobe) for the
    ///   front shot, "now turn to your left" (rarely "…your other left", <see cref="PickTurnLine"/>), time to turn, and the
    ///   profile flash at the same board and camera spot (now off to his right), then a <see cref="MugshotHold"/> s hold on
    ///   the board (the charge is read out) before the banner. A floating board gets out of the banner's way.</item>
    /// </list>
    /// The whole timeline is laid out when the show starts, from the clips' real lengths (<see cref="Sfx.Length"/>), so
    /// its length is known up front and lines never overlap. Everything is built once (pooled anchors, one spotlight quad,
    /// one chart mesh, one placard); a show allocates only its placard text. Scaled time, so the pause menu freezes it.
    /// </summary>
    public class BadCallShow : MonoBehaviour
    {
        const string PrefKey = "his.badShow";
        public static readonly string[] ModeNames = { "All", "Bullhorn", "Sirens", "Helicopter", "Mugshot" };

        static int _mode = -1;
        /// <summary>The picked show; defaults to All.</summary>
        public static BadShowMode Mode
        {
            get { if (_mode < 0) _mode = Mathf.Clamp(PlayerPrefs.GetInt(PrefKey, 0), 0, ModeNames.Length - 1); return (BadShowMode)_mode; }
            set { _mode = Mathf.Clamp((int)value, 0, ModeNames.Length - 1); PlayerPrefs.SetInt(PrefKey, _mode); PlayerPrefs.Save(); }
        }
        public static void Cycle(int dir) => Mode = (BadShowMode)(((int)Mode + dir + ModeNames.Length) % ModeNames.Length);

        // ── "All": one featured bit per bad call, in turn ──
        const string RotationKey = "his.badShow.next";
        /// <summary>
        /// The bits "All" features, one per bad call, in this order (the helicopter first: Brett missed it when every bit
        /// was crammed into one show). Each plays exactly as its own pick does: no extra touches of the others crowding it.
        /// </summary>
        public static readonly BadShowMode[] Rotation = { BadShowMode.Helicopter, BadShowMode.Mugshot, BadShowMode.Bullhorn, BadShowMode.Sirens };
        static int _rotation = -1;

        /// <summary>The next "All" bad call features <c>Rotation[RotationIndex]</c>; saved, so it keeps cycling across sessions.</summary>
        public static int RotationIndex
        {
            get { if (_rotation < 0) _rotation = WrapRotation(PlayerPrefs.GetInt(RotationKey, 0)); return _rotation; }
            set { _rotation = WrapRotation(value); PlayerPrefs.SetInt(RotationKey, _rotation); PlayerPrefs.Save(); }
        }
        static int WrapRotation(int i) => (i % Rotation.Length + Rotation.Length) % Rotation.Length;

        /// <summary>The bit a bad call in <paramref name="mode"/> would feature now: a single pick is itself, All the rotation's next (not advanced).</summary>
        public static BadShowMode Featured(BadShowMode mode) => mode == BadShowMode.All ? Rotation[RotationIndex] : mode;

        // ── the mugshot's turn line ──
        public const string TurnLine = "vo.police.turn", OtherLeftLine = "vo.police.turn.other";
        /// <summary>Odds per mugshot of the "…your other left" take, until it has played once this session.</summary>
        public const float OtherLeftOdds = 1f / 6f;
        /// <summary>PlayerPrefs: 1 = the last mugshot's turn line was "…your other left".</summary>
        public const string OtherLeftPrefKey = "his.badShow.otherLeftLast";
        static bool _otherLeftUsed;
        /// <summary>Tests: turn lines started since the app started, and how many of them were "…your other left".</summary>
        public static int TurnLinesSpoken { get; private set; }
        public static int OtherLeftsSpoken { get; private set; }

        /// <summary>
        /// The mugshot's turn line (Brett 0.1.17: "your other left is also funny the first time annoying on repeat"): the
        /// "Now turn to your left. Your other left." take is a rare treat: at most once per session, a 1-in-6 roll per mugshot
        /// until then, never on two mugshots running (even across a restart: saved), and only when it has audio. Otherwise
        /// plain "Now turn to your left." <paramref name="roll"/> is a uniform 0..1 draw.
        /// </summary>
        public static string PickTurnLine(float roll, bool otherHasAudio)
        {
            if (!otherHasAudio || _otherLeftUsed || roll >= OtherLeftOdds || PlayerPrefs.GetInt(OtherLeftPrefKey, 0) != 0) return TurnLine;
            return OtherLeftLine;
        }

        /// <summary>A turn line started (the show calls it as the line plays, so a show skipped before it uses nothing up).</summary>
        public static void TurnLineSpoken(string key)
        {
            bool other = key == OtherLeftLine;
            TurnLinesSpoken++;
            if (other) { _otherLeftUsed = true; OtherLeftsSpoken++; }
            if (PlayerPrefs.GetInt(OtherLeftPrefKey, 0) == (other ? 1 : 0)) return;
            PlayerPrefs.SetInt(OtherLeftPrefKey, other ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>Tests: a new session for the "other left" rule, as after an app restart (the saved last-mugshot flag stays).</summary>
        public static void NewOtherLeftSession() => _otherLeftUsed = false;

        /// <summary>Re-read the saved pick and rotation from PlayerPrefs on next use (app start; tests simulating a restart).</summary>
        public static void ForgetCachedPrefs() { _mode = -1; _rotation = -1; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { ForgetCachedPrefs(); _otherLeftUsed = false; TurnLinesSpoken = OtherLeftsSpoken = 0; }

        const float TailOverlap = 0.45f;  // the bullhorn clips end in ~0.4 s of street echo; the next line may start in it
        /// <summary>Seconds the mugshot board holds after the profile flash before the banner (Brett 0.1.16: too subtle).</summary>
        public const float MugshotHold = 3f;
        /// <summary>
        /// The profile flash comes at least this long after the turn line starts: "Now turn to your left." is done by ~1 s,
        /// then ~1.2 s to actually turn. The same beat the full "…your other left" take had (its flash at +2.38 s).
        /// </summary>
        public const float TurnSeconds = 2.2f;
        const float SirenStart = 20f, SirenEnd = 7f, SirenApproach = 5f;
        const float LoudnessCap = 1.7f;    // all sirens together ≈ this × one siren's amplitude (+4.6 dB)

        // The pile-up, in arrival order: direction around the door's outward normal, pitch, which recording.
        static readonly float[] ExtraAngle = { -65f, 50f, -25f, 80f, 20f, -85f };
        static readonly float[] ExtraPitch = { 0.9f, 1.07f, 0.95f, 1.12f, 0.85f, 1.38f };
        static readonly float[] ExtraDist = { 16f, 13f, 18f, 12f, 15f, 11f };
        static readonly string[] ExtraKey = { "outcome.siren.pa", "outcome.siren", "outcome.siren.pa", "outcome.siren", "outcome.siren.pa", "outcome.siren" };
        const int MaxExtras = 6;

        enum Op { Siren1, Siren1Stop, Click, Line, ExtraSiren, SirensOut, RotorIn, RotorOut, Spot, Props, Flash, PropsAway }
        struct Cue { public float At; public Op Op; public int Arg; public float F; }

        PoliceLights _lights;
        HeliSpotlight _spot;
        MugshotProps _mug;
        readonly List<Cue> _cues = new(32);
        readonly string[] _keys = new string[8];
        int _keyCount, _next;
        readonly Transform[] _sirenAnchor = new Transform[MaxExtras + 1];
        readonly Vector3[] _sirenFrom = new Vector3[MaxExtras + 1], _sirenTo = new Vector3[MaxExtras + 1];
        readonly float[] _sirenAt = new float[MaxExtras + 1], _sirenTravel = new float[MaxExtras + 1];
        readonly int[] _sirenHandle = new int[MaxExtras + 1];
        Transform _rotorAnchor;
        Vector3 _rotorFrom, _rotorOver, _rotorAway;
        float _rotorAt = -1f, _rotorLeaveAt = -1f;
        int _rotorHandle, _lineHandle, _clickHandle;

        float _t, _length, _speechFree, _speechEnd, _extraVolume;
        int _extras;
        bool _active;
        Vector3 _door, _outward, _bullhorn;
        string _charge, _booking;

        public bool Active => _active;
        /// <summary>The bit this show features (with "All": the rotation's pick) and the mode it was started in.</summary>
        public BadShowMode Playing { get; private set; }
        public BadShowMode Picked { get; private set; }
        /// <summary>Tests: where the last shutter sound was played.</summary>
        public Vector3 LastShutter { get; private set; }
        /// <summary>Tests: lines started this show, sirens that joined, flashes fired.</summary>
        public int LinesSpoken { get; private set; }
        public int SirensJoined { get; private set; }
        public int FlashesFired { get; private set; }
        public HeliSpotlight Spotlight => _spot;
        public MugshotProps Mugshot => _mug;

        [Header("Levels (on top of the library's quiet, low-passed mix)")]
        [SerializeField, Range(0f, 1f)] float sirenVolume = 0.9f;
        [SerializeField, Range(0f, 2f)] float rotorVolume = 1f;
        [SerializeField] float bullhornDistance = 3f;

        public void Init(PoliceLights lights, Material fx, TMP_FontAsset font, Material textMaterial, Material panelMaterial)
        {
            _lights = lights;
            for (int i = 0; i < _sirenAnchor.Length; i++)
            {
                _sirenAnchor[i] = new GameObject($"Siren{i}").transform;
                _sirenAnchor[i].SetParent(transform, false);
            }
            _rotorAnchor = new GameObject("Rotor").transform;
            _rotorAnchor.SetParent(transform, false);
            _spot = Child<HeliSpotlight>("Spotlight");
            _spot.Init(fx);
            _mug = Child<MugshotProps>("Mugshot");
            _mug.Init(fx, font, textMaterial, panelMaterial);
        }

        /// <summary>
        /// Lay out and start the show. Returns its length (s); <paramref name="bannerAt"/> is when the verdict banner should
        /// appear and <paramref name="lightsSeconds"/> how long the red/blue alternation runs (the lights are done before
        /// any camera flash). <see cref="BadShowMode.All"/> plays the rotation's next bit and moves the rotation on (every bad
        /// call and every "Preview bad call" comes through here).
        /// </summary>
        public float Begin(BadShowMode mode, in Verdict v, Vector3 door, Vector3 outward, Vector3 eye, Vector3 forward,
                           out float bannerAt, out float lightsSeconds, bool booking = false)
        {
            Clear();
            var bit = Featured(mode);
            if (mode == BadShowMode.All) RotationIndex++;
            Picked = mode;
            Playing = bit;
            _door = door; _outward = outward;
            _bullhorn = door + outward * bullhornDistance;
            _charge = v.Charge;
            _booking = $"{System.DateTime.Now:yy}-{Random.Range(10000, 99999)}";
            _cues.Clear(); _keyCount = 0; _next = 0;
            _t = 0f; _speechFree = 0f; _speechEnd = 0f;
            LinesSpoken = SirensJoined = FlashesFired = _extras = 0;
            string charge = VoiceLines.PoliceCharge(v.Charge);

            switch (bit)
            {
                case BadShowMode.Bullhorn:
                {
                    // The cop keeps going, deadpan, until the charge.
                    lightsSeconds = 6f;
                    Add(0.3f, Op.Siren1);
                    Add(0.6f, Op.Click);
                    Say("vo.police.handsup", 0.9f);
                    Say("vo.police.window", 0f);
                    Say("vo.police.neighbors", 0f);
                    // (Brett 0.1.23: no "step away from the confetti cannon")
                    Say(charge, 0f);
                    bannerAt = 0.2f;
                    _length = Mathf.Clamp(_speechEnd - 1.2f, 7.4f, 12.5f);
                    Add(_length - 1.5f, Op.Siren1Stop, 1, 1.5f);
                    break;
                }
                case BadShowMode.Sirens:
                {
                    // One car, then everyone. The small high one arrives last.
                    lightsSeconds = 7f;
                    Add(0.3f, Op.Siren1);
                    Add(0.8f, Op.Click);
                    Say("vo.police.handsup", 1f);
                    Extras(2f, 3f, 3.6f, 4.1f, 4.5f, 4.8f);
                    Say("vo.police.surround", 3.9f);   // (Brett 0.1.23: no "every car we have" — silly, and too much talking)
                    bannerAt = 0.2f;
                    _length = Mathf.Clamp(_speechEnd + 0.6f, 10f, 12f);
                    Add(_length - 2.4f, Op.SirensOut, 0, 2f);
                    break;
                }
                case BadShowMode.Helicopter:
                {
                    lightsSeconds = 6f;
                    Add(0.3f, Op.Siren1);
                    Add(0.8f, Op.Click);
                    Say("vo.police.handsup", 1f);
                    Add(1.6f, Op.RotorIn);
                    Add(2.8f, Op.Spot, 3, 2f);                 // three passes, 2 s each
                    Say(VoiceLines.PoliceHeli(Sfx.Has), 3.9f); // as the light crosses you (a clip-less new line → the window one)
                    // (Brett 0.1.23: no "It's a whole thing" — silly, and too much talking)
                    Add(8.8f, Op.RotorOut);
                    bannerAt = 0.2f;
                    _length = Mathf.Clamp(_speechEnd + 0.8f, 10.5f, 12f);
                    Add(8.4f, Op.Siren1Stop, 1, 1.5f);
                    break;
                }
                default: // Mugshot
                {
                    // Short lights (done before the flash), then the booking photo: the board goes up where you look as he
                    // says "look at the camera" and stays there; front shot; "now turn to your left" (rarely "…your other
                    // left"); time to turn; the profile shot at the same board; a hold with the charge; banner.
                    // After the standoff's arrest (booking): he's already cuffed — no siren or "hands up", the jail greeting.
                    lightsSeconds = 2.4f;
                    if (!booking) { Add(0.3f, Op.Siren1); Add(3f, Op.Siren1Stop, 1, 1.5f); }
                    Add(0.45f, Op.Click);
                    Say(booking ? "vo.cop.book.soap" : "vo.police.handsup", 0.7f);
                    float board = Mathf.Max(3.1f, _speechFree);
                    Add(board, Op.Props);
                    Say("vo.police.camera", board);
                    float f1 = Mathf.Max(board + 1.4f, _speechFree + 0.05f);
                    Add(f1, Op.Flash, 0);
                    float turn = Say(PickTurnLine(Random.value, Sfx.Has(OtherLeftLine)), f1 + 0.35f);
                    float f2 = Mathf.Max(_speechFree + 0.05f, turn + TurnSeconds);
                    Add(f2, Op.Flash, 1);
                    Say(charge, f2 + 0.45f);                   // read out during the hold
                    bannerAt = f2 + MugshotHold;
                    Add(bannerAt - 0.3f, Op.PropsAway);
                    _length = Mathf.Clamp(Mathf.Max(bannerAt + 3.2f, _speechEnd - 1f), 8f, 12.5f);
                    break;
                }
            }
            DropLate();
            _cues.Sort((a, b) => a.At.CompareTo(b.At));
            _active = true;
            Debug.Log($"[HIS] Bad-call show: {(mode == BadShowMode.All ? $"All → {bit} (next {Rotation[RotationIndex]})" : bit.ToString())}, " +
                      $"{_length:F1} s, banner at {bannerAt:F1} s, {_keyCount} line(s), {_extras} extra siren(s)");
            return _length;
        }

        /// <summary>Stop everything now (sounds, light, props). Safe any time.</summary>
        public void Clear()
        {
            _active = false;
            for (int i = 0; i < _sirenHandle.Length; i++) StopHandle(ref _sirenHandle[i], 0.25f);
            StopHandle(ref _rotorHandle, 0.25f);
            StopHandle(ref _lineHandle, 0f);
            StopHandle(ref _clickHandle, 0f);
            _rotorAt = _rotorLeaveAt = -1f;
            if (_spot != null) _spot.Stop();
            if (_mug != null) _mug.Hide();
        }

        /// <summary>The verdict banner is going up now: nothing of the show may hang in front of it.</summary>
        public void MakeWayForBanner()
        {
            if (_mug != null) _mug.MakeWay();
        }

        /// <summary>End of the show: the loops wind down; the props (and a line in its echo) stay until <see cref="Clear"/>.</summary>
        public void Finish()
        {
            _active = false;
            for (int i = 0; i < _sirenHandle.Length; i++) StopHandle(ref _sirenHandle[i], 1f);
            StopHandle(ref _rotorHandle, 1.5f);
            if (_spot != null) _spot.Stop();
        }

        /// <summary>Tests: anything still running or showing after <see cref="Clear"/> (null = clean).</summary>
        public string DebugLeftovers()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _sirenHandle.Length; i++) if (_sirenHandle[i] != 0) sb.Append($"siren{i} ");
            if (_rotorHandle != 0) sb.Append("rotor ");
            if (_lineHandle != 0) sb.Append("line ");
            if (_spot != null && _spot.Visible) sb.Append("spotlight ");
            if (_mug != null && _mug.Visible) sb.Append("mugshot ");
            if (_active) sb.Append("still active ");
            return sb.Length == 0 ? null : sb.ToString().Trim();
        }

        public void Tick(float dt)
        {
            if (_active)
            {
                _t += dt;
                while (_next < _cues.Count && _cues[_next].At <= _t) Run(_cues[_next++]);
            }
            // Sirens drive in; the helicopter comes over, hangs about, leaves.
            for (int i = 0; i < _sirenAnchor.Length; i++)
            {
                if (_sirenHandle[i] == 0) continue;
                float k = Mathf.Clamp01((_t - _sirenAt[i]) / _sirenTravel[i]);
                _sirenAnchor[i].position = Vector3.Lerp(_sirenFrom[i], _sirenTo[i], k * k * (3f - 2f * k));
            }
            if (_rotorHandle != 0 || _rotorLeaveAt >= 0f)
            {
                Vector3 p;
                if (_rotorLeaveAt >= 0f) p = Vector3.Lerp(_rotorOver, _rotorAway, Mathf.Clamp01((_t - _rotorLeaveAt) / 3f));
                else
                {
                    float k = Mathf.Clamp01((_t - _rotorAt) / 3.5f);
                    p = Vector3.Lerp(_rotorFrom, _rotorOver, k * (2f - k));
                    p += new Vector3(Mathf.Sin(_t * 0.7f), 0f, Mathf.Cos(_t * 0.5f)) * 2f * k; // a lazy hover
                }
                _rotorAnchor.position = p;
            }
            _spot.Tick(dt);
            _mug.Tick(dt);
        }

        // ───────────────────────────── timeline ─────────────────────────────

        void Add(float at, Op op, int arg = 0, float f = 0f) => _cues.Add(new Cue { At = at, Op = op, Arg = arg, F = f });

        /// <summary>Queue a bullhorn line no earlier than <paramref name="earliest"/> and never over the previous one; returns when it starts.</summary>
        float Say(string key, float earliest)
        {
            if (key == null || _keyCount >= _keys.Length) return earliest;
            float len = Sfx.Length(key);
            if (len <= 0f) len = Estimate(VoiceLines.Text(key));
            float at = Mathf.Max(earliest, _speechFree);
            _keys[_keyCount] = key;
            Add(at, Op.Line, _keyCount++, len);
            _speechFree = at + len - TailOverlap;
            _speechEnd = at + len;
            return at;
        }

        void Extras(params float[] at) // (Begin only; a handful of floats per show)
        {
            _extras = Mathf.Min(at.Length, MaxExtras);
            // Power sum: 1 + n·x² = cap² → each extra at x of the first siren's level, times its extra distance back.
            _extraVolume = sirenVolume * Mathf.Sqrt((LoudnessCap * LoudnessCap - 1f) / Mathf.Max(1, _extras));
            for (int i = 0; i < _extras; i++) Add(at[i], Op.ExtraSiren, i);
        }

        // A line that would still be talking when the director clears the show (length + 1.5 s) is dropped, not cut off.
        void DropLate()
        {
            for (int i = _cues.Count - 1; i >= 0; i--)
                if (_cues[i].Op == Op.Line && _cues[i].At + _cues[i].F > _length + 1.45f)
                {
                    Debug.Log($"[HIS] Bad-call show: no time for {_keys[_cues[i].Arg]}");
                    _cues.RemoveAt(i);
                }
        }

        static float Estimate(string text) => text == null ? 1.5f : 0.9f + text.Split(' ').Length * 0.3f;

        void Run(in Cue c)
        {
            switch (c.Op)
            {
                case Op.Siren1:
                    StartSiren(0, "outcome.siren", _door + _outward * SirenStart, _door + _outward * SirenEnd, SirenApproach, sirenVolume, 1f, 2.5f);
                    break;
                case Op.Siren1Stop:
                    StopHandle(ref _sirenHandle[0], c.F);
                    break;
                case Op.ExtraSiren:
                {
                    int i = c.Arg;
                    var dir = Quaternion.AngleAxis(ExtraAngle[i], Vector3.up) * _outward;
                    float d = ExtraDist[i];
                    // Farther back than the first (7 m) → a touch louder at the source, so each arrival is heard.
                    float vol = _extraVolume * Mathf.Clamp(d / SirenEnd, 1f, 2.2f);
                    StartSiren(i + 1, ExtraKey[i], _door + dir * (d + 18f) + Vector3.up, _door + dir * d + Vector3.up, 4f, vol, ExtraPitch[i], 1.2f);
                    SirensJoined++;
                    break;
                }
                case Op.SirensOut:
                    for (int i = 0; i < _sirenHandle.Length; i++) StopHandle(ref _sirenHandle[i], c.F);
                    break;
                case Op.Click:
                    _clickHandle = Sfx.Play("outcome.bullhorn", _bullhorn, 0.8f);
                    break;
                case Op.Line:
                {
                    var key = _keys[c.Arg];
                    StopHandle(ref _lineHandle, 0.15f); // the last one's echo tail
                    _lineHandle = Sfx.Play(key, _bullhorn);
                    HIS.UI.Subtitles.Show("Police", VoiceLines.Text(key), c.F + 0.3f);
                    LinesSpoken++;
                    if (key == TurnLine || key == OtherLeftLine) TurnLineSpoken(key);
                    break;
                }
                case Op.RotorIn:
                {
                    Eye(out var eye, out _);
                    _rotorFrom = _door + _outward * 70f + Vector3.up * 35f;
                    _rotorOver = new Vector3(eye.x, eye.y + 20f, eye.z) + _outward * 3f;
                    _rotorAt = _t; _rotorLeaveAt = -1f;
                    _rotorAnchor.position = _rotorFrom;
                    _rotorHandle = Sfx.PlayLoop("outcome.rotor", _rotorFrom, rotorVolume, 2.5f, _rotorAnchor);
                    break;
                }
                case Op.RotorOut:
                    _rotorLeaveAt = _t;
                    _rotorOver = _rotorAnchor.position;
                    _rotorAway = _rotorOver - _outward * 80f + Vector3.up * 10f;
                    StopHandle(ref _rotorHandle, 2.5f);
                    break;
                case Op.Spot:
                {
                    Eye(out var eye, out var fwd);
                    RoomHeights(eye, out float floorY, out float ceilingY);
                    _spot.Begin(eye, fwd, floorY, ceilingY, c.Arg, c.F);
                    break;
                }
                case Op.Props:
                {
                    Eye(out var eye, out var fwd);
                    _mug.Show(eye, fwd, _charge, _booking, System.DateTime.Now.ToString("MM dd yyyy"));
                    break;
                }
                case Op.Flash:
                {
                    Eye(out var eye, out var fwd);
                    bool gentle = OutcomeShow.ReduceFlashing;
                    if (_lights != null) _lights.Flash(gentle ? 0.5f : 0.85f, gentle ? 0.6f : 0.35f); // 0.85: blown out, not a full-white slap
                    // The shutter, same frame, from the camera: in front of where he faced when the board went up (it stays
                    // there, so after "turn to your left" it clicks off to his right), not wherever he looks now.
                    LastShutter = _mug.Visible ? _mug.CameraSpot : eye + fwd * 0.8f;
                    Sfx.Play("outcome.camera", LastShutter);
                    if (_mug.Visible) _mug.Shot(c.Arg, gentle ? 0.5f : 1f);                               // 0 front, 1 profile
                    FlashesFired++;
                    break;
                }
                case Op.PropsAway:
                    _mug.Tuck();
                    break;
            }
        }

        void StartSiren(int i, string key, Vector3 from, Vector3 to, float travel, float volume, float pitch, float fadeIn)
        {
            StopHandle(ref _sirenHandle[i], 0f);
            _sirenFrom[i] = from; _sirenTo[i] = to; _sirenAt[i] = _t; _sirenTravel[i] = Mathf.Max(0.1f, travel);
            _sirenAnchor[i].position = from;
            _sirenHandle[i] = Sfx.PlayLoop(key, from, volume, fadeIn, _sirenAnchor[i]);
            if (pitch != 1f) Sfx.ScalePitch(_sirenHandle[i], pitch);
        }

        static void StopHandle(ref int handle, float fade)
        {
            if (handle == 0) return;
            Sfx.Stop(handle, fade);
            handle = 0;
        }

        static void Eye(out Vector3 eye, out Vector3 forward) => OutcomeShow.Head(out eye, out forward);

        static void RoomHeights(Vector3 eye, out float floorY, out float ceilingY)
        {
            OutcomeShow.RoomHeights(out floorY, out ceilingY, out var room);
            if (room == null) { floorY = eye.y - 1.6f; ceilingY = floorY + 2.4f; }
        }

        T Child<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            return go.AddComponent<T>();
        }
    }
}
