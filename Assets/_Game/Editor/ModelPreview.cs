using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Renders a model asset from three angles into Builds/preview-<name>.png so it can be inspected without Play Mode.</summary>
public static class ModelPreview
{
    [MenuItem("HIS/Preview Weapon Prefabs")]
    public static void PreviewWeaponPrefabs()
    {
        foreach (var path in new[] { "Assets/_Game/Prefabs/Weapon_Pistol.prefab", "Assets/_Game/Prefabs/Weapon_M9.prefab", "Assets/_Game/Prefabs/Weapon_Shotgun.prefab", AR15Builder.WeaponPath, "Assets/_Game/Prefabs/Weapon_Knife.prefab" })
            Render(path);
    }

    [MenuItem("HIS/Preview Weapon Models")]
    public static void PreviewAll()
    {
        foreach (var path in new[] { "Assets/ThirdParty/Sketchfab/G19_Rotuma/G19_Display.fbx", "Assets/ThirdParty/Sketchfab/Remington870_tris09/Remington870.fbx", "Assets/ThirdParty/Sketchfab/KaBar_vitvitskyi/Ka_Bar_pack.obj", "Assets/_Game/Prefabs/Intruder_Alex.prefab" })
            Render(path);
    }

    public static void Render(string assetPath)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (asset == null) { Debug.LogWarning("[HIS] preview: missing " + assetPath); return; }
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset);
        inst.transform.position = new Vector3(1000, 1000, 1000); // far from the scene
        var rs = inst.GetComponentsInChildren<Renderer>(true);
        var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);

        var camGo = new GameObject("PreviewCam"); var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.2f, 0.2f, 0.22f); cam.cullingMask = ~0;
        var lightGo = new GameObject("PreviewLight"); var light = lightGo.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.2f;
        lightGo.transform.rotation = Quaternion.Euler(40, -35, 0);

        int w = 640, h = 480; var rt = new RenderTexture(w * 3, h, 24); var tex = new Texture2D(w * 3, h, TextureFormat.RGB24, false);
        var views = new[] { Quaternion.Euler(0, 0, 0), Quaternion.Euler(0, 90, 0), Quaternion.Euler(90, 0, 0) };
        var single = new RenderTexture(w, h, 24);
        for (int i = 0; i < 3; i++)
        {
            float dist = b.size.magnitude * 1.2f + 0.01f;
            camGo.transform.rotation = views[i];
            camGo.transform.position = b.center - camGo.transform.forward * dist;
            cam.nearClipPlane = dist * 0.05f; cam.farClipPlane = dist * 4f; cam.fieldOfView = 40;
            cam.targetTexture = single; cam.Render();
            RenderTexture.active = single; var part = new Texture2D(w, h, TextureFormat.RGB24, false); part.ReadPixels(new Rect(0, 0, w, h), 0, 0); part.Apply();
            tex.SetPixels(i * w, 0, w, h, part.GetPixels()); Object.DestroyImmediate(part);
        }
        tex.Apply(); RenderTexture.active = null;
        string outPath = "Builds/preview-" + Path.GetFileNameWithoutExtension(assetPath) + ".png";
        File.WriteAllBytes(outPath, tex.EncodeToPNG());
        Debug.Log($"[HIS] preview {outPath} bounds {b.size}");
        Object.DestroyImmediate(inst); Object.DestroyImmediate(camGo); Object.DestroyImmediate(lightGo); Object.DestroyImmediate(single); Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
    }
}
