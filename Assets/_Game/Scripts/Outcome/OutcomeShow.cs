using System.Text;
using HIS.Core;
using HIS.Room;
using HIS.Scenario;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;

namespace HIS.Outcome
{
    /// <summary>
    /// The end-of-scenario show (PLAN.md §2, M2b "minimal show"; research/11 + corrections), about 7-8 s:
    /// <list type="bullet">
    /// <item>GOOD CALL: world-locked confetti from two floor cannons plus a ceiling rain, a popper and a cheer, and a gold
    ///   banner. A close call gets less confetti and a "Close call" line.</item>
    /// <item>PERFECT: the most confetti, a sting and the cheer.</item>
    /// <item>BAD CALL: the real room pulses red/blue through the passthrough LUTs (<see cref="PoliceLights"/>). A siren is
    ///   heard far away and low-passed, easing from about 20 m to 7 m out past the door (Brett 09-22 "pretty aggressive",
    ///   09-23 "a little louder"), a megaphone click and "This is the police. Come out with your hands up!" from just
    ///   outside the door, then the funny part picked in the pause menu (<see cref="BadCallShow"/>: the bullhorn cop
    ///   escalating, the sirens multiplying, the helicopter searchlight or the mugshot; "All" = one of them per bad call, in
    ///   turn, <see cref="BadCallShow.Rotation"/>; ≤ 12.5 s),
    ///   and a red banner with the charge (after the mugshot when there is one).</item>
    /// <item>YOU DIDN'T SURVIVE: the room dims and desaturates, with a banner.</item>
    /// <item>Quiet (a Marshal night's visitor left alone, research/27 Q3): a good or perfect call with the banner only.</item>
    /// </list>
    /// Every banner shows the judge's reason and who the intruder really was. The banner and the "you died" dim stay up
    /// after the show ends, until <see cref="Clear"/> or the next <see cref="Play"/>, so the director decides when to move
    /// on. The police tint always restores itself at the end of the show.
    /// The director ends it early on a B/Y skip (<see cref="ScenarioDirector"/>); the banner's last line says so.
    /// Driven by scaled time, so the pause menu freezes the show. Everything is built once in Awake, so a show allocates
    /// only its banner strings.
    /// </summary>
    public class OutcomeShow : MonoBehaviour
    {
        public static OutcomeShow Instance { get; private set; }

        /// <summary>
        /// Photosensitivity: slower (0.8 Hz), sine-shaped red/blue swaps. OFF by default since the 0.1.7 playtest (Brett:
        /// "flash brighter and faster"); the full rate still stays under WCAG's 3 flashes/s. Becomes a persisted setting in M5.
        /// </summary>
        public static bool ReduceFlashing = false;

        [Header("Passthrough: the single '[HIS] PassthroughUnderlay' layer (never add a second one)")]
        [SerializeField] OVRPassthroughLayer passthrough;

        [Header("Materials: saved assets, because a runtime Shader.Find can be stripped from builds")]
        [Tooltip("Assets/_Game/Materials/UI_Text_URP.mat: TMP's XR-safe URP text material (legacy SDF draws nothing in stereo).")]
        [SerializeField] Material textMaterial;
        [Tooltip("Optional. Empty = TMP's default font, which is the one UI_Text_URP.mat was built from.")]
        [SerializeField] TMP_FontAsset font;
        [Tooltip("Assets/_Game/Materials/UI_Panel.mat: dark unlit plate behind the banner text.")]
        [SerializeField] Material panelMaterial;
        [Tooltip("Universal Render Pipeline/Particles/Unlit, opaque. Cull is forced off on a runtime copy.")]
        [SerializeField] Material confettiMaterial;
        [Tooltip("FX_RoomLight.mat (HIS/OcclusionUnlitBias): depth-occluded, alpha-blended, depth bias 0.06 — the bad-call searchlight and height chart use runtime copies.")]
        [SerializeField] Material fxMaterial;

        [Header("Police (bad call)")]
        [SerializeField] float flashHz = 2f;             // WCAG 2.3.1: ≤ 3 flashes/s; one red→blue→red cycle = one flash
        [SerializeField] float reducedFlashHz = 0.8f;

        static readonly Color Gold = new Color32(0xFF, 0xC8, 0x3D, 0xFF);
        static readonly Color PerfectGold = new Color32(0xFF, 0xDF, 0x6B, 0xFF);
        static readonly Color GoodAccent = new Color32(0x22, 0xC5, 0x5E, 0xFF);
        static readonly Color BadRed = new Color32(0xE6, 0x39, 0x46, 0xFF);
        static readonly Color DiedWhite = new Color32(0xE8, 0xE8, 0xE8, 0xFF);
        static readonly Color DiedAccent = new Color32(0x7A, 0x1E, 0x1E, 0xFF);

        const string Disclaimer = "Simplified game rules inspired by California law (2026). Not legal advice.";
        /// <summary>A federal officer's card (no home presumption: the Marshal; research/27 §4).</summary>
        const string DisclaimerFederal = "Inspired by federal and California law (2026). Not legal advice.";
        /// <summary>
        /// A quiet show's length (research/27 Q3): the banner alone, up at 0.25 s, then the usual linger, so it's up about 5 s
        /// (≥ <see cref="MinBannerSeconds"/>) without holding the night up for the full show's 7.
        /// </summary>
        const float QuietSeconds = 4f;

        public bool Playing { get; private set; }
        /// <summary>The verdict banner is up (the director only takes the B/Y skip once the player can see it).</summary>
        public bool BannerUp => _banner != null && _banner.Visible;
        /// <summary>
        /// B/Y may end the show now: once the banner is up, or a second into a bad-call show whose banner waits for the
        /// mugshot (the long funny part is what you'd want to skip).
        /// </summary>
        public bool CanSkip => BannerUp || (Playing && _tier == Tier.Bad && _t > 1f);
        public BadCallShow BadCall => _bad;
        public PoliceLights Lights => _lights;
        public float Elapsed => _t;
        /// <summary>The running (or last) show's length in seconds, as returned by <see cref="Play"/>.</summary>
        public float Length => _length;
        /// <summary>When the banner comes up, seconds into the show.</summary>
        public float BannerAt => _bannerAt;
        /// <summary>
        /// How long the director leaves the banner up after the show ends: 1.5 s, or longer when the banner came late (after
        /// the mugshot's hold) so it is always up at least <see cref="MinBannerSeconds"/> before the room moves on.
        /// </summary>
        public float Linger => Mathf.Max(1.5f, (_bannerAt < float.MaxValue ? _bannerAt : 0f) + MinBannerSeconds - _length);
        public const float MinBannerSeconds = 4.5f;

        /// <summary>Tests: a fake head for everything the show places (banner, mugshot, searchlight). Null = the camera.</summary>
        public static Pose? TestHead;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => TestHead = null; // domain reload off: an aborted test must not leave a fake head behind

        /// <summary>The player's eye and gaze (<see cref="TestHead"/> in tests; a default pose without a camera).</summary>
        public static void Head(out Vector3 eye, out Vector3 forward)
        {
            if (TestHead is Pose tp) { eye = tp.position; forward = tp.forward; return; }
            var cam = Camera.main;
            if (cam != null) { eye = cam.transform.position; forward = cam.transform.forward; return; }
            eye = new Vector3(0f, 1.6f, 0f);
            forward = Vector3.forward;
        }

        /// <summary>The pause menu's "Preview bad call": a made-up bad verdict, no scenario needed.</summary>
        public static Verdict SampleBadVerdict => new()
        {
            Tier = Tier.Bad, Headline = "BAD CALL", Charge = Judge.ChargeKilled,
            Reason = "Preview: this is the bad-call show. Pick another in the pause menu.",
            WhoItWas = "Nobody. It was a preview",
        };

        PoliceLights _lights;
        ConfettiBurst _confetti;
        VerdictBanner _banner;
        BadCallShow _bad;
        readonly StringBuilder _sb = new(256);

        Tier _tier;
        bool _quiet; // a good / perfect show with the banner only (research/27 Q3): no confetti, popper, sting or cheer
        float _t, _length, _prepTimer;
        Vector3 _eye, _forward;
        string _title, _body;
        Color _titleColor, _accentColor;

        // Timeline cues (seconds from Play) and whether each has fired.
        float _bannerAt, _cheerAt;
        bool _bannerDone, _cheerDone, _popper2Done;

        void Awake()
        {
            Instance = this;
            if (passthrough == null) passthrough = FindAnyObjectByType<OVRPassthroughLayer>();
            if (passthrough == null) Debug.LogWarning("[HIS] OutcomeShow: no OVRPassthroughLayer; police/dim tints disabled");

            if (!TryGetComponent(out _lights)) _lights = gameObject.AddComponent<PoliceLights>();
            _lights.Init(passthrough);
            _confetti = Child<ConfettiBurst>("Confetti");
            _confetti.Init(confettiMaterial);
            _banner = Child<VerdictBanner>("Banner");
            _banner.Init(font, textMaterial, panelMaterial);
            _bad = Child<BadCallShow>("BadCall");
            _bad.Init(_lights, fxMaterial, font, textMaterial, panelMaterial);
        }

        void OnDisable() => Clear();

        void OnDestroy()
        {
            Clear();
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Run the show for <paramref name="v"/>. <paramref name="doorPoint"/> is the door the police are behind, and
        /// <paramref name="doorOutward"/> points from the room out through that door. Returns the show length in seconds.
        /// A show already running is cleared first.
        /// </summary>
        /// <param name="quiet">
        /// A good or perfect call gets the banner only: no confetti, popper, sting or cheer, and a shorter show. A Marshal night's
        /// quiet visitors (Ortiz, the courier, Sal) left alone get this mid-shift (research/27 Q3); WITNESS DELIVERED keeps the
        /// full show. Ignored for a bad call or a death (those shows are the point).
        /// </param>
        public float Play(Verdict v, Vector3 doorPoint, Vector3 doorOutward, bool quiet = false)
        {
            Clear(raise: Playing); // (nothing was playing, e.g. after a standoff: the parked police cars stay for the booking)
            HeadPose(out _eye, out _forward);
            _tier = v.Tier;
            _quiet = quiet && (v.Tier is Tier.Perfect or Tier.Good);
            _t = 0f;
            _bannerDone = _cheerDone = _popper2Done = false;
            _bannerAt = _cheerAt = float.MaxValue;
            ComposeBanner(v);

            switch (v.Tier)
            {
                case Tier.Perfect when _quiet:
                case Tier.Good when _quiet:
                    _bannerAt = 0.25f;                                       // (as the full show's; _cheerAt stays off)
                    _length = QuietSeconds;
                    break;
                case Tier.Perfect:
                case Tier.Good:
                {
                    bool perfect = v.Tier == Tier.Perfect;
                    int perCannon = perfect ? 150 : v.CloseCall ? 70 : 110;
                    int rain = perfect ? 150 : v.CloseCall ? 50 : 90;
                    RoomHeights(out float floorY, out float ceilingY, out var room);
                    _confetti.Fire(_eye, _forward, floorY, ceilingY, room, perCannon, rain);
                    Sfx.Play2D("win.confetti");
                    if (perfect) Sfx.Play2D("win.sting");
                    _bannerAt = 0.25f;
                    _cheerAt = perfect ? 0.9f : 0.5f;
                    _length = 7f;
                    break;
                }
                case Tier.Bad:
                {
                    DoorFrame(doorPoint, ref doorOutward);
                    bool booking = ForceBadMode.HasValue;
                    var mode = ForceBadMode ?? BadCallShow.Mode; ForceBadMode = null; // (the police ending's booking: the mugshot)
                    float show = _bad.Begin(mode, v, doorPoint, doorOutward, _eye, _forward, out _bannerAt, out float alternate, booking);
                    float hz = ReduceFlashing ? reducedFlashHz : flashHz;
                    float lights = _lights.BeginPolice(hz, 0.6f, alternate, 0.8f, gentle: ReduceFlashing);
                    _length = Mathf.Max(show, lights + 0.3f);
                    break;
                }
                default: // Tier.YouDied
                    _lights.BeginDim(-0.35f, -0.7f, 1.5f);
                    _bannerAt = 0.6f;
                    _length = 6f;
                    break;
            }

            Playing = true;
            Debug.Log($"[HIS] Outcome show: {v.Tier}{(v.CloseCall ? " (close call)" : "")}{(_quiet ? " (quiet)" : "")}, {_length:F1} s, reduce flashing {ReduceFlashing}");
            Started?.Invoke(v.Tier, _length);
            return _length;
        }

        /// <summary>The next bad-call show plays this bit instead of the picked one (once): the arrest's booking = the mugshot.</summary>
        public static BadShowMode? ForceBadMode;

        /// <summary>A show began (tier, its length): the door camera's police cars pull up on a bad call (Brett 0.1.22).</summary>
        public static event System.Action<Tier, float> Started;
        /// <summary>The show was cleared (skip, next round, session reset): anything it started outside goes too.</summary>
        public static event System.Action Cleared;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetShowEvents() { Started = null; Cleared = null; ForceBadMode = null; }

        /// <summary>
        /// Stop everything now: sounds, confetti, passthrough tint/dim (restored), banner. Safe to call any time,
        /// including when nothing is playing (session reset).
        /// </summary>
        public void Clear() => Clear(raise: true);

        /// <summary>
        /// As <see cref="Clear()"/>, without <see cref="Cleared"/> (which sends the hitmen's parked cars away): written for the
        /// quiet banner of a Marshal visitor left alone between the waves (Brett 0.1.57). No caller since Brett 0.1.58 — that
        /// visitor gets no show at all now (ScenarioDirector.RunScenario).
        /// </summary>
        public void ClearBanner() => Clear(raise: false);

        void Clear(bool raise)
        {
            Playing = false;
            if (_bad != null) _bad.Clear();
            if (_confetti != null) _confetti.StopAll();
            if (_lights != null) _lights.Restore();
            if (_banner != null) _banner.Hide();
            if (raise) Cleared?.Invoke();
        }

        void Update()
        {
            // Build the police LUTs ahead of time (a few ms), as soon as passthrough is up; poll about once a second.
            if (_lights != null && !_lights.LutsReady)
            {
                _prepTimer -= Time.unscaledDeltaTime;
                if (_prepTimer <= 0f) { _prepTimer = 1f; _lights.TryPrepare(); }
            }

            float dt = Time.deltaTime; // scaled: the pause menu (timeScale 0) freezes the show
            if (Playing)
            {
                _t += dt;
                RunCues();
            }
            _bad.Tick(dt);
            // A camera flash runs out on real time: a pause mid-flash must not hold the room blown white behind the menu.
            _lights.Tick(_lights.FlashActive ? Time.unscaledDeltaTime : dt);
            _confetti.Tick(dt);
            _banner.Tick(dt);

            if (Playing && _t >= _length) Finish();
        }

        void RunCues()
        {
            if (!_bannerDone && _t >= _bannerAt) ShowBanner();
            if (!_popper2Done && !_quiet && (_tier == Tier.Good || _tier == Tier.Perfect) && _t >= 0.12f)
            {
                _popper2Done = true;
                Sfx.Play2D("win.confetti", 0.8f); // the second cannon
            }
            if (!_cheerDone && _t >= _cheerAt)
            {
                _cheerDone = true;
                Sfx.Play2D("win.cheer", 0.7f);
            }
        }

        // End of the show: the police tint is gone (it ends on its own; this is the backstop), sounds wind down.
        // The banner and a "you died" dim stay until Clear().
        void Finish()
        {
            Playing = false;
            _lights.FinishPolice();
            _bad.Finish();
            if (!_bannerDone) ShowBanner();
            Debug.Log($"[HIS] Outcome show finished ({_tier})");
        }

        // Placed where the player is looking when it appears, then world-locked; in front of the intruder if he's in the way.
        // A quieter last line names the skip buttons (B / Y; one if only one controller is found).
        void ShowBanner()
        {
            _bannerDone = true;
            _bad.MakeWayForBanner(); // a floating mugshot must never hang in front of the banner text
            HeadPose(out var eye, out var forward);
            var keys = ScenarioDirector.SkipButtons();
            var body = _body;
            if (keys != null)
                body = (string.IsNullOrEmpty(body) ? "" : body + "\n") + "<size=60%><color=#A4ADBA>Press " + keys + " to skip</color></size>";
            _banner.Show(_title, _titleColor, _accentColor, body, eye, forward, IntruderBounds());
        }

        // The scenario intruder's visible body (explicit null checks: `?.` skips Unity's destroyed-object check).
        readonly System.Collections.Generic.List<Renderer> _renderers = new();
        Bounds? IntruderBounds()
        {
            var dir = ScenarioDirector.Instance;
            var who = dir != null ? dir.Intruder : null;
            if (who == null) return null;
            who.GetComponentsInChildren(false, _renderers);
            Bounds b = default; bool any = false;
            foreach (var r in _renderers)
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            _renderers.Clear();
            return any ? b : (Bounds?)null;
        }

        void ComposeBanner(Verdict v)
        {
            switch (v.Tier)
            {
                case Tier.Perfect: _title = Or(v.Headline, "PERFECT"); _titleColor = PerfectGold; _accentColor = Gold; break;
                case Tier.Good: _title = Or(v.Headline, "GOOD CALL"); _titleColor = Gold; _accentColor = GoodAccent; break;
                case Tier.Bad: _title = Or(v.Headline, "BAD CALL"); _titleColor = BadRed; _accentColor = BadRed; break;
                default: _title = Or(v.Headline, "YOU DIDN'T SURVIVE"); _titleColor = DiedWhite; _accentColor = DiedAccent; break;
            }

            _sb.Clear();
            if (v.CloseCall && v.Tier != Tier.YouDied) Line("<color=#FFD166><i>Close call</i></color>");
            if (v.Tier == Tier.Bad && !string.IsNullOrEmpty(v.Charge) && string.IsNullOrEmpty(v.Sentence)) Line("<b>Charged with " + LowerFirst(v.Charge) + "</b>");
            if (!string.IsNullOrEmpty(v.Reason)) Line(v.Reason);
            if (!string.IsNullOrEmpty(v.Sentence)) Line(v.Sentence); // the police ending: every charge and the estimated sentence
            if (!string.IsNullOrEmpty(v.WhoItWas)) Line("<color=#B8C0CC>Who it was: " + v.WhoItWas + "</color>");
            // By role (research/27 §4): a Marshal's card is weighed without the home presumption, partly on federal law.
            if (v.Tier != Tier.YouDied) Line("<size=60%><color=#8A93A0>" + (Judge.HomePresumption ? Disclaimer : DisclaimerFederal) + "</color></size>");
            _body = _sb.ToString();
        }

        void Line(string s)
        {
            if (_sb.Length > 0) _sb.Append('\n');
            _sb.Append(s);
        }

        static string Or(string s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;

        // "Voluntary manslaughter" → "voluntary manslaughter"; leaves acronyms ("ADW …") alone.
        static string LowerFirst(string s) =>
            s.Length > 1 && char.IsUpper(s[0]) && char.IsLower(s[1]) ? char.ToLowerInvariant(s[0]) + s.Substring(1) : s;

        static void HeadPose(out Vector3 eye, out Vector3 forward) => Head(out eye, out forward);

        /// <summary>A usable outward direction even if the caller passed none: away from the player through the door.</summary>
        void DoorFrame(Vector3 doorPoint, ref Vector3 outward)
        {
            outward.y = 0f;
            if (outward.sqrMagnitude > 1e-4f) { outward.Normalize(); return; }
            outward = doorPoint - _eye; outward.y = 0f;
            if (outward.sqrMagnitude < 1e-4f) { outward = -_forward; outward.y = 0f; }
            if (outward.sqrMagnitude < 1e-4f) outward = Vector3.back;
            outward.Normalize();
        }

        /// <summary>Real floor and ceiling heights from the room scan (y = 0 floor and a 2.4 m ceiling without one).</summary>
        internal static void RoomHeights(out float floorY, out float ceilingY, out MRUKRoom room)
        {
            floorY = 0f;
            ceilingY = float.NaN;
            room = null;
            var scanner = RoomScanner.Instance;
            if (scanner != null && scanner.Room != null)
            {
                room = scanner.Room;
                var floors = room.FloorAnchors;
                if (floors.Count > 0 && floors[0] != null) floorY = floors[0].transform.position.y;
                var ceilings = room.CeilingAnchors;
                if (ceilings.Count > 0 && ceilings[0] != null) ceilingY = ceilings[0].transform.position.y;
            }
            if (float.IsNaN(ceilingY) || ceilingY - floorY < 1.8f || ceilingY - floorY > 6f) ceilingY = floorY + 2.4f;
        }

        /// <summary>
        /// No director (or no scenario mode): play the sample bad call at the scanned door, or behind the player without one.
        /// Returns the show length. The director's own preview (<see cref="ScenarioDirector.PreviewBadCall"/>) also handles
        /// the skip and clears the room; this is the bare fallback.
        /// </summary>
        public float PlayPreview()
        {
            HeadPose(out var eye, out var fwd);
            var rs = RoomScanner.Instance;
            var cur = VirtualDoor.Current;
            var door = rs != null ? rs.PrimaryDoor : null;
            Vector3 point = cur != null ? cur.Center : door != null ? door.transform.position : eye - new Vector3(fwd.x, 0f, fwd.z).normalized * 3f;
            return Play(SampleBadVerdict, point, Vector3.zero); // zero outward → away from the player through that point
        }

        T Child<T>(string name) where T : Component
        {
            var t = transform.Find(name);
            var go = t != null ? t.gameObject : new GameObject(name);
            if (t == null) go.transform.SetParent(transform, false);
            if (!go.TryGetComponent(out T c)) c = go.AddComponent<T>();
            return c;
        }
    }
}
