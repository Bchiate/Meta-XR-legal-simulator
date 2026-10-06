using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIS.Weapons;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Turns the Sketchfab models into game weapon models:
///  - G19: split the exploded "Display" mesh into islands, keep frame/slide/magazine, reassemble, save as a prefab with a movable Slide.
///  - Remington 870: wrap so forward = +Z, pump as a movable part.
///  - Ka-Bar: the axis-aligned "White" variant, scaled to 31 cm.
/// HIS > Build Weapon Models. Outputs to Assets/_Game/Models/.
/// </summary>
public static class WeaponModelBuilder
{
    const string OutDir = "Assets/_Game/Models";
    const string G19 = "Assets/ThirdParty/Sketchfab/G19_Rotuma/G19_Display.fbx";
    const string G19Tex = "Assets/ThirdParty/Sketchfab/G19_Rotuma/Textures/";
    const string R870 = "Assets/ThirdParty/Sketchfab/Remington870_tris09/Remington870.fbx";
    const string R870Tex = "Assets/ThirdParty/Sketchfab/Remington870_tris09/Textures/";
    const string KaBar = "Assets/ThirdParty/Sketchfab/KaBar_vitvitskyi/Ka_Bar_pack.obj";
    const string KaBarTex = "Assets/ThirdParty/Sketchfab/KaBar_vitvitskyi/Textures/";

    [MenuItem("HIS/Build Weapon Models")]
    public static void BuildAll()
    {
        Directory.CreateDirectory(OutDir);
        BuildG19(); BuildR870(); BuildKaBar();
        AssetDatabase.SaveAssets();
        foreach (var p in new[] { OutDir + "/Model_G19.prefab", OutDir + "/Model_R870.prefab", OutDir + "/Model_KaBar.prefab" }) ModelPreview.Render(p);
    }

    static Material Pbr(string name, string baseColor, string normal, string metallic, string roughness, string ao = null)
    {
        var path = $"{OutDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        var shader = Shader.Find("HIS/LitPBR"); // weapons: no depth occlusion (the player's body would hide holstered guns)
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        mat.shader = shader;
        Texture2D T(string p) { if (p == null) return null; var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p); if (t == null) Debug.LogWarning("[HIS] texture missing " + p); return t; }
        if (normal != null)
        {
            var ti = AssetImporter.GetAtPath(normal) as TextureImporter;
            if (ti != null && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); }
        }
        foreach (var p in new[] { baseColor, normal, metallic, roughness, ao })
        {
            var ti = p != null ? AssetImporter.GetAtPath(p) as TextureImporter : null;
            if (ti != null && ti.maxTextureSize > 2048) { ti.maxTextureSize = 2048; ti.SaveAndReimport(); }
        }
        mat.SetTexture("_MainTex", T(baseColor)); mat.SetTexture("_BumpMap", T(normal));
        mat.SetTexture("_MetallicMap", T(metallic)); mat.SetTexture("_RoughnessMap", T(roughness));
        if (ao != null) mat.SetTexture("_OcclusionMap", T(ao));
        mat.SetFloat("_Metallic", 1f); mat.SetFloat("_Glossiness", 1f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static GameObject Part(string name, Mesh mesh, Material mat, Transform parent)
    {
        var go = new GameObject(name); go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go;
    }

    // ---------------- G19 ----------------
    // The "Display" mesh holds an assembled pistol near the origin plus exploded copies laid out at z ≈ +0.2 and bullets at z ≈ -0.19.
    // Keep the assembled set: grip is at -Z so the muzzle already points +Z, Y is up.
    static void BuildG19()
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(G19);
        var mf = src.GetComponentInChildren<MeshFilter>();
        int gunSub = GunSubMesh(src, mf);
        var islands = MeshIslands.Split(mf.sharedMesh).Where(i => i.triangles > 50 && i.subMesh == gunSub && i.bounds.center.z < 0.15f && i.bounds.center.z > -0.15f).ToList();
        Debug.Log("[HIS] G19 assembled-set islands: " + string.Join(" | ", islands.Select(i => $"tris={i.triangles} size={i.bounds.size} c={i.bounds.center}")));
        var slide = islands.OrderByDescending(i => i.bounds.size.z * (i.bounds.center.y > 0.02f ? 1f : 0f)).First();
        var frame = islands.Where(i => i != slide).OrderByDescending(i => i.triangles).First();
        var rest = islands.Where(i => i != slide && i != frame).ToList(); // magazine, barrel, small bits
        var mat = Pbr("G19", G19Tex + "Low_Explode_Glock_Mat_BaseColor.png", G19Tex + "Low_Explode_Glock_Mat_Normal.png", G19Tex + "Low_Explode_Glock_Mat_Metallic.png", G19Tex + "Low_Explode_Glock_Mat_Roughness.png");

        var wrap = new GameObject("Model_G19");
        var parts = new GameObject("Parts"); parts.transform.SetParent(wrap.transform, false);
        var frameGo = Part("Frame", Save(frame.mesh, "G19_Frame"), mat, parts.transform);
        // Real G19 slide is 174 mm long.
        float scale = 0.174f / slide.bounds.size.z;
        // The trigger shoe + its safety blade: thin centred islands inside the guard. They turn on a pivot at the shoe's top
        // (the trigger pin) so the pull shows (research/16 §1.1; WeaponGrip.SetTrigger).
        bool IsTrigger(MeshIslands.Island i)
        {
            var c = i.bounds.center * scale; var sz = i.bounds.size * scale;
            return Mathf.Abs(c.x) < 0.003f && sz.x < 0.009f && c.y > 0f && c.y < 0.03f && c.z > -0.02f && c.z < 0.015f;
        }
        var shoe = rest.Where(IsTrigger).OrderByDescending(i => i.bounds.size.x).FirstOrDefault();
        Transform trigger = null;
        if (shoe != null)
        {
            trigger = new GameObject("Trigger").transform; trigger.SetParent(frameGo.transform, false);
            trigger.localPosition = new Vector3(0f, shoe.bounds.max.y - 0.002f / scale, shoe.bounds.center.z);
        }
        int k = 0;
        foreach (var r in rest)
        {
            var go = Part("FramePart" + k, Save(r.mesh, "G19_Part" + (k++)), mat, frameGo.transform);
            if (trigger != null && IsTrigger(r)) go.transform.SetParent(trigger, true);
        }
        Debug.Log($"[HIS] G19 trigger: {(trigger != null ? trigger.childCount + " parts, pivot " + (trigger.localPosition * scale).ToString("F4") : "NOT FOUND")}");
        var slideGo = Part("Slide", Save(slide.mesh, "G19_Slide"), mat, parts.transform);
        parts.transform.localScale = Vector3.one * scale;
        var muzzle = new GameObject("Muzzle"); muzzle.transform.SetParent(wrap.transform, false);
        muzzle.transform.localPosition = new Vector3(0, slide.bounds.center.y * scale, slide.bounds.max.z * scale);
        // GripPoint = the web of the hand on the gun: the tang, high in the notch under the slide's rear (research/16 §3.1).
        // HandController puts it on the controller's web point, so the gun sits high in the hand like a real grip.
        var grip = new GameObject("GripPoint"); grip.transform.SetParent(wrap.transform, false);
        grip.transform.localPosition = Tang(rest.Where(r => !IsTrigger(r)).Select(r => r.mesh).Append(frame.mesh), slide.bounds.min.y, scale);
        grip.transform.localRotation = Quaternion.Euler(-15f, 0, 0); // tuned aim (Brett): barrel ≈5° above the controller's aim
        Debug.Log($"[HIS] G19 GripPoint (tang) {grip.transform.localPosition.ToString("F4")}");
        Debug.Log($"[HIS] G19 assembled: slide {slide.bounds.size.z:F3} u → scale {scale:F3}; total {RendererBounds(wrap).size}");
        SavePrefab(wrap, OutDir + "/Model_G19.prefab");
    }

    /// <summary>
    /// The web point on a pistol frame: the deepest (most forward) point of the backstrap's notch under the beavertail.
    /// Rays along +Z on the centre line, 3–20 mm under the slide, hit the backstrap; the notch is the most forward hit.
    /// Model units in, scaled metres out.
    /// </summary>
    static Vector3 Tang(IEnumerable<Mesh> frameParts, float slideBottom, float scale)
    {
        var meshes = frameParts.Select(m => (v: m.vertices, t: m.triangles)).ToList();
        float bestZ = float.MinValue, bestY = 0f;
        for (float y = slideBottom - 0.020f / scale; y <= slideBottom - 0.003f / scale; y += 0.0005f / scale)
        {
            float hit = float.MaxValue; // first surface along +Z from far behind
            // Two rays 2 mm either side of the centre line: a ray on the mesh's own seam at x = 0 slips through to the inside.
            foreach (var x0 in new[] { -0.002f / scale, 0.002f / scale })
                foreach (var (v, t) in meshes)
                    for (int i = 0; i < t.Length; i += 3)
                        if (RayZ(v[t[i]], v[t[i + 1]], v[t[i + 2]], x0, y, out float z)) hit = Mathf.Min(hit, z);
            if (hit < 0f && hit > bestZ) { bestZ = hit; bestY = y; }
        }
        return new Vector3(0f, bestY * scale, bestZ * scale);
    }

    // Where the line x = x0, y = y0 (along Z) crosses a triangle (2-D barycentric test in XY).
    static bool RayZ(Vector3 a, Vector3 b, Vector3 c, float x0, float y0, out float z)
    {
        z = 0f;
        float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
        if (Mathf.Abs(d) < 1e-12f) return false;
        float l1 = ((b.y - c.y) * (x0 - c.x) + (c.x - b.x) * (y0 - c.y)) / d;
        float l2 = ((c.y - a.y) * (x0 - c.x) + (a.x - c.x) * (y0 - c.y)) / d;
        float l3 = 1f - l1 - l2;
        if (l1 < 0f || l2 < 0f || l3 < 0f) return false;
        z = l1 * a.z + l2 * b.z + l3 * c.z;
        return true;
    }

    static int GunSubMesh(GameObject src, MeshFilter mf)
    {
        var mr = mf.GetComponent<MeshRenderer>(); var mats = mr.sharedMaterials;
        for (int i = 0; i < mats.Length; i++) if (mats[i] != null && mats[i].name.ToLower().Contains("glock")) return i;
        return 0;
    }

    /// <summary>+1 if the frame's "front" (muzzle) is toward +axis. Heuristic: the grip (tall part) is at the rear, so the end with more vertical extent is the rear.</summary>
    static float SignFront(MeshIslands.Island frame, int axis)
    {
        var v = frame.mesh.vertices; float c = axis == 0 ? frame.bounds.center.x : frame.bounds.center.z;
        float minPos = float.MaxValue, minNeg = float.MaxValue;
        foreach (var p in v) { float a = axis == 0 ? p.x : p.z; if (a > c) minPos = Mathf.Min(minPos, p.y); else minNeg = Mathf.Min(minNeg, p.y); }
        return minNeg < minPos ? 1f : -1f; // grip hangs lower on the rear side → front is the other side
    }

    static void Recentre(GameObject go, Vector3 centre)
    {
        var mf = go.GetComponent<MeshFilter>(); var m = mf.sharedMesh; var v = m.vertices;
        for (int i = 0; i < v.Length; i++) v[i] -= centre; m.vertices = v; m.RecalculateBounds(); EditorUtility.SetDirty(m);
    }

    // ---------------- Remington 870 ----------------
    static void BuildR870()
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(R870);
        var mat = Pbr("R870", R870Tex + "remington_Base_Color.jpg", R870Tex + "remington_Normal_DirectX.jpg", R870Tex + "remington_Metallic.jpg", R870Tex + "remington_Roughness.jpg", R870Tex + "remington_Mixed_AO.jpg");
        var shellMat = Pbr("R870_Shell", R870Tex + "12ge_Base_Color.png", R870Tex + "12ge_Normal_DirectX.png", R870Tex + "12ge_Metallic.png", R870Tex + "12ge_Roughness.png", R870Tex + "12ge_Mixed_AO.png");
        var wrap = new GameObject("Model_R870");
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(src); inst.name = "Parts"; inst.transform.SetParent(wrap.transform, false);
        PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterial = r.name.StartsWith("12ge") ? shellMat : mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (r.name.StartsWith("12ge")) r.gameObject.SetActive(false); // loose shell prop
        }
        // Model is 4 units long for a ~1.0 m shotgun; the muzzle points -X, so yaw +90° makes it +Z.
        inst.transform.localScale = Vector3.one * 0.25f;
        inst.transform.localRotation = Quaternion.Euler(0, 90, 0);
        // Pump pivot: the "action_low" part moves along the barrel when racked.
        var pump = inst.transform.Find("action_low");
        if (pump != null)
        {
            var pivot = new GameObject("Pump"); pivot.transform.SetParent(wrap.transform, false);
            pivot.transform.position = pump.GetComponent<Renderer>().bounds.center; pivot.transform.rotation = wrap.transform.rotation;
            pump.SetParent(pivot.transform, true);
        }
        // Trigger pivot on its pin at the top of the blade, so the pull shows (WeaponGrip.SetTrigger).
        var trig = inst.transform.Find("trigger");
        if (trig != null)
        {
            var tb = trig.GetComponent<Renderer>().bounds;
            var pivot = new GameObject("Trigger"); pivot.transform.SetParent(wrap.transform, false);
            pivot.transform.position = new Vector3(0f, tb.max.y - 0.0015f, tb.center.z); pivot.transform.rotation = wrap.transform.rotation;
            trig.SetParent(pivot.transform, true);
        }
        Debug.Log("[HIS] R870 assembled: " + RendererBounds(wrap).size);
        SavePrefab(wrap, OutDir + "/Model_R870.prefab");
    }

    // ---------------- Ka-Bar ----------------
    static void BuildKaBar()
    {
        var src = AssetDatabase.LoadAssetAtPath<GameObject>(KaBar);
        var blade = Pbr("KaBar_Blade", KaBarTex + "White_Base_color.png", KaBarTex + "White_normal.png", KaBarTex + "White_Metallic.png", KaBarTex + "White_Roughness.png");
        var wrap = new GameObject("Model_KaBar");
        var parts = new GameObject("Parts"); parts.transform.SetParent(wrap.transform, false);
        foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf.name.Contains("White")) continue; // the axis-aligned variant
            Part(mf.name, mf.sharedMesh, blade, parts.transform);
        }
        var b = RendererBounds(parts);
        // Centre on the handle, blade along +Z (already), scale to 31 cm overall length.
        float len = Mathf.Max(b.size.x, b.size.y, b.size.z);
        parts.transform.localScale = Vector3.one * (0.31f / len);
        // Edge down (−Y), flats to the sides: the source lies flat with its edge (the belly curving up to the clip point) at −X
        // and the saw-backed spine at +X. A hammer grip wraps the handle with the edge toward the fingers and the web on the
        // spine (GripPoses.Knife), the same for either hand.
        parts.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
        b = RendererBounds(parts);
        parts.transform.localPosition = -b.center;
        Debug.Log($"[HIS] KaBar assembled: {RendererBounds(wrap).size} (raw length {len:F2})");
        SavePrefab(wrap, OutDir + "/Model_KaBar.prefab");
    }

    static Mesh Save(Mesh m, string name)
    {
        var path = $"{OutDir}/{name}.asset";
        m.name = name;
        // Overwrite in place: the asset keeps its GUID, so a rebuild doesn't churn every reference to it.
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) { EditorUtility.CopySerialized(m, existing); existing.name = name; EditorUtility.SetDirty(existing); return existing; }
        AssetDatabase.CreateAsset(m, path); return m;
    }

    static void SavePrefab(GameObject go, string path)
    {
        PrefabUtility.SaveAsPrefabAsset(go, path); Object.DestroyImmediate(go);
    }

    static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true).Where(r => r.gameObject.activeInHierarchy).ToArray();
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
    }
}
