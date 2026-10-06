using HIS.Room;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// An officer's weapon light (research/22 §4.1 point 5) as a pool of light on the player's real walls, floor and furniture
    /// where his beam lands — the helicopter searchlight's approach (HIS.Outcome.HeliSpotlight): one quad on a runtime copy
    /// of FX_RoomLight (HIS/OcclusionUnlitBias: alpha-blended over passthrough, hidden by real things in front of it) with a
    /// soft radial falloff, placed each frame where a ray down his barrel hits the scanned room. No Unity light, no shadows.
    /// The officer switches it (<see cref="On"/>) and hands it his muzzle; it only shows while he's in the room or the doorway.
    /// </summary>
    public class OfficerFlashlight : MonoBehaviour
    {
        const float PeakAlpha = 0.34f, FadeRate = 4f, MaxRange = 12f;
        const float HalfAngle = 9f;                 // the beam's soft cone
        static readonly Color Cool = new(0.93f, 0.96f, 1f, 1f);
        static readonly LabelFilter Surfaces = new(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.FLOOR
            | MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME | MRUKAnchor.SceneLabels.TABLE
            | MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.SCREEN
            | MRUKAnchor.SceneLabels.CEILING);   // (not couches/lamps/plants: loose boxes, the pool would hang in the air)

        /// <summary>Switch the beam on/off (it fades).</summary>
        public bool On { get; set; }
        /// <summary>Where the light is (his muzzle): the beam leaves along its +Z.</summary>
        public Transform Muzzle { get; set; }

        Transform _disc;
        Material _mat;
        float _alpha;
        static Texture2D s_falloff;
        static Material s_fx;
        static int _roomMask = -1;

        void Awake()
        {
            var fx = FindFx();
            if (fx == null) { enabled = false; return; }
            _mat = new Material(fx) { name = "Officer light (runtime)", renderQueue = 3002 };
            _mat.SetTexture("_BaseMap", Falloff());
            _mat.color = new Color(Cool.r, Cool.g, Cool.b, 0f);
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "LightPool";
            Destroy(go.GetComponent<Collider>());
            // Not under the officer's body: it lands on the room, and the body's Outside-layer switching must not touch it.
            go.transform.SetParent(transform, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _disc = go.transform;
            go.SetActive(false);
        }

        void OnDestroy() { if (_mat != null) Destroy(_mat); }

        void LateUpdate()
        {
            if (_disc == null) return;
            bool want = On && Muzzle != null && Muzzle.gameObject.activeInHierarchy;
            _alpha = Mathf.MoveTowards(_alpha, want ? PeakAlpha : 0f, FadeRate * PeakAlpha * Time.deltaTime);
            if (_alpha <= 0.001f || Muzzle == null) { if (_disc.gameObject.activeSelf) _disc.gameObject.SetActive(false); return; }
            var src = Muzzle.position; var dir = Muzzle.forward;
            if (!Land(src, dir, out var point, out var normal)) { _disc.gameObject.SetActive(false); return; }
            if (!_disc.gameObject.activeSelf) _disc.gameObject.SetActive(true);
            if (Vector3.Dot(normal, dir) > 0f) normal = -normal;
            float dist = Vector3.Distance(src, point);
            float cos = Mathf.Max(0.3f, -Vector3.Dot(normal, dir));
            float width = Mathf.Clamp(0.16f + 2f * dist * Mathf.Tan(HalfAngle * Mathf.Deg2Rad), 0.2f, 1.6f);
            var along = dir - normal * Vector3.Dot(dir, normal);
            if (along.sqrMagnitude < 1e-4f) along = Vector3.Cross(normal, Vector3.right);
            if (along.sqrMagnitude < 1e-4f) along = Vector3.Cross(normal, Vector3.forward);
            // The quad's visible face points down its local -Z: -Z = surface normal; local Y = the stretch axis.
            _disc.SetPositionAndRotation(point + normal * 0.02f, Quaternion.LookRotation(-normal, along.normalized));
            _disc.localScale = new Vector3(width, width / cos, 1f);
            // Dimmer the further it throws (a weapon light is a hot spot up close, a wash across the room).
            float a = _alpha * Mathf.Lerp(1f, 0.55f, Mathf.InverseLerp(1.5f, 6f, dist));
            _mat.color = new Color(Cool.r, Cool.g, Cool.b, a);
        }

        /// <summary>The first scanned surface down the beam (MRUK), else the Room colliders (walls/furniture the rounds stop on).</summary>
        static bool Land(Vector3 src, Vector3 dir, out Vector3 point, out Vector3 normal)
        {
            var room = RoomScanner.Instance != null ? RoomScanner.Instance.Room : null;
            if (room != null && room.Raycast(new Ray(src, dir), MaxRange, Surfaces, out var hit, out _))
            { point = hit.point; normal = hit.normal; return true; }
            if (_roomMask < 0) _roomMask = LayerMask.GetMask("Room");
            if (_roomMask != 0 && Physics.Raycast(src, dir, out var h, MaxRange, _roomMask, QueryTriggerInteraction.Ignore))
            { point = h.point; normal = h.normal; return true; }
            point = normal = default;
            return false;
        }

        /// <summary>FX_RoomLight (the saved material OutcomeShow holds, so its shader is in the build), else the shader by name.</summary>
        static Material FindFx()
        {
            if (s_fx != null) return s_fx;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.name == "FX_RoomLight") return s_fx = m;
            var sh = Shader.Find("HIS/OcclusionUnlitBias") ?? Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) { Debug.LogWarning("[HIS] OfficerFlashlight: no shader; weapon lights off"); return null; }
            return s_fx = new Material(sh) { name = "FX_RoomLight (fallback)" };
        }

        /// <summary>GLSL smoothstep (Mathf.SmoothStep interpolates between its first two arguments instead).</summary>
        static float Edge(float e0, float e1, float x) { float t = Mathf.Clamp01((x - e0) / (e1 - e0)); return t * t * (3f - 2f * t); }

        /// <summary>Soft disc, a slightly hotter core (alpha only; colour from the material). Made once.</summary>
        static Texture2D Falloff()
        {
            if (s_falloff != null) return s_falloff;
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "OfficerLightFalloff", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = 1f - Edge(0.25f, 1f, r);
                a *= 0.7f + 0.3f * (1f - Edge(0f, 0.3f, r));
                px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return s_falloff = tex;
        }
    }
}
