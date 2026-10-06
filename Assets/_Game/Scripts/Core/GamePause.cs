using System.Collections.Generic;
using HIS.UI;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// Freezes the fight whenever the player can't be playing it: Meta menu open (input focus lost), headset taken off,
    /// tracking lost, app paused, or the left Menu button. Resuming is always explicit (pull a trigger, or Menu again)
    /// followed by a 3-second countdown, so nobody comes back to an intruder mid-swing.
    /// Hold Menu for 1.5 s to restart the session. While paused from the menu, either thumbstick drives a small settings
    /// list: up/down picks a row, left/right changes it, stick click resets it to the default. Rows: the loadout choices
    /// (<see cref="HIS.Weapons.Loadout.MenuSlots"/>, e.g. "Sidearm: Glock / M9"; applied at the next room clear), then the
    /// grip-angle trims (research/16 §3.1, 1° per nudge; the held gun follows the change live), then "Height: Auto / N cm"
    /// (<see cref="HIS.Weapons.PlayerHeight"/>, saved: where the belt and holsters hang; the first push from Auto starts at the
    /// measured height, hold to keep stepping, click = Auto), then "Bad-call show:
    /// All / Bullhorn / Sirens / Helicopter / Mugshot" (<see cref="HIS.Outcome.BadCallShow.Mode"/>, saved; All = one bit per
    /// bad call in turn, the next one named under it), then "Preview bad
    /// call" (stick right, A or stick click: unpause at once and play that show with a sample verdict — the room is cleared
    /// first, so no countdown; B/Y skips it), then "Mode: Scenarios / Sandbox / Marshal" (research/27: so Brett can pick
    /// the US Marshal night on the headset; applied when the game resumes, which starts the room over; saved on the headset,
    /// <see cref="HIS.Scenario.ScenarioDirector.SavedMode"/>; click = Scenarios), then, in Marshal mode only, "Weapons check"
    /// (stick right, A or stick click: the night starts over with the weapons check, HIS.Scenario.MarshalBriefing), then "Doors: pick
    /// again", then "Door hinge: flip" (stick right, A or stick click: the front door hangs from its other jamb, at once if it's shut,
    /// remembered for this room — <see cref="HIS.Room.VirtualDoor.FlipHinge"/>; stays paused), then "Credits" (stick right,
    /// A or stick click opens the scrolling attribution page, <see cref="CreditsMenu"/>; B/Y, a trigger or Menu goes back).
    /// </summary>
    public class GamePause : MonoBehaviour
    {
        public static GamePause Instance { get; private set; }

        [SerializeField] float countdownSeconds = 3f;
        [SerializeField] float restartHoldSeconds = 1.5f;

        public bool IsPaused { get; private set; }
        public static event System.Action<bool> PausedChanged;

        const string NoticeKey = "pause";
        readonly HashSet<string> _reasons = new();
        float _savedTimeScale = 1f;
        float _countdownEnd = -1f;
        float _menuDownAt = -1f;
        bool _menuRestartFired;

        void Awake() { Instance = this; }

        void OnEnable()
        {
            OVRManager.InputFocusLost += OnFocusLost;
            OVRManager.InputFocusAcquired += OnFocusAcquired;
            OVRManager.HMDUnmounted += OnUnmounted;
            OVRManager.HMDMounted += OnMounted;
            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
            HIS.Weapons.Loadout.Changed += UpdateNotice;
        }

        void OnDisable()
        {
            OVRManager.InputFocusLost -= OnFocusLost;
            OVRManager.InputFocusAcquired -= OnFocusAcquired;
            OVRManager.HMDUnmounted -= OnUnmounted;
            OVRManager.HMDMounted -= OnMounted;
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
            HIS.Weapons.Loadout.Changed -= UpdateNotice;
            if (IsPaused) ApplyUnpaused(); // never leave the app frozen
        }

        void OnFocusLost() => AddSystemReason("focus");
        void OnFocusAcquired() { _hadFocus = true; RemoveReason("focus"); }
        void OnUnmounted() => AddSystemReason("headset off");
        void OnMounted() => RemoveReason("headset off");
        void OnTrackingLost() => AddSystemReason("tracking lost");
        void OnTrackingAcquired() => RemoveReason("tracking lost");

        // The Meta XR Simulator reports input-focus loss whenever its window is in the background, which is how the
        // tooling drives it — so in the Editor only the Menu button pauses unless this is ticked.
        [SerializeField] bool systemPauseInEditor = false;

        // OVRManager raises InputFocusLost on its first Update, before the OpenXR session reaches FOCUSED; without this
        // guard every launch would start paused. System reasons only count once the app has actually had focus.
        bool _hadFocus;

        void AddSystemReason(string reason)
        {
#if UNITY_EDITOR
            if (!systemPauseInEditor) { Debug.Log($"[HIS] Pause: ignoring '{reason}' in the Editor"); return; }
#endif
            if (!_hadFocus && !OVRManager.hasInputFocus) { Debug.Log($"[HIS] Pause: ignoring '{reason}' before first focus"); return; }
            _hadFocus = true;
            AddReason(reason);
        }

        // In the Editor the app loses OS focus whenever the tooling drives it from the background — ignore that there.
        void OnApplicationPause(bool paused)
        {
#if !UNITY_EDITOR
            if (paused) AddSystemReason("app paused"); else RemoveReason("app paused");
#endif
        }

        void OnApplicationFocus(bool focused)
        {
#if !UNITY_EDITOR
            if (!focused) AddSystemReason("app focus"); else RemoveReason("app focus");
#endif
        }

        public void AddReason(string reason)
        {
            if (!_reasons.Add(reason)) return;
            Debug.Log($"[HIS] Pause: +{reason}");
            _countdownEnd = -1f;
            if (!IsPaused) ApplyPaused();
            UpdateNotice();
        }

        public void RemoveReason(string reason)
        {
            if (!_reasons.Remove(reason)) return;
            Debug.Log($"[HIS] Pause: -{reason}");
            UpdateNotice(); // stay paused until the player explicitly resumes
        }

        void Update()
        {
            if (!_hadFocus && OVRManager.hasInputFocus) _hadFocus = true;
            HandleMenuButton();
            if (!IsPaused) return;

            if (_countdownEnd > 0f)
            {
                float left = _countdownEnd - Time.unscaledTime;
                if (left <= 0f) { _countdownEnd = -1f; ApplyUnpaused(); return; }
                Notice.Show(NoticeKey, $"<size=140%><b>{Mathf.CeilToInt(left)}</b></size>", 100);
                return;
            }

            // Only the "menu" reason (or none) can be cleared by the player; system reasons clear themselves.
            bool onlyUserReasons = _reasons.Count == 0 || (_reasons.Count == 1 && _reasons.Contains("menu"));
            var credits = CreditsMenu.Instance;
            if (credits != null && credits.IsOpen)
            {
                // The credits page owns the sticks and the trigger (a trigger closes it, it doesn't resume the game).
                if (!onlyUserReasons) credits.Close();
                else if (!credits.Tick()) UpdateNotice();
                return;
            }
            if (onlyUserReasons && TriggerPressed()) { BeginResume(); return; }
            if (onlyUserReasons) SettingsMenu();
        }

        int _row, _heldDir; // _row: loadout rows first, then the three trims, height, the bad-call show, its preview, the mode, (Marshal: the weapons check,) the doors, the hinge, then Credits
        float _repeatAt;
        bool _rowLatched;
        /// <summary>The Mode row's pick this pause (a <see cref="HIS.Scenario.ScenarioDirector.Mode"/>), −1: untouched. Applied on unpause.</summary>
        int _modePick = -1;

        static int LoadoutRows => HIS.Weapons.Loadout.Instance != null ? HIS.Weapons.Loadout.Instance.MenuSlots.Count : 0;

        /// <summary>The "Weapons check" row is listed: only while a Marshal night is the mode (research/27; the other modes' menu is main's).</summary>
        static bool CheckRowShown => CurrentMode == (int)HIS.Scenario.ScenarioDirector.Mode.Marshal;
        /// <summary>The weapons-check row's index (−1: not listed) and Credits' (last), after the Mode row.</summary>
        static int CheckRow(int lr) => CheckRowShown ? lr + 7 : -1;
        /// <summary>"Doors: pick again" (every mode), "Door hinge: flip" (every mode), then Credits (last).</summary>
        static int DoorsRow(int lr) => CheckRowShown ? lr + 8 : lr + 7;
        static int HingeRow(int lr) => DoorsRow(lr) + 1;
        static int CreditsRow(int lr) => HingeRow(lr) + 1;

        void SettingsMenu()
        {
            int lr = LoadoutRows, heightRow = lr + 3, showRow = lr + 4, previewRow = lr + 5, modeRow = lr + 6;
            int checkRow = CheckRow(lr), doorsRow = DoorsRow(lr), hingeRow = HingeRow(lr), creditsRow = CreditsRow(lr), rows = creditsRow + 1;
            if (_row >= rows) _row = 0;
            var l = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            var r = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            var st = l.sqrMagnitude > r.sqrMagnitude ? l : r;
            bool changed = false;
            if (!_rowLatched && Mathf.Abs(st.y) > 0.6f && Mathf.Abs(st.y) > Mathf.Abs(st.x))
            { _row = (_row + (st.y > 0f ? rows - 1 : 1)) % rows; _rowLatched = true; changed = true; }
            else if (Mathf.Abs(st.y) < 0.3f) _rowLatched = false;
            int dir = st.x > 0.6f ? 1 : st.x < -0.6f ? -1 : 0;
            bool click = OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch) ||
                         OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);
            bool a = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch) || OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
            if (_row == creditsRow || _row == previewRow || _row == checkRow || _row == doorsRow || _row == hingeRow)
            {
                bool go = (dir == 1 && _heldDir != 1) || a || click;
                _heldDir = dir;
                if (go && _row == creditsRow) OpenCredits();
                else if (go && _row == checkRow) { StartWeaponsCheck(); return; }
                else if (go && _row == doorsRow) { PickDoorsAgain(); return; }
                else if (go && _row == hingeRow) { FlipDoorHinge(); return; } // (stays paused; it redraws the panel)
                else if (go) { PreviewBadCall(); return; }
                if (changed) UpdateNotice();
                return;
            }
            if (_row == heightRow)
            {
                if (dir != 0 && (dir != _heldDir || Time.unscaledTime >= _repeatAt))
                {
                    // From Auto the first push starts at the measured height; then 1 cm a push, hold to keep stepping.
                    if (HIS.Weapons.PlayerHeight.Auto) HIS.Weapons.PlayerHeight.Set(MeasuredCm());
                    else HIS.Weapons.PlayerHeight.Set(HIS.Weapons.PlayerHeight.Cm + dir);
                    _repeatAt = Time.unscaledTime + (dir != _heldDir ? 0.4f : 0.08f);
                    changed = true;
                }
                if (click) { HIS.Weapons.PlayerHeight.Reset(); changed = true; }
                _heldDir = dir;
                if (changed) UpdateNotice();
                return;
            }
            if (_row == showRow)
            {
                if (dir != 0 && dir != _heldDir) { HIS.Outcome.BadCallShow.Cycle(dir); changed = true; } // one step per push
                if (click) { HIS.Outcome.BadCallShow.Mode = HIS.Outcome.BadShowMode.All; changed = true; }
                _heldDir = dir;
                if (changed) UpdateNotice();
                return;
            }
            if (_row == modeRow)
            {
                if (dir != 0 && dir != _heldDir) { PickMode(ModeShown + dir); changed = true; } // one step per push
                if (click) { PickMode((int)HIS.Scenario.ScenarioDirector.Mode.Scenarios); changed = true; }
                _heldDir = dir;
                if (changed) UpdateNotice();
                return;
            }
            if (dir != 0 && _row < lr)
            {
                if (dir != _heldDir) { HIS.Weapons.Loadout.Instance.Cycle(HIS.Weapons.Loadout.Instance.MenuSlots[_row], dir); changed = true; } // one step per push
            }
            else if (dir != 0 && (dir != _heldDir || Time.unscaledTime >= _repeatAt))
            {
                HIS.Weapons.GripTrim.Set(_row - lr, HIS.Weapons.GripTrim.Get(_row - lr) + dir);
                _repeatAt = Time.unscaledTime + (dir != _heldDir ? 0.4f : 0.1f); // hold to keep nudging
                changed = true;
            }
            _heldDir = dir;
            if (click)
            {
                if (_row < lr) HIS.Weapons.Loadout.Instance.ResetPick(HIS.Weapons.Loadout.Instance.MenuSlots[_row]);
                else HIS.Weapons.GripTrim.Reset(_row - lr);
                changed = true;
            }
            if (changed) UpdateNotice();
        }

        void HandleMenuButton()
        {
            if (OVRInput.GetDown(OVRInput.Button.Start))
            {
                _menuDownAt = Time.unscaledTime;
                _menuRestartFired = false;
            }
            if (_menuDownAt > 0f && OVRInput.Get(OVRInput.Button.Start) && !_menuRestartFired &&
                Time.unscaledTime - _menuDownAt >= restartHoldSeconds)
            {
                _menuRestartFired = true;
                Debug.Log("[HIS] Menu held — restarting session");
                FindAnyObjectByType<GameSession>()?.ResetSession(GameSession.MenuHoldReason);
                if (IsPaused) BeginResume(); // clears only "menu"; headset-off / tracking reasons keep it paused
            }
            if (OVRInput.GetUp(OVRInput.Button.Start))
            {
                bool shortPress = !_menuRestartFired;
                _menuDownAt = -1f;
                if (!shortPress) return;
                if (CreditsMenu.Instance != null && CreditsMenu.Instance.IsOpen) { CreditsMenu.Instance.Close(); UpdateNotice(); return; } // Menu = back
                if (!IsPaused) AddReason("menu");
                else if (_reasons.Count <= 1 && (_reasons.Count == 0 || _reasons.Contains("menu"))) BeginResume();
            }
        }

        /// <summary>The mode the game is in now (Scenarios without a director).</summary>
        static int CurrentMode => HIS.Scenario.ScenarioDirector.Instance != null ? (int)HIS.Scenario.ScenarioDirector.Instance.CurrentMode : 0;
        /// <summary>The Mode row's selection: this pause's pick, else the current mode.</summary>
        int ModeShown => _modePick >= 0 ? _modePick : CurrentMode;

        /// <summary>
        /// The Mode row moved: remember the pick for the unpause (a switch clears the room and starts over, so never under the
        /// menu). Saved only once applied (<see cref="ApplyModePick"/>): a row scrolled onto Marshal and the app closed while
        /// paused must not boot the next launch into a mode that was never played.
        /// </summary>
        void PickMode(int m)
        {
            int n = HIS.Scenario.ScenarioDirector.ModeCount;
            _modePick = ((m % n) + n) % n;
            Debug.Log($"[HIS] Pause menu: mode {HIS.Scenario.ScenarioDirector.ModeNames[_modePick]}{(_modePick == CurrentMode ? " (as now)" : ", on resume")}");
        }

        /// <summary>
        /// Unpaused: the Mode row's pick (if it was touched) is saved on the headset like the bad-call show — also when it's the
        /// mode now, so picking Scenarios back keeps it — and the game switches to it if it differs (SetMode clears the room and
        /// starts over). The Editor never saves it: tests play the scene's mode.
        /// </summary>
        void ApplyModePick()
        {
            int pick = _modePick;
            _modePick = -1;
            if (pick < 0) return;
            if (!Application.isEditor) HIS.Scenario.ScenarioDirector.SavedMode = (HIS.Scenario.ScenarioDirector.Mode)pick;
            var director = HIS.Scenario.ScenarioDirector.Instance;
            if (director == null || pick == (int)director.CurrentMode) return;
            director.SetMode((HIS.Scenario.ScenarioDirector.Mode)pick);
        }

        static bool TriggerPressed() =>
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch) ||
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);

        void BeginResume()
        {
            if (CreditsMenu.Instance != null) CreditsMenu.Instance.Close(); // the countdown (100) must not sit under the credits page (101)
            _reasons.Remove("menu");
            if (_reasons.Count > 0) { UpdateNotice(); return; }
            _countdownEnd = Time.unscaledTime + countdownSeconds;
            Debug.Log("[HIS] Pause: resuming after countdown");
        }

        void ApplyPaused()
        {
            IsPaused = true;
            _modePick = -1;                                              // a new pause: the Mode row starts on the mode now
            _savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            InputGate.Set(InputGate.Mode.Paused);
            OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
            Debug.Log("[HIS] PAUSED");
            PausedChanged?.Invoke(true);
        }

        void ApplyUnpaused()
        {
            IsPaused = false;
            _reasons.Clear();
            Time.timeScale = _savedTimeScale;
            AudioListener.pause = false;
            if (InputGate.Current == InputGate.Mode.Paused) InputGate.Set(InputGate.Mode.Gameplay);
            Notice.Hide(NoticeKey);
            if (CreditsMenu.Instance != null) CreditsMenu.Instance.Close();
            Debug.Log("[HIS] RESUMED");
            PausedChanged?.Invoke(false);
            if (isActiveAndEnabled) ApplyModePick();                     // (not from OnDisable: no mode switch while the scene goes away)
        }

        void UpdateNotice()
        {
            if (!IsPaused) return;
            string why = _reasons.Contains("headset off") ? "Headset off" :
                         _reasons.Contains("focus") || _reasons.Contains("app focus") || _reasons.Contains("app paused") ? "Meta menu open" :
                         _reasons.Contains("tracking lost") ? "Tracking lost: turn on a light or look around" : null;
            string msg = why != null
                ? $"<b>PAUSED</b>\n<size=70%>{why}</size>"
                : "<b>PAUSED</b>\n<size=70%>Pull a trigger or press Menu to resume\nHold Menu to restart</size>" + SettingsText();
            Notice.Show(NoticeKey, msg, 100);
        }

        /// <summary>Open the credits page over the pause panel (the pause menu's Credits row; tests).</summary>
        public void OpenCredits()
        {
            if (CreditsMenu.Instance == null) return;
            CreditsMenu.Instance.Open();
        }

        /// <summary>
        /// The "Preview bad call" row: back into the game at once (no countdown: the preview clears the room first) and play
        /// the picked bad-call show. Stays paused if something else holds the pause (headset off, tracking lost).
        /// </summary>
        public void PreviewBadCall()
        {
            Debug.Log($"[HIS] Pause menu: preview bad call ({HIS.Outcome.BadCallShow.Mode})");
            if (CreditsMenu.Instance != null) CreditsMenu.Instance.Close();
            _reasons.Remove("menu");
            _countdownEnd = -1f;
            if (_reasons.Count > 0) { UpdateNotice(); return; }
            if (IsPaused) ApplyUnpaused();
            var director = HIS.Scenario.ScenarioDirector.Instance;
            if (director != null) director.PreviewBadCall();
            else if (HIS.Outcome.OutcomeShow.Instance != null) HIS.Outcome.OutcomeShow.Instance.PlayPreview();
        }

        /// <summary>Why the weapons-check row restarted the session (<see cref="GameSession.LastResetReason"/>; never re-asks the door).</summary>
        public const string WeaponsCheckReason = "weapons check";

        /// <summary>
        /// The "Weapons check" row (Marshal mode only; research/27, Scenario/MarshalBriefing): the night starts over with the
        /// weapons check first. Back into the game at once, as the preview row (the restart clears the room, so no countdown);
        /// if something else holds the pause (headset off, tracking lost) it stays paused and the check waits for the next night.
        /// </summary>
        public void StartWeaponsCheck()
        {
            Debug.Log("[HIS] Pause menu: weapons check");
            HIS.Scenario.MarshalBriefing.Request();
            if (CreditsMenu.Instance != null) CreditsMenu.Instance.Close();
            _reasons.Remove("menu");
            _countdownEnd = -1f;
            if (_reasons.Count > 0) { UpdateNotice(); return; }
            if (IsPaused) ApplyUnpaused();
            FindAnyObjectByType<GameSession>()?.ResetSession(WeaponsCheckReason); // the director starts the night over: Due → the check
        }

        /// <summary>
        /// The "Doors: pick again" row: the room starts over with the door picker open on the scan — the front door, then
        /// (2+ doors) whether another one is a way in too. Back into the game at once, as the weapons-check row.
        /// </summary>
        public void PickDoorsAgain()
        {
            Debug.Log("[HIS] Pause menu: pick the doors again");
            if (CreditsMenu.Instance != null) CreditsMenu.Instance.Close();
            _reasons.Remove("menu");
            _countdownEnd = -1f;
            if (_reasons.Count > 0) { UpdateNotice(); return; }
            if (IsPaused) ApplyUnpaused();
            FindAnyObjectByType<GameSession>()?.ResetSession(GameSession.PickDoorsReason);
        }

        /// <summary>
        /// The "Door hinge: flip" row (Brett 0.1.56: yes to "a flip the hinge option next to Doors: pick again, remembered for his
        /// room"): the front door hangs from its other jamb — at once if it's shut, else once it next is — and that side is saved
        /// for this room, beating the automatic pick on every load of it (HIS.Room.VirtualDoor.FlipHinge). Only the front door:
        /// the one every mode's intruders, visitors and police use first. Stays paused (the panel says what it did).
        /// </summary>
        public void FlipDoorHinge()
        {
            var door = HIS.Room.VirtualDoor.Instance;
            if (door == null) { Debug.Log("[HIS] Pause menu: no door to flip the hinge of"); return; }
            Debug.Log("[HIS] Pause menu: flip the front door's hinge");
            door.FlipHinge();
            UpdateNotice();
        }

        HIS.Weapons.PlayerBody _body;

        /// <summary>The player's measured height in cm (PlayerBody's standing eye height), 175 before there's a body.</summary>
        int MeasuredCm()
        {
            if (_body == null) _body = FindAnyObjectByType<HIS.Weapons.PlayerBody>();
            return _body != null ? HIS.Weapons.PlayerHeight.StatureCm(_body.MeasuredEye) : 175;
        }

        static string FeetInches(int cm)
        {
            int inches = Mathf.RoundToInt(cm / 2.54f);
            return $"{inches / 12}'{inches % 12}\"";
        }

        /// <summary>Tests/tools: the settings list as the pause panel shows it.</summary>
        public string DebugMenuText() => SettingsText();

        // Built only when the panel changes (a stick move), never per frame.
        string SettingsText()
        {
            var sb = new System.Text.StringBuilder("\n\n<size=55%>Stick up/down: pick   left/right: change   click: default");
            var lo = HIS.Weapons.Loadout.Instance;
            int lr = LoadoutRows;
            for (int r = 0; r < lr; r++)
            {
                int slot = lo.MenuSlots[r];
                var def = lo.Slots[slot];
                bool sel = r == _row;
                sb.Append('\n').Append(sel ? "<color=#FFD24A>[ " : "").Append(def.Label).Append(": ");
                for (int k = 0; k < def.Options.Length; k++)
                {
                    var o = def.Options[k];
                    if (k > 0) sb.Append("  /  ");
                    if (!o.Available) sb.Append("<color=#7A7A7A>").Append(o.Label).Append(" (coming soon)</color>");
                    else if (k == lo.Picked(slot)) sb.Append("<b><u>").Append(o.Label).Append("</u></b>");
                    else sb.Append(sel ? "<color=#BFA95A>" : "<color=#A0A0A0>").Append(o.Label).Append("</color>");
                }
                if (sel) sb.Append(" ]</color>");
                if (lo.Pending(slot)) sb.Append("\n<color=#9FD0FF>Swaps in next scenario (or hold Menu to restart now)</color>");
            }
            sb.Append("\n\nGrip angle");
            for (int i = 0; i < 3; i++)
            {
                float v = HIS.Weapons.GripTrim.Get(i);
                string row = $"{HIS.Weapons.GripTrim.Names[i]}  {(v > 0 ? "+" : "")}{v:0}°{(v == 0f ? "  (default)" : "")}";
                sb.Append('\n').Append(lr + i == _row ? $"<color=#FFD24A>[ {row} ]</color>" : row);
            }
            // Height: where the belt and holsters hang.
            bool heightSel = _row == lr + 3;
            int cm = HIS.Weapons.PlayerHeight.Cm, measured = MeasuredCm();
            string height = HIS.Weapons.PlayerHeight.Auto
                ? $"Height: Auto (measured {measured} cm, {FeetInches(measured)})"
                : $"Height: {cm} cm ({FeetInches(cm)})";
            sb.Append("\n\n").Append(heightSel ? $"<color=#FFD24A>[ {height} ]</color>" : height);
            if (heightSel) sb.Append("\n<color=#9FD0FF>Sets where your belt and holsters sit. Click: back to Auto</color>");
            // The bad-call show picker and its preview.
            bool showSel = _row == lr + 4;
            sb.Append("\n\n").Append(showSel ? "<color=#FFD24A>[ " : "").Append("Bad-call show: ");
            var names = HIS.Outcome.BadCallShow.ModeNames;
            int picked = (int)HIS.Outcome.BadCallShow.Mode;
            for (int k = 0; k < names.Length; k++)
            {
                if (k > 0) sb.Append(" / ");
                if (k == picked) sb.Append("<b><u>").Append(names[k]).Append("</u></b>");
                else sb.Append(showSel ? "<color=#BFA95A>" : "<color=#A0A0A0>").Append(names[k]).Append("</color>");
            }
            if (showSel) sb.Append(" ]</color>");
            if (picked == 0) // All = one bit per bad call, in turn (BadCallShow.Rotation)
                sb.Append("\n<color=#9FD0FF>All: one per bad call, in turn. Next: ")
                  .Append(HIS.Outcome.BadCallShow.Featured(HIS.Outcome.BadShowMode.All)).Append("</color>");
            sb.Append('\n').Append(_row == lr + 5 ? "<color=#FFD24A>[ Preview bad call  (A / stick right: play now) ]</color>" : "Preview bad call");
            // The game mode (research/27: the US Marshal night), switched on resume.
            bool modeSel = _row == lr + 6;
            sb.Append("\n\n").Append(modeSel ? "<color=#FFD24A>[ " : "").Append("Mode: ");
            var modes = HIS.Scenario.ScenarioDirector.ModeNames;
            int shown = ModeShown;
            for (int k = 0; k < modes.Length; k++)
            {
                if (k > 0) sb.Append(" / ");
                if (k == shown) sb.Append("<b><u>").Append(modes[k]).Append("</u></b>");
                else sb.Append(modeSel ? "<color=#BFA95A>" : "<color=#A0A0A0>").Append(modes[k]).Append("</color>");
            }
            if (modeSel) sb.Append(" ]</color>");
            if (shown != CurrentMode) sb.Append("\n<color=#9FD0FF>Switches when you resume (the room starts over)</color>");
            // The Marshal night's weapons check, again (research/27; Scenario/MarshalBriefing): Marshal mode only.
            int checkRow = CheckRow(lr);
            if (checkRow >= 0)
            {
                bool checkSel = _row == checkRow;
                sb.Append('\n').Append(checkSel ? "<color=#FFD24A>[ Weapons check  (A / stick right: start now) ]</color>" : "Weapons check");
                if (checkSel) sb.Append("\n<color=#9FD0FF>The night starts over with it</color>");
            }
            bool doorsSel = _row == DoorsRow(lr);
            sb.Append('\n').Append(doorsSel ? "<color=#FFD24A>[ Doors: pick again  (A / stick right) ]</color>" : "Doors: pick again");
            if (doorsSel) sb.Append("\n<color=#9FD0FF>Choose the door(s) they come in by on your scan — the room starts over</color>");
            // The front door's hinge side (Brett 0.1.56), saved for this room.
            bool hingeSel = _row == HingeRow(lr);
            var front = HIS.Room.VirtualDoor.Instance;
            string hingeState = front == null ? "" : front.HingeSaved ? "  (your side, saved)" : "  (auto)";
            sb.Append('\n').Append(hingeSel ? $"<color=#FFD24A>[ Door hinge: flip{hingeState}  (A / stick right) ]</color>" : $"Door hinge: flip{hingeState}");
            if (hingeSel) sb.Append("\n<color=#9FD0FF>If the door swings from the wrong side — remembered for this room</color>");
            if (front != null && front.HingeChangePending) sb.Append("\n<color=#9FD0FF>Flips when the door is shut</color>");
            sb.Append("\n\n").Append(_row == CreditsRow(lr) ? "<color=#FFD24A>[ Credits  (A / stick right: open) ]</color>" : "Credits");
            return sb.Append("</size>").ToString();
        }
    }
}
