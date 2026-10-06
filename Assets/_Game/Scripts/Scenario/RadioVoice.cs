using HIS.Core;
using UnityEngine;

namespace HIS.Scenario
{
    /// <summary>
    /// A voice over the marshal's radio (Brett 2026-10-04: the weapons check as a story handover, and "the ortiz dude could be
    /// anybody walking up"): squelch, the line, its caption. The same treatment as the police dispatcher
    /// (PoliceResponse.Radio), but the line is always captioned — a story line reads even before its take lands — and the
    /// speaker is named. Marshal mode only (the handover, Ortiz calling ahead of a walk-up); the hitmen never use it.
    /// </summary>
    public static class RadioVoice
    {
        /// <summary>
        /// The squelch's lead (s): the line starts this long after the crackle, not under it (the squelch sat only 4–8 dB below
        /// the first word: "Pump it." came out as crackle then "…ump it."). The caption shows at once.
        /// </summary>
        public const float SquelchLead = 0.25f;

        /// <summary>Squelch + <paramref name="key"/> (2D, <see cref="SquelchLead"/> s later) + caption "<paramref name="speaker"/>:
        /// text". Returns the transmission's length in seconds, the lead included (0 when the line's clip isn't there yet), so a
        /// caller can wait it out.</summary>
        public static float Transmit(string key, string speaker, float volume = 0.55f)
        {
            bool squelch = Sfx.Has("radio.squelch");
            if (squelch) Sfx.Play2D("radio.squelch", 0.5f);
            float lead = squelch ? SquelchLead : 0f;
            float len = 0f;
            if (Sfx.Has(key)) { Sfx.Play2D(key, volume, lead); len = Sfx.Length(key) + lead; }
            var text = VoiceLines.Text(key);
            if (!string.IsNullOrEmpty(text))
            {
                HIS.UI.Subtitles.Show(speaker, text, Mathf.Max(2.5f, len + 0.8f));
                Debug.Log($"[HIS] Radio {speaker}: {text}");
            }
            else Debug.LogWarning($"[HIS] Radio {speaker}: no line text for {key}");
            return len;
        }
    }
}
