using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>Debug: renders TextMeshPro with a plain (non-XR) camera to Builds/tmp-test.png to tell shader/font problems from XR ones.</summary>
public static class TextRenderTest
{
    [MenuItem("HIS/Debug/Render Text Test")]
    public static void Run()
    {
        var root = new GameObject("TmpTest"); root.transform.position = new Vector3(2000, 2000, 2000);
        void Make(string label, float fontSize, float scale, float y)
        {
            var go = new GameObject(label); go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0, y, 0); go.transform.localScale = Vector3.one * scale;
            var t = go.AddComponent<TextMeshPro>();
            t.fontSize = fontSize; t.alignment = TextAlignmentOptions.Center; t.color = Color.white;
            t.rectTransform.sizeDelta = new Vector2(1f / scale, 0.2f / scale);
            t.text = $"{label} size {fontSize} scale {scale}";
            t.ForceMeshUpdate(true);
            Debug.Log($"[HIS] TMP test '{label}': font={(t.font ? t.font.name : "null")} mat={(t.fontSharedMaterial ? t.fontSharedMaterial.shader.name : "null")} bounds={t.textBounds.size} verts={t.textInfo.characterCount}");
        }
        Make("A", 0.3f, 1f, 0.25f);
        Make("B", 3f, 0.1f, 0f);
        Make("C", 30f, 0.01f, -0.25f);
        var urp = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Materials/UI_Text_URP.mat");
        if (urp != null) { Make("D-urp", 3f, 0.1f, -0.45f); root.transform.Find("D-urp").GetComponent<TextMeshPro>().fontSharedMaterial = urp; }

        var camGo = new GameObject("TmpTestCam"); var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.1f, 0.1f, 0.2f);
        camGo.transform.position = root.transform.position + new Vector3(0, 0, -1.2f);
        cam.nearClipPlane = 0.05f; cam.farClipPlane = 10f; cam.fieldOfView = 40;
        var rt = new RenderTexture(900, 600, 24); cam.targetTexture = rt; cam.Render();
        RenderTexture.active = rt; var tex = new Texture2D(900, 600, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 900, 600), 0, 0); tex.Apply(); RenderTexture.active = null;
        File.WriteAllBytes("Builds/tmp-test.png", tex.EncodeToPNG());
        Debug.Log("[HIS] TMP test rendered to Builds/tmp-test.png");
        Object.DestroyImmediate(root); Object.DestroyImmediate(camGo); Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
    }
}
