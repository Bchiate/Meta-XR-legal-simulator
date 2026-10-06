using HIS.Core;
using HIS.UI;
using UnityEngine;

namespace HIS.Room
{
    /// <summary>
    /// M1a safety, step 2: a floor spot to fight from — at least 2 m from the confirmed door, 0.6 m clear of walls and
    /// furniture, with the best view of the door (clear sight line, in front of it rather than off to the side, about 3 m
    /// away). A subtle depth-occluded ring marks it at the start of each scenario with a one-line "Stand in the ring"; it
    /// never blocks play (the note goes once you step in, or after a few seconds). Picked once per confirmed door.
    /// </summary>
    public class SafeSpot : MonoBehaviour
    {
        public static SafeSpot Instance { get; private set; }

        [SerializeField] Material ringMaterial; // FX_SafeRing.mat (HIS/OcclusionUnlitBias), assigned by MainSceneBuilder
        [SerializeField] float ringRadius = 0.33f, ringWidth = 0.04f;
        [SerializeField] float noticeSeconds = 6f;

        public const float MinDoorDistance = 2f, MinClearance = 0.6f;
        /// <summary>Only when no spot passes the full rules (a small room): the fallback limits.</summary>
        public const float FallbackDoorDistance = 1.6f, FallbackClearance = 0.45f;

        /// <summary>The ring centre on the floor, or null (no room / door / no spot that passes the rules).</summary>
        public Vector3? Spot { get; private set; }
        public bool RingVisible => _ring != null && _ring.activeSelf;

        const string NoticeKey = "ring";
        GameObject _ring;
        Material _mat;
        Color _baseColor;
        float _noticeUntil;
        RoomScanner _room;

        void Awake() => Instance = this;

        void Start()
        {
            _room = RoomScanner.Instance;
            if (_room != null) { _room.DoorChosen += OnDoorChosen; if (_room.DoorConfirmed) OnDoorChosen(_room); }
            GameSession.WorldCleared += Hide;
        }

        void OnDestroy()
        {
            if (_room != null) _room.DoorChosen -= OnDoorChosen;
            GameSession.WorldCleared -= Hide;
            if (_mat != null) Destroy(_mat);
            if (Instance == this) Instance = null;
        }

        void OnDoorChosen(RoomScanner room)
        {
            Spot = null;
            var g = room.Geometry; var door = room.PrimaryDoor;
            if (g == null || door == null) { HideSpot.DoorChosen(room, null); return; } // (forgets the last room's)
            var centre = room.Room.FloorAnchors is { Count: > 0 } f ? f[0].GetAnchorCenter() : door.transform.position + door.transform.forward;
            RoomGeometry.DoorOnFloor(door.transform.position, door.transform.forward, centre, out var d, out var inward);
            Vector2? d2 = null; // the second door: as far from it too, and a view of it if the room allows
            if (room.SecondDoor != null)
            {
                RoomGeometry.DoorOnFloor(room.SecondDoor.transform.position, room.SecondDoor.transform.forward, centre, out var sd, out _);
                d2 = sd;
            }
            bool strict = Find(g, d, inward, MinDoorDistance, MinClearance, out var spot, out var info, d2);
            if (strict || Find(g, d, inward, FallbackDoorDistance, FallbackClearance, out spot, out info, d2))
            {
                Spot = new Vector3(spot.x, g.FloorY, spot.y);
                Debug.Log($"[HIS] Safe spot at {Spot.Value:F2}: {info}{(strict ? "" : " (small room: relaxed limits)")}");
            }
            else Debug.LogWarning("[HIS] Safe spot: no floor spot 1.6 m+ from the door and clear of furniture — no ring");
            // Ricky's hiding spot on a Marshal night (research/27 §3) keeps off this one. Only noted here: it's worked out when
            // a Marshal night first spawns him (HideSpot.Pick), so the other modes do nothing new.
            HideSpot.DoorChosen(room, Spot);
            Hide();
        }

        /// <summary>
        /// Best spot on a 0.2 m grid over the floor: inside the outline, ≥<paramref name="minDoor"/> from the door (flat),
        /// ≥<paramref name="minClear"/> from walls and body-height furniture. Score: clear sight line to the door (walls and
        /// furniture taller than 1.2 m block it) ≫ in front of the door ≫ close to 3 m away, plus more elbow room.
        /// </summary>
        public static bool Find(RoomGeometry g, Vector2 door, Vector2 inward, float minDoor, float minClear, out Vector2 best, out string info,
                                Vector2? door2 = null)
        {
            best = default; info = null;
            var ext = g.Extent();
            if (ext.width <= 0f || ext.height <= 0f) return false;
            var target = door + inward * 0.3f; // just inside the doorway: the sight line mustn't clip the door's own wall
            float bestScore = float.NegativeInfinity;
            const float step = 0.2f;
            for (float x = ext.xMin + step * 0.5f; x < ext.xMax; x += step)
            for (float z = ext.yMin + step * 0.5f; z < ext.yMax; z += step)
            {
                var p = new Vector2(x, z);
                float dd = Vector2.Distance(p, door);
                if (dd < minDoor || !g.Inside(p)) continue;
                if (door2.HasValue && Vector2.Distance(p, door2.Value) < minDoor) continue;
                float clear = g.Clearance(p);
                if (clear < minClear) continue;
                bool view = !g.Blocked(p, target, 1.2f);
                float front = Vector2.Dot(inward, (p - door) / Mathf.Max(dd, 1e-4f));
                bool view2 = door2.HasValue && !g.Blocked(p, door2.Value, 1.2f);
                float score = (view ? 4f : 0f) + (view2 ? 2f : 0f) + front * 1.5f - Mathf.Abs(dd - 3f) * 0.6f + Mathf.Min(clear, 1.2f) * 0.8f;
                if (score <= bestScore) continue;
                bestScore = score; best = p;
                info = $"{dd:F1} m from the door, {clear:F2} m clear, {(view ? "clear view" : "view blocked")}, {Mathf.Acos(Mathf.Clamp(front, -1f, 1f)) * Mathf.Rad2Deg:F0}° off the door's axis";
            }
            return info != null;
        }

        /// <summary>Scenario start: ring on (if there's a spot) and the one-line note, unless the player is already in it.</summary>
        /// <summary>Off by default — Brett 0.1.15: "remove the stand in circle… it's annoying". The spot is still computed.</summary>
        public static bool ShowRing = false;

        public void ShowForScenario()
        {
            if (Spot == null || !ShowRing) return;
            EnsureRing();
            _ring.transform.position = Spot.Value + Vector3.up * 0.012f; // just above the floor anchor: no z-fight with a real rug
            _ring.SetActive(true);
            if (!PlayerInRing()) { Notice.Show(NoticeKey, "<size=80%>Stand in the ring</size>", 5); _noticeUntil = Time.time + noticeSeconds; }
        }

        public void Hide()
        {
            if (_ring != null) _ring.SetActive(false);
            _noticeUntil = 0f;
            Notice.Hide(NoticeKey);
        }

        public bool PlayerInRing()
        {
            var cam = Camera.main;
            if (Spot == null || cam == null) return false;
            var d = cam.transform.position - Spot.Value; d.y = 0;
            return d.magnitude < ringRadius + 0.1f;
        }

        void Update()
        {
            if (_noticeUntil <= 0f) return;
            // A gentle pulse while the note is up; steady and fainter once it's gone.
            float k = 0.75f + 0.25f * Mathf.Sin(Time.time * 4f);
            if (_mat != null) _mat.color = new Color(_baseColor.r, _baseColor.g, _baseColor.b, _baseColor.a * k);
            if (Time.time < _noticeUntil && !PlayerInRing()) return;
            _noticeUntil = 0f;
            Notice.Hide(NoticeKey);
            if (_mat != null) _mat.color = new Color(_baseColor.r, _baseColor.g, _baseColor.b, _baseColor.a * 0.7f);
        }

        void EnsureRing()
        {
            if (_ring != null) return;
            _ring = new GameObject("[HIS] SafeSpotRing");
            _ring.transform.SetParent(transform, false);
            _ring.AddComponent<MeshFilter>().sharedMesh = Annulus(ringRadius - ringWidth, ringRadius, 64);
            var mr = _ring.AddComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var src = ringMaterial;
            if (src == null)
            {
                // HIS/OcclusionUnlitBias, not Meta's OcclusionUnlit (bias 0): a ring lying on the real floor was eaten by it.
                var sh = Shader.Find("HIS/OcclusionUnlitBias") ?? Shader.Find("EnvironmentDepth/URP/OcclusionUnlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
                src = new Material(sh) { color = new Color(0.35f, 0.9f, 1f, 0.45f), renderQueue = 3000 };
            }
            _mat = new Material(src); // own copy: the pulse animates its colour
            _baseColor = _mat.color;
            mr.sharedMaterial = _mat;
        }

        static Mesh Annulus(float inner, float outer, int segments)
        {
            var v = new Vector3[segments * 2]; var uv = new Vector2[segments * 2]; var tri = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                v[i * 2] = dir * inner; v[i * 2 + 1] = dir * outer;
                uv[i * 2] = new Vector2(i / (float)segments, 0f); uv[i * 2 + 1] = new Vector2(i / (float)segments, 1f);
                int n = (i + 1) % segments;
                // Facing up (clockwise seen from above in Unity's left-handed frame).
                tri[i * 6] = i * 2; tri[i * 6 + 1] = n * 2 + 1; tri[i * 6 + 2] = i * 2 + 1;
                tri[i * 6 + 3] = i * 2; tri[i * 6 + 4] = n * 2; tri[i * 6 + 5] = n * 2 + 1;
            }
            var m = new Mesh { name = "SafeRing", vertices = v, uv = uv, triangles = tri };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }
    }
}
