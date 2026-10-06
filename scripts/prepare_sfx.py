#!/usr/bin/env python3
"""Cut the staged CC0 recordings (SourceAudio/, gitignored) into game one-shots.

    scripts/prepare_sfx.py              # writes Assets/_Game/Audio/Clips/<key>/<key>_NN.wav + Clips/manifest.json
    scripts/prepare_sfx.py --only amb.  # just the keys starting with this (the rest of the folder and manifest are kept)

Every game sound key gets 1-8 variants sliced from long takes (onset segmentation on a 10 ms peak envelope),
trimmed to the first transient, faded, and peak-normalised. 3D sounds are mono; the win/cheer stings and the
night ambience stay stereo (played 2D). No ffmpeg on this Mac: afconvert decodes, numpy does the rest.
Provenance of every source: Assets/ThirdParty/Audio/SOURCES.csv. manifest.json records which slice of which
file became which clip. Re-run after changing a recipe; then HIS > Audio > Build Sfx Library in the Editor.
"""
import argparse, json, os, struct, subprocess, sys, tempfile
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "SourceAudio")
OUT = os.path.join(ROOT, "Assets", "_Game", "Audio", "Clips")
SR = 48000

# key, source (file name without extension, prefix match), mode, options
#   mode: all   = the whole file (trimmed)          range = explicit [start, end] seconds
#         top   = the n strongest hits              first = the first n hits
#         pick  = these hits (1-based, in time order, as segments() numbers them)
#   options: n, length (s from onset), target (peak dBFS), thr (segmentation dB below peak), stereo, pre, fade,
#            loop (played with src.loop: no fades; the last ~1 s is equal-power crossfaded into the head so the seam is inaudible)
RECIPES = [
    # --- guns: player perspective ("behind shooter" master tracks) + near prepared takes -------------------
    ("pistol.shot",       "X_40",   "top",   dict(n=3, length=1.3, target=-0.3, fade=0.5)),
    ("shotgun.shot",      "O_20",   "top",   dict(n=2, length=1.7, target=-0.1, fade=0.6)),
    ("shotgun.shot",      "O_21P",  "top",   dict(n=2, length=1.7, target=-0.1, fade=0.6)),
    ("pistol.empty",      "X_18",   "top",   dict(n=1, length=0.25)),
    ("shotgun.empty",     "O_8",    "top",   dict(n=1, length=0.25)),
    ("pistol.magout",     "X_5",    "first", dict(n=1, length=0.5)),
    ("pistol.magin",      "X_4",    "top",   dict(n=2, length=0.45)),
    ("pistol.slidelock",  "X_13",   "top",   dict(n=1, length=0.4)),       # last shot: the slide locks back on the empty mag
    ("pistol.sliderelease", "X_17", "top",   dict(n=1, length=0.5)),       # reload: the slide slams home
    ("pistol.rack",       "X_23",   "top",   dict(n=2, length=0.55)),
    ("pistol.rack",       "842731", "top",   dict(n=3, length=0.45)),
    ("shotgun.rack",      "O_6",    "top",   dict(n=1, length=0.7)),
    ("shotgun.rack",      "755495", "top",   dict(n=3, length=0.6)),
    ("shotgun.shellin",   "O_3",    "top",   dict(n=4, length=0.45, thr=-20)),
    ("casing.brass",      "318964", "top",   dict(n=5, length=0.6, target=-6)),
    ("casing.shell",      "620929", "first", dict(n=1, length=0.9, target=-6)),
    ("holster.draw",      "707791", "first", dict(n=1, length=0.5, target=-4)),
    ("holster.in",        "707791", "range", dict(start=1.9, end=2.4, target=-4)),
    # --- hits on flesh ---------------------------------------------------------------------------------------
    ("flesh.bullet",      "138481", "all",   dict(target=-1)),
    ("flesh.bullet",      "641040", "all",   dict(length=0.6, target=-1)),
    ("flesh.bullet",      "631895", "top",   dict(n=2, length=0.45, target=-1)),
    ("flesh.punch",       "241950", "top",   dict(n=4, length=0.35, target=-2)),
    ("flesh.punch",       "503277", "top",   dict(n=3, length=0.4, target=-2)),
    ("player.hit",        "503277", "top",   dict(n=2, length=0.4, target=-3)),
    ("flesh.stab",        "870738", "top",   dict(n=4, length=0.4, target=-2)),
    ("flesh.stab",        "411742", "first", dict(n=1, length=0.5, target=-2)),
    ("flesh.slash",       "35213",  "all",   dict(target=-2)),
    ("flesh.slash",       "35214",  "range", dict(start=2.4, end=2.95, target=-2)),
    ("blood.splat",       "406582", "all",   dict(target=-6)),
    ("body.fall",         "128103", "top",   dict(n=3, length=0.7, target=-2)),
    ("body.fall",         "455536", "top",   dict(n=3, length=0.7, target=-2)),
    # --- the intruder's voice and feet -------------------------------------------------------------------------
    # Pain and effort grunts are picked by the male-voice gate (scripts/voice_gender.py, grunt rules: speaker-embedding FLI
    # <= -0.22 and F0 <= 385 Hz), not "the n loudest": the loudest cuts were these performers' highest (350-750 Hz) and
    # several read as a woman (Brett 0.1.17: "still female voice on male attacker sometimes"). The cuts that failed are in
    # ../tools/tts/rejected-female-sounding.
    ("intruder.pain",     "610991", "pick",  dict(pick=[1, 3, 8, 9, 12, 20], length=0.55, target=-2)),
    ("intruder.swing",    "511023", "pick",  dict(pick=[4, 6, 10, 11, 12, 19, 20, 21], length=0.45, target=-3)),  # on each blow
    ("intruder.death",    "547184", "all",   dict(target=-1)),
    ("intruder.step",     "235718", "top",   dict(n=8, length=0.25, target=-8)),
    ("intruder.step.run", "565713", "top",   dict(n=6, length=0.25, target=-8)),
    # drunk.mumble (freesound 842496) read ambiguous (FLI -0.14): Mike mumbles in his own voice now (make_tts.py --engine
    # neural --steps mumble), so this script no longer cuts or wipes that folder.
    # --- the door ------------------------------------------------------------------------------------------------
    ("door.knock",        "256513", "top",   dict(n=3, length=1.2, target=-3)),
    ("door.pound",        "250539", "top",   dict(n=5, length=0.6, target=-1)),
    ("door.kick",         "533858", "top",   dict(n=4, length=0.9, target=-1)),
    ("door.kick",         "452609", "top",   dict(n=2, length=0.95, target=-1)),
    ("door.burst",        "160213", "all",   dict(target=-0.5)),
    ("door.burst",        "262538", "all",   dict(target=-0.5)),
    ("door.slam",         "389811", "first", dict(n=1, length=1.0, target=-1)),
    ("door.open",         "710510", "all",   dict(length=1.8, target=-3, fade=0.5)),
    ("door.close",        "184307", "range", dict(start=0.45, end=1.45, target=-2)),
    ("door.creak",        "273466", "range", dict(start=0.15, end=4.3, target=-4, fade=0.6)),
    ("door.keys.unlock",  "119918", "range", dict(start=0.7, end=1.9, target=-3)),
    ("door.keys.jingle",  "390324", "all",   dict(target=-4)),
    # --- outcomes (the siren is played FAINT and low-passed by Sfx; see Brett 2026-09-22) ------------------------
    ("outcome.siren",     "159753", "range", dict(start=0.5, end=16.8, target=-3, fade=0.8)),
    ("outcome.siren.pa",  "159755", "all",   dict(target=-3)),
    ("outcome.bullhorn",  "556360", "all",   dict(target=-4)),
    ("outcome.radio",     "524205", "all",   dict(target=-4)),
    ("outcome.cardoor",   "778421", "all",   dict(target=-3)),
    ("win.confetti",      "26349",  "all",   dict(target=-2, stereo=True)),
    ("win.horn",          "170583", "all",   dict(target=-3, stereo=True)),
    ("win.sting",         "397355", "all",   dict(target=-3, stereo=True)),
    ("win.cheer",         "752709", "all",   dict(target=-4, stereo=True, fade=0.6)),
    ("amb.night",         "459675", "all",   dict(target=-10, stereo=True, loop=True)),
]


# ------------------------------------------------------------------------------------------------ wav io
def load(p):
    b = open(p, "rb").read()
    i, fmt, data = 12, None, None
    while i + 8 <= len(b):
        cid = b[i:i + 4]; sz = struct.unpack("<I", b[i + 4:i + 8])[0]; body = b[i + 8:i + 8 + sz]
        if cid == b"fmt ": fmt = struct.unpack("<HHIIHH", body[:16])
        elif cid == b"data": data = body
        i += 8 + sz + (sz & 1)
    ch, bits = fmt[1], fmt[5]
    assert bits == 16, p
    a = np.frombuffer(data[:len(data) // (2 * ch) * 2 * ch], dtype="<i2").astype(np.float32) / 32768
    return a.reshape(-1, ch)


def save(p, x):
    x = np.clip(x, -1, 1)
    if x.ndim == 1: x = x[:, None]
    ch = x.shape[1]; d = (x * 32767).astype("<i2").tobytes()
    hdr = b"RIFF" + struct.pack("<I", 36 + len(d)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, ch, SR, SR * ch * 2, ch * 2, 16) \
        + b"data" + struct.pack("<I", len(d))
    open(p, "wb").write(hdr + d)


def decode(src_file, tmp):
    out = os.path.join(tmp, os.path.basename(src_file) + ".wav")
    if not os.path.exists(out):
        subprocess.run(["afconvert", "-f", "WAVE", "-d", f"LEI16@{SR}", src_file, out], check=True, capture_output=True)
    return load(out)


# ------------------------------------------------------------------------------------------------ slicing
def segments(x, thr_db=-28.0, min_gap=0.12, win=0.01):
    m = np.abs(x).max(axis=1)
    h = int(SR * win); k = len(m) // h
    env = m[:k * h].reshape(k, h).max(axis=1)
    on = env > env.max() * 10 ** (thr_db / 20)
    gap = int(min_gap / win)
    out, i = [], 0
    while i < k:
        if on[i]:
            j = i
            while j < k and (on[j] or np.any(on[j:j + gap])): j += 1
            out.append((i * win, j * win, float(env[i:j].max())))
            i = j
        else:
            i += 1
    return [s for s in out if s[1] - s[0] >= 0.02 or s[2] > env.max() * 0.3]


def first_transient(x, level=0.05):
    m = np.abs(x).max(axis=1)
    idx = np.argmax(m > m.max() * level)
    return max(0, idx - int(0.004 * SR))


def loop_seam(x, xf=1.0):
    # Fold the tail over the head (equal power) and drop it: sample -1 of the result runs straight into sample 0.
    n = min(int(xf * SR), len(x) // 4)
    r = np.linspace(0, 1, n)[:, None]
    head = x[:n] * np.sqrt(r) + x[-n:] * np.sqrt(1 - r)
    return np.concatenate([head, x[n:-n]])


def finish(x, target_db, fade, stereo, loop=False):
    if not stereo and x.shape[1] > 1: x = x.mean(axis=1, keepdims=True)
    if loop:
        x = loop_seam(x)
        return x * (10 ** (target_db / 20) / (np.abs(x).max() + 1e-9))
    x = x[first_transient(x):]
    # Trim the tail below -50 dB of the peak, then fade in 2 ms / fade out.
    m = np.abs(x).max(axis=1)
    above = np.nonzero(m > m.max() * 10 ** (-50 / 20))[0]
    if len(above): x = x[:above[-1] + int(0.02 * SR)]
    n_in = min(len(x), int(0.002 * SR)); n_out = min(len(x), int(fade * SR))
    x = x.copy()
    x[:n_in] *= np.linspace(0, 1, n_in)[:, None]
    if n_out > 0: x[-n_out:] *= np.linspace(1, 0, n_out)[:, None] ** 2
    peak = np.abs(x).max() + 1e-9
    return x * (10 ** (target_db / 20) / peak)


def cut(x, mode, o):
    length, pre = o.get("length"), o.get("pre", 0.01)
    if mode == "all":
        return [(0.0, len(x) / SR if length is None else min(len(x) / SR, length))]
    if mode == "range":
        return [(o["start"], o["end"])]
    segs = segments(x, o.get("thr", -28.0))
    if mode == "pick": chosen = [segs[i - 1] for i in o["pick"]]
    else: chosen = sorted(segs, key=lambda s: -s[2])[:o["n"]] if mode == "top" else segs[:o["n"]]
    chosen.sort(key=lambda s: s[0])
    spans = []
    for s in chosen:
        start = max(0.0, s[0] - pre)
        # Only a comparably loud hit (within 12 dB) counts as "the next one"; quieter blips are this hit's tail/echo.
        nxt = [t[0] for t in segs if t[0] > s[0] + 0.01 and t[2] >= s[2] * 0.25]
        end = start + length if length else s[1] + 0.1
        if nxt: end = min(end, nxt[0] - 0.01)   # never bleed into the next hit
        spans.append((start, end))
    return spans


# --- a synthetic bullet-into-drywall hit (nothing CC0 in the staged set): crack + plaster thump + grit ---
def synth_wall(seed):
    rng = np.random.default_rng(seed)
    n = int(0.28 * SR); t = np.arange(n) / SR
    crack = rng.standard_normal(n) * np.exp(-t * 90)
    thump = np.sin(2 * np.pi * (150 + 40 * rng.random()) * t) * np.exp(-t * 28)
    grit = np.convolve(rng.standard_normal(n), np.ones(12) / 12, "same") * np.exp(-t * 14) * 0.5
    x = 0.55 * crack + 0.8 * thump + grit
    return x[:, None].astype(np.float32)


# --- the bad-call show's helicopter and mugshot camera (nothing CC0 staged for either; no downloads) ---
def synth_rotor(seed=11):
    """A helicopter overhead: 12.5 Hz blade slap (the 'whop'), a low body throb, a faint turbine whine, wind noise. Exactly
    50 blade periods after the loop fold (5 s rendered, the last 1 s folded into the head by loop_seam), so the seam lands on the beat."""
    rng = np.random.default_rng(seed)
    n = int(5.0 * SR); t = np.arange(n) / SR
    bpf = 12.5
    phase = (t * bpf) % 1.0                                   # 0 at every blade pass
    attack = np.minimum(1.0, phase / 0.04)                    # ~3 ms rise: a thump, not a click train
    slap_env = attack * np.exp(-phase * 14.0) * (0.85 + 0.15 * np.sin(2 * np.pi * 0.5 * t))
    noise = rng.standard_normal(n)
    lowish = np.convolve(noise, np.ones(40) / 40, "same")     # ~1 kHz-ish box low-pass: a chesty slap, not a click
    slap = lowish * slap_env * 3.0 + np.sin(2 * np.pi * 62 * t) * attack * np.exp(-phase * 9.0) * 0.9
    throb = np.sin(2 * np.pi * 31 * t) * 0.25 * (0.6 + 0.4 * np.cos(2 * np.pi * bpf * t))
    whine = np.sin(2 * np.pi * (1180 + 6 * np.sin(2 * np.pi * 0.3 * t)) * t) * 0.02
    wind = np.convolve(noise[::-1], np.ones(160) / 160, "same") * 1.5
    x = slap + throb + whine + wind
    return x[:, None].astype(np.float32)


def synth_camera(seed=5):
    """A camera: mirror/shutter clack (two band-limited ticks 45 ms apart) and a flash capacitor whine rising after it."""
    rng = np.random.default_rng(seed)
    n = int(0.9 * SR); t = np.arange(n) / SR
    x = np.zeros(n)
    for at, g in ((0.0, 1.0), (0.045, 0.7)):
        i = int(at * SR); m = int(0.03 * SR)
        tick = rng.standard_normal(m) * np.exp(-np.arange(m) / SR * 220)
        tick = np.convolve(tick, [1, -0.6], "same")           # thin it out: plastic, not a thud
        x[i:i + m] += g * tick
    w0 = int(0.12 * SR)
    tw = t[w0:] - t[w0]
    f = 2200 + 5200 * np.minimum(1.0, tw / 0.6)
    x[w0:] += 0.05 * np.sin(2 * np.pi * np.cumsum(f) / SR) * np.minimum(1.0, tw / 0.1) * np.exp(-np.maximum(0, tw - 0.5) * 12)
    return x[:, None].astype(np.float32)


def synth_pry(seed=3):
    """A pry bar worked into the latch gap (pick #1's quiet pry): a gritty metal-on-wood scrape with a resonant squeal
    sliding up, a few stick-slip catches, and the latch giving with a dull click at the end. About 1.1 s."""
    rng = np.random.default_rng(seed)
    n = int(1.1 * SR); t = np.arange(n) / SR
    grit = rng.standard_normal(n)
    grit = np.convolve(grit, np.ones(6) / 6, "same") - np.convolve(grit, np.ones(40) / 40, "same")   # band-limited grit
    env = np.minimum(1.0, t / 0.08) * np.exp(-np.maximum(0, t - 0.85) * 18)
    catches = np.zeros(n)
    for at in (0.18, 0.37, 0.52, 0.71):                                 # stick-slip: the bar catches and jumps
        i = int((at + rng.uniform(-0.02, 0.02)) * SR); m = int(0.025 * SR)
        catches[i:i + m] += np.exp(-np.arange(m) / SR * 160) * rng.standard_normal(m)
    f = 1400 + 900 * t / 1.1 + 120 * np.sin(2 * np.pi * 7 * t)          # the squeal: metal resonance, wobbling as it slides
    squeal = 0.12 * np.sin(2 * np.pi * np.cumsum(f) / SR) * (0.4 + 0.6 * np.abs(np.sin(2 * np.pi * 3.1 * t)))
    x = env * (0.5 * grit + squeal) + 0.8 * catches
    i = int(0.93 * SR); m = int(0.06 * SR)                               # the latch gives
    x[i:i + m] += 0.9 * np.convolve(rng.standard_normal(m) * np.exp(-np.arange(m) / SR * 90), np.ones(12) / 12, "same")
    return x[:, None].astype(np.float32)


def synth_doorbell(seed=21):
    """A classic two-tone doorbell chime (the security monitor's RingDoorbell): a felt-tipped plunger strikes two tuned
    bars, E5 'ding' then C5 'dong' (a falling major third). Each strike: the bar's fundamental with a slightly detuned
    twin (slow beating), the near-octave and the bar's inharmonic partials (2.76x, 5.4x) dying away faster, a soft
    mallet tick on top. About 1.7 s; the dong rings out."""
    rng = np.random.default_rng(seed)
    n = int(1.75 * SR); t = np.arange(n) / SR
    x = np.zeros(n)
    for at, f0, g in ((0.0, 659.25, 0.9), (0.6, 523.25, 1.0)):
        i = int(at * SR); tt = t[:n - i]
        tone = (np.sin(2 * np.pi * f0 * tt) * np.exp(-tt * 2.6)
                + 0.3 * np.sin(2 * np.pi * f0 * 1.0035 * tt + 0.7) * np.exp(-tt * 2.6)
                + 0.28 * np.sin(2 * np.pi * 2.01 * f0 * tt + 0.3) * np.exp(-tt * 5.0)
                + 0.12 * np.sin(2 * np.pi * 2.76 * f0 * tt) * np.exp(-tt * 8.0)
                + 0.05 * np.sin(2 * np.pi * 5.40 * f0 * tt) * np.exp(-tt * 16.0))
        tone *= np.minimum(1.0, tt / 0.0025)                                 # 2.5 ms strike, no click from the sine start
        m = int(0.012 * SR)
        tick = np.zeros(len(tt)); tick[:m] = np.convolve(rng.standard_normal(m), np.ones(8) / 8, "same") * np.exp(-np.arange(m) / SR * 500)
        x[i:] += g * (tone + 0.25 * tick)
    return x[:, None].astype(np.float32)


def synth_motion(seed=0):
    """The security monitor's motion alert: a soft two-note electronic chime (A5 then E6), sine tones with a hint of the
    octave, 8 ms fade-in, short decay. About 0.5 s, quiet and unobtrusive."""
    n = int(0.52 * SR); t = np.arange(n) / SR
    x = np.zeros(n)
    for at, f, g in ((0.0, 880.0, 0.85), (0.15, 1318.51, 1.0)):
        i = int(at * SR); tt = t[:n - i]
        env = np.minimum(1.0, tt / 0.008) * np.exp(-tt * 9.0)
        x[i:] += g * env * (np.sin(2 * np.pi * f * tt) + 0.12 * np.sin(2 * np.pi * 2 * f * tt))
    return x[:, None].astype(np.float32)


def synth_handle(seed=9):
    """A locked lever handle tried (the intruder testing the door before anything else): three quick tries, each a metallic
    latch click as the lever goes down, the leaf rattling a few mm against its stop (dull clunks), and the lever snapping
    back up. About 0.8 s; VirtualDoor plays it muffled through the leaf."""
    rng = np.random.default_rng(seed)
    n = int(0.85 * SR)
    x = np.zeros(n)

    def click(at, g, f):                                   # metal tick: a noise burst and a short resonance
        i = int(at * SR); m = int(0.03 * SR)
        if i < 0 or i + m > n: return
        tt = np.arange(m) / SR
        burst = np.convolve(rng.standard_normal(m) * np.exp(-tt * 600), [1, -0.8], "same")
        x[i:i + m] += g * (0.6 * burst + 0.5 * np.sin(2 * np.pi * f * tt) * np.exp(-tt * 140))

    def clunk(at, g):                                      # the leaf knocking its stop: a damped low thud
        i = int(at * SR); m = int(0.06 * SR)
        if i < 0 or i + m > n: return
        tt = np.arange(m) / SR
        f = 170 + 60 * rng.random()
        body = np.convolve(rng.standard_normal(m), np.ones(20) / 20, "same") * np.exp(-tt * 80)
        x[i:i + m] += g * (np.sin(2 * np.pi * f * tt) * np.exp(-tt * 55) + 0.3 * body)

    for k, at in enumerate((0.005, 0.27, 0.52)):
        at += rng.uniform(0.0, 0.015)
        g = (1.0, 0.85, 1.0)[k]
        click(at, g, 3100 + 400 * rng.random())            # lever down: the bolt hits the strike plate
        clunk(at + 0.03, 0.8 * g)                          # the door rattles in the frame
        clunk(at + 0.075 + 0.01 * rng.random(), 0.45 * g)
        click(at + 0.16 + 0.02 * rng.random(), 0.55 * g, 2600 + 300 * rng.random())  # the lever springs back
    return x[:, None].astype(np.float32)


def synth_gravel_step(seed=101):
    """One footstep on a gravel path (the walk-up, on the monitor's side of things): the heel's dull thump, then the crunch
    of stones shifting under the rolling foot (dozens of tiny band-limited clicks, heel then toe), a few loose stones
    settling. About 0.35 s; each seed is a different step."""
    rng = np.random.default_rng(seed)
    n = int(0.36 * SR); t = np.arange(n) / SR
    x = np.zeros(n)
    f = 85 + 30 * rng.random()                                            # the heel landing
    x += 0.5 * np.sin(2 * np.pi * f * t) * np.exp(-t * 45) * np.minimum(1.0, t / 0.004)
    for phase, (t0, t1, count) in enumerate(((0.0, 0.12, 60), (0.09, 0.26, 45))):   # heel crunch, then toe
        for _ in range(count):
            at = rng.uniform(t0, t1) + rng.exponential(0.01)
            i = int(at * SR); m = int(rng.uniform(0.0015, 0.005) * SR)
            if i + m >= n: continue
            g = rng.uniform(0.15, 0.6) * (1.0 if phase == 0 else 0.75) * np.exp(-(at - t0) * 6)
            click = rng.standard_normal(m) * np.exp(-np.arange(m) / m * 4)
            x[i:i + m] += g * np.convolve(np.convolve(click, np.ones(4) / 4, "same"), [1, -0.5], "same")  # stone on stone
    for _ in range(4):                                                    # stones settling after
        i = int(rng.uniform(0.25, 0.33) * SR); m = int(0.003 * SR)
        x[i:i + m] += rng.uniform(0.05, 0.12) * rng.standard_normal(m)
    x = np.convolve(x, np.ones(5) / 5, "same")                            # gravel, not hiss: most energy 1-5 kHz
    return x[:, None].astype(np.float32)


def synth_gate_latch(seed=7):
    """A metal garden-gate latch: the thumb latch lifts (a light tick), the gate swings a hair and clanks against its post
    (a struck steel bar: inharmonic partials ringing out over ~0.3 s), and the loose latch bar rattles once. About 0.6 s."""
    rng = np.random.default_rng(seed)
    n = int(0.62 * SR); t = np.arange(n) / SR
    x = np.zeros(n)
    m = int(0.02 * SR); tt = np.arange(m) / SR                          # the latch lifting
    x[:m] += 0.35 * (np.sin(2 * np.pi * 3300 * tt) * np.exp(-tt * 260) + 0.4 * np.convolve(rng.standard_normal(m), np.ones(5) / 5, "same") * np.exp(-tt * 700))
    i = int(0.14 * SR); tt = t[:n - i]                                   # the clank on the post
    clank = (np.sin(2 * np.pi * 410 * tt) * np.exp(-tt * 11) + 0.7 * np.sin(2 * np.pi * 1127 * tt + 0.4) * np.exp(-tt * 16)
             + 0.45 * np.sin(2 * np.pi * 2210 * tt + 1.1) * np.exp(-tt * 24) + 0.25 * np.sin(2 * np.pi * 3650 * tt) * np.exp(-tt * 35))
    hit = np.zeros(len(tt)); k = int(0.008 * SR)
    hit[:k] = np.convolve(rng.standard_normal(k), np.ones(6) / 6, "same") * np.exp(-np.arange(k) / SR * 500)
    x[i:] += clank * np.minimum(1.0, tt / 0.0015) + 0.8 * hit
    for at, g in ((0.33, 0.35), (0.38, 0.2)):                            # the latch bar rattles in its keeper
        i = int(at * SR); m = int(0.025 * SR); tt = np.arange(m) / SR
        x[i:i + m] += g * np.sin(2 * np.pi * (2600 + 300 * rng.random()) * tt) * np.exp(-tt * 180)
    return x[:, None].astype(np.float32)


# key → (synth function, peak dBFS, fade s, loop[, variants: fn(seed=101…)])

def synth_cuffs_click(seed=31):
    """A handcuff swinging shut on a wrist (the police standoff's arrest): a bright metal latch click with a tiny second
    tick as the pawl seats. About 0.15 s."""
    rng = np.random.default_rng(seed)
    n = int(0.16 * SR); t = np.arange(n) / SR
    x = np.zeros(n)
    for at, g in ((0.0, 1.0), (0.035, 0.45)):
        i = int(at * SR); m = int(0.03 * SR); tt = np.arange(m) / SR
        burst = rng.standard_normal(m) * np.exp(-tt * 260)
        ring = 0.5 * np.sin(2 * np.pi * 3900 * tt) * np.exp(-tt * 140) + 0.3 * np.sin(2 * np.pi * 6100 * tt) * np.exp(-tt * 200)
        x[i:i + m] += g * (burst - np.convolve(burst, np.ones(8) / 8, "same") + ring)
    return x[:, None].astype(np.float32)


def synth_cuffs_ratchet(seed=32):
    """The ratchet run as the cuff is snugged down: a quick train of 7 metal teeth clicks, slowing a little. About 0.5 s."""
    rng = np.random.default_rng(seed)
    n = int(0.5 * SR); x = np.zeros(n)
    at = 0.0
    for k in range(7):
        i = int(at * SR); m = int(0.018 * SR); tt = np.arange(m) / SR
        tick = rng.standard_normal(m) * np.exp(-tt * 420) + 0.4 * np.sin(2 * np.pi * (4600 - 120 * k) * tt) * np.exp(-tt * 300)
        x[i:i + m] += (0.9 - 0.05 * k) * (tick - np.convolve(tick, np.ones(6) / 6, "same"))
        at += 0.045 + 0.008 * k
    return x[:, None].astype(np.float32)


def synth_taser(seed=33):
    """A Taser's arc: a harsh electrical crackle (a ~19 Hz pulse train of sparks) over a buzzing hum, 1.5 s, with a snap
    at the start."""
    rng = np.random.default_rng(seed)
    n = int(1.5 * SR); t = np.arange(n) / SR
    pulses = np.zeros(n)
    at = 0.0
    while at < 1.45:
        i = int(at * SR); m = int(0.012 * SR)
        pulses[i:i + m] += rng.standard_normal(m) * np.exp(-np.arange(m) / SR * 300) * rng.uniform(0.6, 1.0)
        at += 1 / 19 + rng.uniform(-0.004, 0.004)
    hum = 0.25 * np.sign(np.sin(2 * np.pi * 120 * t)) * (0.6 + 0.4 * np.sin(2 * np.pi * 19 * t))
    env = np.minimum(1.0, t / 0.01) * np.exp(-np.maximum(0, t - 1.35) * 25)
    x = env * (0.9 * pulses + hum)
    m = int(0.02 * SR); x[:m] += 1.2 * rng.standard_normal(m) * np.exp(-np.arange(m) / SR * 200)
    return x[:, None].astype(np.float32)


def synth_radio_squelch(seed=34):
    """A police radio key-up: a short two-note chirp, then a burst of band-limited static cut off. About 0.3 s."""
    rng = np.random.default_rng(seed)
    n = int(0.3 * SR); t = np.arange(n) / SR
    chirp = np.where(t < 0.05, np.sin(2 * np.pi * 1800 * t), np.where(t < 0.1, np.sin(2 * np.pi * 2300 * t), 0.0)) * 0.5
    st = rng.standard_normal(n); st = np.convolve(st, np.ones(4) / 4, "same") - np.convolve(st, np.ones(30) / 30, "same")
    static = np.where((t > 0.1) & (t < 0.27), 0.6 * st, 0.0) * np.exp(-np.maximum(0, t - 0.2) * 30)
    return (chirp + static)[:, None].astype(np.float32)


# --- shootable police vehicles (Brett 0.1.33: "shoot them as they pull up shoot out glass and hit driver maybe they crash") ---
def synth_glass(seed=101):
    """A round through a car window: the sharp crack of the pane, then a shower of tiny glass tinkles falling for ~0.5 s.
    Each seed is a different hit."""
    rng = np.random.default_rng(seed)
    n = int(0.6 * SR); t = np.arange(n) / SR
    crack = rng.standard_normal(n) * np.exp(-t * 180)
    crack = crack - np.convolve(crack, np.ones(10) / 10, "same")                  # bright: the high crack, no thud
    x = 0.9 * crack
    for _ in range(70):                                                           # shards: short high pings, thinning out
        at = rng.exponential(0.12) + 0.01
        i = int(at * SR); m = int(rng.uniform(0.004, 0.02) * SR)
        if i + m >= n: continue
        f = rng.uniform(3500, 9000); tt = np.arange(m) / SR
        x[i:i + m] += rng.uniform(0.05, 0.25) * np.exp(-at * 4) * np.sin(2 * np.pi * f * tt) * np.exp(-tt * rng.uniform(150, 400))
    return x[:, None].astype(np.float32)


def synth_metal(seed=101):
    """A round into a car's body: a hard tick and a short metallic ring (sheet steel), about 0.25 s."""
    rng = np.random.default_rng(seed)
    n = int(0.26 * SR); t = np.arange(n) / SR
    hit = rng.standard_normal(n) * np.exp(-t * 400)
    f = rng.uniform(1100, 1900)
    ring = sum(a * np.sin(2 * np.pi * f * r * t + rng.uniform(0, 6)) * np.exp(-t * d)
               for a, r, d in ((0.6, 1.0, 30), (0.35, 2.76, 45), (0.2, 5.4, 70)))
    body = 0.4 * np.sin(2 * np.pi * rng.uniform(180, 260) * t) * np.exp(-t * 35)
    return (0.7 * hit + ring + body)[:, None].astype(np.float32)


def synth_crash(seed=41):
    """A car (dead driver) rolling into the curb / the other car: a heavy low impact, crumpling sheet metal, a plastic
    crack, glass falling. About 1.4 s."""
    rng = np.random.default_rng(seed)
    n = int(1.4 * SR); t = np.arange(n) / SR
    thud = np.sin(2 * np.pi * 55 * t) * np.exp(-t * 9) + 0.6 * np.sin(2 * np.pi * 90 * t) * np.exp(-t * 14)
    crunch = rng.standard_normal(n)
    crunch = np.convolve(crunch, np.ones(18) / 18, "same") * np.exp(-t * 5) * (0.6 + 0.4 * np.sin(2 * np.pi * 23 * t) ** 2)
    x = 0.9 * thud + 1.4 * crunch
    for _ in range(40):                                                           # bits of metal and plastic
        at = rng.uniform(0.0, 0.5); i = int(at * SR); m = int(rng.uniform(0.01, 0.04) * SR)
        f = rng.uniform(600, 3000); tt = np.arange(m) / SR
        x[i:i + m] += rng.uniform(0.1, 0.35) * np.sin(2 * np.pi * f * tt) * np.exp(-tt * 90)
    for _ in range(60):                                                           # glass settling after
        at = rng.uniform(0.15, 1.1); i = int(at * SR); m = int(0.008 * SR)
        if i + m >= n: continue
        tt = np.arange(m) / SR
        x[i:i + m] += rng.uniform(0.03, 0.12) * np.sin(2 * np.pi * rng.uniform(4000, 8000) * tt) * np.exp(-tt * 300)
    return x[:, None].astype(np.float32)


def synth_horn(seed=42):
    """A car horn held down (the dead driver slumped on the wheel): a two-tone electric horn (~420 + 520 Hz, buzzy),
    steady, 2 s, looped by the game."""
    n = int(2.0 * SR); t = np.arange(n) / SR
    x = np.zeros(n)
    for f in (418.0, 523.0):                                                      # the classic pair (a major third apart)
        ph = 2 * np.pi * f * t
        x += np.tanh(2.2 * np.sin(ph)) + 0.35 * np.sin(2 * ph) + 0.2 * np.sin(3 * ph)
    x = np.convolve(x, np.ones(3) / 3, "same")                                    # take the digital edge off
    return (0.35 * x)[:, None].astype(np.float32)

def synth_car_arrive(seed=51):
    """A car pulling up outside and parking (a Marshal night's hitman car, PoliceCars.Arrival; Brett 0.1.56: "the bad guys
    seemingly come from no where"). An engine coming up the street: firing pulses through two exhaust resonances (so it reads
    on the headset's small speakers, not only as sub-bass), its revs and loudness following PoliceCars' ease-out drive in
    (fast in from ~60 m, braking to the stop ~11-16 m off at 3.8 s); tyre hiss with its speed; the springs settling at the
    stop; ~0.8 s of idle; the key off at 4.6 s. 5 s, mono. The game stops it with the car's lights (0.7 s after the stop)."""
    rng = np.random.default_rng(seed)
    dur, stop, off = 5.0, 3.8, 4.6
    n = int(dur * SR); t = np.arange(n) / SR
    u = np.clip(t / stop, 0.0, 1.0)
    speed = np.where(t < stop, 1.0 - u, 0.0)                           # the ease-out's speed (PoliceCars.Pose: s = 1-(1-u)^2)
    s = 1.0 - (1.0 - u) ** 2
    along = 60.0 - 49.0 * s                                            # 60 m down the street → parked
    near = (np.hypot(11.0, 11.0) / np.hypot(along, 11.0)) ** 1.5       # loudness ~ 1/distance (a little more: it must swell)
    # Firing frequency (a four-cylinder: 2 pulses a turn): ~1900 rpm coming in, falling to a 780 rpm idle as it brakes; at the
    # key off it runs down in ~0.3 s.
    idle = 26.0 + 1.2 * np.sin(2 * np.pi * 0.7 * t)
    f = np.where(t < stop, idle + 38.0 * speed ** 0.8, idle)
    f = np.where(t > off, idle * np.exp(-(t - off) / 0.18), f)
    phase = np.cumsum(f) / SR
    pulses = np.zeros(n)
    k = np.nonzero(np.diff(np.floor(phase)) > 0)[0] + 1                # one pulse per firing
    pulses[k] = 1.0 + 0.18 * rng.standard_normal(len(k))               # uneven, as a real engine is
    # Exhaust: each firing rings two resonances (~110 Hz body, ~380 Hz rasp) with a short noise puff; FFT convolution.
    m = int(0.09 * SR); tt = np.arange(m) / SR
    ir = (0.6 * np.sin(2 * np.pi * 110 * tt) * np.exp(-tt * 45) + np.sin(2 * np.pi * 380 * tt) * np.exp(-tt * 70)
          + 0.5 * np.sin(2 * np.pi * 820 * tt) * np.exp(-tt * 140) + 0.2 * np.sin(2 * np.pi * 1600 * tt) * np.exp(-tt * 220)
          + 0.3 * np.convolve(rng.standard_normal(m), np.ones(8) / 8, "same") * np.exp(-tt * 260))
    L = 1 << int(np.ceil(np.log2(n + m)))
    engine = np.fft.irfft(np.fft.rfft(pulses, L) * np.fft.rfft(ir, L), L)[:n]
    engine = np.tanh(1.6 * engine / (np.abs(engine).max() + 1e-9))     # a little growl
    power = np.where(t < stop, 0.7 + 0.3 * speed, 0.55)                # on the throttle coming in, idling after
    power = np.where(t > off, 0.5 * np.exp(-(t - off) / 0.12), power)  # key off
    # Tyres on the road: band-limited hiss with its speed (gone at the stop), a scrub as it brakes.
    w = rng.standard_normal(n)
    hiss = np.convolve(w, np.ones(12) / 12, "same") - np.convolve(w, np.ones(120) / 120, "same")   # ~0.4-4 kHz
    tyres = hiss * (0.55 * speed ** 1.3 + 0.25 * np.exp(-((t - (stop - 0.35)) / 0.25) ** 2) * (t < stop))
    # The springs settling as it stops: a soft low thump.
    settle = np.zeros(n); i = int(stop * SR); tt = t[:n - i]
    settle[i:] = np.sin(2 * np.pi * 75 * tt) * np.exp(-tt * 14) * np.minimum(1.0, tt / 0.01)
    x = near * (power * engine + 0.5 * tyres) + 0.35 * settle
    x *= np.minimum(1.0, t / 0.06)                                     # (no click at the start)
    return x[:, None].astype(np.float32)


SYNTH = {"outcome.rotor": (synth_rotor, -3, 0.0, True), "outcome.camera": (synth_camera, -2, 0.05, False),
         "door.pry": (synth_pry, -4, 0.03, False), "door.bell": (synth_doorbell, -3, 0.3, False),
         "cam.motion": (synth_motion, -8, 0.08, False), "door.handle": (synth_handle, -3, 0.05, False),
         "steps.gravel": (synth_gravel_step, -6, 0.03, False, 5), "gate.latch": (synth_gate_latch, -3, 0.08, False),
         # the police standoff (research/22)
         "cuffs.click": (synth_cuffs_click, -3, 0.03, False), "cuffs.ratchet": (synth_cuffs_ratchet, -4, 0.04, False),
         "taser.crackle": (synth_taser, -4, 0.05, False), "radio.squelch": (synth_radio_squelch, -8, 0.03, False),
         # shootable police vehicles
         "glass.hit": (synth_glass, -3, 0.05, False, 3), "metal.hit": (synth_metal, -4, 0.04, False, 3),
         "car.crash": (synth_crash, -2, 0.2, False), "car.horn": (synth_horn, -6, 0.0, True),
         # a Marshal night's hitman car pulling up (Brett 0.1.56)
         "car.arrive": (synth_car_arrive, -2, 0.12, False)}


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--only", default="", help="only keys starting with this prefix")
    args = ap.parse_args()
    if not os.path.isdir(SRC): sys.exit(f"missing {SRC} (gitignored staging folder; see SOURCES.csv to re-download)")
    files = [os.path.join(d, f) for d in (os.path.join(SRC, "ffsl"), os.path.join(SRC, "freesound")) if os.path.isdir(d) for f in os.listdir(d)]
    # Only this script's keys are rebuilt; other folders (vo.* voice lines from make_tts.py, Brett's recordings) are kept.
    os.makedirs(OUT, exist_ok=True)
    rebuilt = {k for k in {r[0] for r in RECIPES} | {"impact.wall"} | set(SYNTH) if k.startswith(args.only)}
    if not rebuilt: sys.exit(f"no recipe key starts with '{args.only}'")
    # Delete the old wavs but keep their .meta files, so a re-cut clip keeps its Unity GUID (SfxLibrary refs survive).
    for key in rebuilt:
        d = os.path.join(OUT, key)
        if os.path.isdir(d):
            for f in os.listdir(d):
                if f.endswith(".wav"): os.remove(os.path.join(d, f))
    manifest, counts = [], {}
    if args.only:   # keep the other keys' provenance rows
        try: manifest = [m for m in json.load(open(os.path.join(OUT, "manifest.json"))) if m["clip"].split("/")[0] not in rebuilt]
        except (OSError, ValueError): pass
    with tempfile.TemporaryDirectory() as tmp:
        for key, src, mode, o in RECIPES:
            if key not in rebuilt: continue
            match = [f for f in files if os.path.basename(f).startswith(src + "_") or os.path.basename(f).startswith(src + "__")
                     or os.path.splitext(os.path.basename(f))[0] == src]
            if len(match) != 1: sys.exit(f"{key}: source '{src}' matched {match}")
            x = decode(match[0], tmp)
            for start, end in cut(x, mode, o):
                clip = finish(x[int(start * SR):int(end * SR)], o.get("target", -1.0), o.get("fade", 0.05), o.get("stereo", False), o.get("loop", False))
                counts[key] = counts.get(key, 0) + 1
                d = os.path.join(OUT, key); os.makedirs(d, exist_ok=True)
                name = f"{key}_{counts[key]:02d}.wav"
                save(os.path.join(d, name), clip)
                manifest.append(dict(clip=f"{key}/{name}", source=os.path.basename(match[0]), start=round(start, 3), end=round(end, 3),
                                     seconds=round(len(clip) / SR, 3), channels=int(clip.shape[1])))
        for i in range(4 if "impact.wall" in rebuilt else 0):
            key = "impact.wall"; counts[key] = counts.get(key, 0) + 1
            d = os.path.join(OUT, key); os.makedirs(d, exist_ok=True)
            name = f"{key}_{counts[key]:02d}.wav"
            save(os.path.join(d, name), finish(synth_wall(i), -4, 0.05, False))
            manifest.append(dict(clip=f"{key}/{name}", source="synthesized (scripts/prepare_sfx.py synth_wall)", start=0, end=0.28, seconds=0.28, channels=1))
    for key, spec in SYNTH.items():
        if key not in rebuilt: continue
        fn, target, fade, loop = spec[:4]
        variants = spec[4] if len(spec) > 4 else 1          # (…, n): n takes, fn(seed=101…)
        d = os.path.join(OUT, key); os.makedirs(d, exist_ok=True)
        for v in range(variants):
            name = f"{key}_{v + 1:02d}.wav"
            clip = finish(fn() if variants == 1 else fn(seed=101 + v), target, fade, False, loop)
            save(os.path.join(d, name), clip); counts[key] = v + 1
            manifest.append(dict(clip=f"{key}/{name}", source=f"synthesized (scripts/prepare_sfx.py {fn.__name__})", start=0,
                                 end=round(len(clip) / SR, 3), seconds=round(len(clip) / SR, 3), channels=1))
    for key in rebuilt:   # metas of variants that no longer exist (a recipe now yields fewer clips)
        d = os.path.join(OUT, key)
        if os.path.isdir(d):
            for f in os.listdir(d):
                if f.endswith(".wav.meta") and not os.path.exists(os.path.join(d, f[:-5])): os.remove(os.path.join(d, f))
    json.dump(manifest, open(os.path.join(OUT, "manifest.json"), "w"), indent=1)
    for k in sorted(counts): print(f"{k:22} {counts[k]} clip(s)")
    total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
    print(f"{len(manifest)} clips, {total / 1e6:.1f} MB → {OUT}")


if __name__ == "__main__":
    main()
