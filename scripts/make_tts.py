#!/usr/bin/env python3
"""Render every voice line in VoiceLines.cs as a placeholder clip with macOS `say` (or, --engine neural, local neural TTS).

    scripts/make_tts.py                    # all lines → Assets/_Game/Audio/Clips/<key>/<key>_01.wav
    scripts/make_tts.py --only vo.police   # just the keys starting with this
    scripts/make_tts.py --force            # also overwrite key folders that hold real recordings
    HF_HOME=../tools/tts/hf HF_HUB_OFFLINE=1 ../tools/tts/.venv/bin/python scripts/make_tts.py --engine neural
                                           # designed character voices → <key>/<key>_nNN.wav (see "neural engine" below)

PLACEHOLDERS ONLY: these robot voices stand in until Brett records his own lines (PLAN.md §6: Brett 2026-09-22, "hold
off on recording — use free TTS placeholders until he records; keep the keys stable so his takes drop in later").
Real takes go into the same key folders; a folder holding any wav this script didn't write (sha1 in
scripts/tts_manifest.json) is left alone unless --force, and Build Sfx Library drops this script's placeholder from any
folder that also holds a real take (so the robot never alternates with Brett).

Text comes from the table in Assets/_Game/Scripts/Scenario/VoiceLines.cs (regex over the ("vo.x.y", "text"), lines
between the BEGIN/END TABLE markers), so subtitles and audio never disagree. The key's speaker part picks the voice
(VOICES below). Pipeline: say → AIFF 22.05 kHz → afconvert to 48 kHz 16-bit mono → numpy: trim silence, loudness-match,
peak-limit, fade (wav IO from prepare_sfx.py). vo.police.* get a bullhorn (band-pass ~400-3000 Hz, horn resonance, soft
clip, street/room echo); the custody line gets a narrower radio band instead. `say`'s long pauses are shortened
(tighten), and lines still longer than 2.5 s are re-spoken faster (up to MAX_SPEEDUP).
Then in the Editor: HIS > Audio > Build Sfx Library.
Both engines write straight into Assets/_Game/Audio/Clips/<key>/: no cutting step. (scripts/prepare_sfx.py only rebuilds its
own recipe keys and keeps the vo.* folders.)
"""
import argparse, hashlib, json, os, re, subprocess, sys, tempfile
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prepare_sfx import load, save, SR  # noqa: E402  (same wav IO as the SFX pipeline)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VOICELINES = os.path.join(ROOT, "Assets", "_Game", "Scripts", "Scenario", "VoiceLines.cs")
OUT = os.path.join(ROOT, "Assets", "_Game", "Audio", "Clips")
MANIFEST = os.path.join(ROOT, "scripts", "tts_manifest.json")
MAX_SECONDS = 2.5
MAX_SPEEDUP = 1.4   # beyond this `say` gets chipmunky; a line still over 2.5 s is flagged instead (shorten its text)

# key prefix → say voice, words per minute, effect. First match wins, so specific prefixes go first.
VOICES = [
    ("vo.player.",        "Reed (English (US))",  205, None),        # you
    ("vo.cop.bullhorn.",  "Fred",                 180, "bullhorn"),  # the standoff: the lead on the bullhorn from the street
    ("vo.cop.radio.",     "Fred",                 190, "radio"),     # the dispatcher
    ("vo.cop.",           "Ralph",                195, None),        # the officers in the room (say fallback)
    ("vo.police.custody", "Fred",                 185, "radio"),     # dispatcher over the radio
    ("vo.police.",        "Fred",                 180, "bullhorn"),  # deadpan cop outside the door
    ("vo.neighbor.",      "Eddy (English (US))",  180, None),        # Mike from next door (a bit drunk)
    ("vo.burglar.",       "Rocko (English (US))", 215, None),
    ("vo.surrender.",     "Rocko (English (US))", 215, None),
    ("vo.gunman.",        "Rocko (English (US))", 215, None),        # same man as his surrender lines
    # A US Marshal night (research/27 §5). No effect on any of them: the courier is muffled by the door in game, as the
    # delivery man is (he falls through to "vo." like vo.delivery.).
    ("vo.ricky.",         "Grandpa (English (US))", 190, None),      # the witness: nervous, low
    ("vo.brother.",       "Eddy (English (US))",  175, None),        # Sal
    ("vo.deputy.radio.",  "Fred",                 170, "radio"),     # Ortiz on the radio (the handover, his call-ahead): same man, radio band
    ("vo.deputy.",        "Fred",                 170, None),        # Ortiz checking in: flat, tired
    ("vo.relief.",        "Fred",                 170, None),        # Ortiz again at 2 AM
    ("vo.hitman.street.", "Rocko (English (US))", 215, None),        # wave 1 = the gunman's voice
    ("vo.hitman.pro.",    "Ralph",                170, None),        # waves 2-3: quiet, flat
    ("vo.attacker.",      "Ralph",                190, None),
    ("vo.knife.",         "Ralph",                190, None),
    ("vo.",               "Ralph",                190, None),
]

TABLE_RE = re.compile(r'\(\s*"(vo\.[A-Za-z0-9_.]+)"\s*,\s*"([^"\\]*)"\s*\)')


def read_table():
    src = open(VOICELINES, encoding="utf-8").read()
    try:
        block = src.split("// BEGIN TABLE", 1)[1].split("// END TABLE", 1)[0]
    except IndexError:
        sys.exit(f"{VOICELINES}: BEGIN/END TABLE markers not found")
    rows = TABLE_RE.findall(block)
    if not rows: sys.exit(f"{VOICELINES}: no (\"vo.…\", \"text\") rows between the markers")
    return rows


def voice_for(key):
    for prefix, voice, rate, fx in VOICES:
        if key.startswith(prefix): return voice, rate, fx
    raise KeyError(key)


def speakable(text):
    # What `say` should pronounce: dashes and ellipses become pauses, typographic quotes plain.
    return (text.replace("\u2014", ", ").replace("\u2013", ", ").replace("\u2026", "... ")
                .replace("\u2019", "'").replace("\u2018", "'").replace("\u201c", "").replace("\u201d", ""))


def render(text, voice, rate, tmp):
    aiff, wav = os.path.join(tmp, "line.aiff"), os.path.join(tmp, "line.wav")
    subprocess.run(["say", "-v", voice, "-r", str(int(rate)), "-o", aiff, speakable(text)], check=True, capture_output=True)
    subprocess.run(["afconvert", "-f", "WAVE", "-d", f"LEI16@{SR}", "-c", "1", aiff, wav], check=True, capture_output=True)
    return load(wav)[:, 0].astype(np.float64)


# ------------------------------------------------------------------------------------------------ cleanup
def frames_db(x, win=0.01):
    h = int(SR * win); k = max(1, len(x) // h)
    rms = np.sqrt(np.mean(x[:k * h].reshape(k, h) ** 2, axis=1) + 1e-12)
    return 20 * np.log10(rms / (rms.max() + 1e-12)), h


def trim(x, floor_db=-42.0, pre=0.03, post=0.08):
    db, h = frames_db(x)
    on = np.nonzero(db > floor_db)[0]
    if len(on) == 0: return x
    a = max(0, on[0] * h - int(pre * SR)); b = min(len(x), (on[-1] + 1) * h + int(post * SR))
    return x[a:b]


def tighten(x, max_gap=0.15, floor_db=-36.0, xfade=0.01):
    """Shorten `say`'s long, robotic pauses at commas and sentence ends to max_gap (keeps the phrasing, loses the dead air).
    Only quiet runs longer than max_gap are cut (stop closures are < 0.1 s); joins are crossfaded so nothing clicks."""
    db, h = frames_db(x)
    quiet = db < floor_db
    keep, k = int(max_gap * SR), int(xfade * SR)
    pieces, pos, i, n = [], 0, 0, len(quiet)
    while i < n:
        if not quiet[i]: i += 1; continue
        j = i
        while j < n and quiet[j]: j += 1
        a, b = i * h, j * h
        if i > 0 and j < n and b - a > keep:          # interior gaps only; trim() handles the ends
            pieces.append(x[pos:a + keep // 2]); pos = b - keep // 2
        i = j
    pieces.append(x[pos:])
    y = pieces[0]
    for seg in pieces[1:]:
        m = min(k, len(y), len(seg))
        r = np.linspace(0, 1, m)
        y = np.concatenate([y[:len(y) - m], y[len(y) - m:] * (1 - r) + seg[:m] * r, seg[m:]])
    return y


def loudness(x, target_rms_db=-16.0, peak_db=-1.0):
    db, h = frames_db(x)
    voiced = np.repeat(db > -30, h)[:len(x)]
    seg = x[:len(voiced)][voiced] if voiced.any() else x
    rms = np.sqrt(np.mean(seg ** 2) + 1e-12)
    x = x * (10 ** (target_rms_db / 20) / rms)
    peak = np.abs(x).max() + 1e-12
    if peak > 10 ** (peak_db / 20): x = x * (10 ** (peak_db / 20) / peak)
    return x


def fades(x, fin=0.003, fout=0.03):
    x = x.copy()
    n_in, n_out = min(len(x), int(fin * SR)), min(len(x), int(fout * SR))
    x[:n_in] *= np.linspace(0, 1, n_in)
    if n_out > 0: x[-n_out:] *= np.linspace(1, 0, n_out) ** 2
    return x


# ------------------------------------------------------------------------------------------------ effects
def spectrum_filter(x, gain_fn, pad=0.0):
    n = len(x) + int(pad * SR)
    N = 1 << (n - 1).bit_length()
    X = np.fft.rfft(x, N)
    f = np.fft.rfftfreq(N, 1 / SR)
    return np.fft.irfft(X * gain_fn(f), N)[:n]


def band(lo, hi, order):
    def g(f):
        f = np.maximum(f, 1e-3)
        return 1 / np.sqrt(1 + (lo / f) ** (2 * order)) / np.sqrt(1 + (f / hi) ** (2 * order))
    return g


def soft_clip(x, drive):
    x = x / (np.abs(x).max() + 1e-12)
    return np.tanh(drive * x) / np.tanh(drive)


def fft_convolve(x, ir):
    n = len(x) + len(ir) - 1
    N = 1 << (n - 1).bit_length()
    return np.fft.irfft(np.fft.rfft(x, N) * np.fft.rfft(ir, N), N)[:n]


def bullhorn(x):
    # Horn: narrow band with a resonant bump near 1.8 kHz, then an overdriven little speaker.
    horn = lambda f: band(400, 3000, 4)(f) * (1 + 0.9 * np.exp(-(np.log2(np.maximum(f, 1) / 1800) / 0.45) ** 2))
    y = spectrum_filter(x, horn)
    y = soft_clip(y, 3.0)
    y = spectrum_filter(y, band(300, 3600, 2))
    # Outdoors in front of the house: a few slap-backs off the street/houses plus a short diffuse tail.
    tail = int(0.7 * SR)
    out = np.zeros(len(y) + tail)
    out[:len(y)] += y
    for delay, gain in ((0.085, 0.28), (0.17, 0.16), (0.31, 0.09), (0.47, 0.05)):
        i = int(delay * SR); out[i:i + len(y)] += gain * y
    rng = np.random.default_rng(7)
    t = np.arange(int(0.5 * SR)) / SR
    ir = rng.standard_normal(len(t)) * np.exp(-t * 10)
    ir = spectrum_filter(ir, band(250, 2800, 2))
    wet = fft_convolve(y, ir)[:len(out)]
    wet *= 0.18 * np.abs(y).max() / (np.abs(wet).max() + 1e-12)
    out[:len(wet)] += wet
    return out


def radio(x):
    y = spectrum_filter(x, band(500, 2600, 4))
    y = soft_clip(y, 5.0)
    y = spectrum_filter(y, band(400, 3000, 2))
    y += np.random.default_rng(3).standard_normal(len(y)) * 0.004  # a little hiss under the voice
    return y


# ------------------------------------------------------------------------------------------------ bookkeeping
def sha1(path):
    return hashlib.sha1(open(path, "rb").read()).hexdigest()


def load_manifest():
    try: return json.load(open(MANIFEST))
    except (OSError, ValueError): return {}


def check_voices():
    listing = subprocess.run(["say", "-v", "?"], capture_output=True, text=True, check=True).stdout
    names = {m.group(1) for m in re.finditer(r"^(.*?)\s+[a-z]{2,3}_[A-Za-z0-9]{2,4}\s+#", listing, re.M)}
    missing = sorted({v for _, v, _, _ in VOICES} - names)
    if missing: sys.exit(f"voices not installed: {missing} (System Settings → Accessibility → Spoken Content)")


def script_made(manifest, key, d):
    """{file: "say"|"neural"} for the wavs in a key folder that this script wrote (sha1 matches); anything else is real."""
    out, neural = {}, manifest.get("neural", {})
    for f in sorted(os.listdir(d)) if os.path.isdir(d) else []:
        if not f.endswith(".wav"): continue
        rel, h = f"{key}/{f}", None
        for kind, table in (("say", manifest), ("neural", neural)):
            want = table.get(rel)
            if isinstance(want, str):
                h = h or sha1(os.path.join(d, f))
                if h == want: out[f] = kind
    return out


_TOUCHED = set()   # "key/file.wav" entries this run wrote or removed


def save_manifest(manifest):
    # Merge onto the file as it is NOW: a second make_tts run (other keys, e.g. police lines) may have saved since this one
    # loaded it, and writing the whole in-memory copy back would silently drop its takes from the manifest.
    base = load_manifest()
    for rel in _TOUCHED:
        if isinstance(manifest.get(rel), str): base[rel] = manifest[rel]
        for block in ("neural", "neural_info"):
            if rel in manifest.get(block, {}): base.setdefault(block, {})[rel] = manifest[block][rel]
            elif rel in base.get(block, {}): base[block].pop(rel)
    flat = {k: v for k, v in base.items() if isinstance(v, str)}
    out = dict(sorted(flat.items()))
    for block in ("neural", "neural_info"):
        if base.get(block): out[block] = dict(sorted(base[block].items()))
    tmp = MANIFEST + ".tmp"   # atomic: a failed dump must never leave Build Sfx Library a half-written manifest
    with open(tmp, "w") as f: json.dump(out, f, indent=1, default=_plain)
    os.replace(tmp, MANIFEST)


def _plain(o):   # numpy scalars (np.bool_, np.float64) → plain JSON
    return o.item() if hasattr(o, "item") else str(o)


# ================================================================================================ neural engine
# --engine neural (research 17; tts_bakeoff/ results): Qwen3-TTS VoiceDesign designs ONE reference voice per character
# (scripts/tts_refs/<character>.wav, 6-10 s, outside Assets so it never ships), Chatterbox clones it and performs every
# line with an `exaggeration` per line; Chatterbox-Turbo does the lines that start with a [gasp]/[groan]; Dia renders
# the word-less pain screams (intruder.pain.scream), Turbo Mike's drunk mumble (drunk.mumble). Every take: model rate ->
# afconvert 48 kHz -> trim -> (over 2.5 s: shorten pauses, then a pitch-preserving WSOLA speed-up up to NEURAL_MAX_SPEEDUP)
# -> Whisper check + male gate (GENDER_GATE characters, scripts/voice_gender.py) -> bullhorn/radio (checked again) ->
# loudness -> fades. Best NEURAL_TAKES passing takes -> <key>/<key>_nNN.wav, recorded under "neural" in the
# manifest (neural_info carries the ASR and gate numbers). The `say` placeholder <key>_01.wav stays; Build Sfx Library
# prefers real takes > neural > say.
#
#   HF_HOME=../tools/tts/hf HF_HUB_OFFLINE=1 ../tools/tts/.venv/bin/python scripts/make_tts.py --engine neural
#       --steps refs,lines,scream,mumble (default: all)   --only vo.police   --redo (re-render keys that already have takes)
#       --refs burglar,mike (re-design those references; the old ones go to scripts/tts_refs/retired/)
NEURAL_TAKES = 2
NEURAL_ATTEMPTS = 4          # seeds per key; up to NEURAL_MAX_ATTEMPTS while fewer than NEURAL_TAKES pass
NEURAL_MAX_ATTEMPTS = 7
NEURAL_MAX_SPEEDUP = 1.3     # WSOLA beyond this starts to sound hurried/phasey
ASR_PASS = 0.75
CLEAN_ASR = 0.95             # preferred: every word heard right (0.9 still lets "It's Miles" through)
REFS_DIR = os.path.join(ROOT, "scripts", "tts_refs")
BAKEOFF_REFS = os.path.join(ROOT, "tts_bakeoff", "refs")
SCREAM_KEY = "intruder.pain.scream"
SCREAM_TAKES = 4

# character -> (Qwen voice description, reference sentence, reference emotion, bake-off ref to reuse if present)
CHARACTERS = {
    # 2026-09-25 re-design (Brett: "fix my callouts to be more masculine ... the voice is feminine"): the old "early 30s,
    # firm, mid-low ... shouting at full volume, adrenaline" ref sat at F0 303 Hz and its takes at 254-341 Hz (a strained
    # high shout); kept as tts_refs/retired/player_*.wav. Now a deep chest voice that commands without straining, and the
    # player is gated like the intruders, with a pitch cap of his own (PITCH_CAP).
    "player":   ("A big, broad-chested American man in his late 30s with a deep, resonant baritone voice and a native General "
                 "American accent, like a veteran police officer",
                 "Get down on the ground, right now! Show me your hands! I said get down, do it now!",
                 "loud and forceful, commanding from the chest, low and steady, controlled, no strain, no voice cracks", None),
    "attacker": ("A big American man in his 40s with a deep, gravelly, rough street voice",
                 "You hear me? You're dead! I'm gonna make you pay for this. Get over here, right now!",
                 "snarling, menacing, shouting loudly", "attacker_shout"),
    "knife":    ("A lean American man in his early 30s with a low, raspy, cold voice and a slight hiss on his s's",
                 "You shouldn't have come down here. Nobody's gonna hear you. You're dead, you hear me? Dead!",
                 "cold, menacing, snarling through his teeth, getting louder", None),
    # 2026-09-24 re-design (the old "early 20s, nasal, high-strung ... voice cracking" ref measured FLI -0.08, right at the
    # male/female midpoint; kept as tts_refs/retired/burglar_*.wav): same scared lines, a grown man's lower register.
    "burglar":  ("A wiry American man in his late 20s with a scratchy, mid-low, clearly masculine voice and a chesty tone",
                 "Whoa, whoa, okay, I'm sorry, I'm sorry! I didn't touch anything, I swear, just let me go, please!",
                 "scared and breathless, talking fast, but his voice stays low in his chest, no voice cracks", None),
    "gunman":   ("A stocky American man in his late 30s with a hard, clipped, harsh voice and a tough East Coast city accent",
                 "Don't move! Where's the money? Don't you look at me! Get down, now, or I swear I'll shoot you!",
                 "hard, clipped, aggressive, barking orders", None),
    # 2026-09-24 re-design (the old "friendly middle-aged ... man, beer-drunk" ref measured 309 Hz / FLI -0.26, borderline;
    # kept as tts_refs/retired/mike_*.wav).
    "mike":     ("A heavyset American man in his 50s with a low, husky, beer-soaked baritone voice",
                 "Hey! Hey buddy, it's me, Mike, from next door! Whoa, easy, easy, it's just me. I think I got the wrong house again.",
                 "drunk and slurring, friendly, fairly loud, a little startled", None),
    "police":   ("A bored male American police officer in his 50s with a flat, clipped, deadpan voice",
                 "This is the police. We need you to come out of the house slowly, with your hands where we can see them.",
                 "flat, bored, deadpan, projecting loudly outdoors", "police_calm"),
    # A US Marshal night (research/27 §5): the witness, his brother, the deputy (check-in and 2 AM relief) and the wave 2-3
    # hitmen. All four are men and all four are gated. Ricky is scared but must stay low in the chest (the burglar lesson:
    # a frightened brief drifts toward the female side), so he also gets a pitch cap.
    "ricky":    ("A nervous American man in his early 40s with a low, raspy smoker's baritone voice",
                 "Who's that? Nobody's supposed to know I'm here. Okay, okay, I'm down. I'm staying down. Just get me to Monday.",
                 "anxious but low in the chest, quiet and tense, no voice cracks", None),
    "sal":      ("A heavyset American man in his late 40s with a rough, working-class baritone voice",
                 "Ricky? It's Sal. Open up, it's freezing out here. Come on, man, I brought you some smokes. Open the door.",
                 # 2026-10-03: "speaking up to be heard through a door" gave a 196 Hz reference and 224-264 Hz door takes,
                 # high for a heavyset baritone (Brett: a man must never sound like a woman); now kept in the chest.
                 "gruff and casual, a little impatient, low in the chest, no strain", None),
    "deputy":   ("A tired American lawman in his 40s with a flat, low, calm voice",
                 "Marshals. It's Ortiz, I'm coming in. Everything quiet tonight? Good. Relief's at two. Lock it behind me.",
                 "tired and calm, flat and matter-of-fact, steady, low", None),
    "operator": ("An American man in his 40s with a quiet, flat, very low voice, an ex-military professional",
                 "Contact front. Two moving, stay on my left. Find the witness. Check every room. Reloading. Covering. Go.",
                 "quiet, clipped and cold, no emotion, very low and controlled", None),
}
# key prefix -> character (first match wins). The surrender lines come from whichever intruder gives up; they keep the
# burglar's voice, as the say placeholders did (Rocko) — he is the one who gives up most, and a separate "surrenderer"
# voice would be a stranger in every encounter.
NEURAL_SPEAKERS = [("vo.player.", "player"), ("vo.attacker.", "attacker"), ("vo.knife.", "knife"),
                   ("vo.burglar.", "burglar"), ("vo.surrender.", "burglar"), ("vo.gunman.", "gunman"),
                   ("vo.neighbor.", "mike"), ("vo.police.", "police"),
                   ("vo.delivery.", "police"),
                   # The standoff (research/22): the lead (orders, bullhorn, booking) = the police voice; the cover officer
                   # and SWAT a different man; the dispatcher behind the radio band.
                   ("vo.cop.lead.", "police"), ("vo.cop.bullhorn.", "police"), ("vo.cop.book.", "police"),
                   ("vo.cop.cover.", "attacker"), ("vo.cop.swat.", "attacker"), ("vo.cop.radio.", "knife"),   # the false alarm's bored delivery driver (muffled through the door)
                   # A US Marshal night (research/27 §5). The courier is the delivery man's voice; Ortiz is one man at the
                   # check-in and the relief; wave 1 reuses the gated gunman, so the hitman tier also reads by voice.
                   ("vo.courier.", "police"), ("vo.relief.", "deputy"), ("vo.deputy.", "deputy"),
                   ("vo.brother.", "sal"), ("vo.ricky.", "ricky"),
                   ("vo.hitman.street.", "gunman"), ("vo.hitman.pro.", "operator")]
# key -> (engine, exaggeration, text prefix for Turbo). engine: cbox = Chatterbox (exaggeration), turbo = Chatterbox-Turbo
# (copies the reference's energy; the [tag] adds the breath/groan). Keys not listed use NEURAL_DEFAULT[character].
# The burglar's exaggeration came down from 0.85-0.95 to 0.6-0.7 (2026-09-24): higher exaggeration raises Chatterbox's pitch,
# and his scared lines were drifting up toward the female side (voice audit).
NEURAL_DEFAULT = {"player": ("cbox", 0.5, ""), "attacker": ("cbox", 0.9, ""), "knife": ("cbox", 0.85, ""),
                  "burglar": ("cbox", 0.6, ""), "gunman": ("cbox", 0.85, ""), "mike": ("cbox", 0.6, ""),
                  "police": ("cbox", 0.3, ""),
                  "ricky": ("cbox", 0.5, ""), "sal": ("cbox", 0.5, ""), "deputy": ("cbox", 0.35, ""),
                  "operator": ("cbox", 0.3, "")}
NEURAL_STYLE = {
    "vo.burglar.flee": ("turbo", 0, "[gasp] "),
    "vo.burglar.shot": ("turbo", 0, "[groan] "),
    "vo.surrender.ok": ("cbox", 0.7, ""),
    "vo.surrender.stab": ("cbox", 0.7, ""),
    "vo.gunman.money": ("cbox", 0.8, ""),
    "vo.gunman.dontmove": ("cbox", 0.95, ""),
    "vo.gunman.dropit": ("cbox", 0.95, ""),
    "vo.gunman.backoff": ("cbox", 0.95, ""),
    "vo.gunman.putdown": ("cbox", 0.7, ""),
    "vo.gunman.flee": ("turbo", 0, "[gasp] "),
    "vo.neighbor.hello": ("cbox", 0.35, ""),
    "vo.neighbor.itsme": ("turbo", 0, "[gasp] "),
    "vo.neighbor.easy": ("turbo", 0, "[gasp] "),
    "vo.neighbor.chill": ("cbox", 0.9, ""),     # high exaggeration also speeds Chatterbox up (Mike's drawl runs long)
    "vo.neighbor.nostab": ("cbox", 0.95, ""),
    "vo.neighbor.shot": ("turbo", 0, "[groan] "),
    "vo.neighbor.leave": ("cbox", 0.5, ""),
}
# word-less pain for the door shot (Dia; muffled through the door leaf in game): (reference character, text after [S1]).
# 2026-09-24: only the attacker's deep reference and low cues (groans, "Argh"): the burglar-ref and high "Aaah!" cues gave
# the shrieks that read as a woman through the door, and every take now also has to pass the male gate (scream rules).
# (The "(screams) ..." cues mostly came out as shrieks the gate rejects; the groaned ones hold his voice.)
SCREAMS = [("attacker", "(groans) Uuuugh! (groans)"), ("attacker", "(groans) Ugh... (groans) Aargh!"),
           ("attacker", "(groans) Nngh... Argh!"), ("attacker", "(screams) Aaargh!"), ("attacker", "(groans) Aagh! (groans)"),
           ("attacker", "(groans) Ungh... Argh! (groans)")]
SCREAM_ATTEMPTS = 24
# Mike's drunk mumble (drunk.mumble: at the door, after a trip). The freesound "drunk mumble" read ambiguous (FLI -0.14), so
# it is Mike's own voice now: Chatterbox-Turbo (copies the reference's slur; the [tag] adds the sigh/groan), male gate as
# for his lines. (cue) list, cycled with the seed.
MUMBLE_KEY = "drunk.mumble"
MUMBLES = ["[sigh] Mmm... hmm... where'd I... mmh.", "Hmm... mmh, s'fine... [groan] s'fine...",
           "[groan] Uhh... mmh... wha... hmm.", "Mmh... Dave?... [sigh] hmm... mmh."]
MUMBLE_TAKES, MUMBLE_ATTEMPTS, MUMBLE_MAX_S = 2, 12, 4.5
# Male-voice gate (scripts/voice_gender.py; Brett 0.1.16/0.1.17: "still female voice on male attacker sometimes"): every take
# of these characters must read as a man by pitch, vocal-tract length and speaker embedding, or it is rejected exactly like a
# Whisper failure; the numbers go into neural_info and Logs/tts-neural-summary.json. Thresholds: voice_gender.thresholds().
# Police is left out: not an intruder, his reference already sits at FLI -0.23 (a flat 114 Hz voice that reads male by
# pitch) and every take goes through the bullhorn.
GENDER_GATE = {"player", "attacker", "knife", "burglar", "gunman", "mike", "ricky", "sal", "deputy", "operator"}
# Pitch caps on top of the gate (F0 median, Hz): (reference, each take). The gate's 350 Hz speech cap lets a strained shout
# through that still reads as a woman to a listener (Brett 2026-09-25 on the player's 254-341 Hz takes). Characters listed
# here also rank their reference seeds and takes by the lowest pitch instead of the lowest FLI.
PITCH_CAP = {"player": (175.0, 235.0), "ricky": (170.0, 225.0)}   # Ricky: research/27 §5 (scared, so capped like you)
REF_FLI_MAX = -0.45   # a gated character's reference must sit well inside the male side (the old burglar ref was -0.08)
REF_SEEDS = 3         # gated references: design this many seeds and keep the most male-reading one that passes


def neural_character(key):
    for prefix, name in NEURAL_SPEAKERS:
        if key.startswith(prefix): return name
    raise KeyError(f"no neural character for {key} (add it to NEURAL_SPEAKERS)")


# What the neural TTS is fed instead of the subtitle's spelling (research/27 §5): Chatterbox can read "U.S." as the word
# "us", so it gets "You Ess"; the subtitle, the table and the Whisper check keep "U.S.".
SPOKEN_AS = [("U.S. Marshals", "You Ess Marshals")]
# One line's own spoken form, when its first word trips the model: Ortiz's "Ortiz. Just checking …" came out "Or tis" on all
# 7 seeds (2026-10-03), while his name mid-line was heard right; so only that line spells it out.
SPOKEN_KEY = {"vo.deputy.hello": "Orteez. Just checking on you two.", "vo.deputy.handover.hello": "Marshal. I'm Orteez."}


def neural_spoken(text, key=None):
    if key in SPOKEN_KEY: return SPOKEN_KEY[key]
    for written, said in SPOKEN_AS: text = text.replace(written, said)
    return text


# Whisper spells the abbreviation "U.S." or "US" (or, spoken slowly, "You S"); the word "us" ("Us" at the start of a
# sentence) is the misread. Only the abbreviation's spellings fold back to "U.S." before scoring, and only for a line that
# has it, so a take that says "us Marshals" still fails the ASR check.
_US_HEARD = re.compile(r"(?<![A-Za-z])(?:U\.\s?S\.?|US|[Yy]ou[ ,.-]+(?:[Ee]ss?|S))(?![A-Za-z])")


# Whisper's other spellings of the very same sound ("Marshalls", "2 o'clock", "you too"): a spelling is not a misread, so
# each folds to the table's word, only when the line has that word (the deputy's lines all scored 0.83 on these alone).
HEARD_AS = [(re.compile(r"\bmarshall(s?)\b", re.I), r"Marshal\1", "Marshal"), (re.compile(r"\b2\b"), "two", "Two"),
            (re.compile(r"\btoo\b", re.I), "two", "two")]


def asr_heard(heard, text):
    if not heard: return heard
    if "U.S." in text: heard = _US_HEARD.sub("U.S.", heard)
    for pat, word, needs in HEARD_AS:
        if needs.lower() in text.lower(): heard = pat.sub(word, heard)
    return heard


def wsola(x, speed, win=0.03, tol=0.008):
    """Pitch-preserving speed-up (waveform-similarity overlap-add): 50%-overlapped Hann grains, each taken from around
    its nominal input position where it best continues the previous grain."""
    if speed <= 1.001: return x
    N = int(win * SR) // 2 * 2; hs = N // 2; t = int(tol * SR)
    w = np.hanning(N + 1)[:N]
    xp = np.concatenate([np.zeros(t), x, np.zeros(N + 2 * t)])
    n_out = int(len(x) / speed)
    y = np.zeros(n_out + 2 * N); wsum = np.zeros_like(y)
    prev, k = None, 0
    while k * hs < n_out:
        c = int(k * hs * speed) + t                              # nominal grain start in xp
        if c + N + t > len(xp): break
        if prev is None: s = c
        else:
            tmpl = xp[prev + hs: prev + hs + N]                  # the natural continuation of the last grain
            seg = xp[c - t: c + t + N]
            corr = np.correlate(seg, tmpl, mode="valid")
            s = c - t + int(np.argmax(corr))
        y[k * hs: k * hs + N] += xp[s: s + N] * w; wsum[k * hs: k * hs + N] += w
        prev, k = s, k + 1
    y = y[:n_out] / np.maximum(wsum[:n_out], 1e-3)
    return y


def fit_length(x):
    """Neural takes run long: cut pauses over 0.2 s, then speed up (≤ NEURAL_MAX_SPEEDUP) to land under MAX_SECONDS."""
    speed = 1.0
    if len(x) / SR > MAX_SECONDS: x = tighten(x, max_gap=0.2)
    if len(x) / SR > MAX_SECONDS:
        speed = min(NEURAL_MAX_SPEEDUP, (len(x) / SR) / (MAX_SECONDS - 0.05))
        x = wsola(x, speed)
    return x, speed


def neural_main(args):
    import random, shutil, tempfile, time, wave
    import tts_bakeoff as tb   # Models (mlx-audio loader + cached Whisper), asr_score, neural_text, to_rate, dia_budget
    import voice_gender as vg  # the male-voice gate (pitch, vocal tract, speaker-embedding FLI)

    logf = open(os.path.join(ROOT, "Logs", "tts-neural.log"), "a")

    def log(msg):
        line = f"{time.strftime('%H:%M:%S')} {msg}"
        print(line, flush=True); logf.write(line + "\n"); logf.flush()

    def wait_for_cool():   # Brett's Mac: before every model load and every clip (same limits as scripts/machealth.sh)
        while True:
            ok, line = tb.health_ok()
            if ok: return
            log(f"  mac-health: {line} -> pausing {tb.PAUSE_S} s"); time.sleep(tb.PAUSE_S)

    models = tb.Models(log)
    _get = models.get
    def get(name):
        if models.name != name: wait_for_cool()
        return _get(name)
    models.get = get

    def ref_path(ch): return os.path.join(REFS_DIR, f"{ch}.wav")

    def seed_all(seed):
        import mlx.core as mx
        mx.random.seed(seed); np.random.seed(seed); random.seed(seed)

    def collect(gen, m):
        parts, sr = [], m.sample_rate
        for r in gen:
            parts.append(np.array(r.audio, dtype=np.float32).reshape(-1)); sr = r.sample_rate or sr
        if not parts: raise RuntimeError("model returned no audio")
        return np.concatenate(parts), sr

    steps = args.steps.split(",")
    only = tuple(args.only.split(","))   # neural: comma-separated key prefixes
    seed_list = sorted({s for part in filter(None, args.seeds.split(","))
                        for s in (range(int(part.split("-")[0]), int(part.split("-")[-1]) + 1))})
    table = dict(read_table())
    manifest = load_manifest()
    manifest.setdefault("neural", {}); manifest.setdefault("neural_info", {})
    refs_meta_path = os.path.join(REFS_DIR, "refs.json")
    try: refs_meta = json.load(open(refs_meta_path))
    except (OSError, ValueError): refs_meta = {}
    summary = []
    t0 = time.time()
    with tempfile.TemporaryDirectory() as tmp:
        models.load_whisper(tmp)
        log(f"ASR: {models.whisper_note}")

        # ---------------------------------------------------------------- 1. one reference voice per character
        if "refs" in steps:
            os.makedirs(REFS_DIR, exist_ok=True)
            redo = set(filter(None, args.refs.split(",")))
            unknown = redo - set(CHARACTERS)
            if unknown: sys.exit(f"--refs: no such character(s) {sorted(unknown)} (have {sorted(CHARACTERS)})")
            for ch, (desc, rtext, emo, reuse) in CHARACTERS.items():
                path = ref_path(ch)
                want = args.redo_refs or ch in redo
                if os.path.exists(path) and not want:
                    log(f"ref   {ch:9} (kept)"); continue
                src = reuse and os.path.join(BAKEOFF_REFS, f"{reuse}.wav")
                if src and os.path.exists(src) and not want:
                    shutil.copyfile(src, path)
                    refs_meta[ch] = {"from": f"tts_bakeoff/refs/{reuse}.wav", "description": desc, "emotion": emo, "text": rtext}
                    log(f"ref   {ch:9} reused bake-off {reuse}.wav"); continue
                gated = ch in GENDER_GATE
                best, tried = None, []
                for seed in range(1, 8):
                    # ungated: the first good seed; gated: REF_SEEDS seeds (more while none passes), most male-reading wins
                    if best and best["good"] and (not gated or seed > REF_SEEDS): break
                    if seed > 5 and not gated: break
                    wait_for_cool()
                    m = models.get("qwen"); seed_all(seed)
                    x, sr = collect(m.generate(rtext, instruct=f"{desc}, {emo}", lang_code="english", temperature=0.9,
                                               verbose=False), m)
                    x48 = trim(tb.to_rate(x, sr, tmp)); dur = len(x48) / SR
                    heard = models.transcribe(x48, tmp); sc = tb.asr_score(heard, rtext) if heard is not None else 1.0
                    g_ok, g_why, g = vg.check(fades(loudness(x48, target_rms_db=-18.0)), SR) if gated else (True, [], None)
                    if g and g["fli"] > REF_FLI_MAX:
                        g_ok = False; g_why = g_why + [f"reference FLI {g['fli']:+.2f} > {REF_FLI_MAX:+.2f}"]
                    if g and ch in PITCH_CAP and (g.get("f0_median") or 0) > PITCH_CAP[ch][0]:
                        g_ok = False; g_why = g_why + [f"reference F0 {g.get('f0_median')} Hz > {PITCH_CAP[ch][0]:.0f} Hz"]
                    good = 5.0 <= dur <= 12.0 and sc >= 0.85 and g_ok   # Chatterbox uses the first 10 s; a slow drawl is fine
                    gnote = f" F0 {g.get('f0_median')}/{g.get('f0_p90')} Hz vtl {g.get('vtl_cm')} FLI {g['fli']:+.2f}" if g else ""
                    log(f"ref   {ch:9} seed {seed} {dur:5.2f} s asr {sc:.2f}{gnote}{' MALE-GATE: ' + '; '.join(g_why) if g_why else ''}"
                        f" heard: {heard!r}")
                    tried.append({"seed": seed, "seconds": round(dur, 2), "asr": round(sc, 2), "passed": good,
                                  **({"gender": dict(vg.gender_row(g), why=g_why)} if g else {})})
                    rank = (good, (-(g.get("f0_median") or 0) if ch in PITCH_CAP else -g["fli"]) if g else 0.0, sc)
                    if best is None or rank > best["rank"]:
                        best = dict(rank=rank, good=good, sc=sc, seed=seed, x48=x48, dur=dur, heard=heard)
                good, sc, seed, x48, dur, heard = (best[k] for k in ("good", "sc", "seed", "x48", "dur", "heard"))
                if os.path.exists(path):   # keep the voice it replaces (and what it was), outside the glob of live refs
                    stamp = time.strftime("%Y%m%d-%H%M")
                    os.makedirs(os.path.join(REFS_DIR, "retired"), exist_ok=True)
                    shutil.move(path, os.path.join(REFS_DIR, "retired", f"{ch}_{stamp}.wav"))
                    old = dict(refs_meta.get(ch, {}))
                    if gated: old["gender_at_retirement"] = vg.gender_row(vg.check(load(os.path.join(REFS_DIR, "retired", f"{ch}_{stamp}.wav"))[:, 0], SR)[2])
                    refs_meta.setdefault("retired", {})[f"{ch}_{stamp}"] = old
                    log(f"ref   {ch:9} old reference kept as tts_refs/retired/{ch}_{stamp}.wav")
                save(path, fades(loudness(x48, target_rms_db=-18.0)).astype(np.float32))
                refs_meta[ch] = {"model": tb.MODEL_ID["qwen"], "seed": seed, "seconds": round(dur, 2), "description": desc,
                                 "emotion": emo, "text": rtext, "heard": heard, "asr": round(sc, 2), "passed": good,
                                 **({"gender": next(t["gender"] for t in tried if t["seed"] == seed), "seeds_tried": tried}
                                    if gated else {})}
                log(f"ref   {ch:9} -> seed {seed}{' (NO seed passed: best effort)' if not good else ''}")
                json.dump(refs_meta, open(refs_meta_path, "w"), indent=1, default=_plain)
            json.dump(refs_meta, open(refs_meta_path, "w"), indent=1, default=_plain)
            models.unload()

        def clear_neural(key, d, keep=()):
            for rel in [r for r in manifest["neural"] if r.split("/")[0] == key]:
                f = os.path.join(OUT, rel)
                if rel not in keep and os.path.exists(f):
                    os.remove(f)
                    if os.path.exists(f + ".meta"): os.remove(f + ".meta")
                if rel not in keep:
                    manifest["neural"].pop(rel, None); manifest["neural_info"].pop(rel, None); _TOUCHED.add(rel)

        def write_takes(key, takes, extra):
            d = os.path.join(OUT, key); os.makedirs(d, exist_ok=True)
            rels = [f"{key}/{key}_n{i:02d}.wav" for i in range(1, len(takes) + 1)]
            clear_neural(key, d, keep=rels)
            for rel, tk in zip(rels, takes):
                path = os.path.join(OUT, rel)
                save(path, tk.pop("audio").astype(np.float32))
                manifest["neural"][rel] = sha1(path)
                manifest["neural_info"][rel] = dict(tk, **extra)
                _TOUCHED.add(rel)
            save_manifest(manifest)
            return rels

        def blocked(key):
            d = os.path.join(OUT, key)
            if not os.path.isdir(d) or args.force: return []
            mine = script_made(manifest, key, d)
            return [f for f in sorted(os.listdir(d)) if f.endswith(".wav") and f not in mine]

        # ---------------------------------------------------------------- 2. every line in VoiceLines.cs
        if "lines" in steps:
            keys = [k for k in table if k.startswith(only)]
            engine_of = lambda k: NEURAL_STYLE.get(k, NEURAL_DEFAULT[neural_character(k)])[0]
            keys.sort(key=lambda k: (engine_of(k) != "cbox", list(table).index(k)))   # one model load per engine
            for key in keys:
                text = table[key]; ch = neural_character(key)
                engine, exag, tag = NEURAL_STYLE.get(key, NEURAL_DEFAULT[ch])
                have = [r for r in manifest["neural"] if r.split("/")[0] == key]
                if have and not args.redo and all(os.path.exists(os.path.join(OUT, r)) for r in have):
                    log(f"{key:30} (kept {len(have)} neural take(s))"); continue
                real = blocked(key)
                if real:
                    log(f"{key:30} SKIPPED - real recording(s) present: {', '.join(real)}")
                    summary.append({"key": key, "skipped": real}); continue
                fx = voice_for(key)[2]
                said = tb.neural_text(neural_spoken(text, key))   # "U.S." is fed as "You Ess" (SPOKEN_AS, SPOKEN_KEY)
                sent = (tag + said) if engine == "turbo" else said
                mid = tb.MODEL_ID[engine]
                cands, fails = [], []
                for seed in (seed_list or range(1, NEURAL_MAX_ATTEMPTS + 1)):
                    # enough once NEURAL_TAKES takes are clean (every word heard right) and under the cap; else keep trying
                    # (--seeds: every listed seed is tried, and the best NEURAL_TAKES of them all are kept)
                    if not seed_list and seed > NEURAL_ATTEMPTS and sum(
                            c["pass"] and (c["asr"] or 1) >= CLEAN_ASR and not c["over_cap"] for c in cands) >= NEURAL_TAKES: break
                    wait_for_cool()
                    m = models.get(engine); seed_all(seed); ts = time.time()
                    try:
                        if engine == "turbo":
                            gen = m.generate(sent, ref_audio=ref_path(ch), temperature=0.8)
                        else:
                            gen = m.generate(sent, ref_audio=ref_path(ch), exaggeration=exag, cfg_weight=0.3,
                                             temperature=0.8, lang_code="en", verbose=False)
                        x, sr = collect(gen, m)
                    except Exception as e:  # noqa: BLE001
                        fails.append(f"seed {seed}: {type(e).__name__}: {e}"[:200]); log(f"{key:30} seed {seed} FAILED {e}")
                        continue
                    raw = trim(tb.to_rate(x, sr, tmp)); raw_s = len(raw) / SR
                    y, speed = fit_length(raw); spoken = len(y) / SR
                    heard = models.transcribe(y, tmp)
                    sc = tb.asr_score(asr_heard(heard, text), text) if heard is not None else None
                    if fx == "bullhorn": z = trim(bullhorn(y), floor_db=-50, post=0.05)
                    elif fx == "radio": z = trim(radio(y), floor_db=-50, post=0.03)
                    else: z = y
                    z = fades(loudness(z, target_rms_db=-15.0 if fx else -16.0))
                    if fx and sc is not None:   # score what the player hears: the radio band can eat a word the dry take had
                        heard_fx = models.transcribe(z, tmp)
                        sc_fx = tb.asr_score(asr_heard(heard_fx, text), text) if heard_fx is not None else sc
                        if sc_fx < sc: sc, heard = sc_fx, f"{heard_fx} (through the {fx})"
                    # the male gate on what ships (a bullhorn/radio line: the dry take at the same level - the speaker
                    # embeddings are not level-invariant): a female-side read fails like a misheard word
                    g_ok, g_why, g = (vg.check(z if not fx else fades(loudness(y, target_rms_db=-16.0)), SR, ch)
                                      if ch in GENDER_GATE else (True, [], None))
                    if g and ch in PITCH_CAP and (g.get("f0_median") or 0) > PITCH_CAP[ch][1]:
                        g_ok = False; g_why = g_why + [f"F0 {g.get('f0_median')} Hz > {PITCH_CAP[ch][1]:.0f} Hz ({ch}'s cap)"]
                    ok = 0.3 <= raw_s <= 6.0 and (sc is None or sc >= ASR_PASS) and g_ok
                    over = spoken > MAX_SECONDS + 0.01
                    cands.append({"audio": z, "seed": seed, "pass": ok, "asr": None if sc is None else round(sc, 2),
                                  "heard": heard, "raw_s": round(raw_s, 2), "spoken": round(spoken, 2),
                                  "speed": round(speed, 2), "seconds": round(len(z) / SR, 2), "over_cap": over,
                                  **({"gender": dict(vg.gender_row(g), passed=g_ok, why=g_why)} if g else {})})
                    gnote = f" F0 {g.get('f0_median')} Hz FLI {g['fli']:+.2f}" if g else ""
                    log(f"{key:30} seed {seed} {'ok ' if ok else 'BAD'} raw {raw_s:4.2f} s -> {spoken:4.2f} s x{speed:.2f}"
                        f" gen {time.time() - ts:4.1f} s asr {cands[-1]['asr']}{gnote}"
                        f"{' MALE-GATE: ' + '; '.join(g_why) if g_why else ''} heard: {heard!r}")
                # a misheard word ("It's me, mate") is worse than 0.3 s over the cap: clean first, then under the cap;
                # then the most male-reading
                good = sorted([c for c in cands if c["pass"]],
                              key=lambda c: ((c["asr"] or 1) < CLEAN_ASR, c["over_cap"], c["spoken"] if c["over_cap"] else 0,
                                             -(c["asr"] or 0), c["speed"],
                                             c.get("gender", {}).get("f0_median" if ch in PITCH_CAP else "fli", 0) or 0))
                takes = good[:NEURAL_TAKES]
                if takes:
                    files = write_takes(key, [dict(c) for c in takes],
                                        {"engine": engine, "model": mid, "character": ch, "sent": sent,
                                         **({"exaggeration": exag} if engine == "cbox" else {})})
                else:
                    files = []
                    if have: log(f"{key:30} NO take passed - the {len(have)} old take(s) stay in place: check them")
                gate_rej = [c for c in cands if not c.get("gender", {}).get("passed", True)]
                summary.append({"key": key, "character": ch, "engine": engine, "attempts": len(cands) + len(fails),
                                "passed": sum(c["pass"] for c in cands), "files": files, "failures": fails,
                                "takes": [{k: c[k] for k in ("seed", "asr", "raw_s", "spoken", "speed", "over_cap", "heard", "gender")
                                           if k in c} for c in takes],
                                **({"male_gate": vg.thresholds()["speech"], "male_gate_rejected": len(gate_rej),
                                    "male_gate_rejects": [{"seed": c["seed"], **c["gender"]} for c in gate_rej]}
                                   if ch in GENDER_GATE else {}),
                                "best_reject": None if takes or not cands else
                                max(cands, key=lambda c: c["asr"] or 0)["heard"]})
            models.unload()

        # ---------------------------------------------------------------- 3. word-less door-shot screams (Dia)
        if "scream" in steps and SCREAM_KEY.startswith(only):
            key = SCREAM_KEY
            have = [r for r in manifest["neural"] if r.split("/")[0] == key]
            if have and not args.redo:
                log(f"{key:30} (kept {len(have)} neural take(s))")
            elif blocked(key):
                log(f"{key:30} SKIPPED - real recording(s) present")
            else:
                cands = []
                for i in range(SCREAM_ATTEMPTS):
                    if len([c for c in cands if c["pass"]]) >= SCREAM_TAKES: break
                    ch, cue = SCREAMS[i % len(SCREAMS)]
                    seed = 100 + i
                    wait_for_cool()
                    m = models.get("dia"); seed_all(seed); ts = time.time()
                    rtext = refs_meta.get(ch, {}).get("text") or CHARACTERS[ch][1]
                    try:
                        x, sr = collect(m.generate(f"[S1] {cue}", ref_audio=ref_path(ch), ref_text=f"[S1] {rtext}",
                                                   max_tokens=tb.dia_budget(ref_path(ch), after_s=6.0), verbose=False), m)
                    except Exception as e:  # noqa: BLE001
                        log(f"{key:30} take {i} FAILED {type(e).__name__}: {e}"); continue
                    y = trim(tb.to_rate(x, sr, tmp), floor_db=-36.0, post=0.06); raw_s = len(y) / SR
                    if raw_s > 1.5:   # keep the head of a long scream, fade the rest away
                        n = int(1.5 * SR); y = y[:n].copy(); f = int(0.25 * SR)
                        y[-f:] *= np.linspace(1, 0, f) ** 2
                    heard = models.transcribe(y, tmp) or ""
                    words = re.findall(r"[a-z']+", heard.lower())
                    wordless = all(re.fullmatch(r"[aghrouwnme]+", w) for w in words)
                    peak_db = 20 * np.log10(np.abs(y).max() + 1e-9)
                    z = fades(loudness(y, target_rms_db=-16.0), fout=0.06)
                    # the male gate's scream rules on the shipped take: heard through the door (650 Hz low-pass), pitch,
                    # still the man it was cued with
                    g_ok, g_why, g = vg.check(z, SR, ch, scream=True)
                    ok = bool(0.6 <= min(raw_s, 1.5) and raw_s <= 4.0 and wordless and peak_db > -30 and g_ok)
                    cands.append({"audio": z, "seed": seed, "pass": ok, "voice": ch, "cue": cue, "heard": heard,
                                  "raw_s": round(raw_s, 2), "seconds": round(len(z) / SR, 2), "cropped": raw_s > 1.5,
                                  "gender": dict(vg.gender_row(g), passed=g_ok, why=g_why)})
                    log(f"{key:30} seed {seed} {ch:8} {'ok ' if ok else 'BAD'} raw {raw_s:4.2f} s -> {len(z) / SR:4.2f} s"
                        f" gen {time.time() - ts:4.1f} s F0 {g.get('f0_median')}/{g.get('f0_p90')} Hz muffled FLI "
                        f"{g.get('fli_lp650', 0):+.2f} own {g.get('sim_own_ve')}{' MALE-GATE: ' + '; '.join(g_why) if g_why else ''}"
                        f" heard: {heard!r}  cue {cue!r}")
                takes = [c for c in cands if c["pass"]][:SCREAM_TAKES]
                files = write_takes(key, [dict(c) for c in takes], {"engine": "dia", "model": tb.MODEL_ID["dia"]}) if takes else []
                if not takes and have: log(f"{key:30} NO take passed - the {len(have)} old take(s) stay in place: check them")
                summary.append({"key": key, "engine": "dia", "attempts": len(cands), "passed": sum(c["pass"] for c in cands),
                                "files": files, "male_gate": vg.thresholds()["scream"],
                                "male_gate_rejected": sum(not c["gender"]["passed"] for c in cands),
                                "male_gate_rejects": [{"seed": c["seed"], "cue": c["cue"], **c["gender"]} for c in cands
                                                      if not c["gender"]["passed"]],
                                "takes": [{k: c[k] for k in ("seed", "voice", "cue", "raw_s", "seconds", "cropped", "heard", "gender")}
                                          for c in takes]})
                models.unload()

        # ---------------------------------------------------------------- 4. Mike's drunk mumble (Chatterbox-Turbo)
        if "mumble" in steps and MUMBLE_KEY.startswith(only):
            key, ch = MUMBLE_KEY, "mike"
            have = [r for r in manifest["neural"] if r.split("/")[0] == key]
            if have and not args.redo:
                log(f"{key:30} (kept {len(have)} neural take(s))")
            elif blocked(key):   # the freesound mumble counts as a real recording (move it out, or --force)
                log(f"{key:30} SKIPPED - real recording(s) present: {', '.join(blocked(key))}")
            else:
                cands = []
                for i in range(MUMBLE_ATTEMPTS):
                    # every cue once, then more seeds only while fewer than MUMBLE_TAKES pass
                    if i >= len(MUMBLES) and len([c for c in cands if c["pass"]]) >= MUMBLE_TAKES: break
                    cue, seed = MUMBLES[i % len(MUMBLES)], 200 + i
                    wait_for_cool()
                    m = models.get("turbo"); seed_all(seed); ts = time.time()
                    try:
                        x, sr = collect(m.generate(cue, ref_audio=ref_path(ch), temperature=0.8), m)
                    except Exception as e:  # noqa: BLE001
                        log(f"{key:30} take {i} FAILED {type(e).__name__}: {e}"); continue
                    y = trim(tb.to_rate(x, sr, tmp), floor_db=-40.0, post=0.1); raw_s = len(y) / SR
                    if raw_s > MUMBLE_MAX_S:
                        n = int(MUMBLE_MAX_S * SR); y = y[:n].copy(); f = int(0.4 * SR)
                        y[-f:] *= np.linspace(1, 0, f) ** 2
                    heard = models.transcribe(y, tmp) or ""   # recorded only: a mumble isn't meant to be understood
                    peak_db = 20 * np.log10(np.abs(y).max() + 1e-9)
                    z = fades(loudness(y, target_rms_db=-18.0), fout=0.15)
                    g_ok, g_why, g = vg.check(z, SR, ch)
                    ok = bool(1.0 <= raw_s <= 8.0 and peak_db > -30 and g.get("voiced_frac", 0) >= 0.2 and g_ok)
                    cands.append({"audio": z, "seed": seed, "pass": ok, "cue": cue, "heard": heard, "raw_s": round(raw_s, 2),
                                  "seconds": round(len(z) / SR, 2), "gender": dict(vg.gender_row(g), passed=g_ok, why=g_why)})
                    log(f"{key:30} seed {seed} {'ok ' if ok else 'BAD'} raw {raw_s:4.2f} s gen {time.time() - ts:4.1f} s"
                        f" F0 {g.get('f0_median')} Hz FLI {g['fli']:+.2f}{' MALE-GATE: ' + '; '.join(g_why) if g_why else ''}"
                        f" heard: {heard!r}  cue {cue!r}")
                takes = sorted([c for c in cands if c["pass"]], key=lambda c: c["gender"]["fli"])[:MUMBLE_TAKES]
                files = write_takes(key, [dict(c) for c in takes], {"engine": "turbo", "model": tb.MODEL_ID["turbo"],
                                                                    "character": ch}) if takes else []
                summary.append({"key": key, "character": ch, "engine": "turbo", "attempts": len(cands),
                                "passed": sum(c["pass"] for c in cands), "files": files, "male_gate": vg.thresholds()["speech"],
                                "male_gate_rejected": sum(not c["gender"]["passed"] for c in cands),
                                "male_gate_rejects": [{"seed": c["seed"], "cue": c["cue"], **c["gender"]} for c in cands
                                                      if not c["gender"]["passed"]],
                                "takes": [{k: c[k] for k in ("seed", "cue", "raw_s", "seconds", "heard", "gender")} for c in takes]})
                models.unload()
    save_manifest(manifest)
    sum_path = os.path.join(ROOT, "Logs", "tts-neural-summary.json")   # merged by key, so a partial run keeps the rest
    try: prev = [r for r in json.load(open(sum_path)) if r["key"] not in {r["key"] for r in summary}]
    except (OSError, ValueError, KeyError, TypeError): prev = []
    json.dump(prev + summary, open(sum_path, "w"), indent=1, default=_plain)
    log(f"neural done in {time.time() - t0:.0f} s; summary -> Logs/tts-neural-summary.json (then: Build Sfx Library)")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--only", default="", help="only keys starting with this prefix")
    ap.add_argument("--force", action="store_true", help="overwrite key folders that hold real recordings")
    ap.add_argument("--engine", default="say", choices=["say", "neural"])
    ap.add_argument("--steps", default="refs,lines,scream,mumble", help="neural only: refs,lines,scream,mumble")
    ap.add_argument("--redo", action="store_true", help="neural only: re-render keys that already have neural takes")
    ap.add_argument("--redo-refs", action="store_true", help="neural only: re-design ALL the character reference voices")
    ap.add_argument("--refs", default="", help="neural only: re-design just these references (comma list, e.g. burglar,mike);"
                                               " the old one is kept in scripts/tts_refs/retired/")
    ap.add_argument("--seeds", default="", help="neural lines only: try exactly these seeds (e.g. 1-14 or 1,3,8-12) and keep"
                                                " the best NEURAL_TAKES of them all - for a line whose takes came out long or"
                                                " misheard on seeds 1-7 (with --redo; a seed's take is close to, not"
                                                " identical with, its earlier take)")
    args = ap.parse_args()
    if args.engine == "neural": return neural_main(args)

    check_voices()
    rows = [(k, t) for k, t in read_table() if k.startswith(args.only)]
    manifest = load_manifest()
    written, skipped = [], []
    with tempfile.TemporaryDirectory() as tmp:
        for key, text in rows:
            d = os.path.join(OUT, key)
            name = f"{key}_01.wav"
            rel = f"{key}/{name}"
            if os.path.isdir(d) and not args.force:
                mine = script_made(manifest, key, d)   # say placeholders and neural takes both count as ours
                foreign = [f for f in sorted(os.listdir(d)) if f.endswith(".wav") and f not in mine]
                if foreign:
                    skipped.append((key, foreign)); continue
            voice, rate, fx = voice_for(key)
            x = tighten(trim(render(text, voice, rate, tmp)))
            speed = 1.0
            while len(x) / SR > MAX_SECONDS and speed < MAX_SPEEDUP:   # keep lines snappy: re-speak faster
                speed = min(MAX_SPEEDUP, speed * (len(x) / SR) / (MAX_SECONDS - 0.05))
                x = tighten(trim(render(text, voice, rate * speed, tmp)))
            spoken = len(x) / SR
            if fx == "bullhorn": x = trim(bullhorn(x), floor_db=-50, post=0.05)
            elif fx == "radio": x = trim(radio(x), floor_db=-50, post=0.03)
            x = fades(loudness(x, target_rms_db=-15.0 if fx else -16.0))
            os.makedirs(d, exist_ok=True)
            path = os.path.join(d, name)
            save(path, x.astype(np.float32))
            manifest[rel] = sha1(path); _TOUCHED.add(rel)
            written.append((key, voice, fx, spoken, len(x) / SR, speed))
    save_manifest(manifest)

    for key, voice, fx, spoken, total, speed in written:
        flag = "  (over 2.5 s)" if spoken > MAX_SECONDS + 0.01 else ""
        rate = f" x{speed:.2f}" if speed > 1.0 else ""
        print(f"{key:22} {voice:22} {fx or '':8} speech {spoken:4.2f} s  clip {total:4.2f} s{rate}{flag}")
    for key, files in skipped:
        print(f"{key:22} SKIPPED — holds recordings not made by this script: {', '.join(files)} (use --force)")
    keys = {k for k, _ in read_table()}
    stale = sorted({k.split("/")[0] for k, v in manifest.items() if isinstance(v, str)} - keys)
    if stale: print(f"note: TTS clips for keys no longer in VoiceLines.cs: {', '.join(stale)}")
    print(f"{len(written)} line(s) → {OUT}  (then in Unity: HIS > Audio > Build Sfx Library)")


if __name__ == "__main__":
    main()
