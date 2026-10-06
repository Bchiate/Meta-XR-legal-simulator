using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HIS.Core;
using UnityEditor;
using UnityEngine;

/// <summary>
/// HIS > Audio > Build Sfx Library: every folder in Assets/_Game/Audio/Clips (one per key, cut by scripts/prepare_sfx.py)
/// becomes a SfxLibrary entry with the mix defaults below. Re-running keeps nothing hand-edited — change the table.
/// Import rules for those clips live in <see cref="SfxImportRules"/>.
/// </summary>
public static class SfxLibraryBuilder
{
    const string ClipsDir = "Assets/_Game/Audio/Clips";
    const string LibraryPath = "Assets/_Game/Audio/SfxLibrary.asset";
    const string TtsManifest = "scripts/tts_manifest.json"; // "key/file.wav": sha1 of every placeholder make_tts.py wrote

    // key prefix → volume, spatial, minDist, maxDist, cooldown, maxVoices, priority, lowpassHz, pitchJitter.
    // First matching prefix wins, so specific keys go first. Priority: 0 = never stolen, 256 = first to go.
    static readonly (string prefix, float vol, bool spatial, float min, float max, float cool, int voices, int prio, float lp, float jitter)[] Mix =
    {
        // The siren stays distant: low-passed, wide rolloff (Brett 09-22 "pretty aggressive"; 09-23 "a little louder").
        // Up to 7 at once in the bad-call show's pile-up (BadCallShow caps their summed loudness, not this table).
        ("outcome.siren.pa", 0.30f, true,  4f, 60f, 0f,    4, 20,  2600f, 0f),
        ("outcome.siren",    0.28f, true,  4f, 60f, 0f,    6, 20,  2000f, 0f),
        // Helicopter overhead (synthesized thump loop, prepare_sfx.py synth_rotor): wide rolloff so 20 m up still reads.
        ("outcome.rotor",    0.60f, true,  12f, 150f, 0f,  1, 22,  1600f, 0f),
        ("outcome.camera",   0.80f, true,  1f, 15f, 0f,    2, 25,  0f,    0.03f),
        ("outcome.",         0.70f, true,  1f, 30f, 0f,    1, 30,  0f,    0f),
        // A Marshal night's hitman car pulling up (synthesized, prepare_sfx.py synth_car_arrive; Brett 0.1.56): played toward the
        // car within 6 m of the front door (PoliceCars.Arrival), a wide rolloff and the house's low-pass; never pitch-jittered.
        ("car.arrive",       0.80f, true,  5f, 80f, 0f,    2, 30,  2000f, 0f),
        ("win.",             0.80f, false, 1f, 20f, 0f,    2, 10,  0f,    0f),
        ("amb.",             0.35f, true,  1.5f, 20f, 0f,  1, 40,  0f,    0f),
        // Voice lines (text: VoiceLines.cs; audio: scripts/make_tts.py neural takes, say placeholders, Brett's recordings). The player's yells are in his own head;
        // the police are outside the door, a little muffled; everyone else talks from where they stand. vo.player gets 2 voices
        // so a repeat yell isn't dropped while CommandSystem fades out the previous take of the same line.
        ("vo.player.",       1.00f, false, 1f, 10f, 0f,    2, 5,   0f,    0f),
        ("vo.police.",       0.80f, true,  3f, 60f, 0f,    1, 15,  3000f, 0f),
        ("vo.",              1.00f, true,  1f, 25f, 0f,    1, 5,   0f,    0f),
        ("pistol.shot",      1.00f, true,  1.5f, 60f, 0f,  4, 0,   0f,    0.04f),
        ("shotgun.shot",     1.00f, true,  1.5f, 60f, 0f,  3, 0,   0f,    0.03f),
        ("pistol.",          0.80f, true,  0.4f, 12f, 0.05f, 2, 60, 0f,   0.04f),
        ("shotgun.",         0.85f, true,  0.4f, 12f, 0.05f, 2, 60, 0f,   0.04f),
        ("holster.",         0.60f, true,  0.3f, 8f,  0.15f, 1, 90, 0f,   0.05f),
        ("casing.",          0.45f, true,  0.3f, 10f, 0.03f, 4, 200, 0f,  0.08f),
        ("impact.wall",      0.70f, true,  0.6f, 20f, 0.02f, 3, 120, 0f,  0.08f),
        ("flesh.",           0.95f, true,  0.6f, 20f, 0.04f, 2, 40,  0f,  0.06f),
        ("blood.",           0.45f, true,  0.4f, 10f, 0.2f,  1, 180, 0f,  0.08f),
        ("body.fall",        0.90f, true,  0.8f, 20f, 0.1f,  2, 60,  0f,  0.05f),
        ("intruder.death",   1.00f, true,  1f,  25f, 0f,    1, 20,  0f,  0f),
        // Word-less neural screams (make_tts.py --engine neural, Dia) for a shot through the closed door; same mix as pain.
        ("intruder.pain.scream", 0.90f, true, 1f, 25f, 0.35f, 1, 30,  0f,  0.04f),
        ("intruder.pain",    0.90f, true,  1f,  25f, 0.35f, 1, 30,  0f,  0.04f),
        ("intruder.swing",   0.75f, true,  1f,  20f, 0.3f,  1, 70,  0f,  0.04f),
        ("intruder.step",    0.45f, true,  0.6f, 15f, 0.12f, 2, 220, 0f, 0.07f),
        ("drunk.",           0.90f, true,  1f,  20f, 0f,    1, 30,  0f,  0f),
        // The security monitor (Scripts/Security): a tuned doorbell chime (never pitch-jittered, one at a time) and its soft
        // motion blip at the screen (synthesized, prepare_sfx.py synth_doorbell / synth_motion).
        ("door.bell",        0.90f, true,  1.5f, 25f, 1.0f, 1, 20,  0f,  0f),
        ("cam.",             0.60f, true,  0.5f, 8f,  0.3f, 1, 60,  0f,  0f),
        // The walk-up outside (prepare_sfx.py synth_gravel_step ×5 / synth_gate_latch).
        ("steps.gravel",     0.55f, true,  0.6f, 15f, 0.12f, 2, 210, 0f, 0.07f),
        ("gate.",            0.80f, true,  1f,  20f, 0.2f,  1, 60,  0f,  0.04f),
        ("door.",            1.00f, true,  1f,  30f, 0.05f, 2, 30,  0f,  0.04f),
        ("player.",          0.90f, false, 1f,  10f, 0.08f, 1, 20,  0f,  0f),
    };

    [MenuItem("HIS/Audio/Build Sfx Library")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(ClipsDir)) { Debug.LogError($"[HIS] {ClipsDir} missing — run scripts/prepare_sfx.py first"); return; }
        var lib = AssetDatabase.LoadAssetAtPath<SfxLibrary>(LibraryPath);
        if (lib == null) { lib = ScriptableObject.CreateInstance<SfxLibrary>(); AssetDatabase.CreateAsset(lib, LibraryPath); }
        lib.entries.Clear();
        var (say, neural) = LoadTtsManifest();
        int clips = 0, placeholders = 0;
        foreach (var dir in Directory.GetDirectories(ClipsDir).OrderBy(d => d))
        {
            string key = Path.GetFileName(dir);
            var files = Directory.GetFiles(dir, "*.wav").OrderBy(f => f).ToList();
            // Best tier wins, so Pick never alternates robot/Brett: real takes (Brett's recordings) > neural TTS takes
            // (make_tts.py --engine neural) > `say` placeholders. A tier only counts once one of its clips actually loads
            // (a just-dropped wav may not be imported yet) — never leave the key empty.
            var sayTier = files.Where(f => IsScriptMade(say, key, f)).ToList();
            var neuralTier = files.Where(f => IsScriptMade(neural, key, f)).ToList();
            var realTier = files.Except(sayTier).Except(neuralTier).ToList();
            var found = new AudioClip[0];
            foreach (var tier in new[] { realTier, neuralTier, sayTier })
            {
                if (found.Length > 0) { placeholders += tier.Count; continue; }
                found = LoadClips(tier);
            }
            if (found.Length == 0) continue;
            var m = Mix.FirstOrDefault(x => key.StartsWith(x.prefix));
            if (m.prefix == null) m = ("", 0.8f, true, 0.6f, 20f, 0f, 0, 128, 0f, 0.05f);
            lib.entries.Add(new SfxLibrary.Entry
            {
                key = key, clips = found, volume = m.vol, spatial = m.spatial, minDistance = m.min, maxDistance = m.max,
                cooldown = m.cool, maxVoices = m.voices, priority = m.prio, lowpassHz = m.lp, pitchJitter = m.jitter,
            });
            clips += found.Length;
        }
        lib.ResetRuntime(); // a map cached by an earlier Play session would still point at the old entries
        EditorUtility.SetDirty(lib);
        AssetDatabase.SaveAssets();
        string skipped = placeholders > 0 ? $" ({placeholders} TTS placeholder(s) set aside for better takes: real > neural > say)" : "";
        Debug.Log($"[HIS] Sfx library: {lib.entries.Count} keys, {clips} clips{skipped} → {LibraryPath}");
    }

    static AudioClip[] LoadClips(IEnumerable<string> files) =>
        files.Select(f => AssetDatabase.LoadAssetAtPath<AudioClip>(f.Replace('\\', '/'))).Where(c => c != null).ToArray();

    // scripts/tts_manifest.json: flat "key/file.wav": "sha1" pairs are `say` placeholders; the "neural": { … } block holds the
    // neural takes ("neural_info" carries their metadata and no sha1s, so the pair regex never picks anything up there).
    static (Dictionary<string, string> say, Dictionary<string, string> neural) LoadTtsManifest()
    {
        var say = new Dictionary<string, string>();
        var neural = new Dictionary<string, string>();
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), TtsManifest);
        if (!File.Exists(path)) return (say, neural);
        string text = File.ReadAllText(path);
        var block = Regex.Match(text, "\"neural\"\\s*:\\s*\\{([^{}]*)\\}");
        if (block.Success)
        {
            Pairs(block.Groups[1].Value, neural);
            text = text.Remove(block.Index, block.Length);
        }
        Pairs(text, say);
        return (say, neural);
    }

    static void Pairs(string text, Dictionary<string, string> into)
    {
        foreach (Match x in Regex.Matches(text, "\"([^\"]+)\"\\s*:\\s*\"([0-9a-f]{40})\""))
            into[x.Groups[1].Value] = x.Groups[2].Value;
    }

    // Byte-identical to what make_tts.py wrote (a real take saved over the same name has a different sha1).
    static bool IsScriptMade(Dictionary<string, string> tts, string key, string file)
    {
        if (!tts.TryGetValue($"{key}/{Path.GetFileName(file)}", out var want)) return false;
        using var sha = SHA1.Create();
        var hex = string.Concat(sha.ComputeHash(File.ReadAllBytes(file)).Select(b => b.ToString("x2")));
        return hex == want;
    }
}

/// <summary>
/// Import rules for game audio (Quest): short one-shots decompress on load (no CPU decode when 8 fire at once),
/// mid-length clips stay Vorbis in memory, long loops (siren, night ambience) stream. Everything but 2D stings is mono.
/// </summary>
public class SfxImportRules : AssetPostprocessor
{
    public override uint GetVersion() => 1;

    void OnPreprocessAudio()
    {
        if (!assetPath.StartsWith("Assets/_Game/Audio/")) return;
        var imp = (AudioImporter)assetImporter;
        string key = Path.GetFileName(Path.GetDirectoryName(assetPath));
        bool stereo2D = key.StartsWith("win.") || key.StartsWith("amb.");
        imp.forceToMono = !stereo2D;
        imp.loadInBackground = false;
        long bytes = new FileInfo(assetPath).Length;
        float seconds = bytes / (48000f * 2f * (stereo2D ? 2f : 1f));
        var s = imp.defaultSampleSettings;
        s.sampleRateSetting = AudioSampleRateSetting.OptimizeSampleRate;
        if (seconds > 8f) { s.loadType = AudioClipLoadType.Streaming; s.compressionFormat = AudioCompressionFormat.Vorbis; s.quality = 0.6f; s.preloadAudioData = false; }
        else if (seconds > 1.5f) { s.loadType = AudioClipLoadType.CompressedInMemory; s.compressionFormat = AudioCompressionFormat.Vorbis; s.quality = 0.7f; s.preloadAudioData = true; }
        else { s.loadType = AudioClipLoadType.DecompressOnLoad; s.compressionFormat = AudioCompressionFormat.Vorbis; s.quality = 0.8f; s.preloadAudioData = true; }
        imp.defaultSampleSettings = s;
    }
}
