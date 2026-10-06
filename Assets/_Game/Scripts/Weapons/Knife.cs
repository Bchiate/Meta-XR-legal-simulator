using System.Collections.Generic;
using HIS.Core;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// Blade weapon. Tracks blade-tip velocity; on contact with a damageable, classifies the hit:
    /// velocity mostly along the blade axis = stab (deep, high damage), across it = slash (shallow, bleeds).
    /// The blade collider is a trigger so it never physically shoves the intruder around.
    /// </summary>
    public class Knife : WeaponBase
    {
        [SerializeField] Transform bladeBase;
        [SerializeField] Transform bladeTip;
        /// <summary>The blade's root, where it meets the guard (the handle runs the other way from here; the Marshal check's
        /// label points at the handle's far end).</summary>
        public Transform BladeBase => bladeBase;
        [SerializeField] float stabDamage = 45f;
        [SerializeField] float slashDamage = 18f;
        [SerializeField] float minHitSpeed = 1.2f;     // m/s — slower contact is a nudge, not a wound
        [SerializeField] float rehitCooldown = 0.25f;

        Vector3 _tipVelocity, _lastTip;
        readonly Dictionary<IDamageable, float> _lastHit = new();

        /// <summary>The blade tip's speed (m/s) over the last physics step (the Marshal weapons check's swing fallback reads it).</summary>
        public float TipSpeed => _tipVelocity.magnitude;

        protected override void Awake()
        {
            base.Awake();
            if (bladeBase == null) bladeBase = transform;
            if (bladeTip == null) bladeTip = transform;
            _lastTip = bladeTip.position;
        }

        // FixedUpdate doesn't run while paused (timeScale 0) but the held knife keeps following the hand, so the first
        // physics step after resuming would read the whole pause's movement as blade speed — a free stab.
        void OnEnable() => GamePause.PausedChanged += OnPausedChanged;
        void OnDisable() => GamePause.PausedChanged -= OnPausedChanged;
        void OnPausedChanged(bool paused) { _lastTip = bladeTip.position; _tipVelocity = Vector3.zero; }

        public override void HandUpdate(HandController hand) { }

        void FixedUpdate()
        {
            _tipVelocity = (bladeTip.position - _lastTip) / Time.fixedDeltaTime;
            _lastTip = bladeTip.position;
        }

        void OnTriggerEnter(Collider other) => Contact(other);
        void OnTriggerStay(Collider other) => Contact(other);

        void Contact(Collider other)
        {
            if (!IsHeld) return;
            var target = other.GetComponentInParent<IDamageable>();
            if (target == null || target is IInanimate) return;   // flesh only (not a car or the ground outside)
            if (_lastHit.TryGetValue(target, out float t) && Time.time - t < rehitCooldown) return;

            float speed = _tipVelocity.magnitude;
            if (speed < minHitSpeed) return;

            // Stab = tip travelling within ~53° of where the blade points, or of the hand's thrust line: the blade is raised
            // 35° above the aim line in the hand (a knife held ready), and a punch-straight thrust follows the hand, not the
            // raised blade (0.1.10 left ~3° of stab window around the pistol-aim thrust). Held, the grip point is the aim anchor.
            var dir = _tipVelocity.normalized;
            var bladeAxis = (bladeTip.position - bladeBase.position).normalized;
            float along = Mathf.Max(Vector3.Dot(dir, bladeAxis), Vector3.Dot(dir, Grabbable.GripPoint.forward));
            bool stab = along > 0.6f;

            var point = other.ClosestPoint(bladeTip.position);
            var normal = (point - other.bounds.center).normalized;
            var info = new DamageInfo
            {
                Amount = stab ? stabDamage : slashDamage * Mathf.Clamp(speed / 3f, 0.5f, 1.5f),
                Type = stab ? DamageType.Stab : DamageType.Slash,
                Point = point, Normal = normal, Direction = dir,
                Penetration = stab ? Mathf.Clamp01(speed / 4f) : 0.2f, Source = gameObject,
            };
            target.ApplyDamage(info);
            Sfx.Play(stab ? "flesh.stab" : "flesh.slash", point, 0.9f);
            _lastHit[target] = Time.time;
            Hand?.Haptic(stab ? 0.9f : 0.5f, stab ? 0.12f : 0.06f);
        }

        public override string Status => DisplayName;
    }
}
