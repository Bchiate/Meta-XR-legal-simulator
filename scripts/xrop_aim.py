#!/usr/bin/env python3
"""Helpers on top of xrop.Client: aim a held weapon at a target and pull the trigger.
Usage: scripts/xrop_aim.py <held_weapon_path> <target_path> [shots]
"""
import json, math, sys, time
sys.path.insert(0, 'scripts')
from xrop import Client

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx, aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz]
def qinv(q): return [-q[0], -q[1], -q[2], q[3]]
def q_axis(axis, deg):
    s = math.sin(math.radians(deg) / 2); return [axis[0]*s, axis[1]*s, axis[2]*s, math.cos(math.radians(deg) / 2)]
def look_rotation_unity(fwd, up=(0, 1, 0)):
    """Unity LookRotation as a quaternion (x,y,z,w), Unity handedness."""
    f = fwd; n = math.sqrt(sum(v*v for v in f)); f = [v/n for v in f]
    r = [up[1]*f[2]-up[2]*f[1], up[2]*f[0]-up[0]*f[2], up[0]*f[1]-up[1]*f[0]]; n = math.sqrt(sum(v*v for v in r)); r = [v/n for v in r]
    u = [f[1]*r[2]-f[2]*r[1], f[2]*r[0]-f[0]*r[2], f[0]*r[1]-f[1]*r[0]]
    m00, m01, m02 = r[0], u[0], f[0]; m10, m11, m12 = r[1], u[1], f[1]; m20, m21, m22 = r[2], u[2], f[2]
    t = m00 + m11 + m22
    if t > 0:
        s = math.sqrt(t + 1) * 2; return [(m21-m12)/s, (m02-m20)/s, (m10-m01)/s, 0.25*s]
    if m00 > m11 and m00 > m22:
        s = math.sqrt(1 + m00 - m11 - m22) * 2; return [0.25*s, (m01+m10)/s, (m02+m20)/s, (m21-m12)/s]
    if m11 > m22:
        s = math.sqrt(1 + m11 - m00 - m22) * 2; return [(m01+m10)/s, 0.25*s, (m12+m21)/s, (m02-m20)/s]
    s = math.sqrt(1 + m22 - m00 - m11) * 2; return [(m02+m20)/s, (m12+m21)/s, 0.25*s, (m10-m01)/s]
def unity_to_openxr_quat(q): return [-q[0], -q[1], q[2], q[3]]
def unity_to_openxr_pos(p): return [p[0], p[1], -p[2]]

class Rig:
    def __init__(self): self.c = Client(); self.c.init()
    def call(self, n, a):
        r = self.c.request("tools/call", {"name": n, "arguments": a})
        return "".join(i.get("text", "") for i in r.get("result", {}).get("content", []))
    def pose(self, path):
        d = json.loads(self.call("unity_get_world_pose", {"path": path})); m = d["matches"][0]
        p = m["position"]; return [p["x"], p["y"], p["z"]]
    def aim_at(self, hand, muzzle_path, target_path, aim_pitch=-60.0, hold_pos=None):
        m = self.pose(muzzle_path); t = self.pose(target_path)
        d = [t[i] - m[i] for i in range(3)]
        # R_ctrl = LookRotation(d) * inverse(R_aim)   (Unity), then convert to OpenXR
        r_ctrl = look_rotation_unity(d)  # the hand AimAnchor already levels the weapon: controller rotation == muzzle rotation
        pos = hold_pos or self.pose(muzzle_path)
        self.call("openxr_set_controller_pose", {"hand": hand, "pose_type": "grip", "position": unity_to_openxr_pos(pos),
                                                 "orientation": unity_to_openxr_quat(r_ctrl), "duration": 0.2})
        time.sleep(0.4)
        return m, t
    def press(self, hand, component, hold=0.25):
        self.call("openxr_set_controller_input", {"hand": hand, "component": component, "value": 1.0}); time.sleep(hold)
        self.call("openxr_set_controller_input", {"hand": hand, "component": component, "value": 0.0}); time.sleep(0.3)

if __name__ == "__main__":
    weapon, target = sys.argv[1], sys.argv[2]; shots = int(sys.argv[3]) if len(sys.argv) > 3 else 1
    rig = Rig()
    hand_pos = rig.pose("[HIS] OVRCameraRig/TrackingSpace/RightHandAnchor/RightControllerAnchor")
    m, t = rig.aim_at("right", weapon + "/Model/Muzzle", target, hold_pos=hand_pos)
    print("aimed from", [round(v, 2) for v in m], "at", [round(v, 2) for v in t])
    for _ in range(shots): rig.press("right", "Trigger")
    print(f"fired {shots}")
