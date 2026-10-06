using System.Collections.Generic;
using System.Linq;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;

/// <summary>
/// AR-15 (SpatialNeglect "AR-15 style rifle", Sketchfab, CC BY 4.0 — credit in LICENSE.md next to the FBX and in CREDITS.md;
/// the textures carry only fictional markings; never a brand name in game: "AR-15" / "Rifle"). Brett 2026-10-03: "maybe work
/// on a semi automatic ar 15 we can swap out for the shotgun ... a scope or if not a laser which ever is less heavy", then
/// "whatever will look the best but wont make the headset heat up much faster or hoter" — so a non-magnified red dot (HIS/RedDot,
/// one small disc, no second camera). Parts map and every measured point: ArtSource/AR15_SpatialNeglect/prep/parts.md.
///
/// Like the M9, the FBX ships named, separately pivoted meshes, so the builder re-parents them:
///   Model_AR15                     muzzle +Z, Y up, +X = the gun's right, metres; origin on the web seat, X = 0 on the bore
///     Parts                        identity
///       Frame                      the receivers, barrel, handguard, stock, grip (one mesh, 'ar15') + the forward assist
///         Trigger                  pivot at the blade's node origin (top of the blade in the receiver slot); +X rotation = the
///                                  shoe swings back (WeaponGrip.SetTrigger)
///         DustCover                pivot on its hinge pin, along Z; built SHUT (the model hangs it open): Firearm swings it open
///                                  about local Z on the first shot (it stays open, like the real spring-loaded cover)
///         RedDot                   housing (a few primitives, AR15_Optic.mat) + Glass (one disc, HIS/RedDot) on the top rail
///         EjectionPort             casing spawn (right side, port centre)
///       Bolt                       the bolt carrier (Firearm's "slide": back along local −Z each shot, locked back on empty)
///       ChargingHandle             the T-handle; racks back along local −Z
///       Magazine                   top-centre pivot, local +Y = the well's axis (vertical; a drop moves along −Y)
///     Muzzle                       the bore at the flash hider's face
///     GripPoint                    the web seat (M9Builder.Tang's rule, measured in the prep), aimed like every gun (−15°)
///     Handguard                    the support hand's frame: on the bore axis, mid-length of the handguard
///     StockEnd                     the butt plate's centre (the grab zone runs to it; slung stock-up over the right shoulder)
/// Dropped: acog (the red dot replaces it), gps (a wrist GPS strapped to the stock), belt (a stiff strap off the front sight),
/// bullets (only seen through the magazine window, which is baked opaque with the rounds in it).
///
/// HIS > Build AR-15 writes Assets/_Game/Models/Model_AR15.prefab + AR15_*.mat/.asset, then Weapon_AR15 (MainSceneBuilder.
/// BuildRiflePrefab: the same prefab MainSceneBuilder.Build lists as the second Long gun option). The [HIS] AR15 lines print the
/// measured values against the prep's predictions. Headless loop:
///   GRAPHICS=1 scripts/unity-batch.sh AR15Builder.BuildAndRender -hands ar15 → Logs/poses/models_ar15, action_ar15, hands_ar15.png
/// </summary>
public static class AR15Builder
{
    const string Dir = "Assets/ThirdParty/Sketchfab/AR15_SpatialNeglect/";
    const string Fbx = Dir + "AR15.fbx";
    const string Tex = Dir + "Textures/";
    const string OutDir = "Assets/_Game/Models";
    public const string ModelPath = OutDir + "/Model_AR15.prefab";
    public const string WeaponPath = "Assets/_Game/Prefabs/Weapon_AR15.prefab";

    // Scale (prep parts.md §3): this is an 11.5"-class carbine, not a 16" rifle — the fixed-size parts (buffer tube, magazine,
    // rail, gas length) all say 1.08–1.13 mm per FBX unit, so 0.746 m muzzle face → butt plate (raw import 6.7850 m). 0.80 m is
    // the most it should go if it reads short on the headset (the grip and magazine then grow 7 %).
    const float OverallLength = 0.746f;
    const float RawLengthExpect = 6.7850f;

    // The body textures read as a near-black silhouette in passthrough at their own value (linear ≈ 0.011, prep §5 look notes):
    // lifted in the material, not the textures. Furniture (polymer handguard/stock/grip/magazine) the same.
    static readonly Color BodyTint = new(1.8f, 1.8f, 1.8f, 1f);
    static readonly Color FurnitureTint = new(1.6f, 1.6f, 1.6f, 1f);
    static readonly Color OpticColor = new(0.17f, 0.17f, 0.18f, 1f);   // matte black anodised (sRGB): a touch lighter than the body
    static readonly Quaternion GripRotation = Quaternion.Euler(-15f, 0f, 0f); // the aim every gun shares (Brett-tuned on the G19)

    // FBX node names (Unity keeps them; matched with Key(): letters and digits only).
    static readonly string[] Dropped = { "acog", "gps", "belt", "bullets" };

    // Points measured in the FBX (world cm: Y up, muzzle −Z, +X = the gun's right) by prep/measure.py (measure.json).
    static readonly Vector3 WebSeatCm = new(-0.672f, -44.00f, 64.70f);    // M9Builder.Tang's rule: 9.6 mm above the backstrap apex
    static readonly Vector3 MuzzleCm = new(-0.672f, -1.829f, -381.35f);   // flash-hider cylinder centre, at its face
    static readonly Vector3 TriggerPinCm = new(-2.02f, -52.55f, 2.28f);   // the trigger node's origin (fallback if import moved it)
    static readonly Vector3 MagTopCm = new(-0.27f, -14.87f, -65.33f);     // top centre of the straight (vertical) magazine section
    static readonly Vector3 HandguardCm = new(-0.68f, -1.53f, -206.20f);  // handguard axis, mid-length (z −283 … −129)
    static readonly Vector3 RailTopCm = new(-1.23f, 28.71f, -28.60f);     // Picatinny top face; usable run z −95 … +35
    static readonly Vector3 PortCm = new(13.50f, 4.00f, -70.00f);         // ejection port centre on the right wall's outer face
    static readonly Vector3 StockEndCm = new(0f, -33.40f, 297.15f);       // butt plate centre
    // The prep's MODEL predictions (K 0.10995) for the log's cross-check.
    static readonly Vector3 PrepMuzzle = new(0f, 0.0464f, 0.4904f), PrepTrigger = new(-0.0015f, -0.0094f, 0.0686f), PrepMag = new(0.0004f, 0.0320f, 0.1430f);

    // Red dot (prep §4 "Red dot"): a micro reflex sight centred on the rail 10 cm ahead of the web, its axis 31 mm above the rail
    // (lower-1/3 co-witness: the front sight post shows just under the dot). Zeroed so the dot sits on the bore line at 15 m.
    const float SightAxisAboveRail = 0.031f;
    const float ZeroRange = 15f;
    const float DotRadiusRad = 0.003f; // ~8 px across on the Quest 3 (≈ 25 px/°): a bold, easy dot (a real one is ~2 MOA = 0.6 mrad)

    const float TriggerMaxDegrees = 11f; // the prep's 10–12° (≈ 2.2 mm of shoe travel per 10°); the R870 uses 12

    [MenuItem("HIS/Build AR-15 (model + weapon prefab)")]
    public static void BuildAll()
    {
        if (!BuildModel()) return;
        MainSceneBuilder.BuildRiflePrefab();
        AssetDatabase.SaveAssets();
        ModelPreview.Render(ModelPath);
        ModelPreview.Render(WeaponPath);
    }

    /// <summary>
    /// Just Weapon_AR15 again from the existing Model_AR15 (no model import, no renders: a sling or holster change):
    ///   scripts/unity-batch.sh AR15Builder.BuildWeaponPrefab
    /// The prefab keeps its GUID, so the scene's Long gun option needs no MainSceneBuilder.Build.
    /// </summary>
    public static void BuildWeaponPrefab()
    {
        if (MainSceneBuilder.BuildRiflePrefab() == null) { Debug.LogError("[HIS] AR15: no Model_AR15 — run HIS > Build AR-15 first"); return; }
        AssetDatabase.SaveAssets();
        Debug.Log($"[HIS] AR15: {WeaponPath} rebuilt");
    }

    /// <summary>Headless iteration loop: build, then every AR-15 render (pass -hands ar15):
    /// GRAPHICS=1 scripts/unity-batch.sh AR15Builder.BuildAndRender -hands ar15 → Logs/poses/models_ar15, action_ar15, hands_ar15.png.</summary>
    public static void BuildAndRender()
    {
        BuildAll();
        foreach (var mode in new[] { "MODELS", "ACTION", null })
        {
            if (mode != null) System.Environment.SetEnvironmentVariable(mode, "1");
            try { HandSnapshots.Render(); }
            finally { if (mode != null) System.Environment.SetEnvironmentVariable(mode, null); }
        }
    }

    // ---------------- model ----------------
    public static bool BuildModel()
    {
        if (!ConfigureImporter()) return false;
        System.IO.Directory.CreateDirectory(OutDir);
        var body = Pbr("AR15_Body", "AR15_1", true, BodyTint);           // material 'ar15 1': receivers, barrel, FSB, flash hider, small parts
        var furniture = Pbr("AR15_Furniture", "AR15_2", false, FurnitureTint); // 'ar 15 2': handguard, stock, tube, grip, magazine (no metallic map)

        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        var wrap = new GameObject("Model_AR15");
        var src = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
        PrefabUtility.UnpackPrefabInstance(src, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        src.transform.SetParent(wrap.transform, false);

        var nodes = src.GetComponentsInChildren<Transform>(true).Where(t => t != src.transform).ToList();
        Debug.Log("[HIS] AR15 nodes: " + string.Join(", ", nodes.Select(n => n.name)));
        Transform Node(string name) => nodes.FirstOrDefault(n => n != null && Key(n.name) == Key(name));
        foreach (var d in Dropped) { var t = Node(d); if (t != null) { nodes.Remove(t); Object.DestroyImmediate(t.gameObject); } else Debug.LogWarning("[HIS] AR15: part to drop not found " + d); }
        nodes = nodes.Where(n => n != null).ToList();

        Transform bodyT = Node("ar15"), coverT = Node("dust cover"), handleT = Node("ar15.005"), boltT = Node("bolt carrier"),
                  assistT = Node("forward assist"), trigT = Node("trigger"), magT = Node("magazine");
        if (new[] { bodyT, coverT, handleT, boltT, assistT, trigT, magT }.Any(t => t == null))
        { Debug.LogError("[HIS] AR15: parts missing — node names changed on import?"); Object.DestroyImmediate(wrap); return false; }

        // Materials: the body mesh has two submeshes; the metal one is the one that reaches the flash hider (the furniture stops
        // at the handguard's front, 10 cm short). Raw import: the muzzle is toward −Z.
        var bodyMesh = bodyT.GetComponent<MeshFilter>().sharedMesh;
        int metal = 0; float best = float.MaxValue;
        var bm = bodyT.localToWorldMatrix; var bverts = bodyMesh.vertices;
        for (int sub = 0; sub < bodyMesh.subMeshCount; sub++)
        {
            float minZ = bodyMesh.GetTriangles(sub).Min(i => bm.MultiplyPoint3x4(bverts[i]).z);
            if (minZ < best) { best = minZ; metal = sub; }
        }
        Debug.Log($"[HIS] AR15 body submeshes: {string.Join(", ", Enumerable.Range(0, bodyMesh.subMeshCount).Select(s => $"#{s} {bodyMesh.GetTriangles(s).Length / 3} tris"))}; " +
                  $"metal ('ar15 1', expect 5714 tris) = #{metal}");
        foreach (var r in src.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
            for (int i = 0; i < mats.Length; i++) mats[i] = r.transform == magT || (r.transform == bodyT && i != metal) ? furniture : body;
            r.sharedMaterials = mats;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
        }

        // Orientation: Y up, muzzle −Z in the FBX; Unity mirrors X on import, so yaw 180° gives muzzle +Z with +X on the gun's
        // right (as for the M9; checked from the parts: the magazine is ahead of the trigger, the port on the right).
        src.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        if (Bounds(magT).center.z < Bounds(trigT).center.z) src.transform.localRotation = Quaternion.identity;
        if (Bounds(magT).center.z < Bounds(trigT).center.z) Debug.LogError("[HIS] AR15: could not point the muzzle at +Z");

        // Scale: flash-hider face → butt plate.
        var bb = Bounds(bodyT);
        float len = bb.max.z - bb.min.z;
        float k = OverallLength / len;
        src.transform.localScale *= k;
        Debug.Log($"[HIS] AR15 raw length {len:F4} m (expect {RawLengthExpect:F4}) → scale ×{k:F5} (prep K 0.10995), overall {OverallLength} m");

        // FBX world cm → model metres: the import is (−x, y, z)·0.01 in the FBX root's frame (prep parts.md §4 "RAW").
        Vector3 M(Vector3 cm) => src.transform.TransformPoint(new Vector3(-cm.x, cm.y, cm.z) * 0.01f);
        // The web seat on the origin (its x is the bore's, so X = 0 is the bore).
        src.transform.position -= M(WebSeatCm);
        float pinErr = Vector3.Distance(M(TriggerPinCm), trigT.position) * 1000f;
        Debug.Log($"[HIS] AR15 FBX→model mapping check: the trigger node origin is {pinErr:F2} mm from its FBX point (expect < 0.5)");
        if (pinErr > 2f) Debug.LogError("[HIS] AR15: the FBX→model mapping is off — the prep's points would be misplaced");
        if (Bounds(coverT).center.x < 0.005f) Debug.LogError("[HIS] AR15: the dust cover / port are on −X — the model is mirrored (they belong on the gun's RIGHT)");

        // Muzzle: the bore at the flash hider's face (the prep's cylinder centre; the face ring's average is logged as a check).
        var bv = WorldVerts(bodyT).ToList();
        float zMax = bv.Max(v => v.z);
        var ring = bv.Where(v => v.z > zMax - 0.0015f).ToList();
        var muzzleAt = M(MuzzleCm); muzzleAt.z = zMax;
        var web = Vector3.zero;
        float webSurface = bv.Min(v => Vector3.Distance(v, web)) * 1000f;
        Vector3 guardAt = M(HandguardCm), stockAt = M(StockEndCm), portAt = M(PortCm), railAt = M(RailTopCm);

        // Groups.
        var parts = Group("Parts", wrap.transform, Vector3.zero);
        var frame = Group("Frame", parts, Vector3.zero);
        var bolt = Group("Bolt", parts, Bounds(boltT).center);
        var handle = Group("ChargingHandle", parts, Bounds(handleT).center);
        var mag = Group("Magazine", parts, M(MagTopCm));
        var trigAt = trigT.position;
        var tb = Bounds(trigT); tb.Expand(0.004f);
        if (!tb.Contains(trigAt)) { trigAt = M(TriggerPinCm); Debug.LogWarning($"[HIS] AR15: trigger node origin off the blade — using the measured point {trigAt.ToString("F4")}"); }
        var trigger = Group("Trigger", frame, trigAt);
        var hinge = Hinge(coverT);
        var cover = Group("DustCover", frame, hinge);

        foreach (var n in nodes.ToList())
        {
            if (n == null || n.GetComponent<MeshFilter>() == null) continue;
            Transform to = n == boltT ? bolt : n == handleT ? handle : n == magT ? mag : n == trigT ? trigger : n == coverT ? cover : frame;
            n.SetParent(to, true);
        }
        foreach (Transform left in src.transform.Cast<Transform>().ToList()) { Debug.LogWarning("[HIS] AR15: leftover node " + left.name); left.SetParent(frame, true); }
        Object.DestroyImmediate(src);

        // The dust cover is modelled hanging open; build it shut (swung up over the port, the way it travelled: outward).
        float shut = CoverClosedDegrees(cover);
        cover.localRotation = Quaternion.AngleAxis(shut, Vector3.forward);
        var cb = Bounds(coverT);
        var carrier = Bounds(boltT);
        Debug.Log($"[HIS] AR15 dust cover: hinge {hinge.ToString("F4")}, shut by {shut:F1}° about +Z; shut it spans x {cb.min.x:F4}…{cb.max.x:F4} " +
                  $"y {cb.min.y:F4}…{cb.max.y:F4} (the carrier: x ≤ {carrier.max.x:F4}, y {carrier.min.y:F4}…{carrier.max.y:F4})");

        var muzzle = new GameObject("Muzzle").transform; muzzle.SetParent(wrap.transform, false);
        muzzle.localPosition = muzzleAt;
        var grip = new GameObject("GripPoint").transform; grip.SetParent(wrap.transform, false);
        grip.localPosition = web; grip.localRotation = GripRotation;
        var guard = new GameObject("Handguard").transform; guard.SetParent(wrap.transform, false);
        guard.localPosition = guardAt;
        var stock = new GameObject("StockEnd").transform; stock.SetParent(wrap.transform, false);
        stock.localPosition = stockAt;
        var port = new GameObject("EjectionPort").transform; port.SetParent(frame, false);
        port.localPosition = portAt;

        var sight = BuildRedDot(frame, railAt, muzzleAt);

        var total = RendererBounds(wrap);
        Debug.Log($"[HIS] AR15 assembled: size {total.size.ToString("F4")} (prep: 0.075 × 0.271 × 0.746 incl. the sight's ≈ +0.03) " +
                  $"muzzle {muzzleAt.ToString("F4")} (prep {PrepMuzzle.ToString("F4")}; face ring average {(ring.Count > 0 ? new Vector3(ring.Average(v => v.x), ring.Average(v => v.y), zMax).ToString("F4") : "none")}) " +
                  $"bore {(muzzleAt.y - web.y) * 1000f:F1} mm above the web (prep 46.4; M9 36.7) — the web seat is {webSurface:F1} mm from the body's nearest vertex; " +
                  $"trigger pivot {trigger.localPosition.ToString("F4")} (prep {PrepTrigger.ToString("F4")}) magazine {mag.localPosition.ToString("F4")} (prep {PrepMag.ToString("F4")}) " +
                  $"handguard {guard.localPosition.ToString("F4")} stock end {stock.localPosition.ToString("F4")} port {port.localPosition.ToString("F4")} " +
                  $"bolt {bolt.localPosition.ToString("F4")} charging handle {handle.localPosition.ToString("F4")} red dot {sight.localPosition.ToString("F4")}; " +
                  $"{wrap.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh.triangles.Length / 3)} tris, " +
                  $"{wrap.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().Count()} materials");
        PrefabUtility.SaveAsPrefabAsset(wrap, ModelPath); Object.DestroyImmediate(wrap);
        return true;
    }

    /// <summary>
    /// The dust cover's hinge pin, from the cover as modelled (hanging open below the port): its knuckles curl round the pin
    /// along the cover's top edge, so the pin's centre is the middle of the top 3 mm of the part in X and Y (prep: knuckles
    /// x 12.4…15.9, y −5.7…−2.8 FBX cm → the pin ≈ (14.2, −4.3)).
    /// </summary>
    static Vector3 Hinge(Transform cover)
    {
        var v = WorldVerts(cover).ToList();
        float top = v.Max(p => p.y);
        var knuckles = v.Where(p => p.y > top - 0.003f).ToList();
        float x0 = knuckles.Min(p => p.x), x1 = knuckles.Max(p => p.x), y0 = knuckles.Min(p => p.y);
        return new Vector3((x0 + x1) * 0.5f, (y0 + top) * 0.5f, Bounds(cover).center.z);
    }

    /// <summary>
    /// How far (degrees about +Z) the DustCover group turns the cover from as-modelled (open, hanging) to shut (standing up over
    /// the port): its hinge → centroid direction swung from where it hangs to straight up, the long way round through +X
    /// (outside the receiver: the way it really swings). Measured in the group's own frame, so it reads the same before and
    /// after the group is turned — MainSceneBuilder.BuildRiflePrefab gives Firearm the opening swing as minus this.
    /// </summary>
    public static float CoverClosedDegrees(Transform coverGroup)
    {
        var mf = coverGroup.GetComponentInChildren<MeshFilter>();
        if (mf == null) return 0f;
        var toGroup = coverGroup.worldToLocalMatrix * mf.transform.localToWorldMatrix;
        var c = Vector3.zero; var v = mf.sharedMesh.vertices;
        foreach (var p in v) c += toGroup.MultiplyPoint3x4(p);
        c /= Mathf.Max(1, v.Length);
        float hangs = Mathf.Atan2(c.y, c.x) * Mathf.Rad2Deg;   // ≈ −78° (down and a little out)
        float shut = 90f - hangs;                               // ≈ +168°: up through +X (out)
        // Unity's AngleAxis about +Z turns +X toward +Y; check the half-way point is outside (+X), else go the other way.
        var mid = Quaternion.AngleAxis(shut * 0.5f, Vector3.forward) * new Vector3(c.x, c.y, 0f);
        return mid.x >= 0f ? shut : shut - 360f;
    }

    // ---------------- the red dot ----------------
    /// <summary>
    /// A micro reflex sight on the top rail (rail point = the rail's top face, centred on the bore line): a clamp hugging the rail,
    /// a riser, the round body (a short tube, its bore the window), turret caps, and one glass disc inside with HIS/RedDot. ~270
    /// triangles, two materials (the dull housing; the glass). The glass's zero: toward the bore line at <see cref="ZeroRange"/>.
    /// </summary>
    static Transform BuildRedDot(Transform frame, Vector3 rail, Vector3 muzzle)
    {
        var root = new GameObject("RedDot").transform; root.SetParent(frame, false);
        root.localPosition = new Vector3(0f, rail.y, rail.z); // on the bore's centre line (the rail centre is within 0.6 mm of it)
        float a = SightAxisAboveRail;
        const float rOut = 0.0155f, rIn = 0.0115f, half = 0.017f;
        var m = new MeshBuild();
        m.Box(new Vector3(-0.0125f, -0.0015f, -0.019f), new Vector3(0.0125f, 0.009f, 0.019f));          // clamp on the rail
        m.Box(new Vector3(0.0105f, -0.0062f, -0.019f), new Vector3(0.0125f, -0.0015f, 0.019f));         // its jaws over the rail's sides
        m.Box(new Vector3(-0.0125f, -0.0062f, -0.019f), new Vector3(-0.0105f, -0.0015f, 0.019f));
        m.Box(new Vector3(-0.0095f, 0.009f, -0.015f), new Vector3(0.0095f, a - 0.012f, 0.015f));         // riser
        m.Tube(new Vector3(0f, a, 0f), rOut, rIn, -half, half, 20);                                       // body; its bore is the window
        m.Box(new Vector3(-0.0055f, a + rOut - 0.0015f, -0.0055f), new Vector3(0.0055f, a + rOut + 0.005f, 0.0055f)); // elevation cap
        m.Box(new Vector3(rOut - 0.0015f, a - 0.0055f, -0.0055f), new Vector3(rOut + 0.005f, a + 0.0055f, 0.0055f));  // windage cap (right)
        m.Box(new Vector3(-rOut - 0.0045f, a - 0.006f, -0.004f), new Vector3(-rOut + 0.0015f, a + 0.006f, 0.009f));   // brightness knob (left)
        var housing = new GameObject("Housing"); housing.transform.SetParent(root, false);
        housing.AddComponent<MeshFilter>().sharedMesh = SaveMesh(m.ToMesh("AR15_RedDotHousing"), "AR15_RedDotHousing");
        var hr = housing.AddComponent<MeshRenderer>(); hr.sharedMaterial = OpticMaterial();
        hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; hr.receiveShadows = false;

        // The glass: a disc across the window, a little forward of the body's middle, facing the shooter (−Z).
        var g = new MeshBuild();
        g.Disc(Vector3.zero, rIn + 0.0004f, 24, Vector3.back);
        var glass = new GameObject("Glass"); glass.transform.SetParent(root, false);
        glass.transform.localPosition = new Vector3(0f, a, 0.006f);
        glass.AddComponent<MeshFilter>().sharedMesh = SaveMesh(g.ToMesh("AR15_RedDotGlass"), "AR15_RedDotGlass");
        var gr = glass.AddComponent<MeshRenderer>();
        gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; gr.receiveShadows = false;
        gr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; gr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        // Zero: the dot (at infinity along this axis) sits on the bore line at 15 m. Bullets fly straight (no drop), so closer
        // in a round lands up to the sight height (65 mm) under the dot — the real AR's offset, fine in a room.
        var o = frame.TransformPoint(root.localPosition + glass.transform.localPosition); // frame = model space here
        var target = new Vector3(0f, muzzle.y, muzzle.z + ZeroRange);
        var zero = (target - o).normalized; // glass has the model's orientation: object space = model space
        gr.sharedMaterial = DotMaterial(zero);
        Debug.Log($"[HIS] AR15 red dot: rail top y {rail.y:F4}, axis {(o.y - muzzle.y) * 1000f:F1} mm above the bore (prep 64.6), window Ø {rIn * 2000f:F0} mm; " +
                  $"zero at {ZeroRange} m: the axis tips {Mathf.Asin(-zero.y) * 1000f:F2} mrad down; dot radius {DotRadiusRad * 1000f:F1} mrad");
        return root;
    }

    static Material OpticMaterial()
    {
        var path = OutDir + "/AR15_Optic.mat";
        var shader = Shader.Find("HIS/LitPBR");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        mat.shader = shader;
        mat.SetColor("_Color", OpticColor);
        mat.SetFloat("_Metallic", 0f); mat.SetFloat("_Glossiness", 0.7f); // no maps: smoothness 0.5 × 0.7 — dull anodised aluminium
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Material DotMaterial(Vector3 zero)
    {
        var path = OutDir + "/AR15_RedDot.mat";
        var shader = Shader.Find("HIS/RedDot");
        if (shader == null) { Debug.LogError("[HIS] AR15: shader HIS/RedDot not found"); shader = Shader.Find("Universal Render Pipeline/Unlit"); }
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        mat.shader = shader;
        mat.SetVector("_ZeroDir", new Vector4(zero.x, zero.y, zero.z, 0f));
        mat.SetFloat("_DotRadius", DotRadiusRad);
        mat.renderQueue = 3000;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ---------------- import, materials ----------------
    static bool ConfigureImporter()
    {
        var imp = AssetImporter.GetAtPath(Fbx) as ModelImporter;
        if (imp == null) { Debug.LogError("[HIS] AR15: FBX not found at " + Fbx + " — copy ArtSource/AR15_SpatialNeglect/Unity/ there first"); return false; }
        bool dirty = false;
        void Set<T>(T cur, T want, System.Action<T> set) { if (!EqualityComparer<T>.Default.Equals(cur, want)) { set(want); dirty = true; } }
        Set(imp.materialImportMode, ModelImporterMaterialImportMode.None, v => imp.materialImportMode = v); // we assign AR15_*.mat
        Set(imp.importCameras, false, v => imp.importCameras = v);
        Set(imp.importLights, false, v => imp.importLights = v);
        Set(imp.importAnimation, false, v => imp.importAnimation = v);
        Set(imp.animationType, ModelImporterAnimationType.None, v => imp.animationType = v);
        Set(imp.useFileScale, true, v => imp.useFileScale = v); // cm file → the raw import is 6.785 m long; BuildModel scales it
        Set(imp.importTangents, ModelImporterTangents.CalculateMikk, v => imp.importTangents = v); // for the baked normal maps
        if (dirty) imp.SaveAndReimport();
        return true;
    }

    /// <summary>A HIS/LitPBR material from one prep texture set (prep §5): BaseColor + Normal at 2048 (the two body sets carry
    /// the markings and wear that smear at 1024), Metallic/Roughness/AO at 1024 and linear (they are data). Normals are OpenGL
    /// (+Y), no flip. ASTC on the headset (QuestProjectSetup's default format).</summary>
    static Material Pbr(string name, string set, bool hasMetallic, Color tint)
    {
        string bc = Tex + set + "_BaseColor.png", n = Tex + set + "_Normal.png", r = Tex + set + "_Roughness.png", ao = Tex + set + "_AO.png";
        string m = hasMetallic ? Tex + set + "_Metallic.png" : null;
        Texture2D T(string p, int size, bool normal, bool data)
        {
            if (p == null) return null;
            var ti = AssetImporter.GetAtPath(p) as TextureImporter;
            if (ti == null) { Debug.LogWarning("[HIS] AR15 texture missing " + p); return null; }
            bool dirty = false;
            if (ti.maxTextureSize != size) { ti.maxTextureSize = size; dirty = true; }
            if (normal && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; dirty = true; }
            if (data && ti.sRGBTexture) { ti.sRGBTexture = false; dirty = true; }
            if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; dirty = true; }
            if (dirty) ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        }
        var path = $"{OutDir}/{name}.mat";
        var shader = Shader.Find("HIS/LitPBR"); // weapons: no depth occlusion (the player's own body must not hide them)
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        mat.shader = shader;
        mat.SetTexture("_MainTex", T(bc, 2048, false, false));
        mat.SetTexture("_BumpMap", T(n, 2048, true, false));
        mat.SetTexture("_MetallicMap", T(m, 1024, false, true));   // set 2 has none: the shader's black = polymer, metallic 0
        mat.SetTexture("_RoughnessMap", T(r, 1024, false, true));
        mat.SetTexture("_OcclusionMap", T(ao, 1024, false, true)); // (the shader samples it anyway; a white default would cost the same)
        mat.SetColor("_Color", tint);
        mat.SetFloat("_Metallic", 1f); mat.SetFloat("_Glossiness", 1f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // ---------------- helpers ----------------
    static string Key(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static Transform Group(string name, Transform parent, Vector3 worldPos)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false);
        t.position = worldPos; t.rotation = Quaternion.identity;
        return t;
    }

    static IEnumerable<Vector3> WorldVerts(Transform t)
    {
        var mf = t.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) yield break;
        var m = t.localToWorldMatrix;
        foreach (var p in mf.sharedMesh.vertices) yield return m.MultiplyPoint3x4(p);
    }

    static Bounds Bounds(Transform t)
    {
        var vs = WorldVerts(t).ToList(); if (vs.Count == 0) return new Bounds(t.position, Vector3.zero);
        var b = new Bounds(vs[0], Vector3.zero); foreach (var v in vs) b.Encapsulate(v); return b;
    }

    static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true).Where(r => r.gameObject.activeInHierarchy).ToArray();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
    }

    static Mesh SaveMesh(Mesh m, string name)
    {
        var path = $"{OutDir}/{name}.asset";
        m.name = name;
        var old = AssetDatabase.LoadAssetAtPath<Mesh>(path); // overwrite in place: the asset keeps its GUID
        if (old != null) { EditorUtility.CopySerialized(m, old); old.name = name; EditorUtility.SetDirty(old); Object.DestroyImmediate(m); return old; }
        AssetDatabase.CreateAsset(m, path); return m;
    }

    /// <summary>A few-triangle mesh from boxes, a tube and a disc. Each face's winding is set from its outward normal (Unity's
    /// front faces: Cross(b − a, c − a) points toward the viewer).</summary>
    sealed class MeshBuild
    {
        readonly List<Vector3> _v = new(), _n = new();
        readonly List<int> _t = new();

        int Add(Vector3 p, Vector3 n) { _v.Add(p); _n.Add(n); return _v.Count - 1; }

        void Tri(int a, int b, int c, Vector3 n)
        {
            if (Vector3.Dot(Vector3.Cross(_v[b] - _v[a], _v[c] - _v[a]), n) < 0f) (b, c) = (c, b);
            _t.Add(a); _t.Add(b); _t.Add(c);
        }

        /// <summary>A quad a-b-c-d (in order round its edge), per-corner normals, facing <paramref name="face"/>.</summary>
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd, Vector3 face)
        {
            int i0 = Add(a, na), i1 = Add(b, nb), i2 = Add(c, nc), i3 = Add(d, nd);
            Tri(i0, i1, i2, face); Tri(i0, i2, i3, face);
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n) => Quad(a, b, c, d, n, n, n, n, n);

        public void Box(Vector3 mn, Vector3 mx)
        {
            Quad(new(mx.x, mn.y, mn.z), new(mx.x, mx.y, mn.z), new(mx.x, mx.y, mx.z), new(mx.x, mn.y, mx.z), Vector3.right);
            Quad(new(mn.x, mn.y, mn.z), new(mn.x, mx.y, mn.z), new(mn.x, mx.y, mx.z), new(mn.x, mn.y, mx.z), Vector3.left);
            Quad(new(mn.x, mx.y, mn.z), new(mx.x, mx.y, mn.z), new(mx.x, mx.y, mx.z), new(mn.x, mx.y, mx.z), Vector3.up);
            Quad(new(mn.x, mn.y, mn.z), new(mx.x, mn.y, mn.z), new(mx.x, mn.y, mx.z), new(mn.x, mn.y, mx.z), Vector3.down);
            Quad(new(mn.x, mn.y, mx.z), new(mx.x, mn.y, mx.z), new(mx.x, mx.y, mx.z), new(mn.x, mx.y, mx.z), Vector3.forward);
            Quad(new(mn.x, mn.y, mn.z), new(mx.x, mn.y, mn.z), new(mx.x, mx.y, mn.z), new(mn.x, mx.y, mn.z), Vector3.back);
        }

        /// <summary>A thick-walled tube along Z: smooth outer and inner walls, flat end rings.</summary>
        public void Tube(Vector3 c, float rOut, float rIn, float z0, float z1, int seg)
        {
            for (int i = 0; i < seg; i++)
            {
                float a0 = 2f * Mathf.PI * i / seg, a1 = 2f * Mathf.PI * (i + 1) / seg;
                var d0 = new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0f); var d1 = new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0f);
                var mid = (d0 + d1).normalized;
                Vector3 P(Vector3 d, float r, float z) => c + d * r + new Vector3(0f, 0f, z);
                Quad(P(d0, rOut, z0), P(d1, rOut, z0), P(d1, rOut, z1), P(d0, rOut, z1), d0, d1, d1, d0, mid);       // outside
                Quad(P(d0, rIn, z0), P(d1, rIn, z0), P(d1, rIn, z1), P(d0, rIn, z1), -d0, -d1, -d1, -d0, -mid);      // the window's wall
                Quad(P(d0, rIn, z1), P(d1, rIn, z1), P(d1, rOut, z1), P(d0, rOut, z1), Vector3.forward);              // front ring
                Quad(P(d0, rIn, z0), P(d1, rIn, z0), P(d1, rOut, z0), P(d0, rOut, z0), Vector3.back);                 // rear ring
            }
        }

        public void Disc(Vector3 c, float r, int seg, Vector3 n)
        {
            int centre = Add(c, n);
            var right = Vector3.Cross(n, Vector3.up).normalized; var up = Vector3.Cross(right, n).normalized;
            int first = _v.Count;
            for (int i = 0; i < seg; i++) { float a = 2f * Mathf.PI * i / seg; Add(c + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * r, n); }
            for (int i = 0; i < seg; i++) Tri(centre, first + i, first + (i + 1) % seg, n);
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(_v); m.SetNormals(_n); m.SetTriangles(_t, 0);
            m.SetUVs(0, Enumerable.Repeat(Vector2.zero, _v.Count).ToList());
            m.SetTangents(Enumerable.Repeat(new Vector4(1f, 0f, 0f, 1f), _v.Count).ToList()); // (no normal map on these: any frame)
            m.RecalculateBounds();
            return m;
        }
    }
}
