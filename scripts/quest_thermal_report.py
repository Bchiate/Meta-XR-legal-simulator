#!/usr/bin/env python3
"""Summarise a Quest soak/playtest logcat: frame rate, CPU/GPU levels, GPU load, temperature curve, throttling signs.

Usage:
  scripts/quest_thermal_report.py Logs/soak-<date>.log            # summary + per-minute table
  scripts/quest_thermal_report.py LOG --every 5                   # table every 5 minutes
  scripts/quest_thermal_report.py LOG --csv Logs/soak.csv         # per-second rows for a spreadsheet
  scripts/quest_thermal_report.py LOG --compare Logs/device-0.1.15-play2.log

Reads the compositor's once-a-second VrApi line (FPS=, CPU4/GPU=a/b, GPU%=, App=, Temp=battery/second, PLS=, LP=, Fov=,
SF=), the game's "[HIS] Thermal: ..." lines (governor tiers, hints, battery/headroom), and "[soak] ..." lines written by
scripts/quest_soak.sh (skin/SoC/battery sensors from dumpsys thermalservice). Duplicate logcat lines are dropped.
Throttling signs: PLS>0 (power level SAVE/DANGER), LP=1 (battery saver), sustained FPS under 65 outside loading.
Skin sensor (surf-virt-usr) throttles LIGHT at 48 C, MODERATE 50, SEVERE 52; battery at 58; SoC at 89 (Quest 3, read 2026-09-24).
"""
import argparse, csv, re, statistics, sys
from datetime import datetime

VRAPI = re.compile(r"^(\d\d-\d\d \d\d:\d\d:\d\d)\.\d+ .*VrApi.*?: FPS=(\d+)/(\d+),(.*)$")
FIELD = re.compile(r"([A-Za-z%&0-9]+)=([^,]+)")
LEVELS = re.compile(r"CPU\d*/GPU=(\d+)/(\d+)")
HIS = re.compile(r"^(\d\d-\d\d \d\d:\d\d:\d\d)\.\d+ .*\[HIS\] Thermal: (.*)$")
SOAK = re.compile(r"^(\d\d-\d\d \d\d:\d\d:\d\d)\S* \[soak\] (.*)$")
KV = re.compile(r"([\w-]+)=([-\d.]+)")
SKIN_LIGHT, SKIN_SEVERE = 48.0, 52.0


def ts(s):
    return datetime.strptime("2000-" + s, "%Y-%m-%d %H:%M:%S")


def num(v):
    m = re.match(r"-?[\d.]+", v or "")
    return float(m.group(0)) if m else None


def parse(path):
    seen, rows, events, sensors = set(), [], [], []
    with open(path, errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            if line in seen:
                continue
            seen.add(line)
            m = VRAPI.match(line)
            if m:
                t, fps, target, rest = m.groups()
                d = dict(FIELD.findall(rest))
                lv = LEVELS.search(rest)
                temp = d.get("Temp", "")
                rows.append(dict(
                    t=ts(t), fps=int(fps), target=int(target),
                    cpu=int(lv.group(1)) if lv else None, gpu=int(lv.group(2)) if lv else None,
                    gpu_pct=num(d.get("GPU%")), cpu_pct=num(d.get("CPU%")), app=num(d.get("App")),
                    batt=num(temp.split("/")[0]) if temp else None,
                    pls=int(num(d.get("PLS")) or 0), lp=int(num(d.get("LP")) or 0),
                    fov=d.get("Fov"), sf=num(d.get("SF"))))
                continue
            m = HIS.match(line)
            if m:
                events.append((ts(m.group(1)), m.group(2)))
                continue
            m = SOAK.match(line)
            if m:
                sensors.append((ts(m.group(1)), {k: float(v) for k, v in KV.findall(m.group(2))}))
    rows.sort(key=lambda r: r["t"])
    # One second can appear twice (two logcat buffers with different ms): keep the first per second.
    out, last = [], None
    for r in rows:
        if r["t"] != last:
            out.append(r)
        last = r["t"]
    return out, events, sensors


def pct(vals, p):
    vals = sorted(v for v in vals if v is not None)
    if not vals:
        return None
    return vals[min(len(vals) - 1, int(round(p / 100 * (len(vals) - 1))))]


def fmt(v, f="{:.1f}"):
    return "-" if v is None else f.format(v)


def mode(vals):
    vals = [v for v in vals if v is not None]
    return statistics.mode(vals) if vals else None


def summary(rows, events, sensors, every, name):
    if not rows:
        print(f"{name}: no VrApi lines found (capture with: adb logcat -v time VrApi:I Unity:I *:S)")
        return
    t0 = rows[0]["t"]
    mins = (rows[-1]["t"] - t0).total_seconds() / 60
    fps = [r["fps"] for r in rows]
    settled = [r for r in rows if (r["t"] - t0).total_seconds() > 60]  # skip the first minute (loading)
    low = [r for r in settled if r["fps"] < 65]
    # Longest run of consecutive low-fps seconds after the first minute.
    run = best = 0
    for r in settled:
        run = run + 1 if r["fps"] < 65 else 0
        best = max(best, run)
    lv = {}
    for r in rows:
        lv[(r["cpu"], r["gpu"])] = lv.get((r["cpu"], r["gpu"]), 0) + 1
    temps = [r["batt"] for r in rows if r["batt"]]
    print(f"== {name}: {mins:.1f} min, {len(rows)} samples, {rows[0]['t']:%H:%M:%S} - {rows[-1]['t']:%H:%M:%S}")
    print(f"FPS      median {statistics.median(fps):.0f}/{rows[0]['target']}, p5 {pct(fps, 5)}, "
          f"seconds <65 after 1st min: {len(low)} (longest run {best} s)")
    print("Levels   " + ", ".join(f"{c}/{g}: {100 * n / len(rows):.0f}%" for (c, g), n in sorted(lv.items(), key=lambda kv: -kv[1])[:6]))
    print(f"GPU load median {fmt(pct([r['gpu_pct'] for r in settled], 50), '{:.2f}')}, p90 {fmt(pct([r['gpu_pct'] for r in settled], 90), '{:.2f}')}; "
          f"CPU median {fmt(pct([r['cpu_pct'] for r in settled], 50), '{:.2f}')}; "
          f"App GPU ms median {fmt(pct([r['app'] for r in settled], 50), '{:.2f}')}, p95 {fmt(pct([r['app'] for r in settled], 95), '{:.2f}')} "
          f"(budget {1000 / rows[0]['target']:.1f})")
    print(f"Battery  {fmt(temps[0] if temps else None)} -> {fmt(temps[-1] if temps else None)} C, max {fmt(max(temps) if temps else None)} "
          f"(OS throttles battery at 58)")
    fovs = sorted({r['fov'] for r in rows if r['fov'] is not None})
    sfs = sorted({r['sf'] for r in rows if r['sf'] is not None})
    print(f"Render   Fov={','.join(fovs) or '-'}  SF={','.join(f'{s:.2f}' for s in sfs) or '-'}")
    pls = max(r["pls"] for r in rows)
    lps = max(r["lp"] for r in rows)
    throttled = pls > 0 or lps > 0 or best >= 10
    print(f"Throttle PLS max {pls} (0 normal, 1 save, 2 danger), LP max {lps} -> "
          f"{'THROTTLING SIGNS' if throttled else 'no throttling'}")
    if sensors:
        skin = [(t, s.get("surf-virt-usr")) for t, s in sensors if s.get("surf-virt-usr") is not None]
        if skin:
            mx = max(v for _, v in skin)
            print(f"Skin     {skin[0][1]:.1f} -> {skin[-1][1]:.1f} C, max {mx:.1f} (LIGHT throttle 48, SEVERE 52) "
                  f"-> {'OVER LIGHT' if mx >= SKIN_LIGHT else f'{SKIN_LIGHT - mx:.1f} C of margin'}")
        soc = [s.get("soc-usr") for _, s in sensors if s.get("soc-usr") is not None]
        if soc:
            print(f"SoC      max {max(soc):.1f} C (throttles at 89)")
    tiers = [(t, e) for t, e in events if "->" in e or "forced" in e]
    hints = [e for _, e in events if "hint" in e]
    if events:
        print(f"Governor {len(tiers)} tier change(s), {len(hints)} hint change(s)")
        for t, e in tiers:
            print(f"  {(t - t0).total_seconds() / 60:5.1f} min  {e}")
        notes = [e for _, e in events if "break notice" in e]
        if notes:
            print(f"  break notice shown {len(notes)}x")
    else:
        print("Governor no [HIS] Thermal lines (build before 0.1.16, or Unity tag not captured)")

    print(f"\n{'min':>5} {'fps':>4} {'min':>4} {'lvl':>5} {'gpu%':>5} {'app':>5} {'batt':>5} {'skin':>5} {'pls':>3}")
    bucket = []
    def flush(start):
        if not bucket:
            return
        sk = [s.get("surf-virt-usr") for t, s in sensors if start <= (t - t0).total_seconds() / 60 < start + every and s.get("surf-virt-usr")]
        c, g = mode([(r["cpu"], r["gpu"]) for r in bucket]) or (None, None)
        print(f"{start:5.0f} {statistics.median(r['fps'] for r in bucket):4.0f} {min(r['fps'] for r in bucket):4d} "
              f"{c}/{g:<3} {fmt(pct([r['gpu_pct'] for r in bucket], 50), '{:.2f}'):>5} {fmt(pct([r['app'] for r in bucket], 50)):>5} "
              f"{fmt(bucket[-1]['batt']):>5} {fmt(max(sk) if sk else None):>5} {max(r['pls'] for r in bucket):3d}")
    start = 0
    for r in rows:
        m = (r["t"] - t0).total_seconds() / 60
        if m >= start + every:
            flush(start)
            bucket = []
            while m >= start + every:
                start += every
        bucket.append(r)
    flush(start)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log")
    ap.add_argument("--every", type=float, default=1.0, help="table bucket in minutes (default 1)")
    ap.add_argument("--csv", help="write per-second rows here")
    ap.add_argument("--compare", help="a second log to summarise after the first (e.g. the 0.1.15 baseline)")
    a = ap.parse_args()
    rows, events, sensors = parse(a.log)
    summary(rows, events, sensors, a.every, a.log)
    if a.csv:
        with open(a.csv, "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=list(rows[0].keys()) if rows else ["t"])
            w.writeheader()
            w.writerows(rows)
        print(f"\nwrote {a.csv} ({len(rows)} rows)")
    if a.compare:
        print()
        r2, e2, s2 = parse(a.compare)
        summary(r2, e2, s2, a.every, a.compare)


if __name__ == "__main__":
    sys.exit(main())
