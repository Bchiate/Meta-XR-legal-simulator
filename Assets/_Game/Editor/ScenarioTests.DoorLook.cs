using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Room;
using UnityEditor;
using UnityEngine;

/// <summary>
/// How the virtual door looks (Brett 0.1.17: "the door appearing gray sometimes with like a light gray outline in the door
/// that flickers a bit"), run with the safety checks (Brett's room):
///   key light  — the one directional light hits the closed leaf and the open leaf's face toward the doorway at the same
///                N·L for every session heading (it was world-fixed: 0.01 in one of his sessions, 0.51 in another), and
///                follows the door again after the world turns under it (a recenter);
///   door plane — nothing outside the door rig has a surface within 5 mm of the leaf's front face over the opening (the eye
///                buffer's depth is 16-bit: 0.6-2.4 mm steps at 2-4 m, so anything that close z-fights with the leaf —
///                the wall wireframe and the orange debug outline both did).
///   Just these: scripts/unity-batch.sh ScenarioTests.RunDoorLookHeadless
/// </summary>
public static partial class ScenarioTests
{
    /// <summary>Batch entry for just the door-look checks: scripts/unity-batch.sh ScenarioTests.RunDoorLookHeadless.</summary>
    public static void RunDoorLookHeadless()
    {
        HISEditorMenu.OpenMain();
        Application.runInBackground = true;
        float deadline = (float)EditorApplication.timeSinceStartup + 300f;
        EditorApplication.update += () =>
        {
            if (EditorApplication.timeSinceStartup > deadline) { Debug.LogError("[HIS] SCENARIO watchdog: timeout"); EditorApplication.Exit(3); }
        };
        EditorApplication.playModeStateChanged += async s =>
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            _running = true; _pass = _fail = 0;
            try { if (await ConfirmDoorForDoorwayRun()) await DoorLookChecks(); }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task DoorLookChecks()
    {
        KeyLightAnyHeading();
        await KeyLightLive();
        DoorPlaneClear();
    }

    // ───────────────────────────── key light ─────────────────────────────

    const float KeyNdlMin = 0.35f, KeyNdlMax = 0.6f; // closed leaf / open leaf lit like a room light, never ambient-only

    static void KeyLightAnyHeading()
    {
        const string what = "door key light (any heading)";
        float min = 9f, max = -9f, oldMin = 9f, oldMax = -9f;
        var legacy = Quaternion.Euler(50f, -30f, 0f) * Vector3.forward; // MainSceneBuilder's fixed light
        for (int yaw = 0; yaw < 360; yaw += 15)
        {
            var turn = Quaternion.Euler(0f, yaw, 0f);
            Vector3 inward = turn * Vector3.forward, latch = turn * Vector3.right;
            var toLight = -(DoorKeyLight.Aim(inward, latch, 50f) * Vector3.forward);
            foreach (float ndl in new[] { Vector3.Dot(inward, toLight), Vector3.Dot(latch, toLight) }) { min = Mathf.Min(min, ndl); max = Mathf.Max(max, ndl); }
            float old = Vector3.Dot(inward, -legacy); oldMin = Mathf.Min(oldMin, old); oldMax = Mathf.Max(oldMax, old);
        }
        string err = min < KeyNdlMin || max > KeyNdlMax || max - min > 0.01f ? $"N·L {min:F2}..{max:F2} over 24 headings" : null;
        Check(what, err, $"closed + open leaf N·L {min:F2}..{max:F2} at every heading (the fixed light gave {oldMin:F2}..{oldMax:F2})");
    }

    static async Task KeyLightLive()
    {
        const string what = "door key light (live)";
        var kl = DoorKeyLight.Instance;
        if (kl == null) { ExtraFail(what, "no DoorKeyLight (self-starting)"); return; }
        float Front() => Vector3.Dot(VirtualDoor.Instance.Inward, -kl.Light.transform.forward);
        float Open() => Vector3.Dot(VirtualDoor.Instance.HingeToLatch, -kl.Light.transform.forward);
        bool Lit() => VirtualDoor.Instance != null && kl.Light != null && Front() is >= KeyNdlMin and <= KeyNdlMax && Open() is >= KeyNdlMin and <= KeyNdlMax;
        await Wait(2f); // the hinge side is picked 1.5 s after the rig is built
        if (!await WaitFor(Lit, 5))
        {
            ExtraFail(what, kl.Light == null ? "no directional light found" : VirtualDoor.Instance == null ? "no door rig" : $"closed N·L {Front():F2}, open {Open():F2}");
            return;
        }
        float front = Front(), open = Open();
        // A recenter turns the world under the (world-fixed) light: turn the light instead and it must come back.
        var t = kl.Light.transform;
        t.rotation = Quaternion.AngleAxis(120f, Vector3.up) * t.rotation;
        float stale = Front();
        bool back = await WaitFor(Lit, DoorKeyLight.CheckSeconds * 4f);
        Check(what, back ? null : $"not re-aimed after a 120° turn (closed N·L {Front():F2})",
              $"closed leaf N·L {front:F2}, open leaf {open:F2}; turned 120° ({stale:F2}) → back to {Front():F2}");
    }

    // ───────────────────────────── door plane ─────────────────────────────

    const float PlaneTolerance = 0.005f;

    /// <summary>Nothing but the door rig within 5 mm of the leaf's front face (z = 0 of the door frame) over the opening.</summary>
    static void DoorPlaneClear()
    {
        const string what = "door plane clear";
        var rs = RoomScanner.Instance;
        var door = VirtualDoor.Instance;
        if (rs == null || rs.PrimaryDoor == null || !rs.PrimaryDoor.PlaneRect.HasValue || door == null) { ExtraFail(what, "no door rig"); return; }
        var frame = door.transform;
        var r = rs.PrimaryDoor.PlaneRect.Value;
        var inward = door.Inward;
        // Sample points over the leaf face, 5 cm in from the opening's edges.
        var points = new List<Vector3>();
        for (int i = 0; i <= 4; i++)
        for (int j = 0; j <= 8; j++)
            points.Add(frame.TransformPoint(new Vector3(Mathf.Lerp(r.xMin + 0.05f, r.xMax - 0.05f, i / 4f), Mathf.Lerp(r.yMin + 0.05f, r.yMax - 0.05f, j / 8f), 0f)));
        var slab = new Bounds(frame.TransformPoint(new Vector3(r.center.x, r.center.y, 0f)), Vector3.zero);
        foreach (var c in new[] { new Vector3(r.xMin, r.yMin), new Vector3(r.xMax, r.yMin), new Vector3(r.xMin, r.yMax), new Vector3(r.xMax, r.yMax) })
            slab.Encapsulate(frame.TransformPoint(c));
        slab.Expand(0.02f);

        var offenders = new List<string>();
        int meshes = 0, lines = 0;
        foreach (var rend in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (!rend.enabled || !rend.gameObject.activeInHierarchy || rend.transform.IsChildOf(frame)) continue;
            if (rend is ParticleSystemRenderer || rend is SkinnedMeshRenderer) continue;
            if (rend is LineRenderer lr)
            {
                lines++;
                var pts = new Vector3[lr.positionCount];
                lr.GetPositions(pts);
                foreach (var p in pts)
                {
                    var w = lr.useWorldSpace ? p : lr.transform.TransformPoint(p);
                    var local = frame.InverseTransformPoint(w);
                    float m = lr.widthMultiplier;
                    if (Mathf.Abs(local.z) < 0.01f && local.x > r.xMin - m && local.x < r.xMax + m && local.y > r.yMin - m && local.y < r.yMax + m)
                    { offenders.Add($"{ObjPath(rend.transform)} (line)"); break; }
                }
                continue;
            }
            if (!rend.bounds.Intersects(slab)) continue;
            var mf = rend.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) continue;
            meshes++;
            if (CoversPlane(mesh, rend.transform.localToWorldMatrix, points, inward)) offenders.Add(ObjPath(rend.transform));
        }
        string err = offenders.Count > 0 ? $"on the leaf's plane (z-fights with the door): {string.Join(", ", offenders)}" : null;
        // The detector itself: an uncut wall 0.2 mm off the frame plane (the old wireframe) is caught, the trim's face 1.5 cm proud isn't.
        Mesh Quad(float z)
        {
            var m = new Mesh { vertices = new[] { new Vector3(r.xMin - 1f, r.yMin - 1f, z), new Vector3(r.xMax + 1f, r.yMin - 1f, z),
                                                  new Vector3(r.xMax + 1f, r.yMax + 1f, z), new Vector3(r.xMin - 1f, r.yMax + 1f, z) } };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            return m;
        }
        var wall = Quad(-0.0002f * Mathf.Sign(frame.InverseTransformDirection(inward).z));
        var trim = Quad(0.015f * Mathf.Sign(frame.InverseTransformDirection(inward).z));
        if (!CoversPlane(wall, frame.localToWorldMatrix, points, inward)) err ??= "detector missed a wall quad 0.2 mm behind the leaf";
        if (CoversPlane(trim, frame.localToWorldMatrix, points, inward)) err ??= "detector flagged a quad 1.5 cm in front of the leaf";
        UnityEngine.Object.Destroy(wall); UnityEngine.Object.Destroy(trim);
        Check(what, err, $"{points.Count} points on the leaf, {meshes} mesh(es) near it and {lines} line(s) checked: nothing within {PlaneTolerance * 1000:0} mm");
    }

    /// <summary>A triangle of the mesh crosses the ray through one of the points (along the door normal) within the tolerance.</summary>
    static bool CoversPlane(Mesh mesh, Matrix4x4 toWorld, List<Vector3> points, Vector3 normal)
    {
        var v = mesh.vertices;
        for (int k = 0; k < v.Length; k++) v[k] = toWorld.MultiplyPoint3x4(v[k]);
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
            var tri = mesh.GetTriangles(sub);
            for (int k = 0; k + 2 < tri.Length; k += 3)
                foreach (var p in points)
                    if (RayTriangle(p + normal * 0.05f, -normal, v[tri[k]], v[tri[k + 1]], v[tri[k + 2]], out float d) && Mathf.Abs(d - 0.05f) <= PlaneTolerance)
                        return true;
        }
        return false;
    }

    // Möller–Trumbore, both faces.
    static bool RayTriangle(Vector3 o, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float dist)
    {
        dist = 0f;
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (Mathf.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float w = Vector3.Dot(dir, q) * inv;
        if (w < 0f || u + w > 1f) return false;
        dist = Vector3.Dot(e2, q) * inv;
        return dist >= 0f;
    }

    static string ObjPath(Transform t) => t.parent != null ? $"{t.parent.name}/{t.name}" : t.name;
}
