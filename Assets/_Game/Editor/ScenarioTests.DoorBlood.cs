using System.Collections.Generic;
using HIS.Intruder;
using HIS.Room;
using HIS.Scenario;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

/// <summary>
/// Blood under the door (Brett 0.1.17: "maybe some blood pours from under the door after a second"): a man killed through the
/// closed door bleeds out under it — nothing at first, a pool starting about a second after the shot, at the door's inner
/// bottom edge (back edge under the leaf, on the real floor, within the opening) and growing into the room. The next round
/// starts with it gone (GameSession.ClearWorld), and a man hit in plain view puts nothing under the door.
///   Just these: scripts/unity-batch.sh ScenarioTests.RunDoorBloodHeadless
/// </summary>
public static partial class ScenarioTests
{
    static bool s_doorBloodOnly;

    /// <summary>Batch entry for just the door-blood cases: scripts/unity-batch.sh ScenarioTests.RunDoorBloodHeadless.</summary>
    public static void RunDoorBloodHeadless() { s_doorBloodOnly = s_doorwayOnly = true; RunHeadless(); } // (the doorway run's short start)

    static IEnumerable<Case> WithDoorBloodCases(IEnumerable<Case> cases)
    {
        if (!s_doorBloodOnly) foreach (var c in cases) yield return c;
        foreach (var c in DoorBloodCases()) yield return c;
    }

    static Case[] DoorBloodCases() => new[]
    {
        // Killed through the door before any kick landed (he drops behind it, out of sight): about a second later his blood
        // comes out under the door on our side and spreads.
        new Case { Name = "Burglar who runs", Expect = Tier.Good, Doorway = s_doorBloodOnly, Act = async () =>
            {
                const string what = "door blood (kill)";
                await WaitFor(() => State() == IntruderT.State.BreakIn, 40); await Wait(0.8f);
                var i = Live();
                Shoot(1, 40f, HumanBodyBones.Head);
                float t0 = Time.time;
                if (DoorShots.Wanted) _ = DoorShots.Run(t0);
                if (i == null || i.Current != IntruderT.State.Dead || !i.BehindDoor) { ExtraFail(what, "not killed behind the door: " + i?.Summary); return; }
                await WaitFor(() => Time.time - t0 >= 0.5f, 3);
                int early = BloodDecals.ShownSeeps(null, out int waiting);
                if (!await WaitFor(() => BloodDecals.ShownSeeps(null, out _) > 0, 5)) { ExtraFail(what, $"nothing came under the door in 5 s ({waiting} waiting at 0.5 s; {BloodDecals.DebugSummary})"); return; }
                float after = Time.time - t0;
                float t1 = Time.time;
                await WaitFor(() => Time.time - t1 >= 0.8f, 3);
                int n1 = MeasureSeeps(out float deep1, out float wide1, out string wrong1);
                await WaitFor(() => Time.time - t1 >= 4f, 8);
                int n2 = MeasureSeeps(out float deep2, out float wide2, out string wrong2);
                string info = $"started {after:F1} s after the shot (0.5 s: {early} shown, {waiting} waiting); +0.8 s: {n1} run(s) " +
                              $"{deep1:F2} m into the room, {wide1:F2} m along the door; +4 s: {n2} run(s) {deep2:F2} m in, {wide2:F2} m wide";
                // (0.1.28: the seep is decided when the body settles, ~0.85 s on, so nothing is queued at 0.5 s any more.)
                string err = early > 0 || after < 0.7f || after > 1.5f ? "not a second's delay (shown at 0.5 s, or not 0.7–1.5 s after)"
                           : after < 0.7f || after > 1.8f ? "didn't start about a second after the shot"
                           : wrong1 ?? wrong2
                           ?? (deep1 < 0.02f ? "barely out of the gap at +0.8 s"
                           : deep2 < deep1 + 0.05f || wide2 < wide1 ? "didn't grow"
                           : n2 < 2 ? "one run only: a kill should come out along the gap" : null);
                if (err == null) ExtraPass(what, info); else ExtraFail(what, err + " — " + info);
            } },
        // The round after: gone with the world clear. A hit on a man in plain view: nothing under the door.
        new Case { Name = "Knife attacker", Expect = Tier.Good, Doorway = s_doorBloodOnly, Act = async () =>
            {
                int left = BloodDecals.ShownSeeps(null, out int leftWaiting);
                if (left + leftWaiting == 0) ExtraPass("door blood cleared with the round", BloodDecals.DebugSummary);
                else ExtraFail("door blood cleared with the round", $"{left} shown, {leftWaiting} waiting after the clear");
                await WaitFor(() => Alive() && Dist() < 3f, 40);
                var i = Live();
                bool inView = i != null && !i.BehindDoor;
                Shoot(1, 20f, HumanBodyBones.Hips);
                await Wait(2.5f);
                int shown = BloodDecals.ShownSeeps(null, out int waiting);
                string info = $"in view {inView}, {shown} shown, {waiting} waiting 2.5 s after the hit ({BloodDecals.DebugSummary})";
                if (inView && shown + waiting == 0) ExtraPass("no door blood for a hit in view", info); else ExtraFail("no door blood for a hit in view", info);
                Shoot(9, 13f, HumanBodyBones.Chest, HumanBodyBones.Spine);
            } },
    };

    static readonly List<BloodDecals.SeepView> s_seeps = new();

    /// <summary>
    /// The seeps drawn now, against the door: how many, how far the deepest reaches into the room past the door plane and
    /// how wide they spread along it (m); <paramref name="wrong"/> = the first one not at the door's inner bottom edge (its back
    /// edge under the leaf, on the real floor, within the opening + 15 cm), else null.
    /// </summary>
    static int MeasureSeeps(out float deepest, out float wide, out string wrong)
    {
        deepest = 0f; wide = 0f; wrong = null;
        s_seeps.Clear();
        int n = BloodDecals.ShownSeeps(s_seeps, out _);
        var door = VirtualDoor.Instance;
        var floors = RoomScanner.Instance != null && RoomScanner.Instance.Room != null ? RoomScanner.Instance.Room.FloorAnchors : null;
        if (door == null || floors == null || floors.Count == 0) { wrong = "no door / floor"; return n; }
        var o = door.Opening; var dt = door.transform;
        var inward = door.Inward; inward.y = 0f; inward.Normalize();
        float floorY = floors[0].transform.position.y, minX = float.MaxValue, maxX = float.MinValue;
        foreach (var s in s_seeps)
        {
            float back = Vector3.Dot(s.Edge - dt.position, inward), front = Vector3.Dot(s.Front - dt.position, inward);
            float x = dt.InverseTransformPoint(s.Edge).x;
            deepest = Mathf.Max(deepest, front);
            minX = Mathf.Min(minX, x - s.Width * 0.5f); maxX = Mathf.Max(maxX, x + s.Width * 0.5f);
            if (wrong != null) continue;
            if (back > 0.005f || back < -0.06f) wrong = $"back edge {back:F3} m off the door plane (should be just under the leaf)";
            else if (Mathf.Abs(s.Edge.y - floorY) > 0.015f) wrong = $"{s.Edge.y - floorY:F3} m off the floor";
            else if (x < o.xMin - 0.15f || x > o.xMax + 0.15f) wrong = $"{x:F2} m along the door, outside the opening ({o.xMin:F2}..{o.xMax:F2})";
            else if (front <= back) wrong = "front behind its back edge";
        }
        if (n > 0) wide = maxX - minX;
        return n;
    }

    /// <summary>
    /// Eyes on it (GRAPHICS=1 SHOTS=1 scripts/unity-batch.sh ScenarioTests.RunDoorBloodHeadless → Logs/doorblood.png): the
    /// door-kill seep at 1.2 / 2 / 3.5 / 5.5 / 8 s after the shot, standing 1.4 m back (top row) and crouched 0.7 m back
    /// (bottom). A grey floor quad stands in for the passthrough floor.
    /// </summary>
    static class DoorShots
    {
        const int W = 360, H = 270;
        static readonly float[] Times = { 1.2f, 2f, 3.5f, 5.5f, 8f };
        public static bool Wanted => System.Environment.GetEnvironmentVariable("SHOTS") == "1"
                                     && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        public static async System.Threading.Tasks.Task Run(float t0)
        {
            var door = VirtualDoor.Instance;
            var floors = RoomScanner.Instance != null && RoomScanner.Instance.Room != null ? RoomScanner.Instance.Room.FloorAnchors : null;
            if (door == null || floors == null || floors.Count == 0) return;
            var inward = door.Inward; inward.y = 0f; inward.Normalize();
            var across = Vector3.Cross(Vector3.up, inward);
            var sill = door.transform.TransformPoint(new Vector3(door.Opening.center.x, door.Opening.yMin, 0f));
            sill.y = floors[0].transform.position.y;
            var trash = new List<Object>();
            var floor = GameObject.CreatePrimitive(PrimitiveType.Quad); trash.Add(floor);
            Object.DestroyImmediate(floor.GetComponent<Collider>());
            var grey = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(0.42f, 0.4f, 0.37f) }; trash.Add(grey);
            floor.GetComponent<MeshRenderer>().sharedMaterial = grey;
            floor.transform.SetPositionAndRotation(sill + inward * 1.5f - Vector3.up * 0.001f, Quaternion.LookRotation(Vector3.down, inward));
            floor.transform.localScale = new Vector3(3f, 3f, 1f);
            var cam = new GameObject("DoorShotCam").AddComponent<Camera>(); trash.Add(cam.gameObject);
            cam.enabled = false; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.18f, 0.18f, 0.2f);
            cam.fieldOfView = 55f; cam.nearClipPlane = 0.02f;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 }; trash.Add(rt);
            var read = new Texture2D(W, H, TextureFormat.RGBA32, false); trash.Add(read);
            var sheet = new Texture2D(W * Times.Length, H * 2, TextureFormat.RGBA32, false); trash.Add(sheet);
            (Vector3 at, Vector3 look)[] views =
            {
                (sill + inward * 1.4f + Vector3.up * 1.55f, sill + inward * 0.25f),
                (sill + inward * 0.7f + across * 0.3f + Vector3.up * 0.5f, sill + inward * 0.1f),
            };
            try
            {
                for (int c = 0; c < Times.Length; c++)
                {
                    float at = Times[c];
                    if (!await WaitFor(() => Time.time - t0 >= at, at + 5f)) return;
                    for (int r = 0; r < views.Length; r++)
                    {
                        cam.transform.position = views[r].at; cam.transform.LookAt(views[r].look);
                        cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
                        var prev = RenderTexture.active; RenderTexture.active = rt;
                        read.ReadPixels(new Rect(0, 0, W, H), 0, 0); read.Apply(false);
                        RenderTexture.active = prev;
                        sheet.SetPixels(c * W, (views.Length - 1 - r) * H, W, H, read.GetPixels());
                    }
                }
                sheet.Apply(false);
                System.IO.File.WriteAllBytes("Logs/doorblood.png", sheet.EncodeToPNG());
                Debug.Log($"[HIS] SCENARIO door blood shots: Logs/doorblood.png ({string.Join(", ", Times)} s after the shot)");
            }
            finally { foreach (var o in trash) if (o != null) Object.Destroy(o); }
        }
    }
}
