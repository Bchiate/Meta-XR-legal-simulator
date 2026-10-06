using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HIS.Weapons;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Eyes for the gun hands (research/16 §3.4): renders each weapon prefab held by the virtual hand exactly as the game
/// places it (Grabbable.OnGrab math against the HandController's AimAnchor, GripHand's pose), with a translucent ghost of
/// the Touch Plus controller at its tracked pose, into Logs/poses/hands_&lt;weapon&gt;.png.
/// Columns = finger states (trigger OFF / REST / HALF / FULL pull, THUMB lifted; shotgun adds PUMPED; knife GRIP only).
/// Rows = right side, left side, top (ortho, 1 cm grid, 10 cm bold, red = the controller's axes), below-front and first-person
/// (perspective). Markers: green = the controller's web-of-hand point (HandController.WebPoint), red = the hand's web point,
/// yellow = index pad. Each cell is labelled with the hand vertices inside the weapon per finger (T I M R P, count/deepest mm).
/// GRAPHICS=1 scripts/unity-batch.sh HandSnapshots.Render [-hands pistol,shotgun,knife] ; MODELS=1 renders the bare models
/// (Model_*.prefab in their own frame, parts listed) into Logs/poses/models_*.png instead; ACTION=1 renders a DA/SA gun's
/// action states (hammer, trigger, slide, slide catch, decocker) into Logs/poses/action_*.png.
/// </summary>
public static class HandSnapshots
{
    const string OutDir = "Logs/poses";
    const int Header = 22, Cell = 340;
    const int LSubject = 31, LSide = 30, LLeft = 29, LTop = 28, LMark = 27;
    const string Core = "Packages/com.meta.xr.sdk.core/";

    /// <summary>A weapon to render; the Solve* targets (pose frame, right hand) drive SOLVE=1: where the index pad lies off the
    /// trigger and where the thumb pad rests.</summary>
    sealed class Spec
    {
        public string Name, Prefab, Model; public GripKind Kind; public float Ortho;
        public Vector3 IndexOff, Thumb, SupportThumb; public bool WrapIndex;
        /// <summary>Height band of the trigger shoe (0 = bottom, 1 = top) whose front-most point is the index pad's target: a
        /// straight shoe (Glock) takes its lower 70 %; the M9's shoe curls forward at the tip, so the middle of it.</summary>
        public Vector2 FaceBand = new(0f, 0.7f);
    }
    static readonly Spec[] Specs =
    {
        new() { Name = "pistol", Prefab = "Assets/_Game/Prefabs/Weapon_Pistol.prefab", Model = "Assets/_Game/Models/Model_G19.prefab", Kind = GripKind.Pistol, Ortho = 0.085f,
                IndexOff = new Vector3(0.019f, 0.030f, 0.028f), Thumb = new Vector3(-0.018f, 0.022f, -0.018f) },
        new() { Name = "m9", Prefab = "Assets/_Game/Prefabs/Weapon_M9.prefab", Model = "Assets/_Game/Models/Model_M9.prefab", Kind = GripKind.PistolM9, Ortho = 0.095f,
                IndexOff = new Vector3(0.020f, 0.021f, 0.036f), Thumb = new Vector3(-0.028f, 0.016f, -0.018f), FaceBand = new Vector2(0.18f, 0.3f) },
        new() { Name = "shotgun", Prefab = "Assets/_Game/Prefabs/Weapon_Shotgun.prefab", Model = "Assets/_Game/Models/Model_R870.prefab", Kind = GripKind.ShotgunRear, Ortho = 0.1f,
                IndexOff = new Vector3(0.022f, -0.018f, -0.105f), Thumb = new Vector3(-0.016f, 0.0f, -0.16f), SupportThumb = new Vector3(0.02f, 0f, 0.03f) },
        new() { Name = "knife", Prefab = "Assets/_Game/Prefabs/Weapon_Knife.prefab", Model = "Assets/_Game/Models/Model_KaBar.prefab", Kind = GripKind.Knife, Ortho = 0.1f,
                Thumb = new Vector3(-0.021f, -0.008f, -0.045f), WrapIndex = true },
        // AR-15 (Model_AR15: web seat at the origin, bore 46 mm above it): index off the trigger along the magwell's right side,
        // thumb round the grip's left side under the selector; support thumb along the handguard's near side.
        new() { Name = "ar15", Prefab = AR15Builder.WeaponPath, Model = AR15Builder.ModelPath, Kind = GripKind.RifleGrip, Ortho = 0.12f,
                IndexOff = new Vector3(0.019f, 0.0f, 0.10f), Thumb = new Vector3(-0.023f, -0.008f, 0.024f), SupportThumb = new Vector3(0.026f, 0.004f, 0.03f),
                FaceBand = new Vector2(0.35f, 0.65f) },
    };

    struct State { public string Label; public float Onto, Pull, ThumbLift, Pump; public bool Support; }

    [MenuItem("HIS/Debug/Render Hand Snapshots")]
    public static void Render()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
        { Debug.LogError("[HIS] HANDS: no GPU — run with GRAPHICS=1"); return; }
        var args = Environment.GetCommandLineArgs();
        int ai = Array.IndexOf(args, "-hands");
        var only = ai >= 0 && ai + 1 < args.Length ? args[ai + 1].Split(',') : null;
        bool models = Environment.GetEnvironmentVariable("MODELS") == "1";
        Directory.CreateDirectory(OutDir);
        bool async = ShaderUtil.allowAsyncCompilation; ShaderUtil.allowAsyncCompilation = false;
        bool single = Application.isBatchMode;
        var prevActive = SceneManager.GetActiveScene();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        bool hitBack = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true;
        var report = new StringBuilder($"# Hand snapshots ({DateTime.Now:yyyy-MM-dd HH:mm})\n");
        try
        {
            foreach (var s in Specs)
            {
                if (only != null && !only.Contains(s.Name)) continue;
                using var st = new Stage();
                if (Environment.GetEnvironmentVariable("RIG") == "1") { st.RenderRig(s, report); continue; }
                if (Environment.GetEnvironmentVariable("ACTION") == "1") { if (!st.RenderRifleAction(s, report)) st.RenderAction(s, report); }
                else if (models) st.RenderModel(s, report); else st.RenderHeld(s, report);
            }
        }
        finally
        {
            Physics.queriesHitBackfaces = hitBack;
            ShaderUtil.allowAsyncCompilation = async;
            if (!single) { if (prevActive.IsValid()) SceneManager.SetActiveScene(prevActive); EditorSceneManager.CloseScene(scene, true); }
        }
        bool action = Environment.GetEnvironmentVariable("ACTION") == "1";
        File.WriteAllText(Path.Combine(OutDir, action ? "action.txt" : models ? "models.txt" : "hands.txt"), report.ToString());
        Debug.Log("[HIS] HANDS rendered → " + OutDir);
    }

    sealed class Stage : IDisposable
    {
        readonly List<Object> _trash = new();
        readonly RenderTexture _rt;
        readonly Texture2D _read;
        readonly Material _grid, _red, _green, _yellow, _ghost, _skin;
        readonly Camera _cam;

        public Stage()
        {
            _rt = new RenderTexture(Cell, Cell, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 }; _trash.Add(_rt);
            _read = new Texture2D(Cell, Cell, TextureFormat.RGBA32, false); _trash.Add(_read);
            var grid = GridTexture(); _trash.Add(grid);
            _grid = Mat("Unlit/Texture", Color.white); _grid.mainTexture = grid; _grid.mainTextureScale = new Vector2(20f, 20f);
            _red = Mat("Unlit/Color", new Color(0.95f, 0.1f, 0.1f));
            _green = Mat("Unlit/Color", new Color(0.1f, 0.9f, 0.2f));
            _yellow = Mat("Unlit/Color", new Color(1f, 0.85f, 0.1f));
            _ghost = Mat("Universal Render Pipeline/Unlit", new Color(0.3f, 0.75f, 1f, 0.28f));
            MakeTransparent(_ghost);
            _skin = Mat("HIS/LitPBR", GripHand.HandColor); _skin.SetFloat("_Metallic", 0f); _skin.SetFloat("_Glossiness", 0.3f);

            var light = New("Light", LSubject).AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.1f; light.shadows = LightShadows.None;
            light.cullingMask = ~0;
            light.transform.rotation = Quaternion.LookRotation(new Vector3(-0.35f, -0.7f, 0.45f));
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.58f);
            RenderSettings.skybox = null;

            // World-fixed grids behind each ortho view, on their own layers so no view sees another's backdrop.
            Quad(new Vector3(-0.4f, 0f, 0f), Vector3.right, 2f, _grid, LSide);    // behind the right-side view
            Quad(new Vector3(0.4f, 0f, 0f), Vector3.left, 2f, _grid, LLeft);      // behind the left-side view
            Quad(new Vector3(0f, -0.4f, 0f), Vector3.up, 2f, _grid, LTop);        // under the top view
            foreach (var (l, x) in new[] { (LSide, -0.399f), (LLeft, 0.399f) })
            {
                Bar(new Vector3(x, 0f, 0f), new Vector3(0.001f, 0.0012f, 2f), l);  // y = 0
                Bar(new Vector3(x, 0f, 0f), new Vector3(0.001f, 2f, 0.0012f), l);  // z = 0
            }
            Bar(new Vector3(0f, -0.399f, 0f), new Vector3(0.0012f, 0.001f, 2f), LTop);
            Bar(new Vector3(0f, -0.399f, 0f), new Vector3(2f, 0.001f, 0.0012f), LTop);

            _cam = New("Cam", LSubject).AddComponent<Camera>();
            _cam.enabled = false; _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = new Color(0.22f, 0.22f, 0.25f);
            _cam.nearClipPlane = 0.01f; _cam.farClipPlane = 5f;
        }

        Material Mat(string shader, Color c)
        {
            var s = Shader.Find(shader); if (s == null) throw new Exception("[HIS] shader " + shader);
            var m = new Material(s); m.color = c; if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c); _trash.Add(m); return m;
        }

        static void MakeTransparent(Material m)
        {
            m.SetFloat("_Surface", 1); m.SetFloat("_Blend", 0); m.SetFloat("_ZWrite", 0);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.renderQueue = 3001; m.SetOverrideTag("RenderType", "Transparent");
        }

        GameObject New(string name, int layer) { var g = new GameObject(name) { layer = layer }; _trash.Add(g); return g; }

        void Quad(Vector3 pos, Vector3 normal, float size, Material mat, int layer)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); q.layer = layer; _trash.Add(q);
            Object.DestroyImmediate(q.GetComponent<Collider>());
            q.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-normal, Mathf.Abs(normal.y) > 0.5f ? Vector3.forward : Vector3.up));
            q.transform.localScale = new Vector3(size, size, 1f);
            q.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        void Bar(Vector3 pos, Vector3 scale, int layer)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Cube); q.layer = layer; _trash.Add(q);
            Object.DestroyImmediate(q.GetComponent<Collider>());
            q.transform.position = pos; q.transform.localScale = scale; q.GetComponent<MeshRenderer>().sharedMaterial = _red;
        }

        readonly List<GameObject> _markers = new();
        GameObject Marker(Material m, float size)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere); s.layer = LMark; _trash.Add(s);
            Object.DestroyImmediate(s.GetComponent<Collider>());
            s.transform.localScale = Vector3.one * size; s.GetComponent<MeshRenderer>().sharedMaterial = m;
            s.GetComponent<MeshRenderer>().enabled = false; _markers.Add(s);
            return s;
        }

        static Texture2D GridTexture()
        {
            const int px = 200; // one 10 cm tile: 1 cm lines, bold edge
            var t = new Texture2D(px, px, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var c = new Color32[px * px];
            for (int y = 0; y < px; y++)
                for (int x = 0; x < px; x++)
                {
                    bool bold = x < 2 || y < 2;
                    bool thin = x % (px / 10) == 0 || y % (px / 10) == 0;
                    byte v = bold ? (byte)60 : thin ? (byte)140 : (byte)200;
                    c[y * px + x] = new Color32(v, v, (byte)Mathf.Min(255, v + 8), 255);
                }
            t.SetPixels32(c); t.Apply(true);
            return t;
        }

        static void SetLayer(GameObject g, int layer) { foreach (var t in g.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer; }

        // ── Bare models in their own frame (read coordinates off the grid).
        public void RenderModel(Spec s, StringBuilder report)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(s.Model)); _trash.Add(go);
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            SetLayer(go, LSubject);
            report.AppendLine($"\n## {s.Model}");
            foreach (var r in go.GetComponentsInChildren<Renderer>())
                report.AppendLine($"  {r.name}: min {r.bounds.min.ToString("F4")} max {r.bounds.max.ToString("F4")}");
            foreach (var t in go.GetComponentsInChildren<Transform>()) if (t.GetComponent<Renderer>() == null) report.AppendLine($"  [{t.name}] at {t.position.ToString("F4")} rot {t.eulerAngles.ToString("F1")}");
            var b = Bounds(go);
            var views = new (string, Vector3, Vector3, int)[] { ("RIGHT", Vector3.left, Vector3.up, LSide), ("LEFT", Vector3.right, Vector3.up, LLeft), ("TOP", Vector3.down, Vector3.left, LTop) };
            float size = Mathf.Max(b.extents.y, b.extents.z) * 1.1f;
            // ZOOM="y,z,halfSize" (model frame, metres): a close-up instead of the whole model.
            var zoom = Environment.GetEnvironmentVariable("ZOOM");
            if (!string.IsNullOrEmpty(zoom))
            {
                var z = zoom.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                b.center = new Vector3(0f, z[0], z[1]); size = z[2];
            }
            int big = 900, W = big * 1, H = views.Length * big + Header;
            var sheet = Sheet(W, H, $"{s.Name} MODEL FRAME  1CM GRID  RED = AXES");
            var rt = new RenderTexture(big, big, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 }; _trash.Add(rt);
            var read = new Texture2D(big, big, TextureFormat.RGBA32, false); _trash.Add(read);
            for (int v = 0; v < views.Length; v++)
            {
                var (label, fwd, up, layer) = views[v];
                _cam.orthographic = true; _cam.orthographicSize = size;
                _cam.transform.SetPositionAndRotation(b.center - fwd * 1f, Quaternion.LookRotation(fwd, up));
                _cam.cullingMask = (1 << LSubject) | (1 << layer);
                Blit(sheet, W, H, 0, v, big, rt, read, label, null);
            }
            Save(sheet, W, H, "models_" + s.Name);
        }

        // ── A DA/SA gun's action, state by state (the transforms Firearm drives, posed from its own settings).
        public void RenderAction(Spec s, StringBuilder report)
        {
            var w = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(s.Prefab)); _trash.Add(w);
            PrefabUtility.UnpackPrefabInstance(w, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            SetLayer(w, LSubject);
            var gun = w.GetComponent<Firearm>(); var wg = w.GetComponent<WeaponGrip>();
            if (gun == null || !gun.DoubleAction || gun.Hammer == null) { report.AppendLine($"\n## {s.Name}: not a DA/SA gun"); return; }
            var model = w.transform.GetChild(0);
            Transform slide = gun.Slide, hammer = gun.Hammer, dec = gun.Decocker, cat = gun.SlideCatch;
            Vector3 slideRest = slide.localPosition; Quaternion hRest = hammer.localRotation;
            Quaternion dRest = dec != null ? dec.localRotation : Quaternion.identity, cRest = cat != null ? cat.localRotation : Quaternion.identity;
            // (label, slide back 0..1, hammer 0..1, trigger 0..1, decocker down, catch up)
            var states = new (string, float, float, float, bool, bool)[]
            {
                ("DECOCKED  DA READY", 0f, 0f, 0f, false, false),
                ("DA HALF PULL", 0f, 0.5f / 0.85f, 0.5f, false, false),
                ("DA BREAK 0.85", 0f, 1f, 0.85f, false, false),
                ("FIRED  SLIDE BACK", 1f, 1f, 0.85f, false, false),
                ("SA READY  COCKED", 0f, 1f, 0.4f, false, false),
                ("SA BREAK 0.5", 0f, 1f, 0.5f, false, false),
                ("EMPTY  SLIDE LOCKED", 1f, 1f, 0.4f, false, true),
                ("DECOCKER DOWN", 0f, 0f, 0f, true, false),
            };
            var b = Bounds(w);
            var hp = hammer.position;
            var rows = new (string label, Vector3 center, Vector3 fwd, int layer, float size)[]
            {
                ("RIGHT  ACTION CLOSE-UP", new Vector3(b.center.x, hp.y + 0.008f, hp.z + 0.008f), Vector3.left, LSide, 0.04f),
                ("RIGHT", b.center, Vector3.left, LSide, 0.12f),
                ("LEFT", b.center, Vector3.right, LLeft, 0.12f),
            };
            int W = states.Length * Cell, H = rows.Length * Cell + Header;
            var sheet = Sheet(W, H, $"{s.Name} ACTION  HAMMER {gun.HammerCockedDegrees:0} DEG  SLIDE {gun.SlideTravel * 1000f:0} MM  1CM GRID");
            report.AppendLine($"\n## {s.Name} action: hammer pin {model.InverseTransformPoint(hp).ToString("F4")}");
            _cam.targetTexture = _rt; _cam.Render(); _cam.targetTexture = null;
            for (int c = 0; c < states.Length; c++)
            {
                var (label, back, h, trig, down, up) = states[c];
                slide.localPosition = slideRest + new Vector3(0f, 0f, -gun.SlideTravel * back);
                hammer.localRotation = hRest * Quaternion.Euler(gun.HammerCockedDegrees * h, 0f, 0f);
                if (dec != null) dec.localRotation = dRest * Quaternion.Euler(down ? gun.DecockerDegrees : 0f, 0f, 0f);
                if (cat != null) cat.localRotation = cRest * Quaternion.Euler(up ? gun.SlideCatchDegrees : 0f, 0f, 0f);
                if (wg != null) wg.SetTrigger(trig);
                for (int r = 0; r < rows.Length; r++)
                {
                    var rw = rows[r];
                    _cam.orthographic = true; _cam.orthographicSize = rw.size;
                    _cam.transform.SetPositionAndRotation(rw.center - rw.fwd * 1f, Quaternion.LookRotation(rw.fwd, Vector3.up));
                    _cam.cullingMask = (1 << LSubject) | (1 << rw.layer);
                    Blit(sheet, W, H, c, r, Cell, _rt, _read, r == 0 ? label : rw.label, null);
                }
                // Hammer spur tip (highest-rearmost vertex) for the log: how far it swings back.
                var spur = Vector3.zero; float best = float.MinValue;
                foreach (var mf in hammer.GetComponentsInChildren<MeshFilter>())
                    foreach (var v in mf.sharedMesh.vertices) { var p = model.InverseTransformPoint(mf.transform.TransformPoint(v)); if (p.y - p.z > best) { best = p.y - p.z; spur = p; } }
                report.AppendLine($"  {label}: slide {slide.localPosition.z - slideRest.z:F3} hammer spur {spur.ToString("F4")}");
            }
            Save(sheet, W, H, "action_" + s.Name);
        }

        // ── A rifle's action, state by state (the transforms Firearm drives, posed from its own settings), plus the sight
        // picture: the camera on the red dot's axis 8 cm behind the glass, then 6–7 mm off it (the collimated dot must stay on
        // the yellow marker on the bore line at the 15 m zero: no parallax), and 3 cm off (outside the 23 mm window: no dot,
        // like the real sight). False = not a rifle (no dust cover / charging handle).
        public bool RenderRifleAction(Spec s, StringBuilder report)
        {
            var w = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(s.Prefab)); _trash.Add(w);
            PrefabUtility.UnpackPrefabInstance(w, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var gun = w.GetComponent<Firearm>(); var wg = w.GetComponent<WeaponGrip>();
            if (gun == null || (gun.DustCover == null && gun.ChargingHandle == null)) { Object.DestroyImmediate(w); _trash.Remove(w); return false; }
            SetLayer(w, LSubject);
            var model = w.transform.GetChild(0);
            var mo = gun.Motion;
            Transform bolt = gun.Slide, cover = gun.DustCover, handle = gun.ChargingHandle, mag = model.Find("Parts/Magazine");
            Vector3 boltRest = bolt.localPosition, handleRest = handle != null ? handle.localPosition : Vector3.zero, magRest = mag != null ? mag.localPosition : Vector3.zero;
            Quaternion coverShut = cover != null ? cover.localRotation : Quaternion.identity;
            // (label, bolt 0..1, cover open 0..1, charging handle 0..1, magazine out (m; − = pushed past its seat), trigger 0..1)
            var states = new (string, float, float, float, float, float)[]
            {
                ("READY  COVER SHUT", 0f, 0f, 0f, 0f, 0f),
                ("TRIGGER PULLED", 0f, 0f, 0f, 0f, 1f),
                ("FIRED  BOLT BACK", 1f, 1f, 0f, 0f, 1f),
                ("BOLT HOME  COVER OPEN", 0f, 1f, 0f, 0f, 0f),
                ("EMPTY  LOCKED BACK", 1f, 1f, 0f, 0f, 0f),
                ($"MAG OUT {mo.MagOutTravel * 1000f:0} MM", 1f, 1f, 0f, mo.MagOutTravel, 0f),
                ("FRESH MAG RISING", 1f, 1f, 0f, mo.MagInTravel * 0.5f, 0f),
                ($"SEAT +{mo.SeatOvershoot * 1000f:0} MM", 1f, 1f, 0f, -mo.SeatOvershoot, 0f),
                ("CHARGING HANDLE BACK", 1f, 1f, 1f, 0f, 0f),
            };
            var port = model.Find("Parts/Frame/EjectionPort");
            var b = Bounds(w);
            var rows = new (string label, Vector3 center, Vector3 fwd, int layer, float size)[]
            {
                ("RIGHT  PORT CLOSE-UP", port.position + new Vector3(0f, 0.005f, 0f), Vector3.left, LSide, 0.05f),
                ("RIGHT", b.center, Vector3.left, LSide, 0.4f),
                ("LEFT  RECEIVER", port.position + new Vector3(0f, -0.01f, -0.03f), Vector3.right, LLeft, 0.11f),
            };
            int W = states.Length * Cell, H = (rows.Length + 1) * Cell + Header;
            var sheet = Sheet(W, H, $"{s.Name} ACTION  BOLT {gun.SlideTravel * 1000f:0} MM  COVER {gun.DustCoverOpenDegrees:0} DEG  HANDLE {gun.ChargingHandleTravel * 1000f:0} MM  1CM GRID  ROW 4 SIGHT PICTURE");
            _cam.targetTexture = _rt; _cam.Render(); _cam.targetTexture = null;
            for (int c = 0; c < states.Length; c++)
            {
                var (label, bk, cv, ch, mout, trig) = states[c];
                float boltBack = Mathf.Max(bk, ch);
                bolt.localPosition = boltRest + new Vector3(0f, 0f, -gun.SlideTravel * boltBack);
                if (cover != null) cover.localRotation = coverShut * Quaternion.Euler(0f, 0f, gun.DustCoverOpenDegrees * cv);
                if (handle != null) handle.localPosition = handleRest + new Vector3(0f, 0f, -gun.ChargingHandleTravel * ch);
                if (mag != null) mag.localPosition = magRest + mag.localRotation * Vector3.down * mout;
                if (wg != null) wg.SetTrigger(trig);
                for (int r = 0; r < rows.Length; r++)
                {
                    var rw = rows[r];
                    _cam.orthographic = true; _cam.orthographicSize = rw.size;
                    _cam.transform.SetPositionAndRotation(rw.center - rw.fwd * 1f, Quaternion.LookRotation(rw.fwd, Vector3.up));
                    _cam.cullingMask = (1 << LSubject) | (1 << rw.layer);
                    Blit(sheet, W, H, c, r, Cell, _rt, _read, r == 0 ? label : rw.label, null);
                }
                report.AppendLine($"  {label}: bolt {bolt.localPosition.z - boltRest.z:F3} cover {(cover != null ? cover.localEulerAngles.z : 0f):F1}° handle {(handle != null ? handle.localPosition.z - handleRest.z : 0f):F3} mag {(mag != null ? mag.localPosition.y - magRest.y : 0f):F3}");
            }
            bolt.localPosition = boltRest; if (cover != null) cover.localRotation = coverShut; if (handle != null) handle.localPosition = handleRest; if (mag != null) mag.localPosition = magRest;

            // Sight picture (row 4): eye on the dot's axis 8 cm behind the glass, then off it, wide and narrow, with a yellow marker
            // on the bore line at the zero range and a grid board behind it.
            var glass = model.Find("Parts/Frame/RedDot/Glass");
            var muzzle = gun.Muzzle;
            if (glass != null && muzzle != null)
            {
                var zeroDir = glass.GetComponent<MeshRenderer>().sharedMaterial.GetVector("_ZeroDir");
                var zero = glass.TransformDirection(new Vector3(zeroDir.x, zeroDir.y, zeroDir.z)).normalized;
                var target = muzzle.position + muzzle.forward * 15f;
                var mk = Marker(_yellow, 0.06f); mk.transform.position = target;
                var board = GameObject.CreatePrimitive(PrimitiveType.Quad); _trash.Add(board); Object.DestroyImmediate(board.GetComponent<Collider>());
                board.layer = LSubject; board.transform.SetPositionAndRotation(target + muzzle.forward * 0.05f, Quaternion.LookRotation(muzzle.forward));
                board.transform.localScale = new Vector3(3f, 3f, 1f); board.GetComponent<MeshRenderer>().sharedMaterial = _grid;
                var eyes = new (string, Vector3, float)[]
                {
                    ("EYE ON AXIS  FOV 30", Vector3.zero, 30f), ("EYE ON AXIS  FOV 6", Vector3.zero, 6f),
                    ("EYE 7MM LEFT  FOV 6", new Vector3(-0.007f, 0f, 0f), 6f), ("EYE 7MM RIGHT  FOV 6", new Vector3(0.007f, 0f, 0f), 6f),
                    ("EYE 6MM HIGH  FOV 6", new Vector3(0f, 0.006f, 0f), 6f), ("EYE 6MM LOW  FOV 6", new Vector3(0f, -0.006f, 0f), 6f),
                    ("EYE 3CM LEFT  FOV 30", new Vector3(-0.03f, 0f, 0f), 30f),
                };
                for (int c = 0; c < eyes.Length && c < states.Length; c++)
                {
                    var (label, offset, fov) = eyes[c];
                    var eye = glass.position - zero * 0.08f + glass.TransformDirection(offset);
                    // Looking where the gun points from that eye: at the 15 m marker (off-axis eyes see the dot shift on the glass, not on the target).
                    _cam.orthographic = false; _cam.fieldOfView = fov; _cam.farClipPlane = 40f;
                    _cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target - eye, Vector3.up));
                    _cam.cullingMask = 1 << LSubject;
                    Blit(sheet, W, H, c, rows.Length, Cell, _rt, _read, label, null);
                }
                _cam.farClipPlane = 5f;
                mk.SetActive(false);
                report.AppendLine($"  sight: zero {zero.ToString("F4")} vs bore {muzzle.forward.ToString("F4")} ({Vector3.Angle(zero, muzzle.forward) * Mathf.Deg2Rad * 1000f:F2} mrad); glass {model.InverseTransformPoint(glass.position).ToString("F4")}");
            }
            Save(sheet, W, H, "action_" + s.Name);
            return true;
        }

        // ── The hand alone in its rig frame, posed with this weapon's grip shape (to pick the web point).
        public void RenderRig(Spec s, StringBuilder report)
        {
            if (Environment.GetEnvironmentVariable("ATLAS") == "1") { ThumbAtlas(); return; }
            var hand = HandInstance(false);
            var rig = new HandRig(hand.go, false);
            rig.Apply(GripPoses.Get(s.Kind).Grip);
            Bake(hand);
            Marker(_red, 0.005f).transform.position = rig.Wrist.TransformPoint(rig.WebLocal);
            var views = new (string, Vector3, Vector3, int)[] { ("FROM +Z THUMB SIDE", Vector3.back, Vector3.up, -1), ("FROM +Y BACK", Vector3.down, Vector3.forward, -1), ("FROM -Y PALM", Vector3.up, Vector3.forward, -1), ("FROM +X FINGERS", Vector3.left, Vector3.up, -1) };
            int big = 600, W = big * 2, H = 2 * big + Header;
            var sheet = Sheet(W, H, $"RIG {s.Name} GRIP SHAPE  RED=WEBLOCAL {rig.WebLocal.ToString("F3")}  GRID 1CM AT ORIGIN PLANES");
            var rt = new RenderTexture(big, big, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 }; _trash.Add(rt);
            var read = new Texture2D(big, big, TextureFormat.RGBA32, false); _trash.Add(read);
            var center = new Vector3(0.07f, -0.01f, 0.01f);
            for (int v = 0; v < views.Length; v++)
            {
                var (label, fwd, up, _) = views[v];
                _cam.orthographic = true; _cam.orthographicSize = 0.08f;
                _cam.transform.SetPositionAndRotation(center - fwd * 1f, Quaternion.LookRotation(fwd, up));
                _cam.cullingMask = 1 << LSubject;
                Blit(sheet, W, H, v % 2, v / 2, big, rt, read, label, null);
            }
            Save(sheet, W, H, "rig_" + s.Name);
        }

        // Which way each thumb parameter bends it: ±40° per field, seen from the fingertips and from the palm.
        void ThumbAtlas()
        {
            var hand = HandInstance(false);
            var rig = new HandRig(hand.go, false);
            var vars = new (string, FingerPose)[]
            {
                ("REST", default), ("MCP+40", new FingerPose(40, 0, 0)), ("MCP-40", new FingerPose(-40, 0, 0)), ("SPREAD+40", new FingerPose(0, 0, 0, 40)),
                ("SPREAD-40", new FingerPose(0, 0, 0, -40)), ("PIP+40", new FingerPose(0, 40, 0)), ("DIP+40", new FingerPose(0, 0, 40)), ("TWIST+40", new FingerPose(0, 0, 0, 0, 40)),
            };
            int c = 300, W = c * vars.Length, H = 2 * c + Header;
            var sheet = Sheet(W, H, "THUMB ATLAS  ROW1 FROM +X FINGERTIPS (RIGHT=+Z THUMB SIDE, UP=+Y BACK)  ROW2 FROM -Y PALM (LEFT=+X FINGERS, UP=+Z)");
            var rt = new RenderTexture(c, c, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 }; _trash.Add(rt);
            var read = new Texture2D(c, c, TextureFormat.RGBA32, false); _trash.Add(read);
            var center = new Vector3(0.07f, -0.01f, 0.02f);
            for (int i = 0; i < vars.Length; i++)
            {
                rig.Apply(new HandShape { Thumb = vars[i].Item2 });
                Bake(hand);
                for (int r = 0; r < 2; r++)
                {
                    var fwd = r == 0 ? Vector3.left : Vector3.up; var up = r == 0 ? Vector3.up : Vector3.forward;
                    _cam.orthographic = true; _cam.orthographicSize = 0.09f;
                    _cam.transform.SetPositionAndRotation(center - fwd, Quaternion.LookRotation(fwd, up));
                    _cam.cullingMask = 1 << LSubject;
                    Blit(sheet, W, H, i, r, c, rt, read, vars[i].Item1, null);
                }
            }
            Save(sheet, W, H, "rig_thumb_atlas");
        }

        // ── Held by the virtual hand at the controller's pose.
        public void RenderHeld(Spec s, StringBuilder report)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(s.Prefab);
            var w = (GameObject)PrefabUtility.InstantiatePrefab(prefab); _trash.Add(w);
            PrefabUtility.UnpackPrefabInstance(w, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            SetLayer(w, LSubject);
            var grab = w.GetComponent<Grabbable>();
            var wg = w.GetComponent<WeaponGrip>();
            var frame = wg != null && wg.Frame != null ? wg.Frame : w.transform.Find("Model");
            var pose = GripPoses.Get(s.Kind);

            // The tracked controller at the origin; its AimAnchor as HandController builds it (web point, pitched, trims 0).
            var ctl = New("Controller", LSubject).transform;
            var aim = New("AimAnchor", LSubject).transform; aim.SetParent(ctl, false);
            HandController.ConfigureAimAnchor(aim, -20f, false, Vector3.zero);
            // Grabbable.OnGrab: the weapon's GripPoint lands on the anchor.
            var gripLocalRot = Quaternion.Inverse(w.transform.rotation) * grab.GripPoint.rotation;
            var gripLocalPos = w.transform.InverseTransformPoint(grab.GripPoint.position);
            w.transform.rotation = aim.rotation * Quaternion.Inverse(gripLocalRot);
            w.transform.position = aim.position - w.transform.rotation * gripLocalPos;

            var ghost = Ghost(false); ghost.transform.SetParent(ctl, false);

            var hand = HandInstance(false);
            var rig = new HandRig(hand.go, false);
            var web = frame.InverseTransformPoint(grab.GripPoint.position);
            rig.Apply(pose.Grip);
            rig.Place(frame, web, pose.Rotation, false, pose.Shift);

            // Support hand (shotgun fore-end), the left hand as Brett uses it.
            HandRig support = null; (GameObject go, SkinnedMeshRenderer smr, Mesh baked, Transform bakedT) sup = default;
            GameObject supGhost = null;
            if (wg != null && wg.SupportFrame != null && wg.SupportPose != null)
            {
                sup = HandInstance(true); support = new HandRig(sup.go, true);
                support.Apply(wg.SupportPose.Grip);
                support.Place(wg.SupportFrame, wg.SupportPose.WebAt, wg.SupportPose.Rotation, false, wg.SupportPose.Shift);
                // Where the left controller would be for that hand: web point → controller's web point, same orientation relation as the right.
                supGhost = Ghost(true);
            }


            if (Environment.GetEnvironmentVariable("SOLVE") == "1")
            {
                Solve(s, pose, rig, hand, frame, web, w, wg, false, report);
                rig.Apply(pose.Grip); rig.Place(frame, web, pose.Rotation, false, pose.Shift);
                if (support != null)
                {
                    Solve(s, wg.SupportPose, support, sup, wg.SupportFrame, wg.SupportPose.WebAt, w, wg, true, report);
                    support.Apply(wg.SupportPose.Grip); support.Place(wg.SupportFrame, wg.SupportPose.WebAt, wg.SupportPose.Rotation, false, wg.SupportPose.Shift);
                }
            }

            var webMark = Marker(_red, 0.006f); var ctlMark = Marker(_green, 0.006f); var padMark = Marker(_yellow, 0.005f);
            ctlMark.transform.position = aim.position;

            var states = new List<State>();
            if (pose.TriggerFinger)
            {
                states.Add(new State { Label = "OFF TRIGGER", Onto = 0f });
                states.Add(new State { Label = "REST ON TRIGGER", Onto = 1f });
                states.Add(new State { Label = "HALF PULL", Onto = 1f, Pull = 0.5f });
                states.Add(new State { Label = "FULL PULL", Onto = 1f, Pull = 1f });
                states.Add(new State { Label = "THUMB LIFTED", Onto = 1f, ThumbLift = 1f });
            }
            else { states.Add(new State { Label = "GRIP" }); states.Add(new State { Label = "THUMB LIFTED", ThumbLift = 1f }); }
            var gunHeld = w.GetComponent<Firearm>();
            if (support != null && gunHeld != null && gunHeld.PumpAction) states.Add(new State { Label = "PUMPED BACK", Onto = 1f, Pump = 1f });

            var rows = new (string label, Action place, int layer, bool ortho)[5];
            Bounds focus = default;
            int W = states.Count * Cell, H = rows.Length * Cell + Header;
            var sheet = Sheet(W, H, $"{s.Name} HELD  R HAND  GREEN=CONTROLLER WEB  RED=HAND WEB  YELLOW=INDEX PAD  (GHOST=TOUCH PLUS)");
            report.AppendLine($"\n## {s.Name} (pose {s.Kind}; web on GripPoint {web.ToString("F4")} in {frame.name})");
            var pumpT = wg != null ? wg.SupportFrame : null; var pumpRest = pumpT != null ? pumpT.localPosition : Vector3.zero;

            _cam.targetTexture = _rt; _cam.Render(); _cam.targetTexture = null; // warm-up: first render compiles shaders
            for (int c = 0; c < states.Count; c++)
            {
                var st = states[c];
                var shape = pose.Grip;
                if (pose.TriggerFinger)
                {
                    var idx = FingerPose.Lerp(pose.IndexOff, pose.Grip.Index, st.Onto);
                    shape.Index = FingerPose.Lerp(idx, pose.IndexPull, st.Pull);
                    if (wg != null) wg.SetTrigger(st.Pull);
                }
                shape.Thumb = FingerPose.Lerp(pose.Grip.Thumb, pose.ThumbLift, st.ThumbLift);
                rig.Apply(shape);
                if (pumpT != null) { pumpT.localPosition = pumpRest + new Vector3(0, 0, -0.08f * st.Pump); }
                if (support != null)
                {
                    support.Place(wg.SupportFrame, wg.SupportPose.WebAt, wg.SupportPose.Rotation, false, wg.SupportPose.Shift);
                    // Left controller ghost: the right controller's pose in right-hand space, mirrored (the left rig is the
                    // right one point-mirrored, the left controller the right one mirrored in x → rotation · 180° about X).
                    var hr = hand.go.transform;
                    var relRot = Quaternion.Inverse(hr.rotation) * ctl.rotation;
                    var relPos = hr.InverseTransformPoint(ctl.position);
                    var sr = sup.go.transform;
                    supGhost.transform.SetPositionAndRotation(sr.TransformPoint(-relPos), sr.rotation * relRot * Quaternion.AngleAxis(180f, Vector3.right));
                }
                Bake(hand); if (support != null) Bake(sup);
                webMark.transform.position = rig.Wrist.TransformPoint(rig.WebLocal);
                padMark.transform.position = rig.IndexPad.position;
                string inside = Inside(hand, rig, w);
                string supInside = support != null ? " SUP " + Inside(sup, support, w) : "";
                focus = new Bounds(aim.position + aim.forward * 0.02f + aim.up * 0.0f, Vector3.one * 0.01f);
                float o = s.Ortho;
                var fp = aim.TransformPoint(new Vector3(-0.2f, 0.26f, -0.42f));
                var fpAim = support != null ? w.transform.TransformPoint(new Vector3(0f, 0.08f, 0.05f)) : aim.TransformPoint(new Vector3(0f, 0.02f, 0.06f));
                var views = new (string label, Vector3 pos, Vector3 look, Vector3 up, int layer, bool ortho, float size)[]
                {
                    ("RIGHT SIDE", focus.center + Vector3.right, Vector3.left, Vector3.up, LSide, true, o),
                    ("LEFT SIDE", focus.center + Vector3.left, Vector3.right, Vector3.up, LLeft, true, o),
                    ("TOP", focus.center + Vector3.up, Vector3.down, Vector3.left, LTop, true, o),
                    ("BELOW FRONT", focus.center + new Vector3(0.14f, -0.2f, 0.3f), -new Vector3(0.14f, -0.2f, 0.3f), Vector3.up, -1, false, 38f),
                    ("FIRST PERSON", fp, fpAim - fp, aim.up, -1, false, support != null ? 40f : 20f),
                };
                for (int v = 0; v < views.Length; v++)
                {
                    var vw = views[v];
                    _cam.orthographic = vw.ortho;
                    if (vw.ortho) _cam.orthographicSize = vw.size; else _cam.fieldOfView = vw.size;
                    _cam.transform.SetPositionAndRotation(vw.pos, Quaternion.LookRotation(vw.look, vw.up));
                    _cam.cullingMask = (1 << LSubject) | (vw.layer >= 0 ? 1 << vw.layer : 0);
                    string lab = v == 0 ? st.Label : v == 1 ? inside + supInside : vw.label;
                    Blit(sheet, W, H, c, v, Cell, _rt, _read, v >= 2 ? vw.label : (v == 0 ? vw.label + "  " + st.Label : vw.label), v == 1 ? inside + supInside : null);
                }
                float webErr = Vector3.Distance(webMark.transform.position, aim.position) * 1000f;
                report.AppendLine($"  {st.Label}: inside {inside}{supInside} | hand web↔controller web {webErr:F1} mm | pad {rig.IndexPad.position.ToString("F3")}" +
                                  (wg != null && wg.Trigger != null ? $" | trigger {wg.Trigger.position.ToString("F3")} pad→trigger {Vector3.Distance(rig.IndexPad.position, wg.Trigger.position) * 1000f:F0} mm" : ""));
            }
            Save(sheet, W, H, "hands_" + s.Name);
        }

        (GameObject go, SkinnedMeshRenderer smr, Mesh baked, Transform bakedT) HandInstance(bool left)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(Core + (left ? "Meshes/HandTracking/OculusHand_L.fbx" : "Meshes/HandTracking/OculusHand_R.fbx"));
            var go = Object.Instantiate(src); _trash.Add(go);
            SetLayer(go, LSubject);
            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>(); smr.enabled = false;
            var baked = new Mesh { name = "BakedHand" }; _trash.Add(baked);
            var b = New("BakedHand", LSubject);
            b.AddComponent<MeshFilter>().sharedMesh = baked; b.AddComponent<MeshRenderer>().sharedMaterial = _skin;
            return (go, smr, baked, b.transform);
        }

        static void Bake((GameObject go, SkinnedMeshRenderer smr, Mesh baked, Transform bakedT) h)
        {
            h.smr.BakeMesh(h.baked, true);
            h.bakedT.SetPositionAndRotation(h.smr.transform.position, h.smr.transform.rotation);
            h.baked.RecalculateBounds();
        }

        GameObject Ghost(bool left)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(Core + "Prefabs/OVRControllerPrefab.prefab");
            var go = Object.Instantiate(src); _trash.Add(go);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
            var keep = go.transform.Find(left ? "MetaQuestTouchPlus_Left" : "MetaQuestTouchPlus_Right");
            foreach (Transform ch in go.transform) if (ch != keep) ch.gameObject.SetActive(false);
            keep.gameObject.SetActive(true);
            foreach (var r in keep.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer sk)
                {
                    var m = new Mesh(); _trash.Add(m); sk.BakeMesh(m, true);
                    var g = new GameObject(r.name + "_baked"); g.transform.SetParent(sk.transform, false);
                    g.AddComponent<MeshFilter>().sharedMesh = m; g.AddComponent<MeshRenderer>().sharedMaterial = _ghost;
                    sk.enabled = false;
                }
                else r.sharedMaterial = _ghost;
            }
            foreach (var r in keep.GetComponentsInChildren<Renderer>(true)) if (r.name.Contains("battery")) r.enabled = false;
            SetLayer(go, LSubject);
            return go;
        }

        // Hand vertices inside the weapon: a vertex is inside when rays along ±X, ±Y and ±Z cross the weapon's triangles an odd
        // number of times in the majority of those three axes (game meshes aren't always closed, so one ray can lie). Per finger
        // (dominant bone): count and deepest (nearest crossing along the six rays).
        string Inside((GameObject go, SkinnedMeshRenderer smr, Mesh baked, Transform bakedT) h, HandRig rig, GameObject weapon)
        {
            var tris = new List<Vector3>();
            foreach (var mf in weapon.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.GetComponent<Renderer>().enabled || !mf.gameObject.activeInHierarchy) continue;
                var v = mf.sharedMesh.vertices; var t = mf.sharedMesh.triangles; var m = mf.transform.localToWorldMatrix;
                for (int i = 0; i < t.Length; i++) tris.Add(m.MultiplyPoint3x4(v[t[i]]));
            }
            var wb = new Bounds(tris[0], Vector3.zero); foreach (var p in tris) wb.Encapsulate(p);
            var verts = h.baked.vertices; var bw = h.smr.sharedMesh.boneWeights; var bones = h.smr.bones;
            var count = new int[7]; var depth = new float[7];
            for (int i = 0; i < verts.Length; i++)
            {
                var p = h.bakedT.TransformPoint(verts[i]);
                if (!wb.Contains(p)) continue;
                int odd = 0; float near = float.MaxValue;
                for (int ax = 0; ax < 3; ax++)
                {
                    int n = 0;
                    for (int k = 0; k < tris.Count; k += 3)
                        if (Cross(p, ax, tris[k], tris[k + 1], tris[k + 2], out float d)) { if (d > 0f) n++; near = Mathf.Min(near, Mathf.Abs(d)); }
                    if ((n & 1) == 1) odd++;
                }
                if (odd < 2) continue;
                int f = FingerOf(bones[bw[i].boneIndex0].name);
                count[f]++; depth[f] = Mathf.Max(depth[f], near);
            }
            var sb = new StringBuilder();
            string[] names = { "T", "I", "M", "R", "P", "W", "B" };
            for (int f = 0; f < 7; f++) if (count[f] > 0) sb.Append($"{names[f]}{count[f]}/{depth[f] * 1000f:F0}MM ");
            return sb.Length == 0 ? "CLEAR" : sb.ToString().Trim();
        }

        // ── Solver (SOLVE=1): turn the hand so the palm clears the grip, curl each wrapping finger as far as it goes without
        // sinking in (≤ 1.5 mm), put the index pad on the trigger face (rest / full pull) and on its off-trigger spot, and the
        // thumb pad on its spot, then print the numbers as C# for GripPoses. The render then shows the solved pose.
        sealed class Tri
        {
            public float[] T; public Bounds B;
            // Per ray axis, a 2-D grid over the other two axes: each cell lists the triangles (offsets into T) whose projected
            // bounding box overlaps it. A ray along that axis can only cross those, so the parity test is exact and skips the
            // rest — the AR-15's 17k triangles made the plain loop too slow to finish a solve (2026-10-04).
            const float CellSize = 0.004f;
            readonly int[][][] _cells = new int[3][][];
            readonly int[] _nu = new int[3], _nw = new int[3];
            static readonly int[] Empty = new int[0];

            public Tri(GameObject weapon, Transform skip)
            {
                var l = new List<float>(); bool first = true;
                foreach (var mf in weapon.GetComponentsInChildren<MeshFilter>())
                {
                    if (skip != null && mf.transform.IsChildOf(skip)) continue;
                    var v = mf.sharedMesh.vertices; var t = mf.sharedMesh.triangles; var m = mf.transform.localToWorldMatrix;
                    foreach (var i in t)
                    {
                        var p = m.MultiplyPoint3x4(v[i]); l.Add(p.x); l.Add(p.y); l.Add(p.z);
                        if (first) { B = new Bounds(p, Vector3.zero); first = false; } else B.Encapsulate(p);
                    }
                }
                T = l.ToArray(); B.Expand(0.01f);
                for (int ax = 0; ax < 3; ax++)
                {
                    int u = (ax + 1) % 3, w = (ax + 2) % 3;
                    int nu = _nu[ax] = Mathf.Max(1, Mathf.CeilToInt(B.size[u] / CellSize)), nw = _nw[ax] = Mathf.Max(1, Mathf.CeilToInt(B.size[w] / CellSize));
                    var lists = new List<int>[nu * nw];
                    for (int k = 0; k < T.Length; k += 9)
                    {
                        float u0 = Mathf.Min(T[k + u], Mathf.Min(T[k + 3 + u], T[k + 6 + u])), u1 = Mathf.Max(T[k + u], Mathf.Max(T[k + 3 + u], T[k + 6 + u]));
                        float w0 = Mathf.Min(T[k + w], Mathf.Min(T[k + 3 + w], T[k + 6 + w])), w1 = Mathf.Max(T[k + w], Mathf.Max(T[k + 3 + w], T[k + 6 + w]));
                        int a0 = CellU(ax, u0), a1 = CellU(ax, u1), b0 = CellW(ax, w0), b1 = CellW(ax, w1);
                        for (int a = a0; a <= a1; a++) for (int b = b0; b <= b1; b++) (lists[a * nw + b] ??= new List<int>()).Add(k);
                    }
                    _cells[ax] = new int[nu * nw][];
                    for (int c = 0; c < lists.Length; c++) _cells[ax][c] = lists[c] != null ? lists[c].ToArray() : Empty;
                }
            }

            int CellU(int ax, float v) => Mathf.Clamp(Mathf.FloorToInt((v - B.min[(ax + 1) % 3]) / CellSize), 0, _nu[ax] - 1);
            int CellW(int ax, float v) => Mathf.Clamp(Mathf.FloorToInt((v - B.min[(ax + 2) % 3]) / CellSize), 0, _nw[ax] - 1);

            /// <summary>Offsets into <see cref="T"/> of the triangles a ray along axis <paramref name="ax"/> through p can cross.</summary>
            public int[] Near(int ax, Vector3 p) => _cells[ax][CellU(ax, p[(ax + 1) % 3]) * _nw[ax] + CellW(ax, p[(ax + 2) % 3])];
        }

        static int[] Cats((GameObject go, SkinnedMeshRenderer smr, Mesh baked, Transform bakedT) h)
        {
            var bw = h.smr.sharedMesh.boneWeights; var bones = h.smr.bones; var c = new int[bw.Length];
            for (int i = 0; i < c.Length; i++) c[i] = FingerOf(bones[bw[i].boneIndex0].name);
            return c;
        }

        /// <summary>Deepest penetration (m) of the hand's vertices of category <paramref name="cat"/> into the weapon, and how many.</summary>
        static float Pen((GameObject go, SkinnedMeshRenderer smr, Mesh baked, Transform bakedT) h, int[] cats, int cat, Tri tri, out int n)
        {
            h.smr.BakeMesh(h.baked, true);
            var verts = h.baked.vertices; var m = h.smr.transform.localToWorldMatrix; var T = tri.T;
            float worst = 0f; n = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                if (cats[i] != cat) continue;
                var p = m.MultiplyPoint3x4(verts[i]);
                if (!tri.B.Contains(p)) continue;
                int odd = 0; float near = float.MaxValue;
                for (int ax = 0; ax < 3; ax++)
                {
                    int cnt = 0; int u = (ax + 1) % 3, w = (ax + 2) % 3;
                    float pa = p[ax], pu = p[u], pw = p[w];
                    foreach (int k in tri.Near(ax, p))
                    {
                        float au = T[k + u] - pu, aw = T[k + w] - pw, bu = T[k + 3 + u] - pu, bw = T[k + 3 + w] - pw, cu = T[k + 6 + u] - pu, cw = T[k + 6 + w] - pw;
                        float e0 = bu * cw - bw * cu, e1 = cu * aw - cw * au, e2 = au * bw - aw * bu;
                        if ((e0 < 0f || e1 < 0f || e2 < 0f) && (e0 > 0f || e1 > 0f || e2 > 0f)) continue;
                        float sum = e0 + e1 + e2; if (sum * sum < 1e-28f) continue;
                        float d = (e0 * T[k + ax] + e1 * T[k + 3 + ax] + e2 * T[k + 6 + ax]) / sum - pa;
                        if (d > 0f) cnt++;
                        if (Mathf.Abs(d) < near) near = Mathf.Abs(d);
                    }
                    if ((cnt & 1) == 1) odd++;
                }
                if (odd >= 2) { n++; if (near > worst) worst = near; }
            }
            return worst;
        }

        static FingerPose GetF(in HandShape s, int f) => f switch { 0 => s.Thumb, 1 => s.Index, 2 => s.Middle, 3 => s.Ring, _ => s.Pinky };
        static void SetF(ref HandShape s, int f, FingerPose p) { switch (f) { case 0: s.Thumb = p; break; case 1: s.Index = p; break; case 2: s.Middle = p; break; case 3: s.Ring = p; break; default: s.Pinky = p; break; } }

        void Solve(Spec spec, GripPose pose, HandRig rig, (GameObject go, SkinnedMeshRenderer smr, Mesh baked, Transform bakedT) h, Transform frame,
                   Vector3 web, GameObject weapon, WeaponGrip wg, bool support, StringBuilder rep)
        {
            var t0 = DateTime.Now;
            var tri = new Tri(weapon, wg != null ? wg.Trigger : null);
            var cats = Cats(h);
            const float Touch = 0.0015f, Loose = 0.002f, Soft = 0.0035f;
            void Pose(in HandShape shape) { rig.Apply(shape); rig.Place(frame, web, pose.Rotation, false, pose.Shift); }

            // How close a pad can get to a target by FK alone (coarse), for scoring hand orientations.
            float CanReach(int f, Transform pad, Vector3 target)
            {
                var sh = pose.Grip; float bestD = float.MaxValue;
                for (float m = f == 0 ? -30f : 0f; m <= 80f; m += 10f) for (float q = 0f; q <= 100f; q += 10f) for (float sp = f == 0 ? -60f : -20f; sp <= (f == 0 ? 30f : 20f); sp += 10f)
                {
                    SetF(ref sh, f, new FingerPose(m, q, 0.5f * q, sp)); rig.Apply(sh);
                    bestD = Mathf.Min(bestD, Vector3.Distance(pad.position, target));
                }
                rig.Apply(pose.Grip);
                return bestD;
            }
            Vector3 Face()
            {
                // Front-most point of the trigger shoe in its lower 70 %, a millimetre ahead (the pad's skin rests on it).
                var pts = new List<Vector3>();
                foreach (var mf in wg.Trigger.GetComponentsInChildren<MeshFilter>()) foreach (var v in mf.sharedMesh.vertices) pts.Add(mf.transform.TransformPoint(v));
                float lo = pts.Min(p => Vector3.Dot(p, frame.up)), hi = pts.Max(p => Vector3.Dot(p, frame.up));
                var face = pts.Where(p => { float u = (Vector3.Dot(p, frame.up) - lo) / (hi - lo); return u >= spec.FaceBand.x && u <= spec.FaceBand.y; })
                              .OrderByDescending(p => Vector3.Dot(p, frame.forward)).First();
                return face + frame.forward * 0.001f;
            }
            bool trig = !support && pose.TriggerFinger && wg != null && wg.Trigger != null;
            var thumbAt = support ? spec.SupportThumb : spec.Thumb;
            if (support && rig.Left) thumbAt.x = -thumbAt.x;

            // 1. Orientation: palm clear of the grip, index able to reach the trigger, thumb its spot.
            var baseTweak = pose.Tweak; float best = float.MaxValue; var bestT = baseTweak; var baseShift = pose.Shift; var bestS = baseShift;
            foreach (var sy in new[] { 0f, -0.004f, -0.008f, -0.012f })
            for (int x = -15; x <= 15; x += 5) for (int y = -15; y <= 15; y += 5) for (int z = -15; z <= 15; z += 5)
            {
                pose.Tweak = baseTweak + new Vector3(x, y, z); pose.Shift = baseShift + new Vector3(0f, sy, 0f); Pose(pose.Grip);
                float d = Pen(h, cats, 5, tri, out int n) , d2 = Pen(h, cats, 6, tri, out int n2);
                // A few mm of palm / thumb-base in the grip reads as flesh pressed on it (the gun hides it); only deeper counts.
                float score = Mathf.Max(0f, d - Soft) * n + Mathf.Max(0f, d2 - Soft) * n2 + 0.00005f * (x * x + y * y + z * z);
                if (trig) score += 20f * CanReach(1, rig.IndexPad, Face());
                if (thumbAt != Vector3.zero) score += 10f * CanReach(0, rig.ThumbPad, frame.TransformPoint(thumbAt));
                score += 5f * Mathf.Abs(sy) * 0.01f;
                if (score < best) { best = score; bestT = pose.Tweak; bestS = pose.Shift; }
            }
            pose.Tweak = bestT; pose.Shift = bestS; Pose(pose.Grip);
            var shape = pose.Grip;

            // 2. Wrap: curl as far as it goes (Mcp + Pip) without sinking in; Dip follows Pip.
            var wrap = support || spec.WrapIndex ? new[] { 1, 2, 3, 4 } : new[] { 2, 3, 4 };
            foreach (int f in wrap)
            {
                var keep = GetF(shape, f); float bm = keep.Mcp, bp = keep.Pip, bs = -1f, fm = 0f, fq = 0f, fd = float.MaxValue;
                void Try(float mcp, float pip)
                {
                    if (mcp < -10f || pip < 0f || mcp > 95f || pip > 110f) return;
                    var fp = keep; fp.Mcp = mcp; fp.Pip = pip; fp.Dip = 0.6f * pip; SetF(ref shape, f, fp); rig.Apply(shape);
                    float d = Pen(h, cats, f, tri, out _);
                    if (d <= Touch && mcp + pip > bs) { bs = mcp + pip; bm = mcp; bp = pip; }
                    if (d < fd - 1e-4f || (Mathf.Abs(d - fd) <= 1e-4f && mcp + pip > fm + fq)) { fd = d; fm = mcp; fq = pip; }
                }
                for (float m = 0f; m <= 90f; m += 10f) for (float q = 0f; q <= 100f; q += 10f) Try(m, q);
                if (bs < 0f) { bm = fm; bp = fq; rep.AppendLine($"    finger {f}: sinks in {fd * 1000f:F1} mm at best"); }
                float cm = bm, cp = bp;
                for (float m = -5f; m <= 5f; m += 5f) for (float q = -5f; q <= 5f; q += 5f) Try(cm + m, cp + q);
                var sol = keep; sol.Mcp = bm; sol.Pip = bp; sol.Dip = 0.6f * bp; SetF(ref shape, f, sol);
            }
            rig.Apply(shape);

            // 3. Pads onto targets: forward kinematics over a grid, then the nearest few checked for sinking in.
            FingerPose Reach(int f, Transform pad, Vector3 target, float[] mcps, float[] pips, float[] spreads, float[] dips, float dipRatio)
            {
                var cands = new List<(float d, FingerPose p)>();
                var keep = GetF(shape, f);
                foreach (var m in mcps) foreach (var q in pips) foreach (var sp in spreads) foreach (var dd in dips)
                {
                    var fp = new FingerPose(m, q, dipRatio >= 0f ? dipRatio * q : dd, sp, keep.Twist);
                    SetF(ref shape, f, fp); rig.Apply(shape);
                    cands.Add((Vector3.Distance(pad.position, target), fp));
                }
                cands.Sort((a, b) => a.d.CompareTo(b.d));
                // The nearest few, each charged for sinking in (the pad a little further off beats a finger through the frame).
                float bestScore = float.MaxValue; var pick = cands[0]; float pickPen = 0f;
                foreach (var c in cands.Take(120))
                {
                    if (c.d > cands[0].d + 0.02f) break;
                    SetF(ref shape, f, c.p); rig.Apply(shape);
                    float pen = Pen(h, cats, f, tri, out _), bpen = f == 0 ? Pen(h, cats, 6, tri, out _) : 0f;
                    float score = c.d + 4f * Mathf.Max(0f, pen - Loose) + 2f * Mathf.Max(0f, bpen - Soft);
                    if (score < bestScore) { bestScore = score; pick = c; pickPen = Mathf.Max(pen, bpen); }
                }
                SetF(ref shape, f, pick.p); rig.Apply(shape);
                rep.AppendLine($"    {(f == 0 ? "thumb" : "index")} → target {pick.d * 1000f:F1} mm, sinks {pickPen * 1000f:F1} mm; pad at {frame.InverseTransformPoint(pad.position).ToString("F3")} (frame), target {frame.InverseTransformPoint(target).ToString("F3")}; web joint thumb1 {frame.InverseTransformPoint(rig.Joint(1).position).ToString("F3")} thumb2 {frame.InverseTransformPoint(rig.Joint(2).position).ToString("F3")} index1 {frame.InverseTransformPoint(rig.Joint(4).position).ToString("F3")}");
                return pick.p;
            }
            float[] R(float a, float b, float st) { var l = new List<float>(); for (float v = a; v <= b + 1e-3f; v += st) l.Add(v); return l.ToArray(); }
            var zero = new[] { 0f };
            if (trig)
            {
                var mc = R(0, 80, 4); var pc = R(0, 100, 5); var sc = R(-20, 20, 4);
                wg.SetTrigger(0f); shape.Index = Reach(1, rig.IndexPad, Face(), mc, pc, sc, zero, 0.5f); var rest = shape.Index;
                wg.SetTrigger(1f); var pull = Reach(1, rig.IndexPad, Face(), mc, pc, sc, zero, 0.5f); wg.SetTrigger(0f);
                var off = Reach(1, rig.IndexPad, frame.TransformPoint(spec.IndexOff), R(-10, 40, 4), R(0, 40, 5), R(-10, 30, 4), zero, 0.3f);
                shape.Index = rest; pose.IndexPull = pull; pose.IndexOff = off;
            }
            if (thumbAt != Vector3.zero)
            {
                shape.Thumb = Reach(0, rig.ThumbPad, frame.TransformPoint(thumbAt), R(-30, 60, 10), R(0, 60, 10), R(-60, 30, 10), R(0, 40, 10), -1f);
                if (!support && pose.TriggerFinger)
                {
                    // Lifted: off the frame by 1.5 cm or more, sinking in least.
                    rig.Apply(shape); var restPad = rig.ThumbPad.position; float bestL = float.MaxValue; var lift = shape.Thumb;
                    foreach (var dm in new[] { -20f, -10f, 0f, 10f }) foreach (var ds in new[] { -35f, -20f, 20f, 35f })
                    {
                        var t = shape.Thumb; t.Mcp += dm; t.Spread += ds; t.Pip *= 0.5f; t.Dip *= 0.5f;
                        var sh = shape; sh.Thumb = t; rig.Apply(sh);
                        float moved = Vector3.Distance(rig.ThumbPad.position, restPad);
                        float sc = Pen(h, cats, 0, tri, out _) + Pen(h, cats, 6, tri, out _) + (moved < 0.015f ? 1f : 0f) + 0.0001f * Mathf.Abs(dm) + 0.0001f * Mathf.Abs(ds);
                        if (sc < bestL) { bestL = sc; lift = t; }
                    }
                    pose.ThumbLift = lift;
                }
            }
            pose.Grip = shape;
            rig.Apply(shape);
            string F(FingerPose p) => $"new FingerPose({p.Mcp:0}f, {p.Pip:0}f, {p.Dip:0}f{(p.Spread != 0 || p.Twist != 0 ? $", {p.Spread:0}f" : "")}{(p.Twist != 0 ? $", {p.Twist:0}f" : "")})";
            rep.AppendLine($"  SOLVED {(support ? "support " : "")}{spec.Name} in {(DateTime.Now - t0).TotalSeconds:F0} s:");
            rep.AppendLine($"    Tweak = new Vector3({pose.Tweak.x:0}f, {pose.Tweak.y:0}f, {pose.Tweak.z:0}f), Shift = new Vector3(0f, {pose.Shift.y:0.000}f, 0f),");
            rep.AppendLine($"    Thumb = {F(shape.Thumb)}, Index = {F(shape.Index)},");
            rep.AppendLine($"    Middle = {F(shape.Middle)}, Ring = {F(shape.Ring)}, Pinky = {F(shape.Pinky)},");
            if (!support && pose.TriggerFinger) rep.AppendLine($"    IndexOff = {F(pose.IndexOff)}, IndexPull = {F(pose.IndexPull)}, ThumbLift = {F(pose.ThumbLift)},");
        }

        // The axis line through p (ax 0 = X, 1 = Y, 2 = Z) against a triangle: signed distance along the axis to the crossing.
        static bool Cross(Vector3 p, int ax, Vector3 a, Vector3 b, Vector3 c, out float d)
        {
            d = 0f;
            int u = (ax + 1) % 3, w = (ax + 2) % 3;
            float au = a[u] - p[u], aw = a[w] - p[w], bu = b[u] - p[u], bw = b[w] - p[w], cu = c[u] - p[u], cw = c[w] - p[w];
            float e0 = bu * cw - bw * cu, e1 = cu * aw - cw * au, e2 = au * bw - aw * bu;
            if ((e0 < 0f || e1 < 0f || e2 < 0f) && (e0 > 0f || e1 > 0f || e2 > 0f)) return false;
            float sum = e0 + e1 + e2; if (Mathf.Abs(sum) < 1e-14f) return false;
            d = (e0 * a[ax] + e1 * b[ax] + e2 * c[ax]) / sum - p[ax];
            return true;
        }

        // 0 thumb (MCP on), 1–4 index…pinky phalanges, 5 palm (wrist + pinky metacarpal), 6 thumb base (trapezium + metacarpal).
        static int FingerOf(string bone) =>
            bone.EndsWith("thumb0") || bone.EndsWith("thumb1") ? 6 : bone.EndsWith("pinky0") ? 5 : bone.Contains("thumb") ? 0 : bone.Contains("index") ? 1 : bone.Contains("middle") ? 2 : bone.Contains("ring") ? 3 : bone.Contains("pinky") ? 4 : 5; // 5 = palm/wrist

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(); var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
        }

        Color32[] Sheet(int W, int H, string title)
        {
            var sheet = new Color32[W * H];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(20, 20, 24, 255);
            PoseSnapshots.Text(sheet, W, H, 4, 5, title, new Color32(255, 255, 255, 255));
            return sheet;
        }

        void Blit(Color32[] sheet, int W, int H, int col, int row, int cell, RenderTexture rt, Texture2D read, string label, string label2)
        {
            _cam.targetTexture = rt; _cam.clearFlags = CameraClearFlags.SolidColor; _cam.Render(); _cam.targetTexture = null;
            var prev = RenderTexture.active; RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, cell, cell), 0, 0); read.Apply(false);
            RenderTexture.active = prev;
            var px = read.GetPixels32();
            int x0 = col * cell, yTop = Header + row * cell;
            for (int y = 0; y < cell; y++) Array.Copy(px, y * cell, sheet, (H - 1 - (yTop + (cell - 1 - y))) * W + x0, cell);
            // Markers drawn on top (projected, never hidden inside the hand).
            foreach (var mk in _markers)
            {
                if (mk == null || !mk.activeSelf) continue;
                var sp = _cam.WorldToViewportPoint(mk.transform.position);
                if (sp.z <= 0f) continue;
                var mc = (Color32)mk.GetComponent<MeshRenderer>().sharedMaterial.color;
                int cx = x0 + Mathf.RoundToInt(sp.x * cell), cy = yTop + Mathf.RoundToInt((1f - sp.y) * cell);
                for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
                {
                    int r2 = dx * dx + dy * dy; if (r2 > 10) continue;
                    PoseSnapshots.Put(sheet, W, H, cx + dx, cy + dy, r2 > 5 ? new Color32(0, 0, 0, 255) : mc);
                }
            }
            for (int y = 0; y < cell; y++) PoseSnapshots.Put(sheet, W, H, x0, yTop + y, PoseSnapshots.Border);
            for (int x = 0; x < cell; x++) PoseSnapshots.Put(sheet, W, H, x0 + x, yTop, PoseSnapshots.Border);
            if (label != null) { PoseSnapshots.Band(sheet, W, H, x0 + 1, yTop + 1, cell - 2, 14); PoseSnapshots.Text(sheet, W, H, x0 + 4, yTop + 3, label, PoseSnapshots.Ink); }
            if (label2 != null) { PoseSnapshots.Band(sheet, W, H, x0 + 1, yTop + 15, cell - 2, 14); PoseSnapshots.Text(sheet, W, H, x0 + 4, yTop + 17, label2, label2 == "CLEAR" ? PoseSnapshots.Ink : PoseSnapshots.Warn); }
        }

        static void Save(Color32[] sheet, int W, int H, string file)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.SetPixels32(sheet); tex.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, file + ".png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        public void Dispose()
        {
            foreach (var o in _trash) if (o != null) Object.DestroyImmediate(o);
            _trash.Clear();
        }
    }
}
