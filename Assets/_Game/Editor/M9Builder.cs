using System.Collections.Generic;
using System.Linq;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;

/// <summary>
/// M9 pistol (WPthai "Beretta M9A1 w/ Slide lock", Sketchfab, CC BY 4.0 — credit in LICENSE.md next to the FBX; brand marks
/// painted out of the textures, never call it "Beretta" in game). research/16 §1; parts map in ArtSource/M9A1_WPthai/prep/parts.md.
///
/// Unlike the G19 (one merged mesh split into islands), this FBX ships 23 named, separately pivoted meshes, so the builder only
/// re-parents them:
///   Model_M9                       muzzle +Z, Y up, +X = the gun's right side, metres; the tang sits where the G19's does
///     Parts                        identity (Firearm moves Parts/Slide along local −Z, 1:1 metres)
///       Frame                      frame, grip panels, barrel, guide rod, trigger bar, takedown lever, mag release
///         Trigger                  pivot on the trigger pin; +X rotation = shoe swings back (WeaponGrip.SetTrigger)
///         Hammer                   pivot on the hammer pin; modelled DOWN (decocked); cocking = NEGATIVE X rotation (Firearm DA/SA)
///         SlideCatch               pivot at its front pin
///       Slide                      slide + sights; moves along −Z
///         Safety                   ambidextrous safety/decocker drum, pivot on its axis (turns about X; Firearm's decocker)
///         EjectionPort             casing spawn (right side, over the chamber)
///       Magazine                   magazine + base plate + follower; local +Y = the magazine axis (drop = move along −Y)
///     Muzzle                       bore axis at the crown (the threaded stub is trimmed off: a standard M9A1 isn't threaded)
///     GripPoint                    the web point (tang): WeaponModelBuilder.Tang's rays, seated where the curve turns 45° (see Tang)
/// Accessories (suppressor, laser, red dot, XM9 slide lock) and the loose cartridge are stripped: Brett wants the bare gun;
/// they are a later variant (textures in ArtSource/M9A1_WPthai/Unity/Accessories).
///
/// HIS > Build M9 writes Assets/_Game/Models/Model_M9.prefab + M9.mat and Assets/_Game/Prefabs/Weapon_M9.prefab.
/// MainSceneBuilder lists Weapon_M9 in the Sidearm loadout slot ("M9"). The [HIS] M9 log lines print the measured values;
/// the first import (2026-09-23) matched the prep's predictions (ArtSource/M9A1_WPthai/prep/parts.md §2) to ≤ 0.3 mm:
/// raw length 0.2086 m (Unity converts the file's cm) → ×1.0402; backstrap apex 19.5 mm / web seat 8.0 mm under the slide;
/// bore 36.7 mm above the web (G19 ≈ 25); slide catch on −X (not mirrored); trigger pin (0, 0.0264, 0.0114), hammer pin
/// (0, 0.0326, −0.0522) — node origins sit on the pins, so no fallback pivots are used.
/// </summary>
public static class M9Builder
{
    const string Dir = "Assets/ThirdParty/Sketchfab/M9A1_WPthai/";
    const string Fbx = Dir + "92FS_Finish.fbx";
    const string Tex = Dir + "Textures/";
    const string OutDir = "Assets/_Game/Models";
    const string ModelPath = OutDir + "/Model_M9.prefab";
    const string WeaponPath = "Assets/_Game/Prefabs/Weapon_M9.prefab";

    // Real M9 / 92FS overall length, slide face → beavertail: 208.6 mm raw → ×1.040. That makes the barrel 127.5 mm (real 125)
    // and the height 155 mm incl. sights and base plate (spec sheets say 137 mm). Hand renders (hands_m9) show it in scale
    // with the hand; ~0.211 is the fallback if it reads big on the headset.
    const float OverallLength = 0.217f;
    const float RawOverallCm = 20.861f; // measured 20.86 on import; only used for the pivot fallbacks below

    // Put the M9's web point exactly where Model_G19's GripPoint is (as built 2026-09-23 by WeaponModelBuilder.Tang), so the
    // recoil pivot (Model origin), the hand pose and the holster pose all land where they were tuned for the Glock.
    static readonly Vector3 TangFallback = new(0f, 0.01897f, -0.06725f); // Model_G19 GripPoint as built 2026-09-23 (read live below)
    static Vector3 TangTarget
    {
        get
        {
            var g19 = AssetDatabase.LoadAssetAtPath<GameObject>(OutDir + "/Model_G19.prefab");
            var gp = g19 != null ? g19.transform.Find("GripPoint") : null;
            return gp != null ? gp.localPosition : TangFallback;
        }
    }
    static readonly Quaternion GripRotation = Quaternion.Euler(-15f, 0f, 0f); // same aim as the G19 (bore vs hand is set by this, not by the grip rake)
    // The G19 GripPoint the research/15 holster pose was tuned against (MainSceneBuilder.LegacyPistolGrip).
    static readonly Vector3 LegacyPistolGrip = new(0f, -0.02358f, -0.07368f);

    const float MagRakeDegrees = 18.5f;   // magazine axis from vertical, top toward the muzzle (SVD of Magazine_low; matches models_m9)
    const float TriggerMaxDegrees = 18f;  // M9 DA pull is long (pin → shoe ≈ 25 mm); G19 uses 20. Tune on the headset
    static readonly bool LinearMaskMaps = true;     // metallic/roughness are data: linear is correct (Brett's call 2026-09-23)

    // DA/SA (research/16 §1.5): hammer back this far when cocked (models_m9 / m9_action renders: spur clears the slide's
    // rear and lines up behind it like the real gun's), and the analog pulls that break the shot.
    const float HammerCockedDegrees = -55f;
    const float DaBreak = 0.85f, SaBreak = 0.5f, SaTriggerRest = 0.4f;
    const float DecockerDegrees = 45f;   // safety/decocker lever pushed down (+X turns its lever arm down; action_m9)
    const float SlideCatchDegrees = 6f;   // the catch's rear rises into the slide's notch when it locks back empty
    const float SfxPitch = 0.95f; // pistol.* clips a touch deeper for the heavier all-steel slide

    // Barrel and guide rod: the source's base colour is a light iridescent silver (linear ≈ 0.30, slide ≈ 0.035) that reads as chrome; the
    // real barrel is dark. Its own material multiplies it down (sRGB tint 0.5 → ×0.21 linear), a touch lighter than the slide.
    static readonly Color BarrelTint = new(0.5f, 0.5f, 0.52f);

    // FBX node names (Unity keeps them; spellings are the author's: "Catridge", "releash").
    static readonly string[] Accessories = { "Suppressor_low", "Laser Sight_low", "Red dot_low", "Slide Lock_low", "Bullet_low", "Catridge_low" };
    static readonly string[] SlideParts = { "Slide_low", "Front sight_low", "Rear sight_low" };
    static readonly string[] MagParts = { "Magazine_low", "Mag bottom_low", "Follower_low" };
    // Everything else rides the frame: Frame_low, Grip_low, Barrel_low, Guide rod_low, Trigger rod_low (trigger bar),
    // Slide releash_low (takedown lever), Mag releash_low (mag release). The barrel stays with the frame so the locked-back
    // slide shows the exposed barrel like the real gun (Brett 2026-09-23: keep).
    static readonly bool BarrelRidesSlide = false;

    // Pivot fallbacks (FBX cm, relative to the part's bounds: x from the centre line, y from bounds.min.y, z from the rear =
    // bounds.min.z after the flip). Used only if the imported node origin is not on the part (e.g. transforms got baked).
    static readonly Vector3 TriggerPinCm = new(0f, 3.133f, 0.624f);
    static readonly Vector3 HammerPinCm = new(0f, 0.833f, 1.823f);
    static readonly Vector3 SafetyAxisCm = new(0f, 0.680f, 0.654f);
    static readonly Vector3 SlideCatchPinCm = new(float.NaN, 0.491f, 3.460f); // NaN x = use the part's own centre x

    [MenuItem("HIS/Build M9 (model + weapon prefab)")]
    public static void BuildAll()
    {
        if (!BuildM9()) return;
        BuildWeaponPrefab();
        AssetDatabase.SaveAssets();
        ModelPreview.Render(ModelPath);
        ModelPreview.Render(WeaponPath);
    }

    /// <summary>Headless iteration loop: build, then every M9 render (pass -hands m9):
    /// GRAPHICS=1 scripts/unity-batch.sh M9Builder.BuildAndRender -hands m9 → Logs/poses/models_m9, action_m9, hands_m9.png.</summary>
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
    public static bool BuildM9()
    {
        if (!ConfigureImporter()) return false;
        System.IO.Directory.CreateDirectory(OutDir);
        var mat = Material();

        var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        var wrap = new GameObject("Model_M9");
        var src = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
        PrefabUtility.UnpackPrefabInstance(src, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        src.transform.SetParent(wrap.transform, false);

        var nodes = src.GetComponentsInChildren<Transform>(true).Where(t => t != src.transform).ToList();
        Debug.Log("[HIS] M9 nodes: " + string.Join(", ", nodes.Select(n => n.name)));
        Transform Node(string name) => nodes.FirstOrDefault(n => n != null && Key(n.name) == Key(name));

        foreach (var a in Accessories) { var t = Node(a); if (t != null) { nodes.Remove(t); Object.DestroyImmediate(t.gameObject); } else Debug.LogWarning("[HIS] M9: accessory not found " + a); }
        nodes = nodes.Where(n => n != null).ToList();
        foreach (var r in src.GetComponentsInChildren<MeshRenderer>(true))
        {
            r.sharedMaterials = Enumerable.Repeat(mat, Mathf.Max(1, r.sharedMaterials.Length)).ToArray();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        var slideT = Node("Slide_low"); var barrelT = Node("Barrel_low"); var gripT = Node("Grip_low"); var frameT = Node("Frame_low");
        if (slideT == null || barrelT == null || gripT == null || frameT == null) { Debug.LogError("[HIS] M9: core parts missing — node names changed on import?"); Object.DestroyImmediate(wrap); return false; }

        // Orientation. The FBX is Y-up with the muzzle toward −Z (Blender export); Unity mirrors X on import, so yaw 180° gives
        // muzzle +Z with +X on the gun's right (verified below from the parts, and flipped if an import ever disagrees).
        src.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        if (Bounds(barrelT).center.z < Bounds(gripT).center.z) src.transform.localRotation = Quaternion.identity;
        if (Bounds(barrelT).center.z < Bounds(gripT).center.z) Debug.LogError("[HIS] M9: could not point the muzzle at +Z");
        var catchT = Node("Slide catch_low");
        if (catchT != null && Bounds(catchT).center.x > 0f) Debug.LogError("[HIS] M9: slide catch is on +X — the model is mirrored (it belongs on the gun's LEFT)");

        // Scale: slide front face → rearmost point of the frame/grip (the threaded barrel stub is excluded).
        float rear = Mathf.Min(Bounds(frameT).min.z, Bounds(gripT).min.z);
        float len = Bounds(slideT).max.z - rear;
        float k = OverallLength / len;
        src.transform.localScale *= k;
        float cm = OverallLength / RawOverallCm; // metres per FBX centimetre after scaling
        Debug.Log($"[HIS] M9 raw length {len:F4} m (expect 0.2086) → scale ×{k:F4}");

        // Move the web point (tang) onto the G19's.
        var tang = Tang(frameT, Bounds(slideT).min.y, Node("Trigger_low"));
        var target = TangTarget;
        src.transform.position += target - tang;
        tang = target;

        TrimThread(barrelT, Bounds(slideT).max.z);
        var barrelMat = BarrelMaterial(mat);
        barrelT.GetComponent<MeshRenderer>().sharedMaterial = barrelMat;
        var rod = Node("Guide rod_low"); // bright steel in the source (linear ≈ 0.42); it shows under the barrel when the slide is back
        if (rod != null) rod.GetComponent<MeshRenderer>().sharedMaterial = barrelMat;

        // Groups.
        var parts = Group("Parts", wrap.transform, Vector3.zero);
        var frameG = Group("Frame", parts, Vector3.zero);
        var slideG = Group("Slide", parts, Bounds(slideT).center);
        var magT = Node("Magazine_low");
        var magG = Group("Magazine", parts, magT != null ? TopCentre(Bounds(magT)) : Vector3.zero);
        magG.localRotation = Quaternion.Euler(MagRakeDegrees, 0f, 0f);

        var trigger = PivotFor(Node("Trigger_low"), "Trigger", frameG, TriggerPinCm, cm);
        var hammer = PivotFor(Node("Hammer_low"), "Hammer", frameG, HammerPinCm, cm);
        var slideCatch = PivotFor(catchT, "SlideCatch", frameG, SlideCatchPinCm, cm);
        var safety = PivotFor(Node("Safety_low"), "Safety", slideG, SafetyAxisCm, cm);

        foreach (var n in nodes.ToList())
        {
            if (n == null || n.GetComponent<MeshFilter>() == null) continue;
            Transform to = frameG;
            if (SlideParts.Any(s => Key(s) == Key(n.name)) || (BarrelRidesSlide && n == barrelT)) to = slideG;
            else if (MagParts.Any(s => Key(s) == Key(n.name))) to = magG;
            else if (n == Node("Trigger_low") && trigger != null) to = trigger;
            else if (n == Node("Hammer_low") && hammer != null) to = hammer;
            else if (n == catchT && slideCatch != null) to = slideCatch;
            else if (n == Node("Safety_low") && safety != null) to = safety;
            n.SetParent(to, true);
        }
        foreach (Transform left in src.transform.Cast<Transform>().ToList()) { Debug.LogWarning("[HIS] M9: leftover node " + left.name); left.SetParent(frameG, true); }
        Object.DestroyImmediate(src);

        // Muzzle: the bore centre at the barrel's front face.
        var bv = WorldVerts(barrelT).ToList();
        float zMax = bv.Max(v => v.z);
        var ring = bv.Where(v => v.z > zMax - 0.002f).ToList();
        var muzzle = new GameObject("Muzzle").transform; muzzle.SetParent(wrap.transform, false);
        muzzle.localPosition = new Vector3(ring.Average(v => v.x), ring.Average(v => v.y), zMax);

        // Ejection port: right side of the open-top slide, over the chamber (barrel's rear 12 mm); GunEffects spawns the casing
        // 15 mm to its right (outside the 28 mm slide) and throws it right-up-back.
        var bb = Bounds(barrelT);
        var port = new GameObject("EjectionPort").transform; port.SetParent(slideG, false);
        port.position = new Vector3(0.006f, bb.max.y, bb.min.z + 0.012f);

        var grip = new GameObject("GripPoint").transform; grip.SetParent(wrap.transform, false);
        grip.localPosition = tang; grip.localRotation = GripRotation;

        var total = RendererBounds(wrap);
        Debug.Log($"[HIS] M9 assembled: size {total.size.ToString("F4")} (thread trimmed: expect ≈ 0.036 × 0.155 × 0.218) " +
                  $"muzzle {muzzle.localPosition.ToString("F4")} ({(muzzle.localPosition.z - Bounds(slideT).max.z) * 1000f:F1} mm past the slide face) " +
                  $"tang {tang.ToString("F4")} trigger pin {P(trigger)} hammer pin {P(hammer)} safety {P(safety)} slide catch {P(slideCatch)} " +
                  $"port {port.position.ToString("F4")} bore above web {(muzzle.localPosition.y - tang.y) * 1000f:F1} mm (G19 ≈ 25)");
        SavePrefab(wrap, ModelPath);
        return true;
    }

    /// <summary>MainSceneBuilder: Weapon_M9 rebuilt from Model_M9 (like the Glock's prefab from Model_G19), or null when the model
    /// was never built (the FBX isn't imported) — the loadout then lists the M9 as "coming soon".</summary>
    public static WeaponBase WeaponIfModelBuilt() =>
        AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath) != null ? BuildWeaponPrefab() : null;

    // ---------------- weapon prefab (mirrors MainSceneBuilder.BuildPistolPrefab) ----------------
    public static WeaponBase BuildWeaponPrefab()
    {
        var root = new GameObject("Weapon_M9");
        var rb = root.AddComponent<Rigidbody>(); rb.mass = 0.95f; // real 950–970 g empty (the Glock prefab uses 0.7)
        var grabbable = root.AddComponent<Grabbable>();
        var gun = root.AddComponent<Firearm>(); gun.DisplayName = "M9"; gun.SfxKey = "pistol"; // never "Beretta" (research/16 §1.4); pistol.* at SfxPitch
        gun.HolsterType = HolsterType.Hip;
        gun.HolsterOffsetEuler = new Vector3(95f, 0, 0); // same as the Glock (research/15)
        // 9×19 like the Glock: same damage/spread; 15-round magazine (M9 standard). The heavier all-steel gun climbs a little less
        // (3.5° vs 4°) and its longer slide travels further (M9 stroke ≈ 45 mm; 40 mm reads right at this scale).
        gun.Configure(dmg: 34f, pelletCount: 1, spread: 0.6f, rps: 6f, capacity: 15, reserve: 4, pump: false, shells: false, travel: 0.040f, kick: 3.5f);
        root.AddComponent<GunEffects>();

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (prefab == null) { Object.DestroyImmediate(root); throw new System.IO.FileNotFoundException("[HIS] model prefab missing: " + ModelPath); }
        var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        model.name = "Model"; model.transform.SetParent(root.transform, false); // must stay child 0 (Firearm._model)
        var grip = model.transform.Find("GripPoint");
        model.transform.localPosition = -grip.localPosition; model.transform.localRotation = Quaternion.identity;
        // The web point sits exactly where the G19's does, so the same formula puts the M9's grip where the Glock's rides in the
        // right-hip slot (research/15 pose); the longer slide just reaches 2 cm further down the thigh (logged below).
        gun.HolsterOffsetPosition = Quaternion.Euler(gun.HolsterOffsetEuler) * (grip.localPosition - LegacyPistolGrip);
        var gripPoint = new GameObject("GripPoint"); gripPoint.transform.SetParent(root.transform, false); gripPoint.transform.localRotation = grip.localRotation;
        FitBox(root, model);
        SetPrivate(grabbable, "gripPoint", gripPoint.transform);
        SetPrivate(gun, "muzzle", model.transform.Find("Muzzle"));
        SetPrivate(gun, "slide", model.transform.Find("Parts/Slide"));
        var fx = root.GetComponent<GunEffects>();
        SetPrivate(fx, "muzzle", model.transform.Find("Muzzle"));
        SetPrivate(fx, "ejectionPort", model.transform.Find("Parts/Slide/EjectionPort"));

        var wg = root.AddComponent<WeaponGrip>();
        wg.Kind = GripKind.PistolM9; // own pose: the grip is 6 mm thicker and ~3.5° less raked than the Glock's (SOLVE=1)
        wg.Frame = model.transform; wg.Trigger = model.transform.Find("Parts/Frame/Trigger"); wg.TriggerMaxDegrees = TriggerMaxDegrees;
        if (wg.Trigger == null) Debug.LogWarning("[HIS] Weapon_M9: no trigger part to animate");

        var hammer = model.transform.Find("Parts/Frame/Hammer");
        var decocker = model.transform.Find("Parts/Slide/Safety");
        if (hammer == null || decocker == null) Debug.LogWarning("[HIS] Weapon_M9: hammer / decocker part missing");
        gun.ConfigureDoubleAction(hammer, HammerCockedDegrees, decocker, DecockerDegrees, model.transform.Find("Parts/Frame/SlideCatch"), SlideCatchDegrees,
                                  DaBreak, SaBreak, SaTriggerRest, SfxPitch);
        var hip = Quaternion.Euler(gun.HolsterOffsetEuler);
        Debug.Log($"[HIS] Weapon_M9 holster: offset {gun.HolsterOffsetPosition.ToString("F4")}; in the hip slot the muzzle sits at " +
                  $"{(gun.HolsterOffsetPosition + hip * (model.transform.localPosition + model.transform.Find("Muzzle").localPosition)).ToString("F3")}, " +
                  $"the grip at {(gun.HolsterOffsetPosition).ToString("F3")} (slot frame)");

        SetLayerRecursive(root, QuestProjectSetup.WeaponLayer);
        var saved = PrefabUtility.SaveAsPrefabAsset(root, WeaponPath);
        Object.DestroyImmediate(root);
        Debug.Log("[HIS] Weapon_M9 saved " + WeaponPath);
        return saved.GetComponent<WeaponBase>();
    }

    // ---------------- helpers ----------------
    static bool ConfigureImporter()
    {
        var imp = AssetImporter.GetAtPath(Fbx) as ModelImporter;
        if (imp == null) { Debug.LogError("[HIS] M9: FBX not found at " + Fbx + " — copy ArtSource/M9A1_WPthai/Unity/ there first"); return false; }
        bool dirty = false;
        void Set<T>(T cur, T want, System.Action<T> set) { if (!EqualityComparer<T>.Default.Equals(cur, want)) { set(want); dirty = true; } }
        Set(imp.materialImportMode, ModelImporterMaterialImportMode.None, v => imp.materialImportMode = v); // we assign M9.mat
        Set(imp.importCameras, false, v => imp.importCameras = v);
        Set(imp.importLights, false, v => imp.importLights = v);
        Set(imp.importAnimation, false, v => imp.importAnimation = v);
        Set(imp.animationType, ModelImporterAnimationType.None, v => imp.animationType = v);
        Set(imp.useFileScale, true, v => imp.useFileScale = v); // file is in cm (UnitScaleFactor 1) → 0.01 m (confirmed: raw length 0.2086 m)
        Set(imp.importTangents, ModelImporterTangents.CalculateMikk, v => imp.importTangents = v); // for the baked normal map
        if (dirty) imp.SaveAndReimport();
        return true;
    }

    static Material Material()
    {
        string bc = Tex + "92FS_BaseColor.png", n = Tex + "92FS_Normal.png", m = Tex + "92FS_Metallic.png", r = Tex + "92FS_Roughness.png";
        foreach (var p in new[] { bc, n, m, r })
        {
            var ti = AssetImporter.GetAtPath(p) as TextureImporter;
            if (ti == null) { Debug.LogWarning("[HIS] M9 texture missing " + p); continue; }
            bool dirty = false;
            if (ti.maxTextureSize != 2048) { ti.maxTextureSize = 2048; dirty = true; }
            if (p == n && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; dirty = true; } // OpenGL (+Y) — checked, no flip
            if ((p == m || p == r) && LinearMaskMaps && ti.sRGBTexture) { ti.sRGBTexture = false; dirty = true; }
            if (dirty) ti.SaveAndReimport();
        }
        var path = OutDir + "/M9.mat";
        var shader = Shader.Find("HIS/LitPBR"); // weapons: no depth occlusion, same as the G19
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        mat.shader = shader;
        mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(bc));
        mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(n));
        mat.SetTexture("_MetallicMap", AssetDatabase.LoadAssetAtPath<Texture2D>(m));
        mat.SetTexture("_RoughnessMap", AssetDatabase.LoadAssetAtPath<Texture2D>(r));
        mat.SetFloat("_Metallic", 1f); mat.SetFloat("_Glossiness", 1f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>A second material for the barrel only: the same maps, the base colour tinted down so it reads as dark steel.</summary>
    static Material BarrelMaterial(Material body)
    {
        var path = OutDir + "/M9_Barrel.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(body); AssetDatabase.CreateAsset(mat, path); }
        mat.CopyPropertiesFromMaterial(body);
        mat.SetColor("_Color", BarrelTint);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>
    /// The threaded muzzle stub (7.9 mm, part of Barrel_low) off: every barrel vertex ahead of the thread's shoulder ring (the
    /// first ring past the slide's face) is pulled back onto that ring's plane. The thread's helix collapses to nothing, its
    /// front annulus becomes the crown, and the bore keeps its hole — a clean flat crown 0.7 mm proud of the slide, like a
    /// standard barrel. Zero-area triangles are dropped. Saved as M9_Barrel.asset (the prefab needs a mesh asset).
    /// </summary>
    static void TrimThread(Transform barrel, float slideFace)
    {
        var mf = barrel.GetComponent<MeshFilter>(); var src = mf.sharedMesh;
        Matrix4x4 l2w = barrel.localToWorldMatrix, w2l = barrel.worldToLocalMatrix;
        var v = src.vertices; var w = v.Select(p => l2w.MultiplyPoint3x4(p)).ToArray();
        var ahead = w.Where(p => p.z > slideFace + 0.0003f).ToList();
        if (ahead.Count == 0) { Debug.LogWarning("[HIS] M9: no barrel thread past the slide face — nothing trimmed"); return; }
        float shoulder = ahead.Min(p => p.z), tip = ahead.Max(p => p.z);
        int moved = 0;
        for (int i = 0; i < w.Length; i++)
            if (w[i].z > shoulder + 0.0002f) { w[i].z = shoulder; v[i] = w2l.MultiplyPoint3x4(w[i]); moved++; }
        var m = Object.Instantiate(src); m.name = "M9_Barrel"; m.vertices = v;
        int dropped = 0;
        for (int sub = 0; sub < m.subMeshCount; sub++)
        {
            var t = m.GetTriangles(sub); var keep = new List<int>(t.Length);
            for (int i = 0; i < t.Length; i += 3)
            {
                if (Vector3.Cross(w[t[i + 1]] - w[t[i]], w[t[i + 2]] - w[t[i]]).sqrMagnitude < 1e-16f) { dropped++; continue; }
                keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]);
            }
            m.SetTriangles(keep, sub);
        }
        m.RecalculateBounds();
        var path = OutDir + "/M9_Barrel.asset";
        var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (old != null) { EditorUtility.CopySerialized(m, old); Object.DestroyImmediate(m); m = old; }
        else AssetDatabase.CreateAsset(m, path);
        mf.sharedMesh = m;
        Debug.Log($"[HIS] M9 thread trimmed: {moved} vertices pulled back {(tip - shoulder) * 1000f:F1} mm onto the shoulder " +
                  $"({(shoulder - slideFace) * 1000f:F1} mm past the slide face), {dropped} collapsed triangles dropped");
    }

    static string Key(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    static Transform Group(string name, Transform parent, Vector3 worldPos)
    {
        var t = new GameObject(name).transform; t.SetParent(parent, false);
        t.position = worldPos; t.rotation = Quaternion.identity;
        return t;
    }

    /// <summary>A pivot at the part's own node origin (the author placed them on the pins: hammer hub verified as a vertex ring
    /// around it), falling back to the FBX-measured offset if the origin isn't on the part after import.</summary>
    static Transform PivotFor(Transform part, string name, Transform parent, Vector3 fallbackCm, float cm)
    {
        if (part == null) { Debug.LogWarning("[HIS] M9: no part for pivot " + name); return null; }
        var b = Bounds(part); var at = part.position;
        var grown = b; grown.Expand(0.01f);
        if (!grown.Contains(at))
        {
            at = new Vector3(float.IsNaN(fallbackCm.x) ? b.center.x : fallbackCm.x * cm, b.min.y + fallbackCm.y * cm, b.min.z + fallbackCm.z * cm);
            Debug.LogWarning($"[HIS] M9: {name} node origin off the part — using the measured pin {at.ToString("F4")}");
        }
        return Group(name, parent, at);
    }

    /// <summary>The web point, measured like WeaponModelBuilder.Tang (rays along +Z, 2 mm either side of the centre line,
    /// 3–20 mm under the slide, first hit behind the trigger) but seated differently. The Glock's notch is tight, so "most
    /// forward hit" is the web's seat; the M9's backstrap curve is long and shallow and its most forward point (the apex) lies
    /// ≈19 mm under the slide, which would sit the gun ~11 mm low in the hand (bore 47 mm above the web). The web crease rides
    /// up into the curve until the surface turns steeper than 45°, so the seat is the first sample above the apex where the
    /// profile's slope |dz/dy| reaches WebSeatSlope. Measured outside Unity: apex y = slide − 19.2 mm; seat y = slide − 7.8 mm,
    /// 4.4 mm behind the apex; bore 36 mm above the seat. Unity import: apex 19.5, seat 8.0 mm, 4.2 mm behind the apex, bore
    /// 36.7 mm above the web. The headset check is the pause-menu web sphere (research/16 §3.1).</summary>
    const float WebSeatSlope = 1f;
    static Vector3 Tang(Transform frame, float slideBottom, Transform trigger)
    {
        var mf = frame.GetComponent<MeshFilter>(); var mesh = mf.sharedMesh;
        var m = frame.localToWorldMatrix; var v = mesh.vertices.Select(p => m.MultiplyPoint3x4(p)).ToArray(); var t = mesh.triangles;
        float behind = trigger != null ? trigger.position.z : Bounds(frame).center.z;
        var ys = new List<float>(); var zs = new List<float>();
        for (float y = slideBottom - 0.020f; y <= slideBottom - 0.003f; y += 0.0005f)
        {
            float hit = float.MaxValue;
            foreach (var x0 in new[] { -0.002f, 0.002f })
                for (int i = 0; i < t.Length; i += 3)
                    if (RayZ(v[t[i]], v[t[i + 1]], v[t[i + 2]], x0, y, out float z) && z < behind) hit = Mathf.Min(hit, z);
            if (hit < float.MaxValue) { ys.Add(y); zs.Add(hit); }
        }
        if (ys.Count == 0) { Debug.LogError("[HIS] M9: tang not found"); return new Vector3(0f, slideBottom - 0.008f, Bounds(frame).min.z); }
        int apex = 0; for (int i = 1; i < zs.Count; i++) if (zs[i] > zs[apex]) apex = i;
        int seat = apex;
        for (int i = apex; i < ys.Count - 1; i++)
        {
            seat = i;
            if ((zs[i] - zs[i + 1]) / (ys[i + 1] - ys[i]) >= WebSeatSlope) break;
        }
        Debug.Log($"[HIS] M9 backstrap: apex {(slideBottom - ys[apex]) * 1000f:F1} mm under the slide, web seat {(slideBottom - ys[seat]) * 1000f:F1} mm " +
                  $"(import 2026-09-23: 19.5 / 8.0), seat {(zs[apex] - zs[seat]) * 1000f:F1} mm behind the apex (4.2)");
        return new Vector3(0f, ys[seat], zs[seat]);
    }

    static bool RayZ(Vector3 a, Vector3 b, Vector3 c, float x0, float y0, out float z)
    {
        z = 0f;
        float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
        if (Mathf.Abs(d) < 1e-12f) return false;
        float l1 = ((b.y - c.y) * (x0 - c.x) + (c.x - b.x) * (y0 - c.y)) / d;
        float l2 = ((c.y - a.y) * (x0 - c.x) + (a.x - c.x) * (y0 - c.y)) / d;
        float l3 = 1f - l1 - l2;
        if (l1 < 0f || l2 < 0f || l3 < 0f) return false;
        z = l1 * a.z + l2 * b.z + l3 * c.z; return true;
    }

    static IEnumerable<Vector3> WorldVerts(Transform t)
    {
        var mf = t.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) yield break;
        var m = t.localToWorldMatrix;
        foreach (var p in mf.sharedMesh.vertices) yield return m.MultiplyPoint3x4(p);
    }

    static Bounds Bounds(Transform t)
    {
        var r = t.GetComponent<Renderer>();
        if (r != null) return r.bounds;
        var vs = WorldVerts(t).ToList(); if (vs.Count == 0) return new Bounds(t.position, Vector3.zero);
        var b = new Bounds(vs[0], Vector3.zero); foreach (var v in vs) b.Encapsulate(v); return b;
    }

    static Vector3 TopCentre(Bounds b) => new(b.center.x, b.max.y, b.center.z);
    static string P(Transform t) => t != null ? t.localPosition.ToString("F4") : "none";

    static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true).Where(r => r.gameObject.activeInHierarchy).ToArray();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
    }

    static BoxCollider FitBox(GameObject root, GameObject model, float pad = 0.005f)
    {
        var b = RendererBounds(model);
        var col = root.AddComponent<BoxCollider>();
        col.center = root.transform.InverseTransformPoint(b.center);
        col.size = b.size + Vector3.one * pad * 2f;
        return col;
    }

    static void SetPrivate(Object target, string field, Object value)
    {
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { Debug.LogError($"[HIS] M9: {target.GetType().Name}.{field} not found"); return; }
        p.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
    }

    static void SavePrefab(GameObject go, string path)
    {
        PrefabUtility.SaveAsPrefabAsset(go, path); Object.DestroyImmediate(go);
    }
}
