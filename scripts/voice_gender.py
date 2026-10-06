#!/usr/bin/env python3
"""Does a voice take read as a man? The gate make_tts applies to every take of a male character (all of them: player,
intruders, Mike, police), plus a report over the intruder clips in the library.

Brett 0.1.16/0.1.17: "the attacker's voice should never be a woman if he's a man" / "still female voice on male attacker
sometimes". Method = the 2026-09-24 voice audit (Logs/voice-audit/voice_audit.py, Logs/voice-gender-audit.json):
  - F0: YIN at 16 kHz, 30 ms frames, 10 ms hop, 55-900 Hz, threshold 0.15; median / p90 over voiced frames.
  - Vocal-tract length: LPC order 12 at 11.025 kHz on voiced frames, uniform-tube fit of F1-F4, VTL = 35000 / (2 dF).
  - FLI (female-likeness index): the take's speaker embedding projected on a female-minus-male axis built from anchor
    voices, scaled so the anchors average +-1. FLI = mean(say-VoiceEncoder, neural-VoiceEncoder, neural-CAMPPlus);
    "say" anchors = 7 female + 8 male macOS voices x 2 lines, "neural" anchors = 4 Qwen VoiceDesign prompts rendered as
    "... woman" and "... man" with identical wording. Embeddings: Chatterbox VoiceEncoder (256-d) and CAMPPlus (192-d),
    loaded straight from chatterbox-fp16's model.safetensors (no TTS model is loaded).
  - Screams are heard through the door (Intruder.Cry: 650 Hz low-pass), so they are judged on a muffled FLI built from
    the neural anchors through the same low-pass.
The axes live in scripts/tts_refs/gender_axes.json (small; rebuild with `calibrate` from the anchor wavs).

    cd ../tools/tts && HF_HOME=$PWD/hf HF_HUB_OFFLINE=1 .venv/bin/python <proj>/scripts/voice_gender.py <command>
      calibrate [say_dir qwen_dir]        rebuild gender_axes.json (default dirs: Logs/voice-audit/anchors_{say,qwen})
      measure <wav> [--as CHAR] [--scream]  one file, with the gate verdict against CHAR's reference
      report [out.json]                   every intruder clip in Assets/_Game/Audio/Clips + the refs, gate verdicts
"""
import glob, json, math, os, re, sys
import numpy as np
from scipy.linalg import solve_toeplitz
from scipy.signal import butter, resample_poly, sosfilt

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REFS_DIR = os.path.join(ROOT, "scripts", "tts_refs")
AXES_PATH = os.path.join(REFS_DIR, "gender_axes.json")
CLIPS = os.path.join(ROOT, "Assets", "_Game", "Audio", "Clips")
SR = 16000
DOOR_LP_HZ = 650   # Intruder.Cry / VirtualDoor.PlayVoiceThrough muffle

# ------------------------------------------------------------------ the gate (calibrated on the audit's anchors)
# Anchors (Logs/voice-gender-audit.json): every male anchor FLI <= -0.23 (neural men -0.56..-1.00), every female >= +0.17
# (neural women +0.58..+1.08). The male characters' own references sit at -0.52..-0.68 (attacker, knife, gunman, player).
# A take must land clearly on the male side, past the weakest male anchor with a margin:
SPEECH_FLI_MAX = -0.30
# Pitch relative to the speaker's own reference: a Chatterbox take that drifts far above its reference is the "voice turned
# into a woman" failure even when the embedding still says male. +4 semitones leaves room for shouting; below 180 Hz (the top
# of the usual adult-male speaking range) a take is never rejected for this, so a deep reference (knife 126 Hz) can shout.
SPEECH_F0_OVER_REF_ST, MALE_SPEECH_F0_TOP = 4.0, 180.0
# Absolute pitch ceiling for speech: the neural male anchors (shouting/panicked) reach 337 Hz median, the neural female
# ones run 289-405. Above 350 Hz a man's voice is falsetto/child territory whatever the timbre says.
SPEECH_F0_MAX_HZ = 350.0
# Vocal-tract length: the Qwen man/woman pairs split at 15.1 (women max) / 15.7 (men min) cm, but the say anchors overlap
# (LPC is noisy on short takes: female Kathy/Sandy 17-19 cm, male Albert 15.2). So VTL only rejects together with a
# female-range pitch and a weak embedding read: VTL < 15.5 cm AND F0 > 165 Hz AND FLI > -0.45.
VTL_MIN_CM, VTL_F0_GUARD, VTL_FLI_GUARD = 15.5, 165.0, -0.45
VTL_MIN_FRAMES = 20
# Word-less screams through the door: muffled FLI, pitch (a shriek above ~450 Hz median reads as a woman through a wall),
# and it must still be the man it was cued with (Dia drifts off its reference: similarity 0.52-0.68 in the audit).
SCREAM_FLI_LP_MAX = -0.20
SCREAM_F0_MEDIAN_MAX, SCREAM_F0_P90_MAX = 450.0, 700.0
SCREAM_OWN_SIM_MIN = 0.65
# Grunts (clips under 0.6 s: the freesound pain/effort grunts). Calibrated on the loudest 0.45 s of every anchor: male crops
# FLI <= -0.22 (bar the novelty voice Albert at 918 Hz), female crops >= -0.08; neural male crops F0 <= 372 Hz, neural
# female crops >= 400 Hz. So a grunt passes at FLI <= -0.22 (as male as the weakest male crop) and F0 <= 385 Hz.
SHORT_S, SHORT_FLI_MAX, SHORT_F0_MAX_HZ = 0.6, -0.22, 385.0

def thresholds():
    return {"speech": {"fli_max": SPEECH_FLI_MAX, "f0_median_max_hz": SPEECH_F0_MAX_HZ,
                       "f0_over_ref_semitones_max": SPEECH_F0_OVER_REF_ST, "f0_over_ref_applies_above_hz": MALE_SPEECH_F0_TOP,
                       "vtl_rule": f"reject if vtl < {VTL_MIN_CM} cm and F0 > {VTL_F0_GUARD:.0f} Hz and FLI > {VTL_FLI_GUARD}"},
            "grunt_under_s": SHORT_S, "grunt": {"fli_max": SHORT_FLI_MAX, "f0_median_max_hz": SHORT_F0_MAX_HZ},
            "scream": {"fli_lp650_max": SCREAM_FLI_LP_MAX, "f0_median_max_hz": SCREAM_F0_MEDIAN_MAX,
                       "f0_p90_max_hz": SCREAM_F0_P90_MAX, "sim_to_own_ref_ve_min": SCREAM_OWN_SIM_MIN}}


# ------------------------------------------------------------------ acoustics
def to16(x, sr):
    x = np.asarray(x, dtype=np.float64).reshape(-1) if np.ndim(x) == 1 else np.asarray(x, dtype=np.float64).mean(axis=1)
    if sr != SR:
        g = math.gcd(int(sr), SR); x = resample_poly(x, SR // g, int(sr) // g)
    return x.astype(np.float32)


def load16(path):
    import soundfile as sf
    x, sr = sf.read(path, dtype="float32", always_2d=True)
    return to16(x.mean(axis=1), sr)


def muffle(x, hz=DOOR_LP_HZ):
    return sosfilt(butter(2, hz, "low", fs=SR, output="sos"), x).astype(np.float32)


def yin(x, fmin=55.0, fmax=900.0, win=0.03, hop=0.01, thresh=0.15, vthresh=0.3, floor_db=-30.0):
    W, H = int(win * SR), int(hop * SR)
    tmin, tmax = int(SR / fmax), int(SR / fmin)
    n = (len(x) - W - tmax) // H
    if n <= 0:
        x = np.concatenate([x, np.zeros(W + tmax + H - len(x) + 1, np.float32)]); n = 1
    rms = np.array([np.sqrt(np.mean(x[i * H:i * H + W] ** 2) + 1e-12) for i in range(n)])
    db = 20 * np.log10(rms / (rms.max() + 1e-12))
    f0 = np.full(n, np.nan)
    for i in range(n):
        if db[i] < floor_db: continue
        s = x[i * H:i * H + W + tmax].astype(np.float64)
        a = s[:W]
        e1 = np.sum(a * a)
        c2 = np.concatenate([[0.0], np.cumsum(s * s)])
        e2 = c2[np.arange(tmax + 1) + W] - c2[np.arange(tmax + 1)]
        N = 1 << (len(s) + W).bit_length()
        cross = np.fft.irfft(np.fft.rfft(s, N) * np.conj(np.fft.rfft(a, N)), N)[:tmax + 1]
        d = e1 + e2 - 2 * cross
        d[0] = 0
        cm = np.ones_like(d)
        cs = np.cumsum(d[1:])
        cm[1:] = d[1:] * np.arange(1, tmax + 1) / np.maximum(cs, 1e-12)
        seg = cm[tmin:tmax]
        below = np.nonzero(seg < thresh)[0]
        if len(below):
            t = below[0]
            while t + 1 < len(seg) and seg[t + 1] < seg[t]: t += 1
        else:
            t = int(np.argmin(seg))
        if seg[t] > vthresh: continue
        tau = t + tmin
        if 1 <= tau < tmax:
            y0, y1, y2 = cm[tau - 1], cm[tau], cm[tau + 1]
            den = y0 - 2 * y1 + y2
            tau = tau + (0.5 * (y0 - y2) / den if abs(den) > 1e-12 else 0)
        f0[i] = SR / tau
    return f0, db


def formants(x16, f0, db):
    fs = 11025
    x = resample_poly(x16, 441, 640).astype(np.float64)
    x = np.append(x[0], x[1:] - 0.97 * x[:-1])
    W = int(0.025 * fs)
    win = np.hamming(W)
    out = []
    for i in range(len(f0)):
        if np.isnan(f0[i]) or db[i] < -20: continue
        a0 = int(i * 0.01 * fs + 0.015 * fs - W / 2)
        if a0 < 0 or a0 + W > len(x): continue
        fr = x[a0:a0 + W] * win
        r = np.correlate(fr, fr, "full")[W - 1:W + 13]
        if r[0] <= 0: continue
        try: a = solve_toeplitz(r[:12], r[1:13])
        except Exception: continue   # noqa: BLE001 (singular frame)
        roots = np.roots(np.concatenate([[1.0], -a]))
        roots = roots[np.imag(roots) > 0]
        fq = np.angle(roots) * fs / (2 * np.pi)
        bw = -fs / np.pi * np.log(np.abs(roots) + 1e-12)
        fq = np.sort(fq[(fq > 150) & (fq < fs / 2 - 100) & (bw < 450)])
        if len(fq) >= 4: out.append(fq[:4])
    if len(out) < 3: return {}
    F = np.array(out)
    k = np.array([0.5, 1.5, 2.5, 3.5])
    dF = float(np.median((F * k).sum(1) / (k * k).sum()))
    return {"vtl_cm": round(35000.0 / (2 * dF), 1), "formant_frames": len(out)}


# ------------------------------------------------------------------ speaker embeddings
_ENC = {}


def _encoders():
    if _ENC: return _ENC["ve"], _ENC["cam"]
    import mlx.core as mx
    from mlx_audio.tts.models.chatterbox.voice_encoder import VoiceEncoder
    from mlx_audio.tts.models.chatterbox.s3gen.xvector import CAMPPlus
    hf = os.environ.get("HF_HOME") or os.path.join(os.path.dirname(ROOT), "tools", "tts", "hf")
    snap = glob.glob(os.path.join(hf, "hub/models--mlx-community--chatterbox-fp16/snapshots/*/model.safetensors"))
    if not snap: raise FileNotFoundError(f"chatterbox-fp16 weights not under {hf} (set HF_HOME=../tools/tts/hf)")
    Wt = mx.load(snap[0])
    ve = VoiceEncoder()
    ve.load_weights([(k[3:], v.astype(mx.float32)) for k, v in Wt.items() if k.startswith("ve.")], strict=False)
    ve.eval()
    cam = CAMPPlus()
    spk = {k[len("s3gen.speaker_encoder."):]: v.astype(mx.float32) for k, v in Wt.items() if k.startswith("s3gen.speaker_encoder.")}
    cam.load_weights(list(cam.sanitize(spk).items()), strict=False)
    cam.eval()
    _ENC.update(ve=ve, cam=cam)
    return ve, cam


def embed(x16):
    import mlx.core as mx
    ve, cam = _encoders()
    a = mx.array(np.asarray(x16, dtype=np.float32))
    e1 = np.array(ve.embeds_from_wavs([a], SR, as_spk=True, trim_top_db=None)).astype(np.float64).reshape(-1)
    e2 = np.array(cam.inference(a)).astype(np.float64).reshape(-1)
    return e1 / np.linalg.norm(e1), e2 / np.linalg.norm(e2)


def cos(a, b): return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-12))


# ------------------------------------------------------------------ axes
_AXES = {}


def axes():
    if not _AXES:
        d = json.load(open(AXES_PATH))
        for name, ax in d["axes"].items():
            _AXES[name] = (np.array(ax["dir"]), np.array(ax["mid"]), float(ax["scale"]))
    return _AXES


def project(e, axis):
    d, mid, sc = axes()[axis]
    return round(float(np.dot(e - mid, d)) / sc, 2)


def calibrate(say_dir, qwen_dir):
    """Rebuild gender_axes.json: direction = mean(female) - mean(male), midpoint between, scaled so |anchors| average 1."""
    def load_set(d, lp=False):
        out = {}
        for p in sorted(glob.glob(os.path.join(d, "*.wav"))):
            x = load16(p)
            out[os.path.basename(p)] = embed(muffle(x) if lp else x)
        return out

    def make(anch, i):
        fem = np.mean([e[i] for n, e in anch.items() if n.startswith(("f_", "qf_"))], axis=0)
        mal = np.mean([e[i] for n, e in anch.items() if n.startswith(("m_", "qm_"))], axis=0)
        d = fem - mal; d /= np.linalg.norm(d); mid = (fem + mal) / 2
        sc = float(np.mean([abs(np.dot(e[i] - mid, d)) for e in anch.values()]))
        return {"dir": [round(float(v), 7) for v in d], "mid": [round(float(v), 7) for v in mid], "scale": round(sc, 7)}

    say, qwen, qwen_lp = load_set(say_dir), load_set(qwen_dir), load_set(qwen_dir, lp=True)
    out = {"about": "voice_gender.py gender axes (female minus male); FLI = mean(say_ve, neural_ve, neural_cam); "
                    "screams use mean(neural_lp650_ve, neural_lp650_cam)",
           "anchors": {"say": sorted(say), "neural": sorted(qwen)},
           "axes": {"say_ve": make(say, 0), "neural_ve": make(qwen, 0), "neural_cam": make(qwen, 1),
                    "neural_lp650_ve": make(qwen_lp, 0), "neural_lp650_cam": make(qwen_lp, 1)}}
    json.dump(out, open(AXES_PATH, "w"))
    _AXES.clear()
    return out


# ------------------------------------------------------------------ measure + judge
def measure(x16, scream=False):
    """Acoustics + FLI of one take (16 kHz mono float). Screams also get the through-the-door (650 Hz) FLI."""
    x16 = np.asarray(x16, dtype=np.float32)
    f0, db = yin(x16)
    v = f0[~np.isnan(f0)]
    m = {"seconds": round(len(x16) / SR, 2), "voiced_frac": round(len(v) / max(1, len(f0)), 2)}
    if len(v) >= 3:
        m.update(f0_median=round(float(np.median(v))), f0_p90=round(float(np.percentile(v, 90))))
    m.update(formants(x16, f0, db))
    ve, cam = embed(x16)
    m["fli_parts"] = {"say_ve": project(ve, "say_ve"), "neural_ve": project(ve, "neural_ve"), "neural_cam": project(cam, "neural_cam")}
    m["fli"] = round(sum(m["fli_parts"].values()) / 3, 2)
    m["_ve"] = ve
    if scream:
        lve, lcam = embed(muffle(x16))
        m["fli_lp650"] = round((project(lve, "neural_lp650_ve") + project(lcam, "neural_lp650_cam")) / 2, 2)
    return m


_REFS = {}


def ref_measure(character):
    """The character's reference (scripts/tts_refs/<character>.wav), measured once per process."""
    path = os.path.join(REFS_DIR, f"{character}.wav")
    key = (character, os.path.getmtime(path))
    if key not in _REFS: _REFS[key] = measure(load16(path))
    return _REFS[key]


def judge(m, ref=None, scream=False):
    """(ok, reasons). ok = the take reads as a man by every measure that applies."""
    why = []
    f0 = m.get("f0_median")
    if scream:
        if m.get("fli_lp650") is not None and m["fli_lp650"] > SCREAM_FLI_LP_MAX:
            why.append(f"muffled FLI {m['fli_lp650']:+.2f} > {SCREAM_FLI_LP_MAX:+.2f}")
        if f0 and f0 > SCREAM_F0_MEDIAN_MAX: why.append(f"F0 {f0} Hz > {SCREAM_F0_MEDIAN_MAX:.0f}")
        if m.get("f0_p90") and m["f0_p90"] > SCREAM_F0_P90_MAX: why.append(f"F0 p90 {m['f0_p90']} Hz > {SCREAM_F0_P90_MAX:.0f}")
        if ref is not None:
            s = cos(m["_ve"], ref["_ve"]); m["sim_own_ve"] = round(s, 2)
            if s < SCREAM_OWN_SIM_MIN: why.append(f"sim to own ref {s:.2f} < {SCREAM_OWN_SIM_MIN}")
        return not why, why
    if m["seconds"] < SHORT_S:   # a grunt
        if m["fli"] > SHORT_FLI_MAX: why.append(f"grunt FLI {m['fli']:+.2f} > {SHORT_FLI_MAX:+.2f}")
        if f0 and f0 > SHORT_F0_MAX_HZ: why.append(f"grunt F0 {f0} Hz > {SHORT_F0_MAX_HZ:.0f}")
        return not why, why
    if m["fli"] > SPEECH_FLI_MAX: why.append(f"FLI {m['fli']:+.2f} > {SPEECH_FLI_MAX:+.2f}")
    if f0 and f0 > SPEECH_F0_MAX_HZ: why.append(f"F0 {f0} Hz > {SPEECH_F0_MAX_HZ:.0f}")
    if f0 and ref is not None and ref.get("f0_median"):
        st = 12 * math.log2(f0 / ref["f0_median"]); m["f0_vs_ref_st"] = round(st, 1)
        if st > SPEECH_F0_OVER_REF_ST and f0 > MALE_SPEECH_F0_TOP:
            why.append(f"F0 {f0} Hz, +{st:.1f} st over its reference (> {SPEECH_F0_OVER_REF_ST:.0f})")
    if (m.get("vtl_cm") and m.get("formant_frames", 0) >= VTL_MIN_FRAMES and m["vtl_cm"] < VTL_MIN_CM
            and f0 and f0 > VTL_F0_GUARD and m["fli"] > VTL_FLI_GUARD):
        why.append(f"VTL {m['vtl_cm']} cm < {VTL_MIN_CM} with F0 {f0} Hz and FLI {m['fli']:+.2f}")
    if ref is not None: m["sim_own_ve"] = round(cos(m["_ve"], ref["_ve"]), 2)
    return not why, why


def public(m):
    return {k: v for k, v in m.items() if not k.startswith("_")}


def gender_row(m):
    """The numbers make_tts records per take (neural_info, Logs/tts-neural-summary.json)."""
    keys = ("fli", "fli_lp650", "f0_median", "f0_p90", "f0_vs_ref_st", "vtl_cm", "sim_own_ve", "voiced_frac", "fli_parts")
    return {k: m[k] for k in keys if m.get(k) is not None}


def check(x, sr, character=None, scream=False):
    """make_tts entry point: (ok, reasons, numbers) for one take at any sample rate."""
    m = measure(to16(x, sr), scream=scream)
    ok, why = judge(m, ref_measure(character) if character else None, scream=scream)
    return ok, why, public(m)


# ------------------------------------------------------------------ report over the library
SPEAKERS = [("vo.attacker.", "attacker"), ("vo.knife.", "knife"), ("vo.burglar.", "burglar"), ("vo.surrender.", "burglar"),
            ("vo.gunman.", "gunman"), ("vo.neighbor.", "mike"),
            # a US Marshal night (research/27 §5; make_tts NEURAL_SPEAKERS)
            ("vo.ricky.", "ricky"), ("vo.brother.", "sal"), ("vo.deputy.", "deputy"), ("vo.relief.", "deputy"),
            ("vo.hitman.street.", "gunman"), ("vo.hitman.pro.", "operator")]
SHARED = ("intruder.pain", "intruder.pain.scream", "intruder.death", "intruder.swing", "drunk.mumble")


def intruder_keys():
    return [k for k in sorted(os.listdir(CLIPS)) if os.path.isdir(os.path.join(CLIPS, k)) and
            (k.startswith(tuple(p for p, _ in SPEAKERS)) or k in SHARED)]


def report(keys=None, only_neural_and_real=True):
    rows = []
    for key in keys or intruder_keys():
        ch = next((c for p, c in SPEAKERS if key.startswith(p)), None)
        scream = key in ("intruder.pain.scream", "intruder.death")
        ref = (ref_measure(ch) if ch else ref_measure("attacker") if key == "intruder.pain.scream"
               else ref_measure("mike") if key == "drunk.mumble" else None)
        for f in sorted(os.listdir(os.path.join(CLIPS, key))):
            if not f.endswith(".wav"): continue
            if only_neural_and_real and ch and re.search(r"_01\.wav$", f): continue   # say placeholders (never used)
            m = measure(load16(os.path.join(CLIPS, key, f)), scream=scream)
            ok, why = judge(m, ref, scream=scream)
            rows.append({"key": key, "clip": f, "voice": ch or "shared", "ok": ok, "why": why, **public(m)})
            r = rows[-1]
            print(f"{key:30} {f:36} {'ok ' if ok else 'BAD'} F0 {r.get('f0_median', '-'):>4}/{r.get('f0_p90', '-'):>4} "
                  f"vtl {r.get('vtl_cm', '-'):>5} FLI {r['fli']:+.2f}" + (f" lp {r['fli_lp650']:+.2f}" if 'fli_lp650' in r else "")
                  + f" {'; '.join(why)}", flush=True)
    return rows


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else ""
    if cmd == "calibrate":
        say_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(ROOT, "Logs", "voice-audit", "anchors_say")
        qwen_dir = sys.argv[3] if len(sys.argv) > 3 else os.path.join(ROOT, "Logs", "voice-audit", "anchors_qwen")
        calibrate(say_dir, qwen_dir); print("wrote", AXES_PATH)
    elif cmd == "measure":
        ch = sys.argv[sys.argv.index("--as") + 1] if "--as" in sys.argv else None
        scream = "--scream" in sys.argv
        for p in [a for a in sys.argv[2:] if a.endswith(".wav")]:
            ok, why, m = check(load16(p), SR, ch, scream)
            print(os.path.basename(p), "ok" if ok else "BAD", json.dumps(m), why)
    elif cmd == "refs":
        for p in sorted(glob.glob(os.path.join(REFS_DIR, "*.wav"))):
            m = measure(load16(p)); print(f"{os.path.basename(p):28} F0 {m.get('f0_median')}/{m.get('f0_p90')} vtl {m.get('vtl_cm')} FLI {m['fli']:+.2f} {m['fli_parts']}")
    elif cmd == "report":
        rows = report()
        if len(sys.argv) > 2: json.dump({"thresholds": thresholds(), "clips": rows}, open(sys.argv[2], "w"), indent=1)
    else:
        print(__doc__)
