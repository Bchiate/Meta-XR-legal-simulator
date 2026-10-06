using System;
using System.Collections.Generic;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>Where a loadout slot's weapon lives on the body.</summary>
    public enum LoadoutSlot { Sidearm, Knife, LongGun }

    /// <summary>
    /// Spawns the starting weapons straight into the body holsters: sidearm right hip, knife left hip, long gun on the back.
    /// Each slot is a list of options (MainSceneBuilder fills it); the player picks one per slot in the pause menu
    /// (<see cref="HIS.Core.GamePause"/>) and the pick is kept in PlayerPrefs. Only the picked weapon is spawned. A change
    /// takes effect the next time the room is cleared (next scenario, skip, hold-Menu restart, mode switch) — never out of
    /// a hand: a held weapon keeps the swap pending until a later clear. An option with no prefab is listed as
    /// "coming soon" and can't be picked (the M9 only if Model_M9 was never built — M9Builder).
    /// </summary>
    public class Loadout : MonoBehaviour
    {
        [Serializable]
        public class Option
        {
            [Tooltip("Stable id saved in PlayerPrefs (never rename one that shipped).")] public string Id;
            public string Label;
            [Tooltip("Empty = listed as \"coming soon\", not selectable.")] public WeaponBase Prefab;
            public bool Available => Prefab != null;
        }

        [Serializable]
        public class Slot
        {
            public LoadoutSlot Kind;
            public string Label;
            public Option[] Options = Array.Empty<Option>();
        }

        public static Loadout Instance { get; private set; }
        /// <summary>A pick or a spawned weapon changed (the pause menu redraws).</summary>
        public static event Action Changed;

        [SerializeField] PlayerBody body;
        public Slot[] Slots = Array.Empty<Slot>();

        WeaponBase[] _spawned = Array.Empty<WeaponBase>();
        int[] _spawnedPick = Array.Empty<int>(), _pick = Array.Empty<int>();
        readonly List<int> _menuSlots = new();

        /// <summary>Slots with a real choice (more than one option listed), in menu order.</summary>
        public IReadOnlyList<int> MenuSlots => _menuSlots;

        void Awake()
        {
            Instance = this;
            Refresh();
        }

        void OnEnable() => HIS.Core.GameSession.WorldCleared += ApplyPending;
        void OnDisable() => HIS.Core.GameSession.WorldCleared -= ApplyPending;

        void Start()
        {
            if (body == null) body = FindAnyObjectByType<PlayerBody>();
            if (body == null) { Debug.LogError("[HIS] Loadout: no PlayerBody"); return; }
            var sb = new System.Text.StringBuilder("[HIS] Loadout holstered:");
            for (int i = 0; i < Slots.Length; i++)
            {
                Spawn(i);
                sb.Append(' ').Append(Slots[i].Kind).Append('=').Append(_spawned[i] != null ? _spawned[i].name : "none");
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>Re-read the slot list and the saved picks (call after editing <see cref="Slots"/> at runtime).</summary>
        public void Refresh()
        {
            int n = Slots.Length;
            if (_pick.Length != n) { Array.Resize(ref _pick, n); Array.Resize(ref _spawnedPick, n); Array.Resize(ref _spawned, n); }
            _menuSlots.Clear();
            for (int i = 0; i < n; i++)
            {
                if (Slots[i].Options.Length > 1) _menuSlots.Add(i);
                string saved = PlayerPrefs.GetString(Key(i), "");
                // Brett 0.1.15: the M9 is the sidearm he wants by default. Once, move an old default Glock pick over to it
                // (he can still pick the Glock in the pause menu afterwards, and that choice sticks).
                if (Slots[i].Kind == LoadoutSlot.Sidearm && PlayerPrefs.GetInt(M9DefaultKey, 0) == 0)
                {
                    PlayerPrefs.SetInt(M9DefaultKey, 1);
                    if ((saved == "" || saved == "glock19") && IndexOf(i, "m9") >= 0) { saved = "m9"; PlayerPrefs.SetString(Key(i), saved); }
                    PlayerPrefs.Save();
                }
                _pick[i] = Validate(i, IndexOf(i, saved));
            }
        }

        // ───────────── picks (menu) ─────────────

        /// <summary>The picked option index for a slot; −1 when no option is available.</summary>
        public int Picked(int slot) => _pick[slot];

        /// <summary>Picked but not spawned yet (applies at the next room clear).</summary>
        public bool Pending(int slot) => _pick[slot] >= 0 && _pick[slot] != _spawnedPick[slot];

        public WeaponBase SpawnedIn(int slot) => _spawned[slot];

        /// <summary>Step to the next available option (wrapping), skipping "coming soon" ones.</summary>
        public void Cycle(int slot, int dir)
        {
            var opts = Slots[slot].Options;
            int k = _pick[slot];
            for (int step = 0; step < opts.Length; step++)
            {
                k = ((k + dir) % opts.Length + opts.Length) % opts.Length;
                if (opts[k].Available) break;
            }
            Pick(slot, k);
        }

        /// <summary>Back to the slot's default (the M9 for the sidearm, else the first available option).</summary>
        public void ResetPick(int slot) => Pick(slot, Validate(slot, -1));

        public void Pick(int slot, int option)
        {
            option = Validate(slot, option);
            if (option == _pick[slot] || option < 0) return;
            _pick[slot] = option;
            PlayerPrefs.SetString(Key(slot), Slots[slot].Options[option].Id);
            PlayerPrefs.Save();
            Debug.Log($"[HIS] Loadout: {Slots[slot].Label} → {Slots[slot].Options[option].Label}{(Pending(slot) ? " (next clear)" : "")}");
            Changed?.Invoke();
        }

        /// <summary>Tests: pick by id.</summary>
        public void Pick(int slot, string id) => Pick(slot, IndexOf(slot, id));

        public int SlotIndex(LoadoutSlot kind)
        {
            for (int i = 0; i < Slots.Length; i++) if (Slots[i].Kind == kind) return i;
            return -1;
        }

        // ───────────── spawning ─────────────

        /// <summary>Swap every slot whose pick changed, unless its current weapon is in a hand (then it waits).</summary>
        public void ApplyPending()
        {
            if (body == null) return; // before Start
            bool any = false;
            for (int i = 0; i < Slots.Length; i++)
            {
                if (!Pending(i)) continue;
                var old = _spawned[i];
                if (old != null && old.IsHeld) { Debug.Log($"[HIS] Loadout: {Slots[i].Label} swap waits — {old.name} is in a hand"); continue; }
                if (old != null)
                {
                    var g = old.Grabbable;
                    if (g != null && g.CurrentHolster != null) g.CurrentHolster.Undock(g);
                    old.gameObject.SetActive(false); // gone this frame (hand hover/aim checks), destroyed at its end
                    Destroy(old.gameObject);
                }
                Spawn(i);
                Debug.Log($"[HIS] Loadout: {Slots[i].Label} is now {(_spawned[i] != null ? _spawned[i].name : "none")}");
                any = true;
            }
            if (any) Changed?.Invoke();
        }

        void Spawn(int i)
        {
            _spawned[i] = null; _spawnedPick[i] = -1;
            int k = _pick[i];
            if (k < 0) return;
            var prefab = Slots[i].Options[k].Prefab;
            var home = HolsterFor(Slots[i].Kind);
            var w = Instantiate(prefab);
            w.name = prefab.name;
            _spawned[i] = w; _spawnedPick[i] = k;
            var g = w.Grabbable != null ? w.Grabbable : w.GetComponent<Grabbable>();
            if (home == null || g == null) return;
            g.HomeHolster = home; // auto-return and ties go here, even if something else sits in it right now
            var into = home.IsFree ? home : Holster.FindFree(g);
            if (into != null) into.Dock(g);
            else { w.transform.SetPositionAndRotation(home.transform.position, home.transform.rotation); Debug.LogWarning($"[HIS] Loadout: no free holster for {w.name}"); }
        }

        Holster HolsterFor(LoadoutSlot kind) => kind switch
        {
            LoadoutSlot.Sidearm => body.RightHip,
            LoadoutSlot.Knife => body.LeftHip,
            _ => body.BackSlot,
        };

        // ───────────── helpers ─────────────

        string Key(int slot) => "his.loadout." + Slots[slot].Kind.ToString().ToLowerInvariant();

        int IndexOf(int slot, string id)
        {
            var opts = Slots[slot].Options;
            for (int k = 0; k < opts.Length; k++) if (opts[k].Id == id) return k;
            return -1;
        }

        /// <summary>The option if it can be picked, else the first available one (−1: none).</summary>
        const string M9DefaultKey = "his.loadout.m9default.v1";

        int Validate(int slot, int option)
        {
            var opts = Slots[slot].Options;
            if (option >= 0 && option < opts.Length && opts[option].Available) return option;
            if (Slots[slot].Kind == LoadoutSlot.Sidearm) { int m9 = IndexOf(slot, "m9"); if (m9 >= 0 && opts[m9].Available) return m9; } // default sidearm
            for (int k = 0; k < opts.Length; k++) if (opts[k].Available) return k;
            return -1;
        }
    }
}
