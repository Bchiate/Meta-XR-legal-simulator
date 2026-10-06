using System;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>One finger's bend in degrees. Fingers: Mcp/Pip/Dip flex (+ = curl into the palm), Spread = MCP abduction
    /// (+ = toward the thumb). Thumb: Mcp = CMC flex across the palm, Spread = CMC abduction, Pip = MCP, Dip = IP, Twist =
    /// roll of the thumb metacarpal about its own axis.</summary>
    [Serializable]
    public struct FingerPose
    {
        public float Mcp, Pip, Dip, Spread, Twist;
        public FingerPose(float mcp, float pip, float dip, float spread = 0f, float twist = 0f) { Mcp = mcp; Pip = pip; Dip = dip; Spread = spread; Twist = twist; }
        public static FingerPose Lerp(in FingerPose a, in FingerPose b, float t) => new(
            a.Mcp + (b.Mcp - a.Mcp) * t, a.Pip + (b.Pip - a.Pip) * t, a.Dip + (b.Dip - a.Dip) * t,
            a.Spread + (b.Spread - a.Spread) * t, a.Twist + (b.Twist - a.Twist) * t);
    }

    [Serializable]
    public struct HandShape
    {
        public FingerPose Thumb, Index, Middle, Ring, Pinky;
    }

    /// <summary>
    /// Meta's OculusHand_R/_L rig (as shipped in com.meta.xr.sdk.core) posed by joint angles. Each joint turns about the
    /// flexion / abduction axis Meta marks in the FBX (the *_fe_axis_marker / *_aa_axis_marker nodes), taken from the bind
    /// pose once. The left rig is the right rig point-mirrored with identical local rotations, so the same angles pose
    /// both hands. Rig-local frame (right hand): +X toward the fingertips, −Y out of the palm, +Z toward the thumb.
    /// </summary>
    public sealed class HandRig
    {
        public readonly GameObject Root;
        public readonly Transform Wrist;
        public readonly bool Left;
        /// <summary>The web between thumb and index (the crease a grip's tang sits in), in wrist space, right hand: in the V
        /// between the thumb and index metacarpals, ~1 cm palmar of the palm's plane (a grip's backstrap sits there with its
        /// right panel on the palm). Fixed (bones only rotate).</summary>
        public static readonly Vector3 WebLocalR = new(0.060f, -0.030f, 0.034f);
        public Vector3 WebLocal => Left ? -WebLocalR : WebLocalR;

        // 0..3 thumb0..3, then index/middle/ring/pinky × (mcp, pip, dip) = 4 + 12 joints.
        readonly Transform[] _j = new Transform[16];
        readonly Quaternion[] _rest = new Quaternion[16];
        readonly Vector3[] _fe = new Vector3[16];   // flex axis per joint (parent space)
        readonly Vector3[] _aa = new Vector3[16];   // abduction axis (finger MCPs, thumb CMC), parent space
        readonly Vector3[] _twist = new Vector3[16];
        public readonly Transform IndexTip, IndexPad, ThumbPad;

        static readonly string[] Fingers = { "index", "middle", "ring", "pinky" };

        public HandRig(GameObject instance, bool left)
        {
            Root = instance; Left = left;
            string b = left ? "b_l_" : "b_r_", m = left ? "l_" : "r_";
            Wrist = Find(b + "wrist");
            _j[0] = Find(b + "thumb0"); _j[1] = Find(b + "thumb1"); _j[2] = Find(b + "thumb2"); _j[3] = Find(b + "thumb3");
            Axis(0, m + "thumb_cmc_fe_axis_marker", ref _fe);
            Axis(1, m + "thumb_cmc_aa_axis_marker", ref _aa);
            Axis(2, m + "thumb_mcp_fe_axis_marker", ref _fe);
            Axis(3, m + "thumb_ip_fe_axis_marker", ref _fe);
            _twist[1] = Vector3.right; // thumb metacarpal's own long axis
            for (int f = 0; f < 4; f++)
            {
                int i = 4 + f * 3;
                _j[i] = Find(b + Fingers[f] + "1"); _j[i + 1] = Find(b + Fingers[f] + "2"); _j[i + 2] = Find(b + Fingers[f] + "3");
                Axis(i, m + Fingers[f] + "_mcp_fe_axis_marker", ref _fe);
                Axis(i, m + Fingers[f] + "_mcp_aa_axis_marker", ref _aa);
                Axis(i + 1, m + Fingers[f] + "_pip_fe_axis_marker", ref _fe);
                Axis(i + 2, m + Fingers[f] + "_dip_fe_axis_marker", ref _fe);
            }
            for (int i = 0; i < 16; i++) _rest[i] = _j[i].localRotation;
            IndexTip = Find(m + "index_finger_tip_marker");
            IndexPad = Find(m + "index_finger_pad_marker");
            ThumbPad = Find(m + "thumb_finger_pad_marker");
        }

        Transform Find(string name)
        {
            foreach (var t in Root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            throw new Exception("[HIS] HandRig: no " + name);
        }

        // Meta's axis markers are rotated so their +X is the joint axis; express it in the joint's parent space.
        void Axis(int joint, string marker, ref Vector3[] into)
        {
            var mk = Find(marker);
            into[joint] = _j[joint].parent.InverseTransformDirection(mk.TransformDirection(Vector3.right)).normalized;
        }

        public void Apply(in HandShape s)
        {
            var t = s.Thumb;
            // Thumb: CMC flex across the palm (thumb0), CMC abduction + roll (thumb1), MCP (thumb2), IP (thumb3).
            _j[0].localRotation = Quaternion.AngleAxis(t.Mcp, _fe[0]) * _rest[0];
            _j[1].localRotation = Quaternion.AngleAxis(t.Spread, _aa[1]) * _rest[1] * Quaternion.AngleAxis(t.Twist, _twist[1]);
            _j[2].localRotation = Quaternion.AngleAxis(t.Pip, _fe[2]) * _rest[2];
            _j[3].localRotation = Quaternion.AngleAxis(t.Dip, _fe[3]) * _rest[3];
            Finger(4, s.Index); Finger(7, s.Middle); Finger(10, s.Ring); Finger(13, s.Pinky);
        }

        void Finger(int i, in FingerPose p)
        {
            _j[i].localRotation = Quaternion.AngleAxis(p.Mcp, _fe[i]) * Quaternion.AngleAxis(p.Spread, _aa[i]) * _rest[i];
            _j[i + 1].localRotation = Quaternion.AngleAxis(p.Pip, _fe[i + 1]) * _rest[i + 1];
            _j[i + 2].localRotation = Quaternion.AngleAxis(p.Dip, _fe[i + 2]) * _rest[i + 2];
        }

        /// <summary>Joint transforms for probes: 0..3 thumb, 4 + finger*3 + (0 mcp, 1 pip, 2 dip).</summary>
        public Transform Joint(int i) => _j[i];

        /// <summary>
        /// Places the wrist so the web point lands on <paramref name="webAt"/> with the hand turned by <paramref name="rot"/>
        /// (both in <paramref name="frame"/>'s space, authored for the hand <paramref name="authoredLeft"/>). The other hand
        /// gets the mirror image across the frame's YZ plane (x → −x): the point with x flipped, the rotation premultiplied by
        /// 180° about X (the left rig is the right one point-mirrored, so −mirror = that half-turn).
        /// </summary>
        public void Place(Transform frame, Vector3 webAt, Quaternion rot, bool authoredLeft, Vector3 shift = default)
        {
            if (authoredLeft != Left) { webAt.x = -webAt.x; rot = Quaternion.AngleAxis(180f, Vector3.right) * rot; }
            // The rig root carries the pose; the wrist bone sits at the rig origin.
            Root.transform.SetParent(frame, false);
            Root.transform.localRotation = rot;
            Root.transform.localPosition = webAt - rot * (WebLocal + (Left ? -shift : shift));
        }
    }
}
