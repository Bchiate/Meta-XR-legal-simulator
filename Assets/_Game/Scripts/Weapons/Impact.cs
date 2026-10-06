using HIS.Core;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>Non-living surface hits (bullet holes on real walls/furniture come later; for now a log + tiny debug mark).</summary>
    public static class Impact
    {
        public static void WorldHit(in DamageInfo info, RaycastHit hit, bool playSound = true)
        {
            Log.V($"[HIS] world hit {hit.collider.name} @ {hit.point}");
            if (playSound) Sfx.Play("impact.wall", hit.point, 0.7f);
        }
    }
}
