using System.Collections.Generic;
using HIS.Core;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// The empty magazine an M9 reload drops (research/20 option 1): a render-only copy of the gun's magazine group with a
    /// box collider and a light rigidbody, so it falls out of the grip, clatters on the real floor or furniture (the room's
    /// physics colliders) and lies there. Never grabbable. At most three at once (the oldest goes); each goes after 20 s,
    /// or at once if it falls out of the room (below the floor, e.g. through the open doorway, where there's no floor).
    /// </summary>
    public class DroppedMag : MonoBehaviour
    {
        const int MaxMags = 3;
        const float Lifetime = 20f;
        static readonly Queue<DroppedMag> s_live = new();

        float _dieAt, _floorY;
        int _clinks;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_live.Clear(); // domain reload off

        /// <summary>A loose copy of <paramref name="magazine"/> where it is now, moving at <paramref name="velocity"/>.</summary>
        public static void Spawn(Transform magazine, int layer, Vector3 velocity, Transform gun = null)
        {
            if (magazine == null) return;
            var go = new GameObject("DroppedMag");
            go.transform.SetPositionAndRotation(magazine.position, magazine.rotation);
            go.layer = layer;
            // Only what draws: each mesh of the group, placed as it is in the gun.
            var bounds = new Bounds(); bool any = false;
            foreach (var mf in magazine.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null || !mf.TryGetComponent<MeshRenderer>(out var mr)) continue;
                var part = new GameObject(mf.name);
                part.layer = layer;
                part.transform.SetParent(go.transform, false);
                part.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
                part.transform.localScale = Vector3.Scale(mf.transform.lossyScale, InverseScale(go.transform.lossyScale));
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = part.AddComponent<MeshRenderer>();
                r.sharedMaterials = mr.sharedMaterials;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                // Its bounds in the copy's own space (for the collider).
                var b = mf.sharedMesh.bounds;
                foreach (var corner in Corners(b))
                {
                    var p = go.transform.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                }
            }
            if (!any) { Destroy(go); return; }
            var box = go.AddComponent<BoxCollider>();
            box.center = bounds.center; box.size = Vector3.Max(bounds.size, Vector3.one * 0.01f);
            // Born inside the grip: never knocked about by the gun it came out of (or the hand holding it).
            if (gun != null) foreach (var gc in gun.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(box, gc);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.25f;
            rb.linearVelocity = velocity;
            rb.angularVelocity = new Vector3(Random.Range(-3f, 3f), Random.Range(-2f, 2f), Random.Range(-3f, 3f));
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; // thin and fast: no tunnelling through the floor
            var m = go.AddComponent<DroppedMag>();
            m._dieAt = Time.time + Lifetime;
            m._floorY = HIS.Room.RoomScanner.Instance != null && HIS.Room.RoomScanner.Instance.Geometry != null
                ? HIS.Room.RoomScanner.Instance.Geometry.FloorY : magazine.position.y - 2f;
            s_live.Enqueue(m);
            while (s_live.Count > MaxMags) { var old = s_live.Dequeue(); if (old != null) Destroy(old.gameObject); }
        }

        void Update()
        {
            if (Time.time >= _dieAt || transform.position.y < _floorY - 1f) Destroy(gameObject);
        }

        void OnCollisionEnter(Collision c)
        {
            if (_clinks >= 3 || c.relativeVelocity.magnitude < 0.4f) return; // the first bounce or two, not its settling
            _clinks++;
            float v = Mathf.Clamp01(c.relativeVelocity.magnitude / 3f);
            Sfx.Play(Sfx.Has("mag.drop") ? "mag.drop" : "casing.brass", transform.position, Mathf.Lerp(0.35f, 0.9f, v) / _clinks);
        }

        static Vector3 InverseScale(Vector3 s) => new(s.x != 0f ? 1f / s.x : 0f, s.y != 0f ? 1f / s.y : 0f, s.z != 0f ? 1f / s.z : 0f);

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            var mn = b.min; var mx = b.max;
            for (int i = 0; i < 8; i++) yield return new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y, (i & 4) == 0 ? mn.z : mx.z);
        }
    }
}
