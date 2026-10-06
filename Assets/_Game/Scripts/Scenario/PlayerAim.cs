using HIS.Weapons;
using UnityEngine;

namespace HIS.Scenario
{
    /// <summary>What the player has in his hands, as the intruder sees it (picks his lines: "Don't shoot!" vs "Don't stab me!").</summary>
    public enum PlayerWeapon { None, Gun, Knife }

    /// <summary>
    /// What the intruder can read off the player: is a gun pointed at him, is the player visibly armed.
    /// Pure math against the held weapons' muzzles (no physics), cheap enough to ask every frame.
    /// </summary>
    public static class PlayerAim
    {
        static HandController[] _hands;
        const float AimConeDegrees = 12f, AimRange = 15f;

        static HandController[] Hands
        {
            get
            {
                if (_hands == null || _hands.Length == 0 || _hands[0] == null)
                    _hands = Object.FindObjectsByType<HandController>(FindObjectsInactive.Exclude);
                return _hands;
            }
        }

        /// <summary>A held gun's muzzle points within 12° of <paramref name="chest"/> (within 15 m).</summary>
        public static bool IsAimedAt(Vector3 chest)
        {
            foreach (var h in Hands)
            {
                if (h == null || h.Held == null || !h.Held.TryGetComponent<Firearm>(out var gun) || gun.Muzzle == null) continue;
                var to = chest - gun.Muzzle.position;
                if (to.magnitude > AimRange) continue;
                if (Vector3.Angle(gun.Muzzle.forward, to) < AimConeDegrees) return true;
            }
            return false;
        }

        /// <summary>What the player is holding: a gun in either hand wins over a knife in the other. No allocations.</summary>
        public static PlayerWeapon HeldWeapon
        {
            get
            {
                var held = PlayerWeapon.None;
                foreach (var h in Hands)
                {
                    if (h == null || h.Held == null) continue;
                    if (h.Held.TryGetComponent<Firearm>(out _)) return PlayerWeapon.Gun;
                    if (h.Held.TryGetComponent<Knife>(out _)) held = PlayerWeapon.Knife;
                }
                return held;
            }
        }

        /// <summary>The player holds a gun or a knife.</summary>
        public static bool IsArmed
        {
            get
            {
                foreach (var h in Hands)
                    if (h != null && h.Held != null && h.Held.GetComponent<WeaponBase>() != null) return true;
                return false;
            }
        }
    }
}
