using HIS.Room;
using Meta.XR.MRUtilityKit;
using TMPro;
using UnityEngine;

namespace HIS.Outcome
{
    /// <summary>
    /// The bad-call mugshot (<see cref="BadCallShow"/>): one big booking-photo board, built once and reused, put where the
    /// player is LOOKING (Brett 0.1.16: on the wall "it kinda goes under the radar … sometimes the walls not right in front
    /// of you"):
    /// <list type="bullet">
    /// <item>The board: a light-grey height-chart backdrop (1.2 × 1.36 m, dark frame) with ruler lines from 3'0" to 7'0"
    ///   (every 2", heavy every 6", labelled in feet and inches), a dark head-and-shoulders silhouette standing in front of it
    ///   (front view, then a side profile on the second shot) and the black booking placard at its chest: department,
    ///   booking number, the charge in capitals, the date.</item>
    /// <item>Where: the nearest scanned wall within <see cref="MaxWallDistance"/> whose board lands inside a
    ///   <see cref="PlaceCone"/>° cone of the gaze, facing the player and with nothing scanned in between; there it stands on
    ///   the real floor at true heights, 1.5 cm off the wall, kept inside the wall's ends. Otherwise it floats about
    ///   <see cref="FloatDistance"/> ahead along the gaze (pitch clamped), tilted to face the player, pulled in (and shrunk to
    ///   the same angular size) in front of anything scanned, the floor and the ceiling.</item>
    /// <item>It is placed ONCE, when it goes up (<see cref="Show"/>), and then stays put for the rest of the show: no
    ///   following the head (Brett 0.1.17: "the mug shot thing appearing whereever you look is silly cause he asks you to
    ///   turn left and then it goes left"). After "turn to your left" the board, its flash and the camera's shutter
    ///   (<see cref="CameraSpot"/>) are off to the player's right, like a real booking camera. <see cref="Seen"/> only
    ///   records whether it was looked at (in the <see cref="ViewCone"/>° cone for <see cref="SeenSeconds"/>).</item>
    /// <item>Each camera flash (<see cref="Shot"/>) blows the board white for a moment with the passthrough and bumps it.</item>
    /// </list>
    /// Everything is opaque and not depth-occluded, like the verdict banner (hands never cut holes in it); that's why the
    /// placement insists on a clear line of sight. A floating board gets out of the banner's way (<see cref="Tuck"/>,
    /// <see cref="MakeWay"/>); one on a wall stays, since the banner always hangs ≥ 12 cm in front of the walls.
    /// Scaled time, so the pause menu freezes it.
    /// </summary>
    public class MugshotProps : MonoBehaviour
    {
        const float Inch = 0.0254f;
        const int FromInches = 36, ToInches = 84;
        // The board, in its own space: origin at the board's centre, +Y up, +Z away from the viewer (it's seen from -Z).
        const float BoardWidth = 1.2f, BoardBottom = 0.84f, BoardTop = 2.2f;
        const float BoardHeight = BoardTop - BoardBottom, CentreHeight = (BoardBottom + BoardTop) * 0.5f;
        const float LinesLeft = -0.40f, LinesRight = 0.56f, FigureX = 0.08f;
        const float WallGap = 0.015f;

        /// <summary>A wall this far (horizontally) or nearer can carry the board; farther away it floats.</summary>
        public const float MaxWallDistance = 3f;
        const float MinWallDistance = 0.9f;        // nearer than this the true-size board would overflow the view: it floats, smaller
        /// <summary>The board's centre must be within this many degrees of the gaze when placed.</summary>
        public const float PlaceCone = 28f;
        /// <summary>"Seen" = the board's centre within this many degrees of the head's forward.</summary>
        public const float ViewCone = 30f;
        public const float SeenSeconds = 1f;
        public const float FloatDistance = 1.5f;
        const float MinFloatDistance = 0.5f, FloatClearance = 0.2f;
        const float MinPitch = -40f, MaxPitch = 20f;
        const float PopSeconds = 0.3f, BumpSeconds = 0.22f, FlashSeconds = 0.4f, TuckSeconds = 0.25f;

        static readonly LabelFilter Walls = new(MRUKAnchor.SceneLabels.WALL_FACE);
        // Things that would stand between the player and the board (door/window frames lie on the wall itself).
        static readonly LabelFilter Blockers = new(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE
            | MRUKAnchor.SceneLabels.INNER_WALL_FACE | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH
            | MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.SCREEN
            | MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT);
        static readonly LabelFilter Solid = new(MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE
            | MRUKAnchor.SceneLabels.INNER_WALL_FACE | MRUKAnchor.SceneLabels.DOOR_FRAME | MRUKAnchor.SceneLabels.WINDOW_FRAME
            | MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.COUCH | MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED
            | MRUKAnchor.SceneLabels.SCREEN | MRUKAnchor.SceneLabels.OTHER | MRUKAnchor.SceneLabels.LAMP | MRUKAnchor.SceneLabels.PLANT);
        static readonly float[] YawOffsets = { 0f, -10f, 10f, -20f, 20f };

        static readonly Color BackdropColor = new(0.78f, 0.8f, 0.82f, 1f);
        static readonly Color LineColor = new(0.08f, 0.08f, 0.1f, 1f);
        static readonly Color FigureColor = new(0.2f, 0.21f, 0.24f, 1f);

        /// <summary>Where the board goes: centre, rotation (+Z away from the player), uniform scale, on a wall or floating.</summary>
        public struct Placement
        {
            public Vector3 Center;
            public Quaternion Rotation;
            public float Scale;
            public bool OnWall;
            public float Distance;
        }

        Transform _board, _placard;
        TextMeshPro _placardText;
        Material _backdropMat, _lineMat, _figureMat;
        MeshFilter _figure;
        Mesh _front, _profile;
        bool _built, _shown, _settled, _tucking;
        float _scale = 1f, _popT, _bumpT = BumpSeconds, _flashT = FlashSeconds, _flashPeak = 1f, _tuckT, _seenFor;

        public bool Visible => _shown;
        /// <summary>The board is on a scanned wall (vs floating in front of the player).</summary>
        public bool OnWall { get; private set; }
        /// <summary>It has been in the view cone for <see cref="SeenSeconds"/> (in all) since it went up.</summary>
        public bool Seen => _settled;
        /// <summary>Tests: times it was placed this show (1: it never moves after going up).</summary>
        public int Placements { get; private set; }
        /// <summary>Where the booking camera is: between the player's eyes and the board as it went up (the shutter clicks here).</summary>
        public Vector3 CameraSpot { get; private set; }
        /// <summary>The profile silhouette is up (second shot).</summary>
        public bool Profile { get; private set; }
        public Vector3 Center => _board != null ? _board.position : Vector3.zero;
        public Quaternion Facing => _board != null ? _board.rotation : Quaternion.identity;
        public string PlacardText => _placardText != null ? _placardText.text : null;

        public void Init(Material fx, TMP_FontAsset font, Material textMaterial, Material panelMaterial)
        {
            if (_built) return;
            var baseMat = panelMaterial;
            if (baseMat == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh != null) baseMat = new Material(sh);
            }
            _board = new GameObject("Board").transform;
            _board.SetParent(transform, false);

            if (baseMat != null)
            {
                _backdropMat = Tinted(baseMat, "Mugshot backdrop (runtime)", BackdropColor);
                _lineMat = Tinted(baseMat, "Mugshot lines (runtime)", LineColor);
                _figureMat = Tinted(baseMat, "Mugshot figure (runtime)", FigureColor);
            }
            // Back to front: dark frame, grey backdrop, ruler lines, the figure, the labels, the placard (3 cm proud).
            var frame = HIS.UI.Subtitles.CreatePlate("Frame", _board, panelMaterial);
            frame.localScale = new Vector3(BoardWidth + 0.05f, BoardHeight + 0.05f, 1f);
            frame.localPosition = new Vector3(0f, 0f, 0.013f);
            var backdrop = HIS.UI.Subtitles.CreatePlate("Backdrop", _board, _backdropMat);
            backdrop.localScale = new Vector3(BoardWidth, BoardHeight, 1f);
            backdrop.localPosition = new Vector3(0f, 0f, 0.01f);
            MeshPart("Lines", ChartMesh(), _lineMat, 0.006f);
            _front = FigureMesh(false);
            _profile = FigureMesh(true);
            _figure = MeshPart("Figure", _front, _figureMat, 0.003f);

            for (int inches = FromInches; inches <= ToInches; inches += 6)
            {
                var label = HIS.UI.Subtitles.CreateWorldText($"Label{inches}", _board, 6.5f, 3f, TextAlignmentOptions.Right, textMaterial, font);
                label.text = inches % 12 == 0 ? $"{inches / 12}'" : $"{inches / 12}'{inches % 12}\"";
                label.color = LineColor;
                label.fontStyle = FontStyles.Bold;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.rectTransform.pivot = new Vector2(1f, 0.5f);
                label.transform.localPosition = new Vector3(LinesLeft - 0.03f, inches * Inch - CentreHeight, 0f);
            }

            // The booking placard at the figure's chest.
            _placard = new GameObject("Placard").transform;
            _placard.SetParent(_board, false);
            _placard.localPosition = new Vector3(FigureX, 1.18f - CentreHeight, -0.03f);
            _placard.localScale = Vector3.one * 1.2f;
            var plate = HIS.UI.Subtitles.CreatePlate("Board", _placard, panelMaterial);
            plate.localScale = new Vector3(0.52f, 0.30f, 1f);
            plate.localPosition = new Vector3(0f, 0f, 0.004f);
            _placardText = HIS.UI.Subtitles.CreateWorldText("Text", _placard, 4.5f, 4.7f, TextAlignmentOptions.Center, textMaterial, font);
            _placardText.rectTransform.sizeDelta = new Vector2(4.7f, 2.7f);
            _placardText.enableAutoSizing = true;
            _placardText.fontSizeMin = 1.5f;
            _placardText.fontSizeMax = 4.5f;
            _placardText.overflowMode = TextOverflowModes.Truncate;
            _placardText.color = new Color(0.95f, 0.95f, 0.92f);

            _board.gameObject.SetActive(false);
            _built = true;
        }

        /// <summary>Put the board up where the player is looking (front view), with the charge on the placard. It stays there.</summary>
        public void Show(Vector3 eye, Vector3 gaze, string charge, string booking, string date)
        {
            if (!_built) return;
            _board.gameObject.SetActive(true); // TMP only builds text on an active object
            _placardText.text = "<size=55%><color=#BFC3C9>POLICE DEPT.   BOOKING NO. " + booking + "</color></size>\n<b>"
                                + (string.IsNullOrEmpty(charge) ? "BAD CALL" : charge.ToUpperInvariant()) + "</b>\n<size=55%><color=#BFC3C9>" + date + "</color></size>";
            _placardText.ForceMeshUpdate(true);
            SetProfile(false);
            _flashT = FlashSeconds; _bumpT = BumpSeconds;
            _tucking = false;
            Placements = 0;
            _shown = true;
            Place(eye, gaze);
            var toBoard = _board.position - eye;
            CameraSpot = eye + toBoard.normalized * Mathf.Min(0.8f, 0.6f * toBoard.magnitude);
            ApplyColors(0f);
            Debug.Log($"[HIS] Mugshot: {(OnWall ? "on the wall" : "floating")} {Vector3.Distance(eye, _board.position):F2} m ahead, placard '{charge}' #{booking}");
        }

        /// <summary>
        /// A camera flash, where the board is: 0 = the front shot, 1 = the profile (the figure turns side-on; the board doesn't
        /// move). <paramref name="strength"/> scales how white the board goes (the reduced-flashing setting passes 0.5).
        /// </summary>
        public void Shot(int index, float strength = 1f)
        {
            if (!_shown) return;
            if (index > 0 && !Profile) SetProfile(true);
            _flashPeak = Mathf.Clamp01(strength);
            _flashT = 0f;
            _bumpT = 0f;
        }

        /// <summary>The banner is coming: a floating board shrinks away (one on a wall stays behind the banner).</summary>
        public void Tuck()
        {
            if (!_shown || OnWall || _tucking) return;
            _tucking = true;
            _tuckT = 0f;
        }

        /// <summary>The banner is up now: a floating board must not hang in front of it, so it goes at once.</summary>
        public void MakeWay()
        {
            if (_shown && !OnWall) Hide();
        }

        /// <summary>Tests: is the board up and between <paramref name="eye"/> and <paramref name="point"/> (would it cover it)?</summary>
        public bool Covers(Vector3 eye, Vector3 point)
        {
            if (!_shown) return false;
            var toBoard = _board.position - eye; var toPoint = point - eye;
            float half = Mathf.Atan2(0.5f * Mathf.Max(BoardWidth, BoardHeight) * _scale, Mathf.Max(0.05f, toBoard.magnitude)) * Mathf.Rad2Deg;
            return toBoard.magnitude < toPoint.magnitude - 0.05f && Vector3.Angle(toBoard, toPoint) < half;
        }

        public void Hide()
        {
            _shown = false;
            _tucking = false;
            _settled = false;
            if (_board != null) _board.gameObject.SetActive(false);
        }

        public void Tick(float dt)
        {
            if (!_shown) return;
            if (_tucking)
            {
                _tuckT += dt;
                float k = Mathf.Clamp01(_tuckT / TuckSeconds);
                _board.localScale = Vector3.one * (_scale * Mathf.Max(0.001f, 1f - k * k));
                if (k >= 1f) Hide();
                return;
            }

            // Was it looked at? (Only noted: it never moves.)
            if (!_settled)
            {
                OutcomeShow.Head(out var eye, out var fwd);
                if (Vector3.Angle(fwd, _board.position - eye) <= ViewCone && (_seenFor += dt) >= SeenSeconds)
                {
                    _settled = true;
                    Debug.Log($"[HIS] Mugshot seen ({(OnWall ? "on the wall" : "floating")})");
                }
            }

            // Pop in (ease-out-back), a bump on each shot, the flash on the board.
            float s = 1f;
            if (_popT < PopSeconds)
            {
                _popT += dt;
                float k = Mathf.Clamp01(_popT / PopSeconds);
                const float c1 = 1.70158f, c3 = c1 + 1f;
                s = Mathf.Max(0.001f, 1f + c3 * Mathf.Pow(k - 1f, 3f) + c1 * Mathf.Pow(k - 1f, 2f));
            }
            if (_bumpT < BumpSeconds)
            {
                _bumpT += dt;
                s *= 1f + 0.05f * Mathf.Sin(Mathf.Clamp01(_bumpT / BumpSeconds) * Mathf.PI);
            }
            _board.localScale = Vector3.one * (_scale * s);
            if (_flashT < FlashSeconds)
            {
                _flashT += dt;
                ApplyColors(FlashLevel(_flashT) * _flashPeak);
            }
        }

        // ───────────────────────────── placement ─────────────────────────────

        void Place(Vector3 eye, Vector3 gaze)
        {
            var rs = RoomScanner.Instance;
            var room = rs != null ? rs.Room : null;
            OutcomeShow.RoomHeights(out float floorY, out float ceilingY, out _);
            if (room == null) { floorY = eye.y - 1.6f; ceilingY = floorY + 2.4f; }
            var p = Plan(eye, gaze, room, floorY, ceilingY);
            OnWall = p.OnWall;
            _scale = p.Scale;
            _board.SetPositionAndRotation(p.Center, p.Rotation);
            _board.localScale = Vector3.one * (_scale * 0.001f);
            _popT = 0f;
            _settled = false;
            _seenFor = 0f;
            Placements++;
        }

        /// <summary>
        /// Where the board goes for this eye and gaze (pure; tests call it): on the nearest wall within
        /// <see cref="MaxWallDistance"/> that lands inside <see cref="PlaceCone"/> of the gaze, else floating along the gaze.
        /// <paramref name="room"/> null = no scan (always floating).
        /// </summary>
        public static Placement Plan(Vector3 eye, Vector3 gaze, MRUKRoom room, float floorY, float ceilingY)
        {
            if (gaze.sqrMagnitude < 1e-6f) gaze = Vector3.forward;
            gaze.Normalize();
            var flat = new Vector3(gaze.x, 0f, gaze.z);
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            if (room != null && TryWall(eye, gaze, flat, room, floorY, out var onWall)) return onWall;
            return Floating(eye, gaze, flat, room, floorY, ceilingY);
        }

        static bool TryWall(Vector3 eye, Vector3 gaze, Vector3 flat, MRUKRoom room, float floorY, out Placement best)
        {
            best = default;
            bool found = false;
            // Level rays across the cone (walls always span eye height; a pitched gaze is judged by the cone check below).
            foreach (float yaw in YawOffsets)
            {
                var dir = Quaternion.AngleAxis(yaw, Vector3.up) * flat;
                float reach = MaxWallDistance / Mathf.Cos(yaw * Mathf.Deg2Rad) + 0.3f;
                if (!room.Raycast(new Ray(eye, dir), reach, Walls, out var hit, out var anchor)) continue;
                var n = hit.normal; n.y = 0f;
                if (n.sqrMagnitude < 1e-4f) continue;
                n.Normalize();
                if (Vector3.Dot(n, dir) > 0f) n = -n;
                var at = new Vector3(hit.point.x, floorY + CentreHeight, hit.point.z);
                at = KeepOnWall(at, anchor, BoardWidth * 0.5f + 0.05f) + n * WallGap;

                var to = at - eye;
                var toFlat = new Vector3(to.x, 0f, to.z);
                float d = toFlat.magnitude;
                if (d > MaxWallDistance || d < MinWallDistance) continue;
                if (Vector3.Dot(n, -toFlat / d) < 0.5f) continue;                   // seen at more than 60° off square
                if (Vector3.Angle(gaze, to) > PlaceCone) continue;
                if (room.Raycast(new Ray(eye, to / to.magnitude), to.magnitude - 0.1f, Blockers, out _, out _)) continue; // something in between
                if (found && d >= best.Distance) continue;
                best = new Placement { Center = at, Rotation = Quaternion.LookRotation(-n, Vector3.up), Scale = 1f, OnWall = true, Distance = d };
                found = true;
            }
            return found;
        }

        // Slide the point along the wall so the whole board stays on it (a wall shorter than the board: its middle).
        static Vector3 KeepOnWall(Vector3 p, MRUKAnchor wall, float half)
        {
            if (wall == null || !wall.PlaneRect.HasValue) return p;
            var r = wall.PlaneRect.Value;
            var local = wall.transform.InverseTransformPoint(p);
            local.x = r.width > half * 2f ? Mathf.Clamp(local.x, r.xMin + half, r.xMax - half) : r.center.x;
            var w = wall.transform.TransformPoint(local);
            return new Vector3(w.x, p.y, w.z);
        }

        static Placement Floating(Vector3 eye, Vector3 gaze, Vector3 flat, MRUKRoom room, float floorY, float ceilingY)
        {
            float pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(gaze.y, -1f, 1f)) * Mathf.Rad2Deg, MinPitch, MaxPitch);
            var right = Vector3.Cross(Vector3.up, flat);
            var dir = Quaternion.AngleAxis(-pitch, right) * flat;
            float d = FloatDistance;
            if (room != null && room.Raycast(new Ray(eye, dir), FloatDistance + FloatClearance + 0.1f, Solid, out var hit, out _))
                d = Mathf.Min(d, hit.distance - FloatClearance);
            // Same angular size at any distance (scale d / FloatDistance), so the bottom edge sinks d·(-sin p) + (d/1.5)·h·cos p
            // below the eye: keep it above the floor and the top under the ceiling.
            float sin = Mathf.Sin(pitch * Mathf.Deg2Rad), cos = Mathf.Cos(pitch * Mathf.Deg2Rad);
            float halfPerMetre = BoardHeight * 0.5f / FloatDistance * cos;
            float down = -sin + halfPerMetre, up = sin + halfPerMetre;
            if (down > 1e-3f) d = Mathf.Min(d, (eye.y - floorY - 0.03f) / down);
            if (up > 1e-3f) d = Mathf.Min(d, (ceilingY - eye.y - 0.03f) / up);
            d = Mathf.Max(MinFloatDistance, d);
            return new Placement { Center = eye + dir * d, Rotation = Quaternion.LookRotation(dir, Vector3.up), Scale = d / FloatDistance, OnWall = false, Distance = d };
        }

        // ───────────────────────────── looks ─────────────────────────────

        void SetProfile(bool profile)
        {
            Profile = profile;
            if (_figure != null) _figure.sharedMesh = profile ? _profile : _front;
        }

        // The passthrough goes white on a flash, but it can't touch rendered things: the board blows out with it.
        void ApplyColors(float flash)
        {
            if (_backdropMat != null) _backdropMat.color = Color.Lerp(BackdropColor, Color.white, flash);
            if (_lineMat != null) _lineMat.color = Color.Lerp(LineColor, Color.white, flash * 0.55f);
            if (_figureMat != null) _figureMat.color = Color.Lerp(FigureColor, Color.white, flash * 0.55f);
        }

        // 60 ms up (as the passthrough flash), then an ease back down.
        static float FlashLevel(float t)
        {
            if (t >= FlashSeconds) return 0f;
            if (t < 0.06f) return t / 0.06f;
            float k = (t - 0.06f) / (FlashSeconds - 0.06f);
            return (1f - k) * (1f - k);
        }

        static Material Tinted(Material source, string name, Color c)
        {
            var m = new Material(source) { name = name };
            m.color = c;
            return m;
        }

        MeshFilter MeshPart(string name, Mesh mesh, Material mat, float z)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_board, false);
            go.transform.localPosition = new Vector3(0f, 0f, z);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (mat != null) mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mf;
        }

        // Every 2" a thin line, every 6" a heavy, longer one; one mesh, board space.
        static Mesh ChartMesh()
        {
            int count = (ToInches - FromInches) / 2 + 1;
            var v = new Vector3[count * 4]; var tri = new int[count * 6];
            for (int i = 0; i < count; i++)
            {
                int inches = FromInches + i * 2;
                bool major = inches % 6 == 0;
                float y = inches * Inch - CentreHeight, h = major ? 0.01f : 0.004f;
                float l = major ? LinesLeft : LinesLeft + 0.05f, r = major ? LinesRight : LinesRight - 0.05f;
                int k = i * 4;
                v[k] = new Vector3(l, y - h * 0.5f, 0f); v[k + 1] = new Vector3(l, y + h * 0.5f, 0f);
                v[k + 2] = new Vector3(r, y + h * 0.5f, 0f); v[k + 3] = new Vector3(r, y - h * 0.5f, 0f);
                // Clockwise seen from -Z (the side facing the player).
                tri[i * 6] = k; tri[i * 6 + 1] = k + 1; tri[i * 6 + 2] = k + 2;
                tri[i * 6 + 3] = k; tri[i * 6 + 4] = k + 2; tri[i * 6 + 5] = k + 3;
            }
            var m = new Mesh { name = "HeightChart", vertices = v, triangles = tri };
            m.RecalculateBounds();
            return m;
        }

        // A head-and-shoulders silhouette about 5'10" tall (board space), built from overlapping convex pieces. The profile
        // faces the viewer's right: someone who turned to their left.
        static Mesh FigureMesh(bool profile)
        {
            var v = new System.Collections.Generic.List<Vector3>(160);
            var t = new System.Collections.Generic.List<int>(480);
            float x = FigureX, y0 = -CentreHeight;
            if (!profile)
            {
                Ellipse(v, t, new Vector2(x, 1.665f + y0), new Vector2(0.085f, 0.115f), 2f, 28, false);   // head
                Box(v, t, x - 0.045f, 1.42f + y0, x + 0.045f, 1.6f + y0);                                // neck
                Ellipse(v, t, new Vector2(x, 1.0f + y0), new Vector2(0.24f, 0.46f), 3f, 24, true);        // shoulders
            }
            else
            {
                Ellipse(v, t, new Vector2(x - 0.01f, 1.67f + y0), new Vector2(0.1f, 0.11f), 2f, 28, false); // head, deeper than wide
                Tri(v, t, new Vector2(x + 0.08f, 1.69f + y0), new Vector2(x + 0.128f, 1.638f + y0), new Vector2(x + 0.082f, 1.615f + y0)); // nose
                Ellipse(v, t, new Vector2(x + 0.055f, 1.585f + y0), new Vector2(0.045f, 0.035f), 2f, 16, false); // chin
                Box(v, t, x - 0.045f, 1.42f + y0, x + 0.05f, 1.6f + y0);                                  // neck
                Ellipse(v, t, new Vector2(x, 1.0f + y0), new Vector2(0.14f, 0.45f), 3f, 24, true);        // shoulders, side on
            }
            var m = new Mesh { name = profile ? "MugshotProfile" : "MugshotFront" };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        // A (super)ellipse fan; upperHalf = the top half only (flat bottom). Counter-clockwise points → (c, p[i+1], p[i]).
        static void Ellipse(System.Collections.Generic.List<Vector3> v, System.Collections.Generic.List<int> t, Vector2 c, Vector2 r,
                            float power, int segments, bool upperHalf)
        {
            int c0 = v.Count, n = segments;
            v.Add(new Vector3(c.x, c.y, 0f));
            float span = upperHalf ? Mathf.PI : Mathf.PI * 2f, e = 2f / power; // superellipse: sign·|cos|^(2/p)
            int points = upperHalf ? n + 1 : n;                                   // a half ends on the flat side; a whole wraps
            for (int i = 0; i < points; i++)
            {
                float a = span * i / n, ca = Mathf.Cos(a), sa = Mathf.Sin(a);
                v.Add(new Vector3(c.x + r.x * Mathf.Sign(ca) * Mathf.Pow(Mathf.Abs(ca), e), c.y + r.y * Mathf.Sign(sa) * Mathf.Pow(Mathf.Abs(sa), e), 0f));
            }
            for (int i = 0; i < n; i++)
            {
                int a = c0 + 1 + i, b = c0 + 1 + (upperHalf ? i + 1 : (i + 1) % n);
                t.Add(c0); t.Add(b); t.Add(a);
            }
        }

        static void Box(System.Collections.Generic.List<Vector3> v, System.Collections.Generic.List<int> t, float x0, float y0, float x1, float y1)
        {
            int k = v.Count;
            v.Add(new Vector3(x0, y0, 0f)); v.Add(new Vector3(x0, y1, 0f)); v.Add(new Vector3(x1, y1, 0f)); v.Add(new Vector3(x1, y0, 0f));
            t.Add(k); t.Add(k + 1); t.Add(k + 2); t.Add(k); t.Add(k + 2); t.Add(k + 3);
        }

        static void Tri(System.Collections.Generic.List<Vector3> v, System.Collections.Generic.List<int> t, Vector2 a, Vector2 b, Vector2 c)
        {
            // Clockwise seen from -Z whatever order they came in.
            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (cross > 0f) (b, c) = (c, b);
            int k = v.Count;
            v.Add(new Vector3(a.x, a.y, 0f)); v.Add(new Vector3(b.x, b.y, 0f)); v.Add(new Vector3(c.x, c.y, 0f));
            t.Add(k); t.Add(k + 1); t.Add(k + 2);
        }
    }
}
