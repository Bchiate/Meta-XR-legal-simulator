using System;
using HIS.Core;
using HIS.UI;
using HIS.Weapons;
using TMPro;
using UnityEngine;

namespace HIS.Scenario
{
    /// <summary>
    /// The player's yelled commands (research/08 §1, PLAN.md §6) on A (right) or X (left), either hand:
    /// <list type="bullet">
    /// <item>tap (released within 0.3 s) → the command that fits the moment, from <see cref="ContextResolver"/> (set by the
    /// ScenarioDirector; "Who the hell is in my house?!" without one);</item>
    /// <item>hold ≥ 0.3 s → a tiny 4-way hint by that controller; flick that hand's stick: up "Show me your hands!",
    /// left "Drop the weapon!", down "Get on the ground!", right "Get out of my house!"; no flick → "Who's there?".</item>
    /// </list>
    /// A/X and the stick are under the same thumb, so after a hold is released the hint waits a moment
    /// (<see cref="flickGraceAfterRelease"/>) for the thumb to reach the stick; a flick while still holding also works.
    /// Every yell: the voice line in the player's head, a caption, a light haptic, a 0.7 s cooldown (no spamming), a log
    /// line, and <see cref="Issued"/> — the director records it and tells the intruder. Input edges are derived from the
    /// held level and re-armed only after a release whenever gameplay input was closed (pause, panels, death), because
    /// HandController stops refreshing its button state while the gate is shut.
    /// </summary>
    public class CommandSystem : MonoBehaviour
    {
        public static CommandSystem Instance { get; private set; }

        /// <summary>A yell was issued: the command and the player's head position at that moment.</summary>
        public static event Action<Command, Vector3> Issued;

        /// <summary>Best command for a tap right now (set by the ScenarioDirector). Null, or <see cref="Command.None"/>, → WhoIsThere.</summary>
        public static Func<Command> ContextResolver;

        [SerializeField] HandController left, right;   // wired by MainSceneBuilder; found at Start if empty
        [SerializeField] float holdTime = 0.3f;
        [SerializeField] float flickThreshold = 0.7f;
        [Tooltip("The stick must be back near centre (below this) before a flick counts, so a stick already pushed doesn't fire.")]
        [SerializeField] float stickRest = 0.35f;
        [Tooltip("After a hold is released without a flick, how long the hint waits for the thumb to reach the stick before " +
                 "yelling 'Who's there?'. 0 = yell on release.")]
        [SerializeField] float flickGraceAfterRelease = 0.75f;
        [SerializeField] float cooldown = 0.7f;
        [SerializeField] float captionSeconds = 2.5f;
        [Header("Hint")]
        [SerializeField] float hintFontSize = 1.1f;
        [SerializeField] Vector3 hintOffset = new(0f, 0.10f, 0f);   // above the controller, world space

        /// <summary>The last command yelled (None before the first).</summary>
        public Command Last { get; private set; }
        /// <summary>Game time (Time.time) of the last yell.</summary>
        public float LastTime { get; private set; } = -100f;
        /// <summary>The controllers (null until found); the director reads B/Y from them to skip the verdict show.</summary>
        public HandController Left => _hands[0];
        public HandController Right => _hands[1];

        enum Phase { Idle, Pressed, Menu, Grace, WaitRelease }

        readonly HandController[] _hands = new HandController[2];
        readonly bool[] _down = new bool[2], _pressed = new bool[2], _released = new bool[2], _blocked = new bool[2];
        Phase _phase;
        int _active = -1;                  // index of the hand running the gesture (one gesture at a time)
        float _pressAt, _releaseAt, _cooldownUntil, _nextFind;
        bool _stickRested;
        int _voice;
        Transform _head;

        // The 4-way hint: one small label, moved to whichever controller opened it. Text strings are built once.
        Transform _hintRoot, _hintPlate;
        TextMeshPro _hintText;
        int _hintDir = -2;
        readonly string[] _hintStrings = new string[5]; // [0] no direction, [1..4] up/left/down/right highlighted

        void Awake()
        {
            if (Instance != null && Instance != this) Debug.LogWarning("[HIS] Two CommandSystems in the scene; the newest wins");
            Instance = this;
            _hands[0] = left; _hands[1] = right;
            for (int d = 0; d < 5; d++) _hintStrings[d] = BuildHint(d - 1);
        }

        void Start()
        {
            if (left == null || right == null) FindHands();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_hintRoot != null) Destroy(_hintRoot.gameObject); // lives at the scene root, not under us
        }

        void OnDisable()
        {
            Cancel();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; Issued = null; ContextResolver = null; } // domain reload off

        /// <summary>Yell a command from code (tests, dev tools): same voice, caption, cooldown and event as the buttons.</summary>
        public bool Yell(Command command) => Fire(command, "script", null);

        void Update()
        {
            bool open = InputGate.GameplayActive && Time.deltaTime > 0f &&
                        (PlayerState.Instance == null || !PlayerState.Instance.IsDead);
            if (!open)
            {
                // HandController freezes its button state while the gate is shut: forget the gesture and wait for each
                // button to be seen released before it counts again (no phantom yell on resume).
                Cancel();
                _blocked[0] = _blocked[1] = true;
                return;
            }
            if (_hands[0] == null && _hands[1] == null && Time.unscaledTime >= _nextFind) FindHands();

            for (int i = 0; i < 2; i++)
            {
                var h = _hands[i];
                bool down = h != null && h.PrimaryButton;
                _pressed[i] = down && !_down[i];
                _released[i] = !down && _down[i];
                _down[i] = down;
                if (_blocked[i])
                {
                    if (!down) _blocked[i] = false;
                    _pressed[i] = _released[i] = false;
                }
            }

            float now = Time.unscaledTime;
            if (_phase == Phase.Idle)
            {
                for (int i = 0; i < 2; i++)
                {
                    if (!_pressed[i]) continue;
                    _phase = Phase.Pressed; _active = i; _pressAt = now;
                    _stickRested = _hands[i].Thumbstick.sqrMagnitude < stickRest * stickRest;
                    break;
                }
                return;
            }

            var hand = _active >= 0 ? _hands[_active] : null;
            if (hand == null) { Cancel(); return; }
            var stick = hand.Thumbstick;
            float mag2 = stick.sqrMagnitude;
            if (mag2 < stickRest * stickRest) _stickRested = true;
            bool flick = _stickRested && mag2 >= flickThreshold * flickThreshold;

            switch (_phase)
            {
                case Phase.Pressed:
                case Phase.Menu:
                    if (flick) { Fire(Direction(stick), "flick", hand); EndGesture(Phase.WaitRelease); break; }
                    if (_released[_active])
                    {
                        if (_phase == Phase.Pressed) { Fire(Context(), "tap", hand); EndGesture(Phase.Idle); }
                        else if (flickGraceAfterRelease > 0f) { _phase = Phase.Grace; _releaseAt = now; }
                        else { Fire(Command.WhoIsThere, "hold", hand); EndGesture(Phase.Idle); }
                        break;
                    }
                    if (_phase == Phase.Pressed && now - _pressAt >= holdTime)
                    {
                        _phase = Phase.Menu;
                        ShowHint();
                        hand.Haptic(0.15f, 0.03f); // tick: the menu is open
                    }
                    break;

                case Phase.Grace:
                    if (flick) { Fire(Direction(stick), "flick", hand); EndGesture(Phase.Idle); }
                    else if (_pressed[_active]) { Fire(Command.WhoIsThere, "hold", hand); EndGesture(Phase.WaitRelease); }
                    else if (now - _releaseAt >= flickGraceAfterRelease) { Fire(Command.WhoIsThere, "hold", hand); EndGesture(Phase.Idle); }
                    break;

                case Phase.WaitRelease:
                    if (!_down[_active]) EndGesture(Phase.Idle);
                    break;
            }
        }

        void LateUpdate()
        {
            if (_hintRoot == null || !_hintRoot.gameObject.activeSelf) return;
            var hand = _active >= 0 ? _hands[_active] : null;
            if (hand == null || (_phase != Phase.Menu && _phase != Phase.Grace)) { HideHint(); return; }

            // Preview the direction the stick leans toward before it reaches the flick threshold.
            var s = hand.Thumbstick;
            int dir = s.sqrMagnitude >= stickRest * stickRest && _stickRested ? DirIndex(s) : -1;
            if (dir != _hintDir) SetHint(dir);

            if (_head == null) { var cam = Camera.main; if (cam != null) _head = cam.transform; }
            var pos = hand.transform.position + hintOffset;
            if (_head != null)
            {
                var fromEye = pos - _head.position;
                if (fromEye.sqrMagnitude > 1e-4f)
                {
                    pos -= fromEye.normalized * 0.03f; // a touch toward the eye so the controller doesn't cover it
                    _hintRoot.SetPositionAndRotation(pos, Quaternion.LookRotation(fromEye, Vector3.up));
                    return;
                }
            }
            _hintRoot.position = pos;
        }

        // ───────────────────────────── yelling ─────────────────────────────

        bool Fire(Command c, string how, HandController hand)
        {
            if (c == Command.None) c = Command.WhoIsThere;
            float now = Time.unscaledTime;
            if (now < _cooldownUntil) { Log.V($"[HIS] Command {c} ({how}) ignored: cooldown"); return false; }
            _cooldownUntil = now + cooldown;
            Last = c; LastTime = Time.time;

            string key = VoiceLines.KeyFor(c);
            if (_voice != 0) Sfx.Stop(_voice, 0.08f); // never talk over your own previous line
            _voice = Sfx.Play2D(key);
            Subtitles.Show("You", VoiceLines.Text(key), captionSeconds);
            if (hand != null) hand.HapticLight();

            var head = HeadPosition();
            Debug.Log($"[HIS] Command {c} ({how}{(hand == null ? "" : hand.IsLeft ? ", X" : ", A")})");
            Issued?.Invoke(c, head);
            return true;
        }

        static Command Context()
        {
            var resolver = ContextResolver;
            var c = Command.WhoIsThere;
            if (resolver != null)
            {
                try { c = resolver(); }
                catch (Exception e) { Debug.LogException(e); c = Command.WhoIsThere; }
            }
            return c == Command.None ? Command.WhoIsThere : c;
        }

        static Command Direction(Vector2 s) => DirIndex(s) switch
        {
            0 => Command.ShowHands, 1 => Command.DropWeapon, 2 => Command.GetDown, _ => Command.GetOut,
        };

        /// <summary>0 up, 1 left, 2 down, 3 right (dominant axis).</summary>
        static int DirIndex(Vector2 s) => Mathf.Abs(s.y) >= Mathf.Abs(s.x) ? (s.y > 0f ? 0 : 2) : (s.x < 0f ? 1 : 3);

        Vector3 HeadPosition()
        {
            if (_head == null) { var cam = Camera.main; if (cam != null) _head = cam.transform; }
            return _head != null ? _head.position : transform.position;
        }

        void EndGesture(Phase next)
        {
            _phase = next;
            if (next == Phase.Idle) _active = -1;
            HideHint();
        }

        void Cancel()
        {
            _phase = Phase.Idle; _active = -1;
            HideHint();
        }

        void FindHands()
        {
            _nextFind = Time.unscaledTime + 2f;
            foreach (var h in FindObjectsByType<HandController>(FindObjectsInactive.Exclude))
            {
                if (h.IsLeft) { if (left == null) left = h; }
                else if (right == null) right = h;
            }
            _hands[0] = left; _hands[1] = right;
            if (left == null && right == null) Debug.LogWarning("[HIS] CommandSystem: no HandControllers found — yell commands off");
        }

        // ───────────────────────────── the 4-way hint ─────────────────────────────

        static string BuildHint(int highlight)
        {
            static string W(string word, bool on) => on ? $"<color=#ffd24a><b>{word}</b></color>" : word;
            return $"{W("HANDS", highlight == 0)}\n{W("‹ DROP", highlight == 1)}   <size=70%><color=#8a93a0>who?</color></size>   " +
                   $"{W("OUT ›", highlight == 3)}\n{W("DOWN", highlight == 2)}";
        }

        void ShowHint()
        {
            if (_hintRoot == null)
            {
                _hintRoot = new GameObject("[HIS] CommandHint").transform;
                _hintText = Subtitles.CreateWorldText("Text", _hintRoot, hintFontSize, 4f, TextAlignmentOptions.Center);
                _hintText.textWrappingMode = TextWrappingModes.NoWrap;
                _hintPlate = Subtitles.CreatePlate("Backing", _hintRoot);
            }
            _hintRoot.gameObject.SetActive(true); // TMP builds no geometry while inactive: activate before setting text
            _hintDir = -2;
            SetHint(-1);
            LateUpdate(); // place it this frame, not one frame late at the origin
        }

        void SetHint(int dir)
        {
            _hintDir = dir;
            if (_hintText == null) return;
            _hintText.SetText(_hintStrings[dir + 1]);
            _hintText.ForceMeshUpdate(true);
            Subtitles.FitBacking(_hintText, _hintPlate, new Vector2(0.012f, 0.008f));
        }

        void HideHint()
        {
            if (_hintRoot != null && _hintRoot.gameObject.activeSelf) _hintRoot.gameObject.SetActive(false);
        }
    }
}
