using HIS.Room;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Outcome
{
    /// <summary>
    /// The police helicopter's searchlight on the player's real room (bad-call show, <see cref="BadCallShow"/>): one soft
    /// warm-white disc that slides down one wall, across the floor past the player's feet and up the opposite wall, 2-3
    /// passes. Each frame one ray from just under the ceiling (above the player) is cast into the scanned room (walls,
    /// floor, furniture tops); the disc sits 2 cm off whatever it hits, turned to the surface and stretched along the beam
    /// the way a slanted spotlight spreads. It's a single pooled quad on the Depth API occlusion shader (FX_RoomLight's
    /// HIS/OcclusionUnlitBias: alpha-blended over passthrough, depth bias 0.06 so wall/floor depth noise doesn't eat it; real
    /// furniture/legs in front of it hide it) with a radial
    /// falloff texture made once at start-up. No Unity light, no shadows.
    /// </summary>
    public class HeliSpotlight : MonoBehaviour
    {
        const float PeakAlpha = 0.42f, FadeIn = 0.35f, FadeOut = 0.45f;
        const float Shallow = 24f, Steep = 68f;   // beam pitch below horizontal at the ends / the middle of a pass
        const float HalfSweep = 80f;             // yaw either side of where the player faced when it started
        static readonly Color Warm = new(1f, 0.96f, 0.86f, 1f);
        // Only surfaces whose scanned box matches the real top: a couch box is backrest-high over the seat, and lamp, plant
        // and "other" boxes are loose bounds, so the disc would hang in the air above them. The beam goes through those
        // to the floor instead, where the real object hides it (depth occlusion), which reads as its shadow.
        static readonly LabelFilter Surfaces = new(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.FLOOR
            | MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME | MRUKAnchor.SceneLabels.TABLE
            | MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.SCREEN);

        Transform _disc;
        Material _mat;
        bool _built, _on;
        float _t, _passSeconds, _total, _yaw0, _floorY;
        int _passes;
        Vector3 _source;

        public bool Visible => _on;
        /// <summary>Tests: frames the disc landed on a scanned surface (vs the floor-plane fallback).</summary>
        public int SurfaceHits { get; private set; }

        public void Init(Material fx)
        {
            if (_built) return;
            if (fx == null)
            {
                var sh = Shader.Find("HIS/OcclusionUnlitBias") ?? Shader.Find("EnvironmentDepth/URP/OcclusionUnlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) { Debug.LogWarning("[HIS] HeliSpotlight: no shader; spotlight disabled"); return; }
                fx = new Material(sh);
            }
            _mat = new Material(fx) { name = "Heli spot (runtime)", renderQueue = 3002 };
            _mat.SetTexture("_BaseMap", RadialTexture()); // no [MainTexture] on this shader: set it by name
            _mat.color = new Color(Warm.r, Warm.g, Warm.b, 0f);
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "SpotDisc";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = _mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _disc = go.transform;
            go.SetActive(false);
            _built = true;
        }

        /// <summary>Start sweeping: <paramref name="passes"/> passes of <paramref name="passSeconds"/> each, centred on where the player looks.</summary>
        public void Begin(Vector3 eye, Vector3 forward, float floorY, float ceilingY, int passes, float passSeconds)
        {
            if (!_built) return;
            var f = forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
            _yaw0 = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            _source = new Vector3(eye.x, Mathf.Max(floorY + 1.2f, ceilingY - 0.15f), eye.z);
            _floorY = floorY;
            _passes = Mathf.Max(1, passes);
            _passSeconds = Mathf.Max(0.5f, passSeconds);
            _total = _passes * _passSeconds;
            _t = 0f;
            _on = true;
            SurfaceHits = 0;
            _disc.gameObject.SetActive(true);
            Place();
        }

        public void Stop()
        {
            _on = false;
            if (_disc != null) _disc.gameObject.SetActive(false);
        }

        public void Tick(float dt)
        {
            if (!_on) return;
            _t += dt;
            if (_t >= _total) { Stop(); return; }
            Place();
        }

        void Place()
        {
            int pass = Mathf.Min(_passes - 1, (int)(_t / _passSeconds));
            float s = (_t - pass * _passSeconds) / _passSeconds;
            s = s * s * (3f - 2f * s);                       // eases at the walls, fastest over the floor
            if ((pass & 1) == 1) s = 1f - s;                 // back the other way
            float yaw = _yaw0 + Mathf.Lerp(-HalfSweep, HalfSweep, s);
            float pitch = Mathf.Lerp(Shallow, Steep, Mathf.Sin(s * Mathf.PI));
            var dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;

            Vector3 point, normal;
            var room = RoomScanner.Instance != null ? RoomScanner.Instance.Room : null;
            if (room != null && room.Raycast(new Ray(_source, dir), 15f, Surfaces, out var hit, out _))
            {
                point = hit.point; normal = hit.normal; SurfaceHits++;
            }
            else
            {
                float k = (_source.y - _floorY) / Mathf.Max(0.05f, -dir.y); // no scan: the floor plane
                point = _source + dir * Mathf.Min(k, 15f); normal = Vector3.up;
            }
            if (normal.sqrMagnitude < 1e-4f) normal = -dir;
            if (Vector3.Dot(normal, dir) > 0f) normal = -normal;   // face back toward the beam

            float dist = Vector3.Distance(_source, point);
            float cos = Mathf.Max(0.35f, -Vector3.Dot(normal, dir));
            float width = Mathf.Clamp(0.55f + 0.22f * dist, 0.6f, 1.4f);
            var along = dir - normal * Vector3.Dot(dir, normal);   // the beam's slant across the surface
            if (along.sqrMagnitude < 1e-4f) along = Vector3.Cross(normal, Vector3.right);
            // The quad's visible face points down its local -Z: -Z = surface normal; local Y = the stretch axis.
            _disc.SetPositionAndRotation(point + normal * 0.02f, Quaternion.LookRotation(-normal, along.normalized));
            _disc.localScale = new Vector3(width, width / cos, 1f);

            float a = PeakAlpha * Mathf.Clamp01(_t / FadeIn) * Mathf.Clamp01((_total - _t) / FadeOut);
            _mat.color = new Color(Warm.r, Warm.g, Warm.b, a);
        }

        /// <summary>Soft disc: full in the middle, a gentle shoulder, gone at the rim (alpha only; colour comes from the material).</summary>
        static Texture2D RadialTexture()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "SpotFalloff", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = 1f - Mathf.SmoothStep(0.3f, 1f, r);
                a *= 0.75f + 0.25f * (1f - Mathf.SmoothStep(0f, 0.35f, r)); // a slightly hotter core
                px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
