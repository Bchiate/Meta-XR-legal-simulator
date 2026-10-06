using HIS.Room;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;

namespace HIS.Outcome
{
    /// <summary>
    /// The verdict headline ("GOOD CALL", "BAD CALL", …) with the why/who lines under it, on a dark plate so it reads
    /// against any real wall. Placed once, 2 m ahead at eye height, yaw only, then world-locked (it doesn't follow the head).
    /// The scan's walls, door frames and furniture carry invisible depth occluders, so the WHOLE plate has to stay in front
    /// of them, not just its centre (Brett 0.1.8: the left end sank into a wall at an angle): rays from the eye through a
    /// 5×3 grid over the plate's footprint pull it in until every point is ≥ 12 cm clear, shrinking it to keep the same
    /// angular size. Too close (&lt; 0.5 m) → it turns up to 35° toward the most open side instead. A body in the way (the
    /// attacker who just killed you) is opaque and writes depth, so the banner comes in front of him instead.
    /// It scales in with a small overshoot.
    /// TMP in XR follows <see cref="HIS.UI.Notice"/>: 3D TextMeshPro on the XR-safe URP text material (UI_Text_URP.mat;
    /// TMP's legacy SDF shaders draw nothing in single-pass stereo). Font sizes stay around 1-10 and the objects are scaled
    /// instead. The object is activated before the text is set, because TMP builds no mesh while inactive.
    /// Not depth-occluded, so real furniture or hands never cut holes in it.
    /// </summary>
    public class VerdictBanner : MonoBehaviour
    {
        const float Distance = 2f;
        const float Comfortable = 0.8f;       // a turned placement is taken at the smallest yaw that gets at least this far
        const float TurnBelow = 0.5f;         // straight ahead closer than this → try turning toward open space
        const float MinWallDistance = 0.3f;   // last resort: never nearer than this, even if an edge then touches something
        const float WallMargin = 0.12f;       // every sample point this far (along its ray) in front of what the ray hits
        const float MaxYaw = 35f, YawStep = 7f;
        const float FootprintPad = 0.55f;     // half-extent factor: +10 % over the plate, covers the scale-in overshoot
        const float MinBodyDistance = 0.6f;   // nearest it comes to stay in front of a body standing right in your face
        const float BodyClearance = 0.35f;    // in front of the body's centre
        const float ScaleInSeconds = 0.35f;
        const float TitleScale = 0.25f;   // title em ≤ 8 × 0.1 × 0.25 = 0.2 m at 2 m
        const float BodyScale = 0.1f;     // body em 6.5 × 0.1 × 0.1 = 0.065 m (~23 dmm cap height at 2 m)
        const float Gap = 0.05f;
        static readonly Vector2 Padding = new(0.09f, 0.07f);
        const float AccentHeight = 0.022f;

        Transform _content, _plate, _accent;
        TextMeshPro _title, _body;
        Renderer _accentRenderer;
        MaterialPropertyBlock _mpb;
        int _roomMask;
        Vector2 _plateSize = new(1.6f, 0.6f);
        // The scan's walls AND openings (the occluder mesh and its collider have a hole at the door, so physics alone lets
        // the banner through the doorway into the door's void box) AND the furniture volumes, which carry occluders too.
        static readonly LabelFilter Solid = new(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE
            | MRUKAnchor.SceneLabels.INNER_WALL_FACE | MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME
            | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED
            | MRUKAnchor.SceneLabels.SCREEN | MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT);
        bool _built, _shown, _animating;
        float _t, _baseScale = 1f;

        public bool Visible => _shown;

        /// <summary>
        /// Build the hierarchy once. <paramref name="font"/> may be null (TMP's default font, which is what
        /// UI_Text_URP.mat was made from); <paramref name="textMaterial"/> should be UI_Text_URP.mat.
        /// </summary>
        public void Init(TMP_FontAsset font, Material textMaterial, Material panelMaterial)
        {
            if (_built) return;
            if (textMaterial == null)
                Debug.LogWarning("[HIS] VerdictBanner: no XR text material assigned; TMP's default SDF shader may not draw in stereo");
            if (panelMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null) panelMaterial = new Material(shader) { color = new Color(0.05f, 0.055f, 0.07f) };
            }

            _content = new GameObject("Content").transform;
            _content.SetParent(transform, false);

            _plate = MakeQuad("Backing", panelMaterial).transform;
            var accent = MakeQuad("Accent", panelMaterial);
            _accent = accent.transform;
            _accentRenderer = accent.GetComponent<Renderer>();
            _mpb = new MaterialPropertyBlock();

            _title = MakeText("Title", TitleScale, font, textMaterial);
            _title.fontStyle = FontStyles.Bold;
            _title.enableAutoSizing = true; // long headlines ("YOU DIDN'T SURVIVE") shrink to the width instead of wrapping
            _title.fontSizeMin = 3f;
            _title.fontSizeMax = 8f;
            _title.fontSize = 8f;
            _title.textWrappingMode = TextWrappingModes.NoWrap;
            _title.rectTransform.sizeDelta = new Vector2(6.4f, 1.4f); // 1.6 m × 0.35 m

            _body = MakeText("Body", BodyScale, font, textMaterial);
            _body.fontSize = 6.5f;
            _body.textWrappingMode = TextWrappingModes.Normal;
            _body.rectTransform.sizeDelta = new Vector2(16f, 8f);     // 1.6 m wide

            _roomMask = LayerMask.GetMask("Room", "Door");
            _content.gameObject.SetActive(false);
            _built = true;
        }

        /// <summary>
        /// Show a verdict in front of the eye pose. <paramref name="body"/> is TMP rich text. <paramref name="avoid"/> is the
        /// world bounds of a body the banner must not end up behind (null = none).
        /// </summary>
        public void Show(string title, Color titleColor, Color accent, string body, Vector3 eye, Vector3 forward, Bounds? avoid = null)
        {
            if (!_built) return;
            _content.gameObject.SetActive(true); // TMP only builds geometry for active objects; measure after activating
            _title.text = title;
            _title.color = titleColor;
            _body.text = body ?? string.Empty;
            _body.gameObject.SetActive(!string.IsNullOrEmpty(body));
            _title.ForceMeshUpdate(true);
            if (_body.gameObject.activeSelf) _body.ForceMeshUpdate(true);

            if (_accentRenderer != null)
            {
                _mpb.SetColor("_BaseColor", accent);
                _accentRenderer.SetPropertyBlock(_mpb);
            }

            Layout();
            Place(eye, forward, avoid);
            _t = 0f;
            _animating = true;
            _shown = true;
            _content.localScale = Vector3.one * (0.001f * _baseScale);
        }

        public void Hide()
        {
            _shown = false;
            _animating = false;
            if (_content != null) _content.gameObject.SetActive(false);
        }

        /// <summary>Scale-in animation (ease-out-back).</summary>
        public void Tick(float dt)
        {
            if (!_animating) return;
            Layout(); // follows TMP if it rebuilt the text after Show (fonts, auto-size); scale-in is only a few frames
            _t += dt;
            float k = Mathf.Clamp01(_t / ScaleInSeconds);
            const float c1 = 1.70158f, c3 = c1 + 1f;
            float e = 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f);
            _content.localScale = Vector3.one * (Mathf.Max(0.001f, e) * _baseScale);
            if (k >= 1f) { _content.localScale = Vector3.one * _baseScale; _animating = false; }
        }

        // Stack the title over the body and fit the plate around both. Text bounds are in each text object's local space.
        void Layout()
        {
            var tSize = Measure(_title, TitleScale, out var tCentre, new Vector2(1.2f, 0.2f));
            Vector2 bSize = Vector2.zero, bCentre = Vector2.zero;
            if (_body.gameObject.activeSelf) bSize = Measure(_body, BodyScale, out bCentre, new Vector2(1.2f, 0.3f));

            float gap = bSize.y > 0f ? Gap : 0f;
            float total = tSize.y + gap + bSize.y;
            float top = total * 0.5f;
            _title.transform.localPosition = new Vector3(-tCentre.x, top - tSize.y * 0.5f - tCentre.y, 0f);
            _body.transform.localPosition = new Vector3(-bCentre.x, top - tSize.y - gap - bSize.y * 0.5f - bCentre.y, 0f);

            float w = Mathf.Max(tSize.x, bSize.x) + Padding.x * 2f;
            float h = total + Padding.y * 2f;
            _plateSize = new Vector2(w, h);
            _plate.localScale = new Vector3(w, h, 1f);
            _plate.localPosition = new Vector3(0f, 0f, 0.004f);                            // behind the text (+Z is away from the viewer)
            _accent.localScale = new Vector3(w, AccentHeight, 1f);
            _accent.localPosition = new Vector3(0f, h * 0.5f - AccentHeight * 0.5f, 0.003f); // colour strip along the top edge
        }

        // No mesh bounds yet (TMP skipped the build) → ask TMP for the laid-out size instead of guessing, so the plate
        // never ends up smaller than the text (Brett 0.1.7: "words off the backing").
        static Vector2 Measure(TextMeshPro text, float scale, out Vector2 centre, Vector2 fallback)
        {
            var b = text.textBounds;
            bool valid = b.size.x > 0f && b.size.y > 0f && b.size.x < 1000f && b.size.y < 1000f;
            if (valid) { centre = (Vector2)b.center * scale; return (Vector2)b.size * scale; }
            centre = Vector2.zero;
            var p = text.GetPreferredValues(text.text, text.rectTransform.sizeDelta.x, 0f);
            if (p.x > 0f && p.y > 0f && p.x < 1000f && p.y < 1000f)
            {
                Debug.LogWarning($"[HIS] VerdictBanner: no text bounds for {text.name}; using preferred size {p}");
                return new Vector2(Mathf.Min(p.x, text.rectTransform.sizeDelta.x), p.y) * scale;
            }
            return fallback;
        }

        /// <summary>Test hook: does every visible line sit inside the plate? Reports the worst overhang in metres.</summary>
        public bool DebugFits(out string info)
        {
            info = "not shown";
            if (!_shown) return false;
            float worst = 0f;
            var sb = new System.Text.StringBuilder();
            foreach (var t in new[] { _title, _body })
            {
                if (!t.gameObject.activeInHierarchy) continue;
                t.ForceMeshUpdate(true);
                var b = t.textBounds;
                Vector3 lo = _content.InverseTransformPoint(t.transform.TransformPoint(b.min));
                Vector3 hi = _content.InverseTransformPoint(t.transform.TransformPoint(b.max));
                Vector3 pc = _plate.localPosition, ps = _plate.localScale * 0.5f;
                float over = Mathf.Max(Mathf.Max(pc.x - ps.x - lo.x, hi.x - (pc.x + ps.x)), Mathf.Max(pc.y - ps.y - lo.y, hi.y - (pc.y + ps.y)));
                worst = Mathf.Max(worst, over);
                sb.Append($"{t.name} {(hi - lo).x:F2}×{(hi - lo).y:F2} (size {t.fontSize:F1}) ");
            }
            info = $"{sb}plate {_plate.localScale.x:F2}×{_plate.localScale.y:F2}, overhang {worst * 100f:F1} cm";
            return worst <= 0.005f;
        }

        void Place(Vector3 eye, Vector3 forward, Bounds? avoid)
        {
            var fwd = forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            var dir = fwd;
            float d = Clearance(eye, fwd), yaw = 0f;
            if (d < TurnBelow)
            {
                // Facing a wall/furniture up close: the smallest turn (either side) that reaches a comfortable distance,
                // else whichever turn is most open. Only taken if it's clearly better than straight ahead.
                float bestD = d, bestYaw = 0f;
                for (float a = YawStep; a <= MaxYaw + 0.01f; a += YawStep)
                {
                    float l = Clearance(eye, Quaternion.AngleAxis(-a, Vector3.up) * fwd);
                    float r = Clearance(eye, Quaternion.AngleAxis(a, Vector3.up) * fwd);
                    float c = Mathf.Max(l, r), s = l > r ? -a : a;
                    if (c >= Comfortable) { bestD = c; bestYaw = s; break; }
                    if (c > bestD + 0.1f) { bestD = c; bestYaw = s; }
                }
                if (bestYaw != 0f) { yaw = bestYaw; d = bestD; dir = Quaternion.AngleAxis(yaw, Vector3.up) * fwd; }
            }
            d = Mathf.Max(MinWallDistance, d);
            if (avoid.HasValue && Blocks(eye, dir, avoid.Value, out float bodyDepth))
                d = Mathf.Min(d, Mathf.Max(MinBodyDistance, bodyDepth - BodyClearance));
            _baseScale = d / Distance; // same angular size when pulled in

            transform.SetPositionAndRotation(eye + dir * d, Quaternion.LookRotation(dir, Vector3.up));
            Debug.Log($"[HIS] Verdict banner at {d:F2} m{(yaw != 0f ? $", turned {yaw:F0}°" : "")}");
        }

        /// <summary>
        /// How far along <paramref name="dir"/> (≤ <see cref="Distance"/>) the banner can hang with every point of its
        /// footprint ≥ <see cref="WallMargin"/> in front of the room. Its size scales with distance, so each sample is a
        /// fixed ray from the eye: a point at depth d sits d/cosθ along its ray, so d ≤ (hit − margin)·cosθ.
        /// </summary>
        float Clearance(Vector3 eye, Vector3 dir)
        {
            var right = Vector3.Cross(Vector3.up, dir);
            float hx = _plateSize.x * FootprintPad / Distance, hy = _plateSize.y * FootprintPad / Distance; // tangents
            float d = Distance;
            for (int i = -2; i <= 2; i++)
            for (int j = -1; j <= 1; j++)
            {
                var v = dir + right * (hx * i * 0.5f) + Vector3.up * (hy * j);
                float len = v.magnitude; // 1 / cosθ
                float reach = (Distance + WallMargin) * len + 0.05f;
                float hit = HitDistance(eye, v / len, reach);
                if (hit < reach) d = Mathf.Min(d, (hit - WallMargin) / len);
            }
            return d;
        }

        float HitDistance(Vector3 eye, Vector3 ray, float max)
        {
            float best = max;
            var scanner = RoomScanner.Instance;
            var room = scanner != null ? scanner.Room : null;
            if (room != null && room.Raycast(new Ray(eye, ray), max, Solid, out var mh, out _)) best = Mathf.Min(best, mh.distance);
            if (_roomMask != 0 && Physics.Raycast(eye, ray, out var hit, max, _roomMask, QueryTriggerInteraction.Ignore))
                best = Mathf.Min(best, hit.distance);
            return best;
        }

        /// <summary>
        /// Does <paramref name="b"/> overlap the banner's footprint as seen from the eye (it hangs at eye height, yaw only)?
        /// <paramref name="depth"/> is the body's centre distance along the view.
        /// </summary>
        bool Blocks(Vector3 eye, Vector3 fwd, Bounds b, out float depth)
        {
            depth = Vector3.Dot(b.center - eye, fwd);
            if (depth <= 0.1f) return false;
            var right = Vector3.Cross(Vector3.up, fwd);
            float hx = _plateSize.x * 0.55f / Distance, hy = _plateSize.y * 0.55f / Distance; // half-extents as tangents, +10 %
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            var e = b.extents;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center - eye + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                float z = Mathf.Max(0.05f, Vector3.Dot(c, fwd));
                float x = Vector3.Dot(c, right) / z, y = c.y / z;
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }
            return minX < hx && maxX > -hx && minY < hy && maxY > -hy;
        }

        GameObject MakeQuad(string name, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_content, false);
            var r = go.GetComponent<Renderer>();
            if (mat != null) r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        TextMeshPro MakeText(string name, float scale, TMP_FontAsset font, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_content, false);
            go.transform.localScale = Vector3.one * scale; // TMP renders reliably at font sizes ~1-10; scale the object instead
            var t = go.AddComponent<TextMeshPro>();
            if (font != null) t.font = font;               // font first: assigning it resets the material
            if (mat != null) t.fontSharedMaterial = mat;
            t.alignment = TextAlignmentOptions.Center;
            t.overflowMode = TextOverflowModes.Overflow;
            t.richText = true;
            t.color = Color.white;
            return t;
        }
    }
}
