using System;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>
    /// The player's health and the hit feedback hooks. Lives on the [HIS] Game root.
    /// Weapons wound by Brett's rule (0.1.17 playtest), not by hit points: one round to the head or center mass kills, the
    /// second round to an arm or a leg kills, one knife blow kills (<see cref="IsFatal"/>). Only his fists cost hit points
    /// (regenerating). Limb wounds last the round (<see cref="ResetHealth"/>).
    /// </summary>
    public class PlayerState : MonoBehaviour
    {
        public static PlayerState Instance { get; private set; }

        [SerializeField] float maxHealth = 100f;
        [SerializeField] float regenPerSecond = 2f;
        [SerializeField] float regenDelay = 5f;

        /// <summary>Rounds to the arms/legs that kill (Brett: "twice for legs and arms").</summary>
        public const int LimbWoundsToDie = 2;
        /// <summary>The heartbeat with a limb wound: as if down to this many hit points (one more hit and you're dead).</summary>
        const float LimbWoundBeatHealth = 20f;

        public float Health { get; private set; }
        public float MaxHealth => maxHealth;
        public bool IsDead => Health <= 0f;
        public float LastHitTime { get; private set; } = -100f;
        /// <summary>Rounds taken in the arms or legs this round.</summary>
        public int LimbWounds { get; private set; }
        /// <summary>Hurt at all this round: a limb wound, or hit points not yet back.</summary>
        public bool Hurt => LimbWounds > 0 || Health < maxHealth;

        /// <summary>Hits still land (feedback, logs) but cost no health. Sandbox option later; Editor test menu now.</summary>
        public static bool GodMode;

        public event Action<float> Hit;   // damage amount (a wound: the health it took — a limb wound half, a fatal one the rest)
        public event Action Died;

        /// <summary>
        /// Brett's rule: does this hit kill, given the limb wounds already taken? Gun: head/torso yes, arm/leg on the
        /// <see cref="LimbWoundsToDie"/>th. Knife: any blow. Fists: never by this rule (they cost hit points). None: a miss.
        /// </summary>
        public static bool IsFatal(HitBy by, HitZone zone, int limbWoundsBefore) => zone != HitZone.None && by switch
        {
            HitBy.Knife => true,
            HitBy.Gun => zone is HitZone.Head or HitZone.Torso || limbWoundsBefore + 1 >= LimbWoundsToDie,
            _ => false,
        };

        void Awake() { Instance = this; Health = maxHealth; }

        float _nextBeat;
        void Update()
        {
            if (!IsDead && Health < maxHealth && Time.time - LastHitTime > regenDelay)
                Health = Mathf.Min(maxHealth, Health + regenPerSecond * Time.deltaTime);
            // Heartbeat when hurt: faster the lower the health; a limb wound (one more and you're dead) beats as if nearly out.
            float beat = LimbWounds > 0 ? Mathf.Min(Health, LimbWoundBeatHealth) : Health;
            if (!IsDead && beat < 50f && Time.time >= _nextBeat)
            {
                _nextBeat = Time.time + Mathf.Lerp(0.45f, 1.0f, beat / 50f);
                Sfx.Play2D("player.heartbeat", Mathf.Lerp(0.9f, 0.3f, beat / 50f));
            }
        }

        /// <summary>Hit-point damage from an unknown source: counted as fists.</summary>
        public void TakeHit(float amount, Vector3 from) => TakeHit(HitZone.Torso, HitBy.Fists, from, amount);

        /// <summary>
        /// A blow or a round that landed. Gun and knife wound by <see cref="IsFatal"/> (a surviving gunshot adds a limb wound);
        /// fists take <paramref name="fistDamage"/> hit points. Same feedback either way: the hit sound and a rumble.
        /// </summary>
        public void TakeHit(HitZone zone, HitBy by, Vector3 from, float fistDamage = 0f)
        {
            if (IsDead || zone == HitZone.None) return;
            string result;
            float amount;
            if (by == HitBy.Fists)
            {
                amount = fistDamage;
                if (!GodMode) Health = Mathf.Max(0f, Health - fistDamage);
                result = GodMode ? $"{fistDamage:F0} hp (god mode)" : $"{Health:F0} hp";
            }
            else
            {
                bool fatal = IsFatal(by, zone, LimbWounds);
                amount = fatal ? Health : maxHealth / LimbWoundsToDie;
                if (GodMode) result = fatal ? "would kill (god mode)" : "limb wound (god mode: not counted)";
                else if (fatal) { Health = 0f; result = zone is HitZone.Arm or HitZone.Leg && by == HitBy.Gun ? $"dead ({LimbWoundsToDie} limb wounds)" : "dead"; }
                else { LimbWounds++; result = $"limb wound {LimbWounds}/{LimbWoundsToDie}"; }
            }
            LastHitTime = Time.time;
            Hit?.Invoke(amount);
            Sfx.Play2D("player.hit", 1f);
            OVRInput.SetControllerVibration(0.6f, 0.8f, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0.6f, 0.8f, OVRInput.Controller.RTouch);
            Invoke(nameof(StopRumble), 0.25f);
            Debug.Log($"[HIS] Player hit: {PlayerShape.Name(zone)} by {(by == HitBy.Gun ? "gun" : by == HitBy.Knife ? "knife" : "fists")} -> {result}");
            if (Health <= 0f) { Health = 0f; Debug.Log("[HIS] PLAYER DIED"); Died?.Invoke(); }
        }

        /// <summary>A new round (GameSession.ClearWorld): full hit points, no limb wounds.</summary>
        public void ResetHealth() { Health = maxHealth; LimbWounds = 0; LastHitTime = -100f; }

        void StopRumble()
        {
            OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
        }
    }
}
