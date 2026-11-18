using System.Text;
using System.Text.RegularExpressions;
using LycRomanise.Core;

namespace LycRomanise.Romanize;

/// <summary>Splits a line into runs by script and romanizes each with its own engine. Latin text is
/// left as it is. Han characters read as Japanese in a Japanese song, else as Chinese.</summary>
public static class Romanizer
{
    static string? Class(char ch)
    {
        int o = ch;
        if (o < 0x0370) return null;
        if ((o >= 0x1100 && o <= 0x11FF) || (o >= 0x3130 && o <= 0x318F) || (o >= 0xAC00 && o <= 0xD7A3)) return "ko";
        if ((o >= 0x3040 && o <= 0x30FF) || (o >= 0x31F0 && o <= 0x31FF) || (o >= 0xFF66 && o <= 0xFF9F)) return "kana";
        if ((o >= 0x3400 && o <= 0x4DBF) || (o >= 0x4E00 && o <= 0x9FFF) || (o >= 0xF900 && o <= 0xFAFF)) return "han";
        if ((o >= 0x0600 && o <= 0x06FF) || (o >= 0x0750 && o <= 0x077F) || (o >= 0x08A0 && o <= 0x08FF) || (o >= 0xFB50 && o <= 0xFDFF) || (o >= 0xFE70 && o <= 0xFEFF)) return "ar";
        if (o >= 0x0400 && o <= 0x052F) return "ru";
        if ((o >= 0x0370 && o <= 0x03FF) || (o >= 0x1F00 && o <= 0x1FFF)) return "el";
        if ((o >= 0x0590 && o <= 0x05FF) || (o >= 0xFB1D && o <= 0xFB4F)) return "he";
        if (o >= 0x0900 && o <= 0x097F) return "hi";
        return null;
    }

    public static bool Needs(string? text) => !string.IsNullOrEmpty(text) && text.Any(c => Class(c) != null);

    /// <summary>The dominant non-Latin script: ko, ja, zh, ar, ru, el, he, hi, or null.</summary>
    public static string? DetectScript(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var counts = new Dictionary<string, int>();
        foreach (var ch in text) if (Class(ch) is { } c) counts[c] = counts.GetValueOrDefault(c) + 1;
        if (counts.Count == 0) return null;
        if (counts.ContainsKey("kana")) return "ja";
        if (counts.Remove("han", out var han)) counts["zh"] = han;
        if (counts.GetValueOrDefault("ko") > 0 && counts["ko"] >= counts.GetValueOrDefault("zh")) return "ko";
        return counts.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>The script of a whole song, so Han-only lines in a Japanese song read as Japanese.</summary>
    public static string? SongScript(IEnumerable<LyricLine> lines) => DetectScript(string.Concat(lines.Select(l => l.Text)));

    static string Engine(string cls, string chunk) => cls switch
    {
        "ko" => Korean.Romanize(chunk),
        "ja" => Japanese.Romanize(chunk),
        "zh" => Chinese.Romanize(chunk),
        "ar" => Others.Arabic(chunk),
        "ru" => Others.Cyrillic(chunk),
        "el" => Others.Greek(chunk),
        "he" => Others.Hebrew(chunk),
        "hi" => Others.Devanagari(chunk),
        _ => chunk,
    };

    public static string Line(string text, string? hint)
    {
        if (!Needs(text)) return text;
        var lineScript = DetectScript(text);
        var hanAs = lineScript == "ja" || (lineScript == null && hint == "ja") || (lineScript == "zh" && hint == "ja") ? "ja" : "zh";
        // Runs by script; neutral characters between two runs of the same script stay inside it.
        var raw = new List<(string? cls, StringBuilder sb)>();
        foreach (var ch in text)
        {
            var cls = Class(ch);
            if (cls is "han" or "kana") cls = hanAs;
            if (raw.Count > 0 && raw[^1].cls == cls) raw[^1].sb.Append(ch);
            else raw.Add((cls, new StringBuilder().Append(ch)));
        }
        var merged = new List<(string? cls, StringBuilder sb)>();
        for (int i = 0; i < raw.Count; i++)
        {
            var (cls, sb) = raw[i];
            if (cls == null && merged.Count > 0 && merged[^1].cls != null && i + 1 < raw.Count && raw[i + 1].cls == merged[^1].cls)
            { merged[^1].sb.Append(sb); continue; }
            if (merged.Count > 0 && merged[^1].cls == cls) merged[^1].sb.Append(sb);
            else merged.Add((cls, new StringBuilder().Append(sb)));
        }
        var outp = new StringBuilder();
        foreach (var (cls, sb) in merged)
        {
            var chunk = sb.ToString();
            string reading;
            try { reading = cls == null ? chunk : Engine(cls, chunk); }
            catch (Exception ex) { Log.Error("romanize " + cls, ex); reading = chunk; }
            if (cls != null && outp.Length > 0 && reading.Length > 0 && outp[^1] != ' ' && reading[0] != ' ') outp.Append(' ');
            outp.Append(reading);
        }
        return Regex.Replace(outp.ToString(), " {2,}", " ").Trim();
    }

    /// <summary>A romanized copy of the line, timed to it: word by word for spaced scripts, and spread
    /// along the line's own timing for Japanese and Chinese, which need the whole line for context.
    /// null when the line has nothing to romanize.</summary>
    public static LyricLine? ForLine(LyricLine line, string? hint)
    {
        var text = line.Text;
        if (!Needs(text)) return null;
        var script = DetectScript(text) ?? hint;
        bool unspaced = script is "ja" or "zh" || Karaoke.UnspacedScript(text);
        if (!unspaced)
        {
            var copy = new LyricLine { Start = line.Start, End = line.End, Align = line.Align, Estimated = line.Estimated };
            foreach (var w in line.Words)
            {
                var r = Line(w.Text, hint);
                if (w.Joined && copy.Words.Count > 0)
                {
                    // A syllable of the same word: keep the whole word's romanization together.
                    var prev = copy.Words[^1];
                    prev.Text += r;
                    prev.End = w.End;
                }
                else copy.Words.Add(new LyricWord { Text = r, Start = w.Start, End = w.End });
            }
            return copy;
        }

        var roman = Line(text, hint);
        if (roman.Length == 0) return null;
        // Map each romanized word to a point in time by how far through the original characters it falls.
        var chars = new List<(long start, long end)>();
        foreach (var w in line.Words)
        {
            int n = Math.Max(1, w.Text.Length);
            for (int i = 0; i < n; i++)
                chars.Add((w.Start + (w.End - w.Start) * i / n, w.Start + (w.End - w.Start) * (i + 1) / n));
        }
        var pieces = roman.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double total = pieces.Sum(p => (double)p.Length);
        var result = new LyricLine { Start = line.Start, End = line.End, Align = line.Align, Estimated = line.Estimated };
        double at = 0;
        foreach (var p in pieces)
        {
            double from = at / total, to = (at + p.Length) / total;
            at += p.Length;
            int a = Math.Clamp((int)(from * chars.Count), 0, chars.Count - 1);
            int b = Math.Clamp((int)Math.Ceiling(to * chars.Count) - 1, a, chars.Count - 1);
            result.Words.Add(new LyricWord { Text = p, Start = chars[a].start, End = Math.Max(chars[b].end, chars[a].start) });
        }
        return result;
    }
}
