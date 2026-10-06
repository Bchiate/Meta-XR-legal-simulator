using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace HIS.UI
{
    /// <summary>
    /// Captions for every spoken line (yells, intruder, police): research/08 — innocents are identified by what they
    /// say, so a line must be readable even with the sound low. World-space lower third: a dark plate ~14° below the
    /// eye line that lazily follows the head like <see cref="Notice"/> (placed in front, re-centres only when you look
    /// away, never buried in a real wall). Up to 3 stacked lines, newest at the bottom, speaker name coloured.
    /// Works with nothing wired: the first <see cref="Show"/> creates it, borrowing the XR-safe text material from any
    /// text already in the scene. Lines time out on game time, so they freeze with the audio while paused.
    /// </summary>
    public class Subtitles : MonoBehaviour
    {
        public static Subtitles Instance { get; private set; }

        [Header("Optional wiring (built at runtime when empty)")]
        [SerializeField] TextMeshPro text;
        [SerializeField] Transform backing;              // unit quad scaled to the text
        [Tooltip("Assets/_Game/Materials/UI_Text_URP.mat. TMP's legacy SDF shaders draw nothing in single-pass stereo.")]
        [SerializeField] Material textMaterial;
        [SerializeField] TMP_FontAsset font;
        [Tooltip("Assets/_Game/Materials/UI_Panel.mat (opaque dark plate).")]
        [SerializeField] Material backingMaterial;

        [Header("Layout")]
        [SerializeField] float distance = 1.15f;
        [SerializeField] float minDistance = 0.5f;
        [Tooltip("Degrees below the eye line (lower third, comfortable reading angle).")]
        [SerializeField] float dropAngle = 14f;
        [SerializeField] float recenterAngle = 30f;
        [SerializeField] float followSpeed = 3.5f;
        [SerializeField] float fontSize = 2.4f;          // TMP renders best at ~1-10; the text object is scaled 0.1 (≈3 cm lines)
        [SerializeField] float width = 7.5f;             // wrap width in text units (×0.1 → 0.75 m)
        [SerializeField] Vector2 padding = new(0.035f, 0.02f);
        [SerializeField] int maxLines = 3;

        public const string XrTextShader = "TextMeshPro/SRP/TMP_SDF-URP Unlit";
        const float TextScale = 0.1f;

        struct Line { public string Speaker, Text, Prefix; public float Until; }
        readonly List<Line> _lines = new(4);
        readonly StringBuilder _sb = new(256);
        bool _built, _placed, _visible;
        Transform _head;
        Vector3 _targetPos;
        Quaternion _targetRot;
        int _roomMask;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // A scene-placed Subtitles woke after an early Show() auto-created one: keep the scene one, take its lines.
                var early = Instance;
                _lines.AddRange(early._lines);
                Destroy(early.gameObject);
            }
            Instance = this;
            _roomMask = PlacementMask();
            EnsureBuilt();
            Refresh();
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void OnEnable() => HIS.Core.GameSession.SessionReset += Clear;
        void OnDisable() => HIS.Core.GameSession.SessionReset -= Clear;

        /// <summary>
        /// Caption a spoken line: "<b>Speaker:</b> text" for <paramref name="seconds"/> (stretched for long lines so they
        /// can be read). The same speaker + text again refreshes the existing line instead of stacking a duplicate.
        /// Null/empty text is ignored (e.g. <c>VoiceLines.Text</c> of an unknown key).
        /// </summary>
        public static void Show(string speaker, string text, float seconds = 3f)
        {
            if (string.IsNullOrEmpty(text)) return;
            Debug.Log($"[HIS] Subtitle {speaker}: {text}");
            if (!Application.isPlaying) return;
            var s = Instance != null ? Instance : FindOrCreate();
            if (s != null) s.Add(speaker, text, seconds);
        }

        /// <summary>Remove every line (session reset, mode switch).</summary>
        public static void Clear()
        {
            if (Instance == null || Instance._lines.Count == 0) return;
            Instance._lines.Clear();
            Instance.Refresh();
        }

        static Subtitles FindOrCreate()
        {
            var s = FindAnyObjectByType<Subtitles>(FindObjectsInactive.Include);
            if (s == null)
            {
                var go = new GameObject("[HIS] Subtitles");
                s = go.AddComponent<Subtitles>(); // Awake builds the visuals and sets Instance
            }
            Instance = s;
            s.EnsureBuilt();
            return s;
        }

        void Add(string speaker, string line, float seconds)
        {
            EnsureBuilt();
            float until = Time.time + Mathf.Max(seconds, 1f + line.Length * 0.045f);
            for (int i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].Text != line || _lines[i].Speaker != speaker) continue;
                var same = _lines[i]; same.Until = until;
                _lines.RemoveAt(i); _lines.Add(same);   // newest goes to the bottom
                Refresh();
                return;
            }
            while (_lines.Count >= Mathf.Max(1, maxLines)) _lines.RemoveAt(0);
            _lines.Add(new Line
            {
                Speaker = speaker, Text = line, Until = until,
                Prefix = string.IsNullOrEmpty(speaker) ? "" : $"<color=#{ColorUtility.ToHtmlStringRGB(SpeakerColor(speaker))}><b>{speaker}:</b></color> ",
            });
            Refresh();
        }

        /// <summary>Fixed colours for the regular cast; anyone else gets a stable colour from their name.</summary>
        static Color SpeakerColor(string speaker)
        {
            switch (speaker)
            {
                case "You": return new Color(0.50f, 0.83f, 1.00f);
                case "Police": return new Color(0.55f, 0.66f, 1.00f);
                case "Intruder": return new Color(1.00f, 0.55f, 0.40f);
                case "Mike": return new Color(0.62f, 0.90f, 0.52f);
                case "Family": return new Color(0.96f, 0.78f, 0.42f);
                case "Hitman": return new Color(0.80f, 0.82f, 0.86f);   // a Marshal night's hitmen (research/27): cold grey, no faction colour
            }
            uint h = 2166136261;
            foreach (char c in speaker) h = (h ^ c) * 16777619;
            return Color.HSVToRGB((h % 360) / 360f, 0.45f, 1f);
        }

        void Refresh()
        {
            if (!_built) return;
            if (_lines.Count == 0) { SetVisible(false); _visible = false; return; }
            _sb.Clear();
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append(_lines[i].Prefix).Append(_lines[i].Text);
            }
            SetVisible(true); // TMP builds no geometry while inactive: activate before measuring
            text.SetText(_sb);
            text.ForceMeshUpdate(true);
            FitBacking(text, backing, padding);
            if (!_visible) _placed = false; // a fresh caption appears right in front
            _visible = true;
        }

        void SetVisible(bool on)
        {
            if (text != null && text.gameObject.activeSelf != on) text.gameObject.SetActive(on);
            if (backing != null && backing.gameObject.activeSelf != on) backing.gameObject.SetActive(on);
        }

        void LateUpdate()
        {
            if (!_visible) return;

            // Expire on game time (frozen while paused, like the voice it captions).
            bool changed = false;
            float now = Time.time;
            for (int i = _lines.Count - 1; i >= 0; i--)
                if (now >= _lines[i].Until) { _lines.RemoveAt(i); changed = true; }
            if (changed) { Refresh(); if (!_visible) return; }

            if (_head == null) { var cam = Camera.main; if (cam == null) return; _head = cam.transform; }
            var fwd = _head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            var toPanel = transform.position - _head.position;
            float drop = Mathf.Tan(dropAngle * Mathf.Deg2Rad);
            var flat = toPanel; flat.y = 0f;
            float flatDist = flat.magnitude;
            bool offCentre = flatDist < 1e-3f || Vector3.Angle(fwd, flat) > recenterAngle ||
                             Mathf.Abs(toPanel.y + flatDist * drop) > 0.2f || Mathf.Abs(flatDist - distance) > 0.45f;
            if (!_placed || offCentre)
            {
                var dir = (fwd - Vector3.up * drop).normalized;
                float d = RoomSideDistance(_head.position, dir, distance / Mathf.Cos(dropAngle * Mathf.Deg2Rad), minDistance, _roomMask);
                _targetPos = _head.position + dir * d;
                _targetRot = Quaternion.LookRotation(dir, Vector3.up); // tilted to face the eye
                if (!_placed) { transform.SetPositionAndRotation(_targetPos, _targetRot); _placed = true; }
            }
            float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, _targetPos, k), Quaternion.Slerp(transform.rotation, _targetRot, k));
        }

        void EnsureBuilt()
        {
            if (_built) return;
            _built = true;
            if (text == null)
            {
                text = CreateWorldText("Text", transform, fontSize, width, TextAlignmentOptions.Bottom, textMaterial, font);
                // Bottom-aligned in a short rect: the newest line sits at a fixed height, older lines stack upward.
                text.rectTransform.sizeDelta = new Vector2(width, 1f);
            }
            if (backing == null) backing = CreatePlate("Backing", transform, backingMaterial);
            SetVisible(false);
        }

        // ───────────────────────────── placement (also used by Notice) ─────────────────────────────

        /// <summary>What a head-following panel must stay in front of: the scanned walls ("Room") and an open door's leaf ("Door").</summary>
        public static int PlacementMask()
        {
            int m = LayerMask.GetMask("Room", "Door");
            return m != 0 ? m : ~0;
        }

        /// <summary>
        /// How far along <paramref name="dir"/> from the head a head-following panel may sit: <paramref name="want"/>, pulled in
        /// to stay 15 cm in front of a wall or an open leaf (<paramref name="mask"/>) and in front of every door's frame plane.
        /// The walls' colliders have a hole cut at each door frame (DoorHoles) and a shut leaf has no collider, so without the
        /// plane check a caption faced toward a door from ~1 m landed past the frame: behind the shut leaf (hidden), or out in
        /// the open doorway, where the frame, the outside and the real wall's passthrough eraser cut it into a see-through
        /// rectangle that followed the head (Brett 0.1.40/0.1.41, near the open door). Closer than <paramref name="minDistance"/>
        /// only when the player is right at the wall or the door.
        /// </summary>
        public static float RoomSideDistance(Vector3 head, Vector3 dir, float want, float minDistance, int mask)
        {
            float hitAt = float.PositiveInfinity;
            if (Physics.Raycast(head, dir, out var hit, want + 0.2f, mask, QueryTriggerInteraction.Ignore)) hitAt = hit.distance;
            var doors = HIS.Room.VirtualDoor.All;
            for (int i = 0; i < doors.Count; i++)
            {
                var door = doors[i];
                if (door == null) continue;
                var n = door.Inward;                          // the frame plane's normal, into the room
                float along = Vector3.Dot(dir, -n);           // > 0: heading out through that wall
                if (along < 1e-3f) continue;
                float t = Vector3.Dot(head - door.Center, n) / along;
                if (t <= 0f || t >= hitAt || t > want + 0.2f) continue;   // behind the head (outside already), or farther than a hit
                var p = door.transform.InverseTransformPoint(head + dir * t);
                var o = door.Opening;
                if (p.x < o.xMin - 0.1f || p.x > o.xMax + 0.1f || p.y < o.yMin - 0.1f || p.y > o.yMax + 0.1f) continue;
                hitAt = t;
            }
            if (float.IsPositiveInfinity(hitAt) || hitAt - 0.15f >= want) return want;
            return hitAt - 0.15f >= minDistance ? hitAt - 0.15f : Mathf.Max(0.2f, hitAt - 0.05f);
        }

        // ───────────────────────────── shared XR text helpers (also used by CommandSystem's hint) ─────────────────────────────

        static Material _xrTextMaterial, _plateMaterial;
        static TMP_FontAsset _xrFont;
        static Mesh _quad;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; _xrTextMaterial = null; _plateMaterial = null; _xrFont = null; _quad = null; }

        /// <summary>
        /// A world-space TMP text that renders in both eyes: object scale 0.1 (TMP is reliable at font sizes 1-10), the
        /// URP Shader Graph SDF material. Material source, in order: the one passed in; the material of any text already
        /// in the scene using the URP shader (Notice, built by MainSceneBuilder from UI_Text_URP.mat); a runtime clone of
        /// the default font material on that shader. Rich text on, wrapping on, overflow allowed.
        /// </summary>
        public static TextMeshPro CreateWorldText(string name, Transform parent, float size, float wrapWidth, TextAlignmentOptions align,
                                                  Material material = null, TMP_FontAsset fontAsset = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * TextScale;
            var tmp = go.AddComponent<TextMeshPro>();
            if (material == null) ResolveXrText(out fontAsset, out material);
            if (fontAsset != null) tmp.font = fontAsset;
            if (material != null) tmp.fontSharedMaterial = material;
            tmp.fontSize = size;
            tmp.alignment = align;
            tmp.color = Color.white;
            tmp.richText = true;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.rectTransform.sizeDelta = new Vector2(wrapWidth, 2f);
            return tmp;
        }

        /// <summary>An opaque dark unit quad (no collider) to sit behind text; size it with <see cref="FitBacking"/>.</summary>
        public static Transform CreatePlate(string name, Transform parent, Material material = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = Quad();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material != null ? material : PlateMaterial();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        /// <summary>Scale/offset a plate to the text's measured bounds (call after ForceMeshUpdate). Same math as Notice.</summary>
        public static void FitBacking(TMP_Text tmp, Transform plate, Vector2 pad)
        {
            if (tmp == null || plate == null) return;
            float s = tmp.transform.localScale.x;
            var b = tmp.textBounds;
            bool valid = b.size.x > 0f && b.size.y > 0f && b.size.x < 1000f && b.size.y < 1000f;
            var size = valid ? b.size * s : new Vector3(0.5f, 0.06f, 0f);
            var centre = tmp.transform.localPosition + (valid ? b.center * s : Vector3.zero);
            plate.localScale = new Vector3(size.x + pad.x * 2f, size.y + pad.y * 2f, 1f);
            plate.localPosition = new Vector3(centre.x, centre.y, 0.003f); // just behind the text (text faces -z → +z is behind)
        }

        static void ResolveXrText(out TMP_FontAsset fontAsset, out Material material)
        {
            if (_xrTextMaterial == null)
            {
                _xrFont = null;
                foreach (var t in FindObjectsByType<TMP_Text>(FindObjectsInactive.Include))
                {
                    var m = t.fontSharedMaterial;
                    if (m == null || m.shader == null || m.shader.name != XrTextShader || t.font == null) continue;
                    _xrTextMaterial = m; _xrFont = t.font;
                    break;
                }
            }
            if (_xrTextMaterial == null)
            {
                var f = TMP_Settings.defaultFontAsset;
                var shader = Shader.Find(XrTextShader);
                if (f != null && f.material != null && shader != null)
                {
                    _xrTextMaterial = new Material(f.material) { name = "UI_Text_URP (runtime)", shader = shader };
                    _xrTextMaterial.SetTexture("_MainTex", f.atlasTexture);
                    _xrFont = f;
                }
                else Debug.LogWarning($"[HIS] Subtitles: no XR text material (font {f}, shader {shader}) — text may not render in the headset");
            }
            fontAsset = _xrFont;
            material = _xrTextMaterial;
        }

        static Material PlateMaterial()
        {
            if (_plateMaterial != null) return _plateMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Unlit"); // in the build via UI_Panel.mat (Notice's plate)
            if (shader == null) return null;
            _plateMaterial = new Material(shader) { name = "UI_Panel (runtime)" };
            _plateMaterial.SetColor("_BaseColor", new Color(0.05f, 0.055f, 0.07f));
            return _plateMaterial;
        }

        static Mesh Quad()
        {
            if (_quad != null) return _quad;
            _quad = new Mesh { name = "HIS Quad" };
            _quad.SetVertices(new List<Vector3> { new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(-0.5f, 0.5f, 0f), new(0.5f, 0.5f, 0f) });
            _quad.SetUVs(0, new List<Vector2> { new(0f, 0f), new(1f, 0f), new(0f, 1f), new(1f, 1f) });
            _quad.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            _quad.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            _quad.RecalculateBounds();
            return _quad;
        }
    }
}
