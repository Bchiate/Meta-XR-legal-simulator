using System.Collections.Generic;
using HIS.Room;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// Lays the starting weapons out on the nearest real table if the scan has one, otherwise on a floating
    /// shelf 1 m in front of the player at chest height. Weapons that fall on the floor can be picked back up.
    /// </summary>
    public class WeaponRack : MonoBehaviour
    {
        [SerializeField] List<WeaponBase> weaponPrefabs = new();
        [SerializeField] float spacing = 0.25f;
        [SerializeField] float shelfDistance = 0.8f;
        [SerializeField] float shelfHeight = 1.0f;

        public IReadOnlyList<WeaponBase> Spawned => _spawned;
        readonly List<WeaponBase> _spawned = new();

        void Start()
        {
            var room = RoomScanner.Instance;
            if (room != null && !room.IsReady) room.RoomReady += _ => Layout();
            else Layout();
        }

        void Layout()
        {
            var head = Camera.main != null ? Camera.main.transform : transform;
            Vector3 origin; Quaternion rot;

            var table = FindNearestTable(head.position);
            if (table != null && table.PlaneRect.HasValue)
            {
                // Table plane anchors: local Z is the surface normal (up), local XY spans the top.
                origin = table.transform.position + table.transform.forward * 0.02f;
                var fwd = Vector3.ProjectOnPlane(head.position - origin, Vector3.up).normalized;
                rot = Quaternion.LookRotation(fwd.sqrMagnitude > 0 ? fwd : Vector3.forward, Vector3.up);
                Debug.Log($"[HIS] Weapon rack on real table at {origin}");
            }
            else
            {
                var fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                origin = new Vector3(head.position.x, 0, head.position.z) + fwd * shelfDistance + Vector3.up * shelfHeight;
                rot = Quaternion.LookRotation(-fwd, Vector3.up);
                Debug.Log($"[HIS] Weapon rack floating at {origin} (no table scanned)");
            }

            for (int i = 0; i < weaponPrefabs.Count; i++)
            {
                float x = (i - (weaponPrefabs.Count - 1) * 0.5f) * spacing;
                var w = Instantiate(weaponPrefabs[i], origin + rot * new Vector3(x, 0.05f, 0), rot * Quaternion.Euler(0, 90, 0));
                w.name = weaponPrefabs[i].name;
                // Rest on the rack until first grabbed (no physics settling on a slightly tilted scanned surface).
                var rb = w.GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;
                _spawned.Add(w);
            }
        }

        static MRUKAnchor FindNearestTable(Vector3 from)
        {
            var room = RoomScanner.Instance?.Room;
            if (room == null) return null;
            MRUKAnchor best = null; float bestD = 2.5f; // only use a table within reach-ish
            foreach (var a in room.Anchors)
            {
                if (a.Label != MRUKAnchor.SceneLabels.TABLE) continue;
                float d = Vector3.Distance(from, a.transform.position);
                if (d < bestD) { best = a; bestD = d; }
            }
            return best;
        }
    }
}
