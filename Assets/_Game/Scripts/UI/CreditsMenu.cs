using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HIS.UI
{
    /// <summary>
    /// In-game credits (CC BY 4.0 needs the attribution in the game): the pause menu's "Credits" row opens a scrolling page in
    /// the <see cref="Notice"/> panel. The text is CREDITS.md from the repo root, copied into a TextAsset at build time by
    /// the editor's CreditsSync (MainSceneBuilder and every player build), so that file stays the one source of truth.
    /// Markdown is kept light: "## " headings, "- " / "  - " bullets, **bold**, `code`; the intro before the first heading
    /// (notes for developers) is skipped. Lines are pre-wrapped so the panel keeps one size while scrolling.
    /// Input (from <see cref="HIS.Core.GamePause"/> while paused): stick up/down scrolls (hold to repeat), B/Y or a trigger closes.
    /// </summary>
    public class CreditsMenu : MonoBehaviour
    {
        public static CreditsMenu Instance { get; private set; }

        [SerializeField] TextAsset source;          // Assets/_Game/Data/Credits.txt ← CREDITS.md
        [SerializeField] int visibleLines = 14;
        [SerializeField] int wrapColumns = 64;

        public const string NoticeKey = "credits";
        public bool IsOpen { get; private set; }
        public int Offset { get; private set; }
        /// <summary>Display lines (TMP rich text, wrapped).</summary>
        public IReadOnlyList<string> Lines => Ensure()._lines;
        /// <summary>One plain-text entry per heading/bullet, unwrapped (tests compare these with CREDITS.md).</summary>
        public IReadOnlyList<string> Entries => Ensure()._plain;
        public string SourceText => source != null ? source.text : "";
        public int MaxOffset => Mathf.Max(0, Lines.Count - visibleLines);

        List<string> _lines, _plain;
        int _heldDir;
        float _repeatAt;

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        CreditsMenu Ensure()
        {
            if (_lines != null) return this;
            _lines = new List<string>(); _plain = new List<string>();
            Parse(SourceText, wrapColumns, _lines, _plain);
            if (_lines.Count == 0) _lines.Add("Credits text missing from this build.");
            return this;
        }

        public void Open()
        {
            Ensure();
            IsOpen = true; Offset = 0; _heldDir = 0;
            Show();
            Debug.Log($"[HIS] Credits opened ({_lines.Count} lines, {_plain.Count} entries)");
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Notice.Hide(NoticeKey);
        }

        public void Scroll(int delta)
        {
            int o = Mathf.Clamp(Offset + delta, 0, MaxOffset);
            if (o == Offset) return;
            Offset = o;
            if (IsOpen) Show();
        }

        /// <summary>Per frame while open and paused (unscaled time). Returns false once it has closed itself.</summary>
        public bool Tick()
        {
            if (!IsOpen) return false;
            if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch) || OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) ||
                OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch) ||
                OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            { Close(); return false; }
            var l = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            var r = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            float y = Mathf.Abs(l.y) > Mathf.Abs(r.y) ? l.y : r.y;
            int dir = y > 0.6f ? -1 : y < -0.6f ? 1 : 0; // stick up = back toward the top
            if (dir != 0 && (dir != _heldDir || Time.unscaledTime >= _repeatAt))
            {
                Scroll(dir);
                _repeatAt = Time.unscaledTime + (dir != _heldDir ? 0.35f : 0.07f); // hold to keep scrolling
            }
            _heldDir = dir;
            return true;
        }

        void Show() => Notice.Show(NoticeKey, Page(), 101); // over the pause panel (100)

        /// <summary>The page at the current offset (always <see cref="visibleLines"/> lines tall).</summary>
        public string Page()
        {
            Ensure();
            int end = Mathf.Min(_lines.Count, Offset + visibleLines);
            var sb = new StringBuilder("<b>CREDITS</b>  <size=60%><color=#A0A0A0>")
                .Append(Offset + 1).Append('-').Append(end).Append(" of ").Append(_lines.Count).Append("</color></size>\n<size=62%><align=left><margin=8%>");
            for (int i = Offset; i < Offset + visibleLines; i++) sb.Append(i < _lines.Count ? _lines[i] : "").Append('\n');
            sb.Append("</margin></align>\n<color=#A0A0A0>")
              .Append(Offset > 0 ? "^ " : "").Append("Stick up/down: scroll   B or trigger: back").Append(Offset < MaxOffset ? " v" : "")
              .Append("</color></size>");
            return sb.ToString();
        }

        // ───────────────────────────── markdown → lines ─────────────────────────────

        /// <summary>ASCII only: the XR text material is the primary font's; a glyph from a fallback atlas may not draw in stereo.</summary>
        public static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '—' || c == '–' || c == '−') sb.Append('-');
                else if (c == '“' || c == '”') sb.Append('"');
                else if (c == '‘' || c == '’') sb.Append('\'');
                else if (c == '…') sb.Append("...");
                else if (c == '<') sb.Append('('); // no stray rich-text tags
                else if (c == '>') sb.Append(')');
                else if (c == '\t') sb.Append(' ');
                else if (c < 128) sb.Append(c);
                else sb.Append('?');
            }
            return sb.ToString();
        }

        /// <summary>A markdown line's text as shown: bullet marker, ** and ` removed, sanitized.</summary>
        public static string PlainOf(string mdLine)
        {
            var t = mdLine.Trim();
            if (t.StartsWith("## ")) t = t.Substring(3);
            else if (t.StartsWith("# ")) t = t.Substring(2);
            else if (t.StartsWith("- ")) t = t.Substring(2);
            return Sanitize(t.Replace("**", "").Replace("`", "")).Trim();
        }

        public static void Parse(string md, int columns, List<string> lines, List<string> plain)
        {
            bool started = false;
            foreach (var raw in (md ?? "").Replace("\r", "").Split('\n'))
            {
                if (raw.StartsWith("## "))
                {
                    started = true;
                    if (lines.Count > 0) lines.Add("");
                    var h = PlainOf(raw);
                    lines.Add($"<color=#FFD24A><b>{h}</b></color>");
                    plain.Add(h);
                    continue;
                }
                if (!started || string.IsNullOrWhiteSpace(raw)) continue;
                int indent = raw.Length - raw.TrimStart(' ').Length;
                var body = raw.Trim();
                string first = "", rest = "";
                if (body.StartsWith("- ")) { body = body.Substring(2); first = indent >= 2 ? "    - " : "- "; rest = indent >= 2 ? "      " : "  "; }
                else if (indent >= 2) { first = rest = "      "; }
                plain.Add(PlainOf(body));
                Wrap(Sanitize(body.Replace("`", "")), first, rest, columns, lines);
            }
        }

        /// <summary>Word wrap at <paramref name="columns"/> visible characters; **bold** spans are closed and reopened per line.</summary>
        static void Wrap(string body, string first, string rest, int columns, List<string> lines)
        {
            var words = new List<(string w, bool b)>();
            bool bold = false;
            var cur = new StringBuilder();
            void Flush() { if (cur.Length > 0) { words.Add((cur.ToString(), bold)); cur.Clear(); } }
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '*' && i + 1 < body.Length && body[i + 1] == '*') { Flush(); bold = !bold; i++; continue; }
                if (body[i] == ' ') { Flush(); words.Add((" ", false)); continue; }
                cur.Append(body[i]);
            }
            Flush();

            var line = new StringBuilder(); int len = 0; bool open = false;
            string prefix = first;
            void Emit() { if (open) line.Append("</b>"); lines.Add(prefix + line.ToString().TrimEnd()); line.Clear(); len = 0; open = false; prefix = rest; }
            foreach (var (w, b) in words)
            {
                if (w == " ") { if (len > 0) { if (open) { line.Append("</b>"); open = false; } line.Append(' '); len++; } continue; }
                if (len > 0 && prefix.Length + len + w.Length > columns) Emit();
                if (b && !open) { line.Append("<b>"); open = true; }
                else if (!b && open) { line.Append("</b>"); open = false; }
                line.Append(w); len += w.Length;
            }
            if (len > 0) Emit();
        }
    }
}
