using System;
using TMPro;
using UnityEngine;

namespace HIS.UI
{
    /// <summary>
    /// A small label in the room, AT the thing it names, with an arrow pointing at it (the Marshal weapons check's handover,
    /// Brett 2026-10-04: "you cant look at the target and stuff while also looking at the screen"). Never on the monitor and
    /// never pinned to the view: it rides its thing (a holster, a gun in the hand, the cardboard target, the door handle, the
    /// monitor, a controller), sits beside it on one <see cref="Side"/> as the eye sees it (so it never covers the thing's
    /// centre), a little way toward the eye (<see cref="Pull"/>: never half inside a wall or the door's leaf), and faces the
    /// eye — turned to it about the vertical (a yaw billboard, leaning on the head's heading when it's well below the eye, so a
    /// hip label reads left to right from above), tipped back toward it only when it's well below the eye, never rolled. Its
    /// arrow points at the thing as the eye sees it (one beside the head, the slung gun, gets an arrow pointing the way). Sized by distance: the same angular size (lines ≈ 1.8°, the captions' size) from 0.45 m to 4 m, so it
    /// reads from wherever you stand. The text, the dark plate behind it and the arrow are the captions' own XR-safe parts
    /// (<see cref="Subtitles.CreateWorldText"/>, <see cref="Subtitles.CreatePlate"/>): one TMP text, one quad, one triangle.
    /// No light, no shadow, nothing per frame but a few transforms (the text mesh is rebuilt only when the words change).
    /// </summary>
    public class WorldLabel : MonoBehaviour
    {
        /// <summary>Where the label sits from its thing, as the player sees it (left/right by the way the head faces).</summary>
        public enum Side { Above, Below, Left, Right }

        /// <summary>TMP size at 1 m (scale 1): lines ≈ 3.2 cm ≈ 1.8° — a touch over the captions', for a two- or three-word label.</summary>
        const float FontSize = 2.6f;
        /// <summary>The label's scale is its distance from the eye in metres, held between these (closer stays small, farther grows).</summary>
        const float MinScale = 0.45f, MaxScale = 4f;
        /// <summary>At scale 1 (m): the arrow's length and base, the gap between its tip and the thing, the plate's padding.</summary>
        const float ArrowLength = 0.026f, ArrowWidth = 0.032f, Gap = 0.012f;
        static readonly Vector2 Pad = new(0.022f, 0.013f);
        /// <summary>Tipped toward the eye at most this far (a hip label is read looking down; up close it's never edge-on).</summary>
        const float MaxPitch = 60f, MinPitch = -35f;
        /// <summary>The head's heading in the left/right axis, as metres of flat sight line (a thing ≥ ~0.6 m off is placed
        /// across the sight line; a hip ~0.15 m out in front follows the heading).</summary>
        const float HeadingWeight = 0.3f;
        /// <summary>
        /// The label is worked out at the point this share of the way from its thing toward the eye (its scale: that point's
        /// distance, so the same angular size). Seen from the eye it still sits beside the thing, its arrow on it; but a thing on
        /// a surface — the door's handle, the monitor on its wall — never takes half the plate into the leaf or the wall with
        /// it (0.1.56 render: the door's label vanished into the leaf).
        /// </summary>
        public const float Pull = 0.15f;
        /// <summary>The arrow's colour: the yell hint's highlight (#ffd24a), the one accent the game's world text uses.</summary>
        static readonly Color ArrowColor = new(1f, 0.824f, 0.29f);

        TextMeshPro _text;
        Transform _plate, _arrow;
        Func<Vector3> _thing, _place;
        Side _side;
        Vector2 _plateCentre, _plateHalf = new(0.05f, 0.02f);
        Transform _head;

        /// <summary>The words on it now (null when hidden).</summary>
        public string Text { get; private set; }
        /// <summary>It is up (shown and active).</summary>
        public bool Visible => Text != null && gameObject.activeSelf;
        /// <summary>The point its arrow points at (its thing), now.</summary>
        public Vector3 ThingPoint => _thing != null ? _thing() : transform.position;
        /// <summary>The plate's centre in the world (tests: the label sits beside its thing, not on it).</summary>
        public Vector3 PlateCentre => transform.TransformPoint(new Vector3(_plateCentre.x, _plateCentre.y, 0f));
        /// <summary>The plate's half size in the world (m): x across, y up.</summary>
        public Vector2 PlateHalfSize => _plateHalf * transform.lossyScale.x;

        /// <summary>A hidden label at the scene root (one per slot; reuse it with <see cref="Show"/>, Destroy it when done).</summary>
        public static WorldLabel Create(string name)
        {
            var go = new GameObject("[HIS] WorldLabel " + name);
            var label = go.AddComponent<WorldLabel>();
            label.Build();
            go.SetActive(false);
            return label;
        }

        void Build()
        {
            _text = Subtitles.CreateWorldText("Text", transform, FontSize, 8f, TextAlignmentOptions.Center);
            _text.textWrappingMode = TextWrappingModes.NoWrap;
            _plate = Subtitles.CreatePlate("Backing", transform);
            var a = new GameObject("Arrow");
            a.transform.SetParent(transform, false);
            a.AddComponent<MeshFilter>().sharedMesh = ArrowMesh();
            var r = a.AddComponent<MeshRenderer>();
            r.sharedMaterial = ArrowMaterial();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _arrow = a.transform;
        }

        /// <summary>
        /// Put <paramref name="text"/> (TMP rich text; "\n" for a second line) beside <paramref name="thing"/> on
        /// <paramref name="side"/>, its arrow pointing at it. With <paramref name="place"/>, the label's centre is that point
        /// instead (a thing the eye can't reach, e.g. over the shoulder: the label sits in view and points the way). Both are
        /// read every frame (things move); a delegate that captures nothing costs nothing per frame.
        /// </summary>
        public void Show(string text, Func<Vector3> thing, Side side, Func<Vector3> place = null)
        {
            _thing = thing; _place = place; _side = side;
            if (!gameObject.activeSelf) gameObject.SetActive(true);   // TMP builds no geometry while inactive: on first
            if (text != Text)
            {
                Text = text;
                _text.SetText(text);
                _text.ForceMeshUpdate(true);
                Subtitles.FitBacking(_text, _plate, Pad);
                _plateCentre = new Vector2(_plate.localPosition.x, _plate.localPosition.y);
                _plateHalf = new Vector2(_plate.localScale.x * 0.5f, _plate.localScale.y * 0.5f);
            }
            Place();
        }

        /// <summary>Gone (the step is done).</summary>
        public void Hide()
        {
            Text = null;
            _thing = _place = null;
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }

        void LateUpdate() => Place();

        /// <summary>Beside the thing, facing the eye, sized by distance, the arrow at the plate's edge toward the thing.</summary>
        void Place()
        {
            if (_thing == null) return;
            if (_head == null) { var cam = Camera.main; if (cam == null) return; _head = cam.transform; }
            var head = _head.position;
            // Worked out at a point a little way from the thing toward the eye (Pull): seen from the eye it's the same spot, but
            // a thing on a surface (the door's handle, the monitor on its wall) never takes the plate into the leaf or the wall.
            var thing = Vector3.Lerp(_thing(), head, Pull);
            float s = Mathf.Clamp(Vector3.Distance(head, thing), MinScale, MaxScale);
            // The way the head faces, flat (steady while looking down, where the flat line to a hip is short and swings).
            var face = Vector3.Cross(_head.right, Vector3.up) + _head.forward;
            face.y = 0f;
            face = face.sqrMagnitude < 1e-6f ? Vector3.forward : face.normalized;

            Vector3 centre;
            if (_place != null) centre = _place();
            else
            {
                // Left/right across the line of sight to the thing (so the plate never slides in front of it, however far
                // to the side it is), leaning on the way the head faces the further the thing is below the eye (a hip, a hand
                // at the belt: then "right" is the head's right, as the eye reads it).
                var sight = thing - head;
                float drop = Mathf.Max(0f, -sight.y);
                sight.y = 0f;
                var across = sight + face * Mathf.Max(HeadingWeight, drop);
                if (across.sqrMagnitude < 1e-6f) across = face;
                var right = Vector3.Cross(Vector3.up, across.normalized);
                // Above/below as the eye sees it: world up with the sight line taken out. Level with the eye it is plain up;
                // looking steeply down at a thing (a gun held low for the reload) it leans away from the eye, so the plate
                // never slides into the sight line (straight down: beyond the thing, along the heading).
                var sightN = (thing - head).normalized;
                var upSeen = Vector3.up - sightN * Vector3.Dot(Vector3.up, sightN);
                upSeen = upSeen.sqrMagnitude < 1e-4f ? face : upSeen.normalized;
                Vector3 dir; float half;
                switch (_side)
                {
                    case Side.Below: dir = -upSeen; half = _plateHalf.y; break;
                    case Side.Left: dir = -right; half = _plateHalf.x; break;
                    case Side.Right: dir = right; half = _plateHalf.x; break;
                    default: dir = upSeen; half = _plateHalf.y; break;
                }
                centre = thing + dir * (s * (half + ArrowLength + Gap));
            }

            // Facing the eye: turned to it about the vertical — leaning on the head's heading by how far below the eye it is, so
            // a label at the hip or at a hand by the belt (straight below, its flat line from the eye points sideways) still
            // reads left to right — tipped back toward it (clamped), never rolled.
            var fwd = centre - head;
            var flat = new Vector3(fwd.x, 0f, fwd.z);
            var yaw = flat + face * Mathf.Max(0f, -fwd.y);
            yaw = yaw.sqrMagnitude < 1e-6f ? face : yaw.normalized;
            float pitch = Mathf.Clamp(Mathf.Atan2(-fwd.y, Mathf.Max(Vector3.Dot(flat, yaw), 0.01f)) * Mathf.Rad2Deg, MinPitch, MaxPitch);
            var rot = Quaternion.LookRotation(yaw, Vector3.up) * Quaternion.Euler(pitch, 0f, 0f);
            // The plate's centre (not the text's pivot) lands on the centre point.
            transform.SetPositionAndRotation(centre - rot * new Vector3(_plateCentre.x * s, _plateCentre.y * s, 0f), rot);
            transform.localScale = new Vector3(s, s, s);

            // The arrow: from the plate's edge toward the thing AS THE EYE SEES IT (the thing brought to the plate's depth along
            // its sight line; one beside or behind the eye — the slung gun over the shoulder — by its direction across the view).
            var n = fwd.normalized;
            var v = thing - head;
            float depth = Vector3.Dot(v, n);
            var toThing = depth > 1e-3f ? v * (Vector3.Dot(fwd, n) / depth) - fwd : v - n * depth;
            var u = new Vector2(Vector3.Dot(toThing, transform.right), Vector3.Dot(toThing, transform.up));
            if (u.sqrMagnitude < 1e-10f) { if (_arrow.gameObject.activeSelf) _arrow.gameObject.SetActive(false); return; }
            if (!_arrow.gameObject.activeSelf) _arrow.gameObject.SetActive(true);
            u.Normalize();
            float t = Mathf.Min(_plateHalf.x / Mathf.Max(Mathf.Abs(u.x), 1e-4f), _plateHalf.y / Mathf.Max(Mathf.Abs(u.y), 1e-4f));
            var at = _plateCentre + u * (t + 0.002f);
            _arrow.localPosition = new Vector3(at.x, at.y, 0.001f);
            _arrow.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(u.y, u.x) * Mathf.Rad2Deg - 90f); // the mesh points +y
        }

        // ───────────────────────────── shared parts ─────────────────────────────

        static Mesh _arrowMesh;
        static Material _arrowMat;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _arrowMesh = null; _arrowMat = null; } // domain reload off

        /// <summary>A flat triangle pointing +y from its base on the x axis (faces −z, the eye, like the text).</summary>
        static Mesh ArrowMesh()
        {
            if (_arrowMesh != null) return _arrowMesh;
            _arrowMesh = new Mesh { name = "HIS Label Arrow" };
            _arrowMesh.SetVertices(new System.Collections.Generic.List<Vector3>
                { new(-ArrowWidth * 0.5f, 0f, 0f), new(ArrowWidth * 0.5f, 0f, 0f), new(0f, ArrowLength, 0f) });
            _arrowMesh.SetNormals(new System.Collections.Generic.List<Vector3> { Vector3.back, Vector3.back, Vector3.back });
            _arrowMesh.SetTriangles(new[] { 2, 1, 0 }, 0);   // clockwise seen from the eye (looking along +z)
            _arrowMesh.RecalculateBounds();
            return _arrowMesh;
        }

        /// <summary>Unlit, the accent colour, both faces (the plate's shader: in the build via UI_Panel.mat).</summary>
        static Material ArrowMaterial()
        {
            if (_arrowMat != null) return _arrowMat;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) { Debug.LogWarning("[HIS] WorldLabel: no URP Unlit shader — the arrow won't draw"); return null; }
            _arrowMat = new Material(shader) { name = "HIS Label Arrow (runtime)" };
            _arrowMat.SetColor("_BaseColor", ArrowColor);
            if (_arrowMat.HasProperty("_Cull")) _arrowMat.SetFloat("_Cull", 0f);
            return _arrowMat;
        }
    }
}
