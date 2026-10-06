#!/usr/bin/env python3
"""Voice bake-off (docs/research/17-local-tts-voices.md §5): render 8 test lines with local open models for blind A/B.

    HF_HOME=../tools/tts/hf HF_HUB_OFFLINE=1 ../tools/tts/.venv/bin/python scripts/tts_bakeoff.py
    ...  scripts/tts_bakeoff.py --steps say,refs,qwen      # just some steps (resumable: finished line/engine pairs are kept)
    ...  scripts/tts_bakeoff.py --steps page               # rebuild m4a copies + tts_bakeoff/index.html only

Engines (per line, from VoiceLines.cs by key, so text never drifts from the subtitles):
  say    the current placeholder clip in Assets/_Game/Audio/Clips/<key>/<key>_01.wav (baseline, copied as-is: it already
         went through make_tts.py's chain)
  qwen   A: Qwen3-TTS 1.7B VoiceDesign, instruct = "<speaker voice description>, <line emotion>"
  cbox   B: Chatterbox cloning a Qwen-designed reference (tts_bakeoff/refs/<speaker>_<mood>.wav) with `exaggeration`;
         lines 3, 4, 7 use Chatterbox-Turbo instead with a [gasp]/[groan] tag in front
  dia    C: Dia 1.6B on lines 3-5 only, with the burglar reference + its transcript as the >5 s audio prompt
Every neural take gets the same post-chain as make_tts.py minus tighten(): model rate -> afconvert 48 kHz -> trim ->
(bullhorn on vo.police.*) -> loudness (-16 dB RMS, -15 with an effect) -> fades.
Takes: seeds 1.., up to MAX_ATTEMPTS per line/engine, keep the first TAKES whose Whisper transcript matches the line
(garble filter for Chatterbox #97 / Dia short-input). If Whisper can't load, every take is kept unchecked.
Output: tts_bakeoff/<engine>/<key>/<key>_NN.wav (+ _rejects/), results.json, web/<key>/*.m4a, index.html.
tts_bakeoff/ is outside Assets (Unity never imports it) and git-ignored.

Mac care: one model in memory at a time (plus Whisper), one clip at a time; before every clip health_ok() checks the
same healthy-range limits as scripts/machealth.sh (throttled / thermal warning, battery > 40 C, CPU/GPU die > 90 C; never
load) from the last line of Logs/mac-health.log — or measured directly when that log is stale (machealth.sh not
running) — and while unhealthy the run re-checks every PAUSE_S (60 s).
"""
import argparse, base64, difflib, gc, glob, json, os, random, re, shutil, subprocess, sys, tempfile, time, wave
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prepare_sfx import load, save, SR                                   # noqa: E402  48 kHz 16-bit wav IO
from make_tts import read_table, trim, loudness, fades, bullhorn, radio, voice_for  # noqa: E402  same chain

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "tts_bakeoff")
CLIPS = os.path.join(ROOT, "Assets", "_Game", "Audio", "Clips")
HEALTH_LOG = os.path.join(ROOT, "Logs", "mac-health.log")
RESULTS = os.path.join(OUT, "results.json")

TAKES = 3
MAX_ATTEMPTS = 6
ASR_PASS = 0.6          # word-sequence similarity (0..1) between Whisper's transcript and the line
MAX_TEMP_C = 40.0       # battery; ages faster above ~40 C
MAX_DIE_C = 90.0        # CPU/GPU die; Apple Silicon runs 80-90 C under load by design
PAUSE_S = 60            # re-check every minute
HEALTH_FRESH_S = 120    # machealth.sh logs every 30 s; an older last line is stale (it isn't running) -> measure directly

MODEL_ID = {
    "qwen": "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16",
    "cbox": "mlx-community/chatterbox-fp16",
    "turbo": "mlx-community/chatterbox-turbo-fp16",
    "dia": "mlx-community/Dia-1.6B-fp16",
}
WHISPER_SNAPSHOT = os.path.expanduser("~/.cache/huggingface/hub/models--mlx-community--whisper-large-v3-turbo/snapshots")
# The mlx-community whisper repo ships weights only; mlx-audio needs the HF tokenizer. large-v3-turbo uses the
# large-v3 tokenizer, which is already on disk (WhisperKit's copy). Nothing is downloaded either way.
WHISPER_TOKENIZER_GLOB = os.path.expanduser(
    "~/Library/Developer/CoreSimulator/Devices/*/data/Containers/Data/Application/*/Documents/huggingface/models/openai/whisper-large-v3")

LINES = [  # (#, key, what it tests) — research 17 §5.1
    (1, "vo.player.ground", "Commanding shout (player)"),
    (2, "vo.knife.taunt", "Angry attacker + very short line (gibberish risk)"),
    (3, "vo.burglar.flee", "Startled burglar, dash pause"),
    (4, "vo.burglar.shot", "Pain + shock (non-verbal)"),
    (5, "vo.surrender.ok", "Terrified surrender"),
    (6, "vo.neighbor.hello", "Drunk, quiet, slurred"),
    (7, "vo.neighbor.itsme", "Drunk + panicked, longest line (2.5 s cap)"),
    (8, "vo.police.surround", "Deadpan comic timing through the bullhorn"),
]
DIA_KEYS = {"vo.burglar.flee", "vo.burglar.shot", "vo.surrender.ok"}
TURBO_KEYS = {"vo.burglar.flee", "vo.burglar.shot", "vo.neighbor.itsme"}

# speaker → Qwen voice description (identical across that speaker's lines; only the emotion varies)
SPEAKERS = [
    ("vo.player.", "player", "A man in his early 30s with a firm, mid-low voice and a native General American accent"),
    ("vo.police.", "police", "A bored male American police officer in his 50s with a flat, clipped, deadpan voice"),
    ("vo.neighbor.", "mike", "A friendly middle-aged American man, beer-drunk, thick-tongued and a little slow"),
    ("vo.burglar.", "burglar", "A wiry American man in his early 20s with a slightly nasal, jittery, high-strung voice"),
    ("vo.surrender.", "burglar", "A wiry American man in his early 20s with a slightly nasal, jittery, high-strung voice"),
    ("vo.attacker.", "attacker", "A big American man in his 40s with a deep, gravelly, rough street voice"),
    ("vo.knife.", "attacker", "A big American man in his 40s with a deep, gravelly, rough street voice"),
]
# key → (emotion clause for Qwen, Chatterbox exaggeration, ref mood, Turbo text prefix, Dia text prefix)
STYLE = {
    "vo.player.ground": ("shouting at full volume, commanding, adrenaline", 0.9, "shout", "", ""),
    "vo.knife.taunt": ("snarling, menacing, loud", 0.9, "shout", "", ""),
    "vo.burglar.flee": ("startled, panicked, backing away fast", 0.8, "scared", "[gasp] ", "(gasps) "),
    "vo.burglar.shot": ("sudden cry of pain turning into furious shock", 1.0, "scared", "[groan] ", "(screams) "),
    "vo.surrender.ok": ("terrified, pleading, breathless, voice cracking", 0.9, "scared", "", ""),
    "vo.neighbor.hello": ("drunk, slurring, sleepy, calling softly through a door", 0.4, "calm", "", ""),
    "vo.neighbor.itsme": ("drunk and suddenly panicked, slurred but loud", 0.8, "scared", "[gasp] ", ""),
    "vo.police.surround": ("flat, bored, deadpan, projecting through a megaphone", 0.3, "calm", "", ""),
}
# Chatterbox / Dia reference clips, designed once with Qwen (6-10 s, a longer sentence in the target mood)
REFS = {
    "player_shout": ("Get down on the ground, right now! Show me your hands! I said get down, do it now!",
                     "shouting at full volume, commanding, adrenaline"),
    "attacker_shout": ("You hear me? You're dead! I'm gonna make you pay for this. Get over here, right now!",
                       "snarling, menacing, shouting loudly"),
    "burglar_scared": ("Whoa, whoa, okay, I'm sorry, I'm sorry! I didn't touch anything, I swear, just let me go, please!",
                       "terrified, panicked, breathless, voice cracking"),
    "mike_calm": ("Hey buddy, it's me, Mike, from next door. I think I locked myself out again. You awake in there?",
                  "drunk, slurring, sleepy, speaking softly"),
    "mike_scared": ("Whoa, whoa, hold on! It's me, it's just Mike, from next door! Don't shoot, okay? I'm going, I'm going!",
                    "drunk and suddenly panicked, slurred but loud"),
    "police_calm": ("This is the police. We need you to come out of the house slowly, with your hands where we can see them.",
                    "flat, bored, deadpan, projecting loudly outdoors"),
}


def speaker_for(key):
    for prefix, name, desc in SPEAKERS:
        if key.startswith(prefix): return name, desc
    raise KeyError(key)


def neural_text(text):
    # Dashes become a hesitation, typographic quotes plain; the ellipsis stays a trailing-off "...".
    return (text.replace("—", "... ").replace("–", "... ").replace("…", "...")
                .replace("’", "'").replace("‘", "'").replace("“", "").replace("”", "")).replace("  ", " ")


CONTRACTIONS = {"youre": "you are", "im": "i am", "dont": "do not", "its": "it is", "whats": "what is",
                 "youll": "you will", "ive": "i have", "were": "we are", "gonna": "going to", "cant": "can not",
                 "didnt": "did not", "hes": "he is", "thats": "that is", "lets": "let us", "okay": "ok"}
# interjections Whisper spells many ways; fold each family to one token so "Agh!" heard as "Ah!" still matches
INTERJECTIONS = {"agh": "ah", "argh": "ah", "aah": "ah", "ahh": "ah", "ah": "ah", "ow": "ah", "ouch": "ah", "augh": "ah",
                 "whoa": "wo", "woah": "wo", "wow": "wo", "woe": "wo", "wo": "wo", "whoah": "wo"}


def words(s):
    s = re.sub(r"\[[^\]]*\]|\([^)]*\)", " ", s.lower())
    s = s.replace("'", "").replace("’", "")
    out = []
    for w in re.findall(r"[a-z0-9]+", s):
        w = INTERJECTIONS.get(w, w)
        out.extend(CONTRACTIONS.get(w, w).split())
    return out


def asr_score(heard, text):
    a, b = words(heard), words(text)
    if not b: return 0.0
    return difflib.SequenceMatcher(None, a, b).ratio()


# ------------------------------------------------------------------------------------------------ mac care
def health_ok():
    """Same healthy-range limits as scripts/machealth.sh (Brett: protect the Mac, but don't be too conservative): pause
    only on macOS throttling or a thermal warning, battery > 40 C, or CPU/GPU die > 90 C. Load is not a health metric."""
    last = None
    try:
        if time.time() - os.path.getmtime(HEALTH_LOG) < HEALTH_FRESH_S:
            last = open(HEALTH_LOG).read().strip().splitlines()[-1]
    except (OSError, IndexError):
        pass
    if last is None:   # stale/missing log: a frozen "hot" last line would otherwise pause the run forever
        last = measure_health()
    num = lambda pat, d: float(m.group(1)) if (m := re.search(pat, last)) else d
    bad = num(r"(?:temp|battery)=([\d.]+)C", 0.0) > MAX_TEMP_C or num(r"cpu=([\d.]+)C", 0.0) > MAX_DIE_C \
        or num(r"speedLimit=(\d+)", 100) < 100 or num(r"warn=(\d+)", 0) > 0
    return not bad, last


def measure_health():
    """One mac-health.log-style line measured now, with machealth.sh's sources: scripts/bin/mactemps (CPU/GPU die),
    ioreg AppleSmartBattery (battery), pmset -g therm (speed limit, thermal warning)."""
    def run(cmd):
        try: return subprocess.run(cmd, capture_output=True, text=True, timeout=10).stdout
        except (OSError, subprocess.SubprocessError): return ""
    cpu = re.search(r"cpu=([\d.]+)", run([os.path.join(ROOT, "scripts", "bin", "mactemps")]))
    bat = re.search(r'"Temperature" = (\d+)', run(["ioreg", "-rn", "AppleSmartBattery"]))
    therm = run(["pmset", "-g", "therm"])
    lim = re.search(r"CPU_Speed_Limit\s*=\s*(\d+)", therm)
    warn = sum(1 for l in therm.splitlines() if re.search(r"thermal warning level.*[1-9]|Thermal.*level *= *[1-9]", l))
    return (f"{time.strftime('%H:%M:%S')} (measured) cpu={cpu.group(1) if cpu else 'na'}C "
            f"battery={f'{int(bat.group(1)) / 100:.1f}' if bat else 'na'}C speedLimit={lim.group(1) if lim else 100} warn={warn}")


def wait_for_cool(log):
    while True:
        ok, line = health_ok()
        if ok: return
        log(f"  mac-health: {line} -> pausing {PAUSE_S // 60} min")
        time.sleep(PAUSE_S)


# ------------------------------------------------------------------------------------------------ audio helpers
def write_wav(path, x, sr):
    x = np.clip(np.asarray(x, dtype=np.float64).reshape(-1), -1, 1)
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(int(sr))
        w.writeframes((x * 32767).astype("<i2").tobytes())


def to_rate(x, sr, tmp, rate=SR):
    a, b = os.path.join(tmp, "in.wav"), os.path.join(tmp, f"out{rate}.wav")
    write_wav(a, x, sr)
    subprocess.run(["afconvert", "-f", "WAVE", "-d", f"LEI16@{rate}", "-c", "1", a, b], check=True, capture_output=True)
    return load(b)[:, 0].astype(np.float64)


def post_chain(x48, key):
    """make_tts.py's chain without tighten() (that only exists for `say`'s robotic pauses)."""
    fx = voice_for(key)[2]
    x = trim(x48)
    spoken = len(x) / SR
    if fx == "bullhorn": x = trim(bullhorn(x), floor_db=-50, post=0.05)
    elif fx == "radio": x = trim(radio(x), floor_db=-50, post=0.03)
    x = fades(loudness(x, target_rms_db=-15.0 if fx else -16.0))
    return x, spoken


# ------------------------------------------------------------------------------------------------ models
class Models:
    def __init__(self, log):
        self.log, self.name, self.model, self.whisper, self.whisper_note = log, None, None, None, None

    def get(self, name):
        if self.name == name: return self.model
        self.unload()
        from mlx_audio.tts.utils import load_model
        t = time.time()
        self.log(f"loading {MODEL_ID[name]} ...")
        self.model = load_model(MODEL_ID[name]); self.name = name
        self.log(f"  loaded in {time.time() - t:.1f} s (sample rate {self.model.sample_rate})")
        return self.model

    def unload(self):
        if self.model is None: return
        import mlx.core as mx
        self.model = None; self.name = None
        gc.collect(); mx.clear_cache()

    def load_whisper(self, tmp):
        snaps = sorted(glob.glob(os.path.join(WHISPER_SNAPSHOT, "*")))
        toks = sorted(glob.glob(WHISPER_TOKENIZER_GLOB))
        if not snaps:
            self.whisper_note = "whisper-large-v3-turbo not in ~/.cache/huggingface - ASR filter skipped"; return
        if not toks:
            self.whisper_note = "no local whisper-large-v3 tokenizer - ASR filter skipped"; return
        try:
            import warnings
            from types import SimpleNamespace
            from transformers import WhisperTokenizerFast
            from mlx_audio.stt.utils import load as stt_load
            d = os.path.join(tmp, "whisper")
            os.makedirs(d, exist_ok=True)
            for f in ("config.json", "weights.safetensors"):
                p = os.path.join(d, f)
                if not os.path.exists(p): os.symlink(os.path.realpath(os.path.join(snaps[-1], f)), p)
            with warnings.catch_warnings():
                warnings.simplefilter("ignore")
                w = stt_load(d)
            w._processor = SimpleNamespace(tokenizer=WhisperTokenizerFast.from_pretrained(toks[0]))
            self.whisper = w
            self.whisper_note = "whisper-large-v3-turbo (local cache) + local large-v3 tokenizer"
        except Exception as e:  # noqa: BLE001
            self.whisper_note = f"Whisper failed to load ({type(e).__name__}: {e}) - ASR filter skipped"

    def transcribe(self, x48, tmp):
        if self.whisper is None: return None
        import mlx.core as mx
        x16 = to_rate(x48, SR, tmp, 16000)
        r = self.whisper.generate(mx.array(x16.astype(np.float32)), language="en", temperature=0.0,
                                  condition_on_previous_text=False, verbose=False)
        return (r.text or "").strip()


def dia_budget(ref, after_s=6.0):
    """Dia sizes its KV cache to max_tokens, and the audio prompt is prefilled into it: the budget has to cover the
    prompt's DAC frames (44.1 kHz / 512) plus the new line; a take still running after `after_s` is a runaway."""
    with wave.open(ref) as w: prompt_s = w.getnframes() / w.getframerate()
    return int((prompt_s + after_s) * 44100 / 512) + 64


def synth(models, engine, key, text, seed, tmp):
    """One take at the model's rate → (samples, sr, model id, text actually sent)."""
    import mlx.core as mx
    mx.random.seed(seed); np.random.seed(seed); random.seed(seed)
    spk, desc = speaker_for(key) if key.startswith("vo.") else (None, None)
    if engine == "ref":
        rtext, emo = REFS[key]; spk = key.split("_")[0]
        desc = next(d for _, n, d in SPEAKERS if n == spk)
        m = models.get("qwen"); sent = rtext
        gen = m.generate(sent, instruct=f"{desc}, {emo}", lang_code="english", temperature=0.9, verbose=False)
        mid = MODEL_ID["qwen"]
    elif engine == "qwen":
        emo = STYLE[key][0]; sent = neural_text(text)
        m = models.get("qwen")
        gen = m.generate(sent, instruct=f"{desc}, {emo}", lang_code="english", temperature=0.9, verbose=False)
        mid = MODEL_ID["qwen"]
    elif engine == "cbox":
        emo, exag, mood, turbo_tag, _ = STYLE[key]
        ref = os.path.join(OUT, "refs", f"{spk}_{mood}.wav")
        if key in TURBO_KEYS:
            m = models.get("turbo"); sent = turbo_tag + neural_text(text); mid = MODEL_ID["turbo"]
            gen = m.generate(sent, ref_audio=ref, temperature=0.8)
        else:
            m = models.get("cbox"); sent = neural_text(text); mid = MODEL_ID["cbox"]
            gen = m.generate(sent, ref_audio=ref, exaggeration=exag, cfg_weight=0.3, temperature=0.8,
                             lang_code="en", verbose=False)
    elif engine == "dia":
        dia_tag = STYLE[key][4]; mood = STYLE[key][2]
        ref = os.path.join(OUT, "refs", f"{spk}_{mood}.wav")
        m = models.get("dia"); mid = MODEL_ID["dia"]
        sent = f"[S1] {dia_tag}{neural_text(text)}"
        gen = m.generate(sent, ref_audio=ref, ref_text=f"[S1] {REFS[f'{spk}_{mood}'][0]}",
                         max_tokens=dia_budget(ref), verbose=False)
    else:
        raise ValueError(engine)
    parts, sr = [], m.sample_rate
    for r in gen:
        parts.append(np.array(r.audio, dtype=np.float32).reshape(-1)); sr = r.sample_rate or sr
    if not parts: raise RuntimeError("model returned no audio")
    return np.concatenate(parts), sr, mid, sent


# ------------------------------------------------------------------------------------------------ steps
def load_results():
    try: return json.load(open(RESULTS))
    except (OSError, ValueError): return {}


def save_results(res):
    tmp = RESULTS + ".tmp"
    json.dump(res, open(tmp, "w"), indent=1); os.replace(tmp, RESULTS)


def step_say(res, table, log, models, tmp):
    for n, key, _ in LINES:
        src = os.path.join(CLIPS, key, f"{key}_01.wav")
        d = os.path.join(OUT, "say", key); os.makedirs(d, exist_ok=True)
        dst = os.path.join(d, f"{key}_01.wav")
        shutil.copyfile(src, dst)
        x = load(dst)[:, 0].astype(np.float64)
        heard = None
        if models.whisper is not None:
            # the placeholder already carries the bullhorn; transcribe it as-is (for the report only)
            heard = models.transcribe(x, tmp)
        res.setdefault("say", {})[key] = {"takes": [{"file": os.path.relpath(dst, OUT), "seconds": round(len(x) / SR, 2),
                                                      "heard": heard, "asr": None if heard is None else round(asr_score(heard, table[key]), 2)}],
                                          "model": "macOS say (make_tts.py placeholder)"}
        log(f"say   {key:20} {len(x) / SR:4.2f} s  heard: {heard!r}")
    save_results(res)


def step_refs(res, log, models, tmp):
    d = os.path.join(OUT, "refs"); os.makedirs(d, exist_ok=True)
    needed = sorted({f"{speaker_for(k)[0]}_{STYLE[k][2]}" for _, k, _ in LINES})
    for name in needed:
        path = os.path.join(d, f"{name}.wav")
        if os.path.exists(path) and name in res.get("refs", {}):
            log(f"ref   {name:16} (kept)"); continue
        best = None
        for seed in range(1, 5):
            wait_for_cool(log)
            t = time.time()
            try:
                x, sr, mid, sent = synth(models, "ref", name, None, seed, tmp)
            except Exception as e:  # noqa: BLE001
                log(f"ref   {name:16} seed {seed} FAILED: {type(e).__name__}: {e}"); continue
            dt = time.time() - t
            x48 = trim(to_rate(x, sr, tmp))
            dur = len(x48) / SR
            heard = models.transcribe(x48, tmp)
            sc = None if heard is None else asr_score(heard, REFS[name][0])
            log(f"ref   {name:16} seed {seed} {dur:5.2f} s gen {dt:5.1f} s asr {sc if sc is None else round(sc, 2)}")
            good = 5.0 <= dur <= 11.0 and (sc is None or sc >= 0.8)
            cand = (good, sc or 0, seed, x48, dur, heard, dt)
            if best is None or cand[:2] > best[:2]: best = cand
            if good: break
        if best is None:
            log(f"ref   {name:16} NO REFERENCE (all seeds failed)"); continue
        good, sc, seed, x48, dur, heard, dt = best
        x48 = fades(loudness(x48, target_rms_db=-18.0))
        save(path, x48.astype(np.float32))
        res.setdefault("refs", {})[name] = {"file": os.path.relpath(path, OUT), "seed": seed, "seconds": round(dur, 2),
                                            "text": REFS[name][0], "instruct_emotion": REFS[name][1], "heard": heard,
                                            "asr": None if heard is None else round(sc, 2), "passed": good}
        save_results(res)


def step_engine(res, engine, table, log, models, tmp, only):
    keys = [k for _, k, _ in LINES if (engine != "dia" or k in DIA_KEYS) and k.startswith(only)]
    for key in keys:
        text = table[key]
        entry = res.get(engine, {}).get(key)
        if entry and entry.get("done"):
            log(f"{engine:5} {key:20} (kept)"); continue
        d = os.path.join(OUT, engine, key)
        if os.path.isdir(d): shutil.rmtree(d)
        os.makedirs(os.path.join(d, "_rejects"), exist_ok=True)
        kept, rejects, failures, sent_text, mid = [], [], [], None, None
        for seed in range(1, MAX_ATTEMPTS + 1):
            if len(kept) >= TAKES: break
            wait_for_cool(log)
            t = time.time()
            try:
                x, sr, mid, sent_text = synth(models, engine, key, text, seed, tmp)
            except Exception as e:  # noqa: BLE001
                failures.append({"seed": seed, "error": f"{type(e).__name__}: {e}"[:300]})
                log(f"{engine:5} {key:20} seed {seed} FAILED: {type(e).__name__}: {e}")
                if "load" in str(e).lower() and not kept: break
                continue
            dt = time.time() - t
            x48 = to_rate(x, sr, tmp)
            y, spoken = post_chain(x48, key)
            heard = models.transcribe(trim(x48), tmp)
            sc = None if heard is None else asr_score(heard, text)
            ok = spoken >= 0.3 and spoken <= 6.0 and (sc is None or sc >= ASR_PASS)
            take = {"seed": seed, "seconds": round(len(y) / SR, 2), "spoken": round(spoken, 2), "gen_s": round(dt, 1),
                    "heard": heard, "asr": None if sc is None else round(sc, 2), "over_cap": spoken > 2.51}
            if ok:
                name = f"{key}_{len(kept) + 1:02d}.wav"
                take["file"] = os.path.relpath(os.path.join(d, name), OUT); kept.append(take)
            else:
                name = f"_rejects/{key}_seed{seed}.wav"
                take["file"] = os.path.relpath(os.path.join(d, name), OUT); rejects.append(take)
            save(os.path.join(d, name), y.astype(np.float32))
            log(f"{engine:5} {key:20} seed {seed} {'keep' if ok else 'REJECT'} speech {spoken:4.2f} s  gen {dt:5.1f} s"
                f"  asr {take['asr']}  heard: {heard!r}")
        short = TAKES - len(kept)
        if short > 0 and rejects:   # not enough clean takes: fill with the closest rejects, flagged
            for r in sorted(rejects, key=lambda r: -(r["asr"] or 0))[:short]:
                src = os.path.join(OUT, r["file"])
                name = f"{key}_{len(kept) + 1:02d}.wav"
                shutil.copyfile(src, os.path.join(d, name))
                kept.append(dict(r, file=os.path.relpath(os.path.join(d, name), OUT), filled_from_reject=True))
        res.setdefault(engine, {})[key] = {"model": mid, "sent": sent_text, "takes": kept, "rejects": rejects,
                                           "failures": failures, "done": True, "attempts": len(kept) + len(rejects) + len(failures)
                                           - sum(1 for k in kept if k.get("filled_from_reject"))}
        save_results(res)
    models.unload()


# ------------------------------------------------------------------------------------------------ page
def m4a(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    subprocess.run(["afconvert", "-f", "m4af", "-d", "aac", "-b", "80000", "-c", "1", "-s", "0", src, dst],
                   check=True, capture_output=True)


def step_page(res, table, log):
    web = os.path.join(OUT, "web")
    if os.path.isdir(web): shutil.rmtree(web)
    rows, reveal = [], {}
    for n, key, tests in LINES:
        now = res.get("say", {}).get(key, {}).get("takes", [])
        now_url = None
        if now:
            now_url = f"web/{key}/now.m4a"; m4a(os.path.join(OUT, now[0]["file"]), os.path.join(OUT, now_url))
        engines = [e for e in ("qwen", "cbox", "dia") if res.get(e, {}).get(key, {}).get("takes")]
        order = engines[:]
        random.Random(f"his-bakeoff-{key}").shuffle(order)
        cols = []
        for letter, e in zip("XYZ", order):
            takes = []
            for i, t in enumerate(res[e][key]["takes"], 1):
                url = f"web/{key}/{letter}_{i:02d}.m4a"
                m4a(os.path.join(OUT, t["file"]), os.path.join(OUT, url))
                takes.append({"url": url, "sec": t["seconds"], "flag": bool(t.get("filled_from_reject"))})
            cols.append({"letter": letter, "takes": takes})
            reveal.setdefault(key, {})[letter] = {"qwen": "A · Qwen3 VoiceDesign",
                                                  "cbox": "B · Chatterbox-Turbo (clone)" if key in TURBO_KEYS else "B · Chatterbox (clone)",
                                                  "dia": "C · Dia 1.6B"}[e]
        rows.append({"n": n, "key": key, "text": table[key], "tests": tests, "now": now_url,
                     "nowSec": now[0]["seconds"] if now else 0, "cols": cols})
    enc = base64.b64encode(json.dumps(reveal).encode()).decode()   # not plain text, so a glance at the source doesn't spoil it
    html = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "tts_bakeoff_page.html"), encoding="utf-8").read()
    html = html.replace("__ROWS__", json.dumps(rows)).replace("__REVEAL__", json.dumps(enc))
    open(os.path.join(OUT, "index.html"), "w", encoding="utf-8").write(html)
    size = sum(os.path.getsize(p) for p in glob.glob(os.path.join(web, "*", "*.m4a")))
    log(f"page: {OUT}/index.html, {len(glob.glob(os.path.join(web, '*', '*.m4a')))} m4a, {size / 1024:.0f} KiB")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--steps", default="say,refs,qwen,cbox,dia,page")
    ap.add_argument("--only", default="", help="only line keys starting with this prefix")
    ap.add_argument("--redo", action="store_true", help="re-render line/engine pairs already marked done")
    ap.add_argument("--no-asr", action="store_true")
    args = ap.parse_args()
    steps = args.steps.split(",")
    os.makedirs(OUT, exist_ok=True)
    logf = open(os.path.join(OUT, "run.log"), "a")

    def log(msg):
        line = f"{time.strftime('%H:%M:%S')} {msg}"
        print(line, flush=True); logf.write(line + "\n"); logf.flush()

    table = dict(read_table())
    missing = [k for _, k, _ in LINES if k not in table]
    if missing: sys.exit(f"keys not in VoiceLines.cs: {missing}")
    res = load_results()
    if args.redo:
        for e in ("qwen", "cbox", "dia"):
            for k in list(res.get(e, {})):
                if k.startswith(args.only): res[e][k]["done"] = False
    models = Models(log)
    t0 = time.time()
    with tempfile.TemporaryDirectory() as tmp:
        if any(s != "page" for s in steps) and not args.no_asr:
            models.load_whisper(tmp)
            log(f"ASR: {models.whisper_note}")
            res["asr_note"] = models.whisper_note
        for s in steps:
            ts = time.time()
            if s == "say": step_say(res, table, log, models, tmp)
            elif s == "refs": step_refs(res, log, models, tmp)
            elif s in ("qwen", "cbox", "dia"):
                if s != "qwen" and not res.get("refs"):
                    log(f"{s}: no reference clips (run the refs step first)"); continue
                step_engine(res, s, table, log, models, tmp, args.only)
            elif s == "page": step_page(res, table, log); continue
            else: sys.exit(f"unknown step {s}")
            models.unload()
            res.setdefault("timing_s", {})[s] = round(res.get("timing_s", {}).get(s, 0) + time.time() - ts, 1)
            save_results(res)
            log(f"step {s} done in {time.time() - ts:.0f} s")
    log(f"total {time.time() - t0:.0f} s")


if __name__ == "__main__":
    main()
