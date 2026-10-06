#!/usr/bin/env python3
"""Point the simulated head at something (Unity world coords).
Usage:
  scripts/xrop_look.py --at X Y Z [--from X Y Z]            # look at a point from a head position
  scripts/xrop_look.py --at-object PATH [--dist 2.2] [--bearing 0] [--height 1.6]
      # look at a GameObject from `dist` metres away; bearing = degrees around it from its forward (0 = in front of it)
Converts Unity (x,y,z) -> OpenXR (x,y,-z) and the quaternion (x,y,z,w) -> (-x,-y,z,w).
"""
import argparse, json, math, subprocess, sys, os
HERE = os.path.dirname(os.path.abspath(__file__))

def xrop(tool, args):
    out = subprocess.run([sys.executable, os.path.join(HERE, "xrop.py"), "call", tool, json.dumps(args)], capture_output=True, text=True)
    return out.stdout

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx, aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz)

def look(frm, at):
    dx, dy, dz = at[0]-frm[0], at[1]-frm[1], at[2]-frm[2]
    yaw = math.atan2(dx, dz); pitch = -math.atan2(dy, math.hypot(dx, dz))
    qy = (0, math.sin(yaw/2), 0, math.cos(yaw/2)); qx = (math.sin(pitch/2), 0, 0, math.cos(pitch/2))
    q = qmul(qy, qx)
    return {"position": [frm[0], frm[1], -frm[2]], "orientation": [-q[0], -q[1], q[2], q[3]]}

ap = argparse.ArgumentParser()
ap.add_argument("--at", nargs=3, type=float); ap.add_argument("--from", dest="frm", nargs=3, type=float)
ap.add_argument("--at-object"); ap.add_argument("--dist", type=float, default=2.2)
ap.add_argument("--bearing", type=float, default=0.0); ap.add_argument("--height", type=float, default=1.6)
ap.add_argument("--look-height", type=float, default=0.5)
a = ap.parse_args()
if a.at_object:
    r = json.loads(xrop("unity_get_world_pose", {"path": a.at_object}))
    m = r["matches"][0]; p = m["position"]; yaw = math.radians(m["rotation"]["y"] + a.bearing)
    at = (p["x"], p["y"] + a.look_height, p["z"])
    frm = (p["x"] + math.sin(yaw)*a.dist, a.height, p["z"] + math.cos(yaw)*a.dist)
else:
    at = tuple(a.at); frm = tuple(a.frm) if a.frm else (0.0, 1.6, 0.0)
pose = look(frm, at)
print(json.dumps(pose)); print(xrop("openxr_set_head_pose", pose).strip())
