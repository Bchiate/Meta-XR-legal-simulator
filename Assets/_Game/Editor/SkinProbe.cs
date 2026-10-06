using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where his skin really is (PoseSnapshots' watch, PoseTests' legs-out check): bakes the skinned meshes and returns the lowest
/// vertex of the whole body and of the legs alone (thighs, shins, feet). Leg vertices are picked once, from the pose he has when
/// the probe is made, by the nearest bone segment — the character mesh isn't CPU-readable in Play Mode, so no bone weights.
/// </summary>
public sealed class SkinProbe : System.IDisposable
{
    readonly SkinnedMeshRenderer[] _skins;
    readonly Mesh[] _baked;
    readonly bool[][] _leg;
    readonly List<Vector3> _verts = new();

    static readonly (HumanBodyBones a, HumanBodyBones b, bool leg)[] Segments =
    {
        (HumanBodyBones.Hips, HumanBodyBones.Spine, false), (HumanBodyBones.Spine, HumanBodyBones.Chest, false),
        (HumanBodyBones.Chest, HumanBodyBones.Neck, false), (HumanBodyBones.Neck, HumanBodyBones.Head, false),
        (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, false), (HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, false),
        (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, false), (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, false),
        (HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleDistal, false), (HumanBodyBones.RightHand, HumanBodyBones.RightMiddleDistal, false),
        (HumanBodyBones.Chest, HumanBodyBones.LeftUpperArm, false), (HumanBodyBones.Chest, HumanBodyBones.RightUpperArm, false),
        (HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, false), (HumanBodyBones.Hips, HumanBodyBones.RightUpperLeg, false),
        (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, true), (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, true),
        (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, true),
        (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, true), (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, true),
        (HumanBodyBones.RightFoot, HumanBodyBones.RightToes, true),
    };

    public SkinProbe(Animator anim)
    {
        var skins = new List<SkinnedMeshRenderer>();
        foreach (var s in anim.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (s.sharedMesh != null) skins.Add(s);
        _skins = skins.ToArray();
        _baked = new Mesh[_skins.Length]; _leg = new bool[_skins.Length][];
        var segs = new List<(Vector3 a, Vector3 b, bool leg)>();
        foreach (var (a, b, leg) in Segments)
        {
            var ta = anim.GetBoneTransform(a); var tb = anim.GetBoneTransform(b);
            if (ta != null && tb != null) segs.Add((ta.position, tb.position, leg));
        }
        // The toes on out to the tip of the boot (the toe bone sits at the ball of the foot).
        foreach (var (foot, toes) in new[] { (HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes), (HumanBodyBones.RightFoot, HumanBodyBones.RightToes) })
        {
            var tf = anim.GetBoneTransform(foot); var tt = anim.GetBoneTransform(toes);
            if (tf != null && tt != null) segs.Add((tt.position, tt.position + (tt.position - tf.position) * 0.8f, true));
        }
        for (int i = 0; i < _skins.Length; i++)
        {
            _baked[i] = new Mesh { name = "SkinProbe" };
            Bake(i);
            _leg[i] = new bool[_verts.Count];
            for (int v = 0; v < _verts.Count; v++)
            {
                float best = float.MaxValue; bool leg = false;
                foreach (var (a, b, isLeg) in segs)
                {
                    float d = SegmentDistance(_verts[v], a, b);
                    if (d < best) { best = d; leg = isLeg; }
                }
                _leg[i][v] = leg;
            }
        }
    }

    void Bake(int i)
    {
        var s = _skins[i];
        s.BakeMesh(_baked[i], true);
        _baked[i].GetVertices(_verts);
        var t = s.transform;
        for (int v = 0; v < _verts.Count; v++) _verts[v] = t.position + t.rotation * _verts[v];
    }

    /// <summary>Lowest world y of any skin vertex, and of the leg skin alone.</summary>
    public void Lowest(out float body, out float legs)
    {
        body = legs = float.MaxValue;
        for (int i = 0; i < _skins.Length; i++)
        {
            if (!_skins[i].gameObject.activeInHierarchy) continue;
            Bake(i);
            var mask = _leg[i];
            for (int v = 0; v < _verts.Count; v++)
            {
                float y = _verts[v].y;
                if (y < body) body = y;
                if (v < mask.Length && mask[v] && y < legs) legs = y;
            }
        }
    }

    /// <summary>Which way his belly faces (hip line × spine): straight down = face down, straight up = on his back.</summary>
    public static Vector3 BellyDirection(Animator anim)
    {
        var l = anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg); var r = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        var h = anim.GetBoneTransform(HumanBodyBones.Hips); var n = anim.GetBoneTransform(HumanBodyBones.Neck);
        if (l == null || r == null || h == null || n == null) return Vector3.forward;
        return Vector3.Cross(r.position - l.position, n.position - h.position).normalized;
    }

    static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.sqrMagnitude < 1e-8f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector3.Distance(p, a + ab * t);
    }

    public void Dispose() { foreach (var m in _baked) if (m != null) Object.DestroyImmediate(m); }
}
