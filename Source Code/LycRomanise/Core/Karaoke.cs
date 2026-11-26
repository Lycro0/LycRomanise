using System.Globalization;
using System.Text;

namespace LycRomanise.Core;

public static class Karaoke
{
    // CJK, kana and Thai: each timed syllable stands as a word of its own.
    public static bool UnspacedScript(string text)
    {
        foreach (var ch in text)
        {
            int c = ch;
            if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF)
                || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0x0E00 && c <= 0x0E7F) || (c >= 0xFF66 && c <= 0xFF9F))
                return true;
        }
        return false;
    }

    // Line-synced sources: words are spread across the line by length so the sweep still moves.
    public static List<LyricLine> EstimatedLines(IReadOnlyList<long> starts, IReadOnlyList<string> texts)
    {
        var lines = new List<LyricLine>();
        for (int i = 0; i < texts.Count; i++)
        {
            var text = texts[i].Trim();
            if (text.Length == 0 || text == "♪") continue;
            long start = starts[i];
            // The next later start: a row sharing this stamp (original + translation) is not an end.
            long end = start + 5000;
            for (int j = i + 1; j < starts.Count; j++) if (starts[j] > start) { end = starts[j]; break; }
            if (end <= start) end = start + Math.Max(1500, text.Length * 120);
            // A line is sung over at most about 400 ms a character; a long gap after it is a pause.
            end = Math.Min(end, start + Math.Max(2500, text.Length * 400L));
            var line = SpreadWords(text, start, end);
            line.Estimated = true;
            lines.Add(line);
        }
        return lines;
    }

    public static LyricLine SpreadWords(string text, long start, long end)
    {
        var pieces = SplitPieces(text);
        double total = pieces.Sum(p => Math.Max(1, p.text.Length));
        var line = new LyricLine { Start = start, End = end };
        double at = start;
        double span = Math.Max(1, end - start);
        foreach (var (piece, joined) in pieces)
        {
            double len = span * Math.Max(1, piece.Length) / total;
            line.Words.Add(new LyricWord { Text = piece, Start = (long)at, End = (long)(at + len), Joined = joined });
            at += len;
        }
        return line;
    }

    static List<(string text, bool joined)> SplitPieces(string text)
    {
        var result = new List<(string, bool)>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!UnspacedScript(word)) { result.Add((word, false)); continue; }
            var e = StringInfo.GetTextElementEnumerator(word);
            bool first = true;
            var pending = new StringBuilder();
            while (e.MoveNext())
            {
                var el = (string)e.Current;
                // Punctuation hugs the character before it.
                if (pending.Length > 0 && el.Length == 1 && char.IsPunctuation(el[0])) { pending.Append(el); continue; }
                if (pending.Length > 0) { result.Add((pending.ToString(), !first)); first = false; pending.Clear(); }
                pending.Append(el);
            }
            if (pending.Length > 0) result.Add((pending.ToString(), !first));
        }
        return result;
    }

    public static List<LyricLine> StaticLines(IEnumerable<string> texts)
    {
        var lines = new List<LyricLine>();
        foreach (var t in texts)
        {
            var text = t.Trim();
            if (text.Length == 0 || text == "♪") continue;
            var line = SpreadWords(text, 0, 0);
            line.Estimated = true;
            lines.Add(line);
        }
        return lines;
    }

    public static Timing TimingOf(List<LyricLine> lines)
    {
        if (lines.Count == 0 || lines.All(l => l.Start == 0 && l.End == 0)) return Timing.None;
        return lines.Any(l => !l.Estimated) ? Timing.Word : Timing.Line;
    }

    // Null when the pronunciation reads the same as the line.
    public static LyricLine? Pronunciation(List<LyricWord>? words, string? plain, LyricLine of)
    {
        if (words is { Count: > 0 })
        {
            var line = new LyricLine { Words = words, Start = words[0].Start, End = Math.Max(words[^1].End, words[0].Start) };
            return BareText(line.Text) == BareText(of.Text) ? null : line;
        }
        if (string.IsNullOrWhiteSpace(plain) || BareText(plain) == BareText(of.Text)) return null;
        var est = SpreadWords(plain.Trim(), of.Start, of.End);
        est.Estimated = true;
        return est;
    }

    public static string BareText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }
}

public static class Normalize
{
    public static string MatchKey(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)
                || cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string LeadArtist(string? artist)
    {
        if (string.IsNullOrEmpty(artist)) return "";
        var a = artist;
        foreach (var sep in new[] { " feat", " ft.", " featuring", " with ", " & ", ", ", " x ", ";", " / ", "、" })
        {
            int i = a.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0) a = a[..i];
        }
        return a.Trim();
    }

    public static bool SameArtist(string? credited, string? artist)
    {
        string name = MatchKey(credited), lead = MatchKey(LeadArtist(artist));
        if (name.Length == 0 || lead.Length == 0) return false;
        if (name == MatchKey(artist)) return true;
        if (OneArtist(name, lead)) return true;
        // Credit list: any one name will do. The whole string goes first so "AC/DC" still matches.
        foreach (var part in System.Text.RegularExpressions.Regex.Split(credited!,
            @"、|，|,|;|&|/|\s+(?:feat\.?|ft\.|featuring|with|x)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var p = MatchKey(part);
            if (p.Length > 0 && p != name && OneArtist(p, lead)) return true;
        }
        return false;
    }

    static bool OneArtist(string name, string lead)
    {
        // Short names ("V", "IU", "I") must match exactly, or "V" would match every artist with a v in it.
        if (Math.Min(name.Length, lead.Length) < 3) return name == lead;
        return name.Contains(lead) || lead.Contains(name);
    }

    // "Song - 2011 Remaster" matches "Song"; "YOLO (Japanese ver.)" does not match "YOLO".
    public static bool SameTitle(string? found, string? wanted)
    {
        string a = MatchKey(CleanTitle(found ?? "")), b = MatchKey(CleanTitle(wanted ?? ""));
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b;
    }

    public static string CleanTitle(string title)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(title,
            @"\s*[\(\[](feat\.?|ft\.?|with|prod\.?)[^\)\]]*[\)\]]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t,
            @"\s+-\s+(\d{4}\s+)?(remaster(ed)?|live|radio edit|single version|mono|stereo)\b.*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Trim();
    }

    public static string SongKey(string artist, string title) =>
        (MatchKey(LeadArtist(artist)) + "|" + MatchKey(CleanTitle(title)));
}
