using HIS.Weapons;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Is each gun's muzzle ray (where rounds go) along its barrel as modelled (what the player aims with)? Brett 0.1.31: hits get
/// unreliable at distance — a muzzle a degree or two off the bore misses a man 8 m away. Prints, per gun: the muzzle's
/// forward vs the long axis of the barrel/slide renderers, and where the muzzle ray passes a point 8 m down the bore.
///   scripts/unity-batch.sh GunAlignmentCheck.Run
/// </summary>
public static class GunAlignmentCheck
{
    public static void Run()
    {
        foreach (var path in new[] { "Assets/_Game/Prefabs/Weapon_M9.prefab", "Assets/_Game/Prefabs/Weapon_Shotgun.prefab", "Assets/_Game/Prefabs/Weapon_Pistol.prefab", AR15Builder.WeaponPath })
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Debug.Log($"[HIS] ALIGN {path}: missing"); continue; }
            var go = Object.Instantiate(prefab);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var gun = go.GetComponent<Firearm>();
            var muzzle = gun != null ? gun.Muzzle : null;
            if (muzzle == null) { Debug.Log($"[HIS] ALIGN {prefab.name}: no muzzle"); Object.DestroyImmediate(go); continue; }
            // Every renderer's bounds in the gun's space; the barrel runs along the biggest extent.
            var b = new Bounds(); bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                Debug.Log($"[HIS] ALIGN {prefab.name}: renderer {r.name} bounds {r.bounds.center:F3} size {r.bounds.size:F3}");
            }
            var f = muzzle.forward;
            Debug.Log($"[HIS] ALIGN {prefab.name}: muzzle at {muzzle.position:F3} forward {f:F4}; all renderers {b.center:F3} size {b.size:F3}; " +
                      $"muzzle ray pitch {Mathf.Asin(f.y) * Mathf.Rad2Deg:F2}°, yaw {Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg:F2}° (vs the gun's +z)");
            Object.DestroyImmediate(go);
        }
        EditorApplication.Exit(0);
    }
}
