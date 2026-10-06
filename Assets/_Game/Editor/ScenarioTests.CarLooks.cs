using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HIS.Room;
using HIS.Scenario;
using HIS.Security;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The vehicles' looks (Brett 2026-10-05: "maybe we can make the police cars / mob cars and everything look a little better",
/// then "just make sure it wont be too heavy on the quest"; PoliceCars.Looks.cs). Every vehicle in turn — the two cruisers,
/// the SWAT truck, then the hitmen's (sent once, which builds them; however many looks there are) — driven in alone and
/// parked, then rendered at night with the porch set's own renderer and lighting:
///   cars_&lt;vehicle&gt;_porch.png    from the doorbell camera's spot, zoomed in (the headset's colour look)
///   cars_&lt;vehicle&gt;_doorway.png  from 0.8 m inside the open front door at eye height (what the headset sees through it)
///   cars_&lt;vehicle&gt;_front34 / _side / _rear34 / _rear / _cabin.png   close looks (its own size)
///   cars_&lt;vehicle&gt;_parked.png   3/4 front a few seconds on (its doors as they open, the hitmen's lamps dead)
///   cars_&lt;vehicle&gt;_feed_arriving.png / _feed.png   the real night IR feed as it pulls up (lamps on) and parked
/// and one report line and one budget check per vehicle (VehicleBudget): ≤ 3,500 triangles a car (the cruiser with its light
/// bar), ≤ 4,500 a truck, no more renderers or materials than before the looks pass (cruiser 18 / 4, hitman car 15 / 3 — its body,
/// 2 doors, 4 panes + 2 door windows, its lit lamp faces, 4 glows and the headlights' pool, as at e0fa0a6 —
/// SWAT truck 15 / 4, van 6 / 2) — but the trucks' driver's cab door (2026-10-05: one renderer each, so it can swing open as
/// he climbs out; the van 7, the SWAT truck 16). With -nographics only the budgets run (no pictures).
///   GRAPHICS=1 scripts/unity-batch.sh ScenarioTests.RunCarLooksHeadless
/// </summary>
public static partial class ScenarioTests
{
    public static void RunCarLooksHeadless()
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
            try
            {
                HIS.Core.PlayerState.GodMode = true;
                ScenarioDirector.WaitForForced = true;   // no rounds: only the vehicles
                PoliceResponse.Enabled = false;          // (no standoff of the director's own)
                if (await WaitFor(() => ScenarioDirector.Instance != null, 30) && await ConfirmDoorForDoorwayRun()) await CarLooksChecks();
            }
            catch (Exception e) { _fail++; Debug.LogError($"[HIS] SCENARIO exception: {e}"); }
            finally { _running = false; Debug.Log($"[HIS] SCENARIO {_pass}/{_pass + _fail} passed"); }
            EditorApplication.Exit(_fail == 0 && _pass > 0 ? 0 : 1);
        };
        EditorApplication.EnterPlaymode();
    }

    static async Task CarLooksChecks()
    {
        PorchSet porch = null;
        if (!await WaitFor(() => PoliceCars.Instance != null && VirtualDoor.Instance != null && (porch = PorchSet.For(VirtualDoor.Instance)) != null, 30))
        { ExtraFail("car looks", "no PoliceCars / front door / porch"); return; }
        var pc = PoliceCars.Instance;
        var door = VirtualDoor.Instance;
        var feed = SecurityCamera.For(door);
        bool pictures = SecurityCamera.CanRender;
        if (pictures) System.IO.Directory.CreateDirectory("Logs/poses");
        else Debug.Log("[HIS] CARLOOKS -nographics: budgets only, no pictures (GRAPHICS=1 for them)");
        int total = 0;
        try
        {
            for (int v = 0; v < 32; v++)
            {
                pc.End();
                if (!pc.Send(v)) break;   // (the first hitman vehicle's send builds them all; past the last one: false)
                if (!pc.Crashed(v, out _, out var car) && car == null) { ExtraFail($"car looks: vehicle {v}", "no root"); continue; }
                string slug = car.name.ToLowerInvariant().Replace(' ', '_');
                // As it pulls up (0.6 s short of its stop: the hitmen's lamps still on), then parked with its doors still shut.
                float stopAt = pc.ArriveTime - 1.6f;
                pc.Advance(Mathf.Max(0f, stopAt - 0.6f - pc.Clock));
                if (pictures && feed != null) { feed.RenderNow(); await Wait(0.05f); SaveFeed(feed, $"cars_{slug}_feed_arriving.png"); }
                pc.Advance(Mathf.Max(0f, stopAt + 0.3f - pc.Clock));
                await Wait(0.05f);
                if (pictures)
                {
                    if (feed != null) { feed.RenderNow(); await Wait(0.05f); SaveFeed(feed, $"cars_{slug}_feed.png"); }
                    LookAtVehicle(car, slug, porch, door);
                }
                // A few seconds on: its doors as they swing, the hitmen's lamps dead.
                pc.Advance(3f);
                await Wait(0.05f);
                if (pictures) VehicleShot(car, $"cars_{slug}_parked.png", Local(car, -3.8f, 1.7f, 4.6f), Local(car, 0f, 0.85f, 0.2f), 40f);
                Budget(pc, v, ref total);
            }
        }
        finally { pc.End(); }
        Debug.Log($"[HIS] CARLOOKS all vehicles: {total} triangles together (built once; each drawn only while it's out)");
    }

    /// <summary>One vehicle's budget line and check (PoliceCars.VehicleBudget).</summary>
    static void Budget(PoliceCars pc, int v, ref int total)
    {
        if (!pc.VehicleBudget(v, out var name, out int tris, out int renderers, out int materials, out bool truck, out bool police))
        { ExtraFail($"car looks: vehicle {v} budget", "not built"); return; }
        total += tris;
        int maxTris = truck ? 4500 : 3500;
        int maxRenderers = truck ? (police ? 16 : 7) : (police ? 18 : 15);   // (the trucks: + their cab door; a hitman car had 15 before too)
        int maxMaterials = police ? 4 : truck ? 2 : 3;
        string info = $"{tris} tris (≤ {maxTris}), {renderers} renderers (≤ {maxRenderers}), {materials} materials (≤ {maxMaterials})";
        Debug.Log($"[HIS] CARLOOKS {name}: {info}");
        string what = $"car looks: {name} within the Quest budget";
        if (tris <= maxTris && renderers <= maxRenderers && materials <= maxMaterials) ExtraPass(what, info); else ExtraFail(what, info);
    }

    /// <summary>A point in <paramref name="car"/>'s space, scaled to its length (a car 1, the truck and van ~1.34).</summary>
    static Vector3 Local(Transform car, float x, float y, float z)
    {
        float s = Mathf.Max(1f, VehicleHalfLength(car) / 2.55f);
        return car.TransformPoint(new Vector3(x * s, y * s, z * s));
    }

    /// <summary>
    /// Half its body's length (the "Body" part only: 2026-10-05, measured over every mesh it took in the headlights' pool, 9.9 m
    /// ahead of a car — every close look was framed from 15 m off, 3.3 m over the roof).
    /// </summary>
    static float VehicleHalfLength(Transform car)
    {
        float half = 0f;
        foreach (var mf in car.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0 || mf.name != "Body") continue;
            var b = mf.sharedMesh.bounds;
            foreach (float sz in new[] { -1f, 1f })
                half = Mathf.Max(half, Mathf.Abs(car.InverseTransformPoint(mf.transform.TransformPoint(b.center + new Vector3(0f, 0f, sz * b.extents.z))).z));
        }
        return half;
    }

    /// <summary>The parked vehicle from the doorbell's spot, the open front door, and close looks all round.</summary>
    static void LookAtVehicle(Transform car, string slug, PorchSet porch, VirtualDoor door)
    {
        var centre = Local(car, 0f, 0.85f, 0f);
        VehicleShot(car, $"cars_{slug}_porch.png", porch.CameraPosition, centre, 24f);
        // (In the doorway's plane, at eye height: this camera draws only the outside layer, and 0.8 m inside the room the porch
        // set's own front wall filled the whole frame — the open doorway's pass is SecurityTests' doorway_police_*.)
        var eye = door.Center + Vector3.ProjectOnPlane(door.Outward, Vector3.up).normalized * 0.1f; eye.y = door.Threshold.y + 1.6f;
        VehicleShot(car, $"cars_{slug}_doorway.png", eye, centre, 30f);
        VehicleShot(car, $"cars_{slug}_front34.png", Local(car, -3.8f, 1.7f, 4.6f), Local(car, 0f, 0.85f, 0.2f), 40f);
        VehicleShot(car, $"cars_{slug}_side.png", Local(car, -7f, 1.3f, 0f), centre, 40f);
        VehicleShot(car, $"cars_{slug}_rear34.png", Local(car, 3.6f, 1.8f, -4.8f), Local(car, 0f, 0.85f, -0.2f), 40f);
        VehicleShot(car, $"cars_{slug}_rear.png", Local(car, 0f, 1.5f, -6.5f), centre, 40f);
        bool big = VehicleHalfLength(car) > 3f;
        VehicleShot(car, $"cars_{slug}_cabin.png", big ? car.TransformPoint(new Vector3(-3f, 2.2f, 4.4f)) : car.TransformPoint(new Vector3(-2.3f, 1.55f, 1.9f)),
                    car.TransformPoint(big ? new Vector3(0f, 1.5f, 1.8f) : new Vector3(0f, 1.0f, 0f)), 45f);
    }

    /// <summary>
    /// One picture with the porch set's renderer (only its layer: the night street, the vehicles), its colour look (the
    /// headset's), from <paramref name="eye"/> at <paramref name="look"/> (world): Logs/poses/<paramref name="file"/>.
    /// </summary>
    static void VehicleShot(Transform car, string file, Vector3 eye, Vector3 look, float fov)
    {
        if (car == null || !SecurityCamera.CanRender) return;   // (-nographics: rendering crashed Unity natively before)
        const int W = 960, H = 600;
        var go = new GameObject("CarLooksShot");
        var cam = go.AddComponent<Camera>();
        var rt = new RenderTexture(W, H, 24);
        try
        {
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.01f, 0.012f, 0.02f);
            cam.cullingMask = 1 << PorchSet.OutsideLayer; cam.fieldOfView = fov; cam.nearClipPlane = 0.05f; cam.farClipPlane = 120f;
            DoorwayView.UseFeedRenderer(cam);
            var porch = PorchSet.For(VirtualDoor.Instance);
            Shader.SetGlobalFloat("_HIS_FeedView", 0f);
            go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(look - eye));
            if (porch != null) porch.SetLive(true);
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            if (porch != null) porch.SetLive(false);
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(false);
            RenderTexture.active = null;
            System.IO.File.WriteAllBytes("Logs/poses/" + file, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }
        finally { UnityEngine.Object.Destroy(go); rt.Release(); UnityEngine.Object.Destroy(rt); }
    }
}
