using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using LycRomanise.Core;

namespace LycRomanise.Sources;

/// <summary>The lyric file formats the sources answer in: LRC (and enhanced LRC with word stamps),
/// Apple's TTML, and NetEase yrc / KuGou krc.</summary>
public static partial class Formats
{
    // ------------------------------------------------------------------ LRC
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")] private static partial Regex LrcStamp();
    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")] private static partial Regex LrcWordStamp();
    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Multiline)] private static partial Regex LrcOffset();

    static long StampMs(Match m)
    {
        long minutes = long.Parse(m.Groups[1].Value), seconds = long.Parse(m.Groups[2].Value), fraction = 0;
        if (m.Groups[3].Success)
        {
            var d = m.Groups[3].Value;
            fraction = long.Parse(d) * (d.Length == 1 ? 100 : d.Length == 2 ? 10 : 1);
        }
        return (minutes * 60 + seconds) * 1000 + fraction;
    }

    /// <summary>Timed LRC as lines; enhanced LRC word stamps (&lt;mm:ss.xx&gt;) give word timing.
    /// null when no line is timed.</summary>
    public static List<LyricLine>? ParseLrc(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return null;
        lrc = lrc.TrimStart((char)0xFEFF);   // a byte order mark would hide the first line's stamp
        long offset = 0;
        var om = LrcOffset().Match(lrc);
        if (om.Success) offset = long.Parse(om.Groups[1].Value);
        var stamped = new List<(long ms, string text)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var row = raw.TrimEnd('\r');
            int end = 0;
            var at = new List<long>();
            foreach (Match m in LrcStamp().Matches(row))
            {
                if (m.Index != end) break;   // only the stamps a line opens with are its own
                end = m.Index + m.Length;
                at.Add(StampMs(m));
            }
            if (at.Count == 0) continue;
            var text = row[end..].Trim();
            foreach (var ms in at) stamped.Add((ms - offset, text));
        }
        if (stamped.Count == 0) return null;
        stamped.Sort((a, b) => a.ms.CompareTo(b.ms));

        var starts = stamped.Select(s => s.ms).ToList();
        var lines = new List<LyricLine>();
        bool anyWordStamps = stamped.Any(s => LrcWordStamp().IsMatch(s.text));
        if (!anyWordStamps)
        {
            // Some files write each line twice at the same time: the original and its romanization.
            var merged = new List<(long ms, string text, string? pron)>();
            foreach (var (ms, text) in stamped)
            {
                if (merged.Count > 0 && merged[^1].ms == ms && text.Length > 0 && merged[^1].text.Length > 0)
                {
                    var prev = merged[^1];
                    bool prevLatin = !Romanize.Romanizer.Needs(prev.text), curLatin = !Romanize.Romanizer.Needs(text);
                    if (prevLatin != curLatin && prev.pron == null)
                    {
                        merged[^1] = prevLatin ? (ms, text, prev.text) : (ms, prev.text, text);
                        continue;
                    }
                }
                merged.Add((ms, text, null));
            }
            var est = Karaoke.EstimatedLines(merged.Select(m => m.ms).ToList(), merged.Select(m => m.text).ToList());
            // EstimatedLines skips empty rows; pair the rest back up with their romanization.
            var withText = merged.Where(m => m.text.Trim().Length > 0 && m.text.Trim() != "♪").ToList();
            for (int i = 0; i < est.Count && i < withText.Count; i++)
                if (withText[i].pron != null) est[i].Pronunciation = Karaoke.Pronunciation(null, withText[i].pron, est[i]);
            return est.Count > 0 ? est : null;
        }

        for (int i = 0; i < stamped.Count; i++)
        {
            var (ms, text) = stamped[i];
            long next = i + 1 < stamped.Count ? stamped[i + 1].ms : ms + 5000;
            var marks = LrcWordStamp().Matches(text);
            if (marks.Count == 0)
            {
                var plain = text.Trim();
                if (plain.Length == 0) continue;
                var est = Karaoke.SpreadWords(plain, ms, next);
                est.Estimated = true;
                lines.Add(est);
                continue;
            }
            var line = new LyricLine { Start = ms };
            long cursor = ms;
            int pos = 0;
            bool spaced = true;
            for (int k = 0; k <= marks.Count; k++)
            {
                int from = pos, to = k < marks.Count ? marks[k].Index : text.Length;
                var seg = text[from..to];
                long segStart = cursor, segEnd = k < marks.Count ? StampMs(marks[k]) - offset : next;
                AddSegment(line, seg, segStart, segEnd, ref spaced);
                if (k < marks.Count) { cursor = StampMs(marks[k]) - offset; pos = marks[k].Index + marks[k].Length; }
            }
            if (line.Words.Count == 0) continue;
            line.End = Math.Max(line.Words[^1].End, line.Start);
            lines.Add(line);
        }
        return lines.Count > 0 ? lines : null;
    }

    static void AddSegment(LyricLine line, string seg, long start, long end, ref bool spaced)
    {
        if (seg.Length == 0) return;
        var text = seg.Trim();
        if (text.Length > 0)
        {
            if (line.Words.Count > 0 && !spaced && !Karaoke.UnspacedScript(text) && !line.Words[^1].Text.EndsWith(' '))
                line.Words.Add(new LyricWord { Text = text, Start = start, End = Math.Max(end, start), Joined = true });
            else
                line.Words.Add(new LyricWord { Text = text, Start = start, End = Math.Max(end, start), Joined = !spaced && line.Words.Count > 0 });
            spaced = false;
        }
        if (seg.Length > 0 && char.IsWhiteSpace(seg[^1])) spaced = true;
        if (seg.Length > 0 && char.IsWhiteSpace(seg[0]) && line.Words.Count > 0 && text.Length > 0)
            line.Words[^1].Joined = false;
    }

    // ------------------------------------------------------------- yrc / krc
    [GeneratedRegex(@"^\[(\d+),(\d+)\]")] private static partial Regex PieceHeader();
    [GeneratedRegex(@"\((\d+),(\d+),-?\d+\)")] private static partial Regex YrcPiece();
    [GeneratedRegex(@"<(\d+),(\d+),-?\d+>")] private static partial Regex KrcPiece();

    /// <summary>NetEase yrc ("[start,len](t,d,0)word") or KuGou krc ("[start,len]&lt;t,d,0&gt;word", times
    /// from the line's start) as word timed lines.</summary>
    public static List<LyricLine>? ParsePieces(string? body, bool krc)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var pieceRx = krc ? KrcPiece() : YrcPiece();
        var lines = new List<LyricLine>();
        foreach (var raw in body.Split('\n'))
        {
            var row = raw.TrimEnd('\r');
            var head = PieceHeader().Match(row);
            if (!head.Success) continue;
            long lineStart = long.Parse(head.Groups[1].Value), lineLen = long.Parse(head.Groups[2].Value);
            var pieces = pieceRx.Matches(row, head.Length);
            var words = new List<LyricWord>();
            LyricWord? open = null;
            bool spaced = true;
            for (int i = 0; i < pieces.Count; i++)
            {
                int from = pieces[i].Index + pieces[i].Length, to = i + 1 < pieces.Count ? pieces[i + 1].Index : row.Length;
                var rawText = row[from..to];
                var text = rawText.Trim();
                long start = long.Parse(pieces[i].Groups[1].Value) + (krc ? lineStart : 0);
                long end = start + Math.Max(long.Parse(pieces[i].Groups[2].Value), 1);
                bool unspaced = Karaoke.UnspacedScript(text);
                if (text.Length > 0 && open != null && !unspaced)
                {
                    open.Text += text;
                    open.End = end;
                }
                else if (text.Length > 0)
                {
                    var w = new LyricWord { Text = text, Start = start, End = end, Joined = !spaced && words.Count > 0 };
                    words.Add(w);
                    open = unspaced ? null : w;
                    spaced = false;
                }
                if (rawText.Length > text.Length || text.Length == 0) { open = null; spaced = true; }
            }
            if (words.Count == 0) continue;
            lines.Add(new LyricLine { Words = words, Start = lineStart, End = Math.Max(lineStart + lineLen, words[^1].End) });
        }
        return lines.Count > 0 ? lines : null;
    }

    // ------------------------------------------------------------------ TTML
    /// <summary>A begin/end clock: "1.241", "0:01.241", "1:02:03.456", "1.5s", "500ms". -1 when unreadable.</summary>
    public static long ClockMs(string? clock)
    {
        var text = clock?.Trim();
        if (string.IsNullOrEmpty(text)) return -1;
        double scale = 1000;
        if (text.EndsWith("ms")) { scale = 1; text = text[..^2]; }
        else if (text.EndsWith('s')) text = text[..^1];
        var parts = text.Split(':');
        if (parts.Length > 3) return -1;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return -1;
            total = total * 60 + v;
        }
        return (long)Math.Round(total * (parts.Length > 1 ? 1000 : scale));
    }

    sealed class Container { public List<LyricWord> Words = new(); public bool Spaced = true; }

    sealed class HeadText { public List<LyricWord> Words = new(); public List<LyricWord> Backing = new(); public string Plain = ""; }

    /// <summary>Apple Music TTML as lines: word or line timed, duet voices, backing vocals, and the
    /// head's transliterations and translations matched onto each line by key.</summary>
    public static List<LyricLine>? ParseTtml(string? xml, string? translationLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<tt")) return null;
        var lines = new List<LyricLine>();
        var keys = new List<string?>();
        var translations = new Dictionary<string, Dictionary<string, HeadText>>();
        var pronunciations = new Dictionary<string, Dictionary<string, HeadText>>();
        var translationOrder = new List<string>();
        var voices = new Dictionary<string, int>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null,
            });
            var stack = new List<Container>();
            Container? backing = null;
            long lineStart = -1, lineEnd = -1;
            string? voice = null, key = null, language = null;
            int part = 0, reading = 0;   // 1 translation, 2 transliteration
            var plain = new StringBuilder();
            StringBuilder? word = null;
            long wordStart = 0, wordEnd = 0;
            int spanDepth = 0, bgDepth = 0, wordDepth = 0;
            var depthStack = new Stack<string>();

            void OpenLine()
            {
                stack.Clear();
                stack.Add(new Container());
                backing = null; word = null;
                spanDepth = bgDepth = wordDepth = 0;
                plain.Clear();
            }

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var name = reader.LocalName;
                        bool empty = reader.IsEmptyElement;
                        string? Attr(string local, string? ns = null)
                        {
                            if (!reader.HasAttributes) return null;
                            for (int i = 0; i < reader.AttributeCount; i++)
                            {
                                reader.MoveToAttribute(i);
                                if (reader.LocalName == local) { var v = reader.Value; reader.MoveToElement(); return v; }
                            }
                            reader.MoveToElement();
                            return null;
                        }
                        if (name == "p")
                        {
                            OpenLine();
                            reading = 0;
                            lineStart = ClockMs(Attr("begin"));
                            lineEnd = ClockMs(Attr("end"));
                            voice = Attr("agent");
                            key = Attr("key");
                            if (empty) { stack.Clear(); }
                            break;
                        }
                        if (name is "translation" or "transliteration")
                        {
                            part = name == "translation" ? 1 : 2;
                            language = Attr("lang") ?? "";
                            if (part == 1 && !translationOrder.Contains(language)) translationOrder.Add(language);
                            break;
                        }
                        if (name == "text" && part != 0 && Attr("for") is { Length: > 0 } f)
                        {
                            OpenLine();
                            reading = part;
                            key = f;
                            if (empty) { stack.Clear(); reading = 0; }
                            break;
                        }
                        if (name != "span" || stack.Count == 0) break;
                        if (empty) break;
                        spanDepth++;
                        var role = Attr("role");
                        if (role == "x-bg" && backing == null)
                        {
                            backing = new Container();
                            stack.Add(backing);
                            bgDepth = spanDepth;
                            break;
                        }
                        if (word != null) break;   // nested ruby / translation spans belong to the word
                        long start = ClockMs(Attr("begin")), end = ClockMs(Attr("end"));
                        if (start < 0) break;
                        word = new StringBuilder();
                        wordStart = start;
                        wordEnd = Math.Max(end, start);
                        wordDepth = spanDepth;
                        break;
                    }
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                    {
                        if (stack.Count == 0) break;
                        var chars = reader.Value;
                        plain.Append(chars);
                        if (word != null) { word.Append(chars); break; }
                        if (chars.Trim().Length == 0 && chars.Length > 0) stack[^1].Spaced = true;
                        break;
                    }
                    case XmlNodeType.EndElement:
                    {
                        var name = reader.LocalName;
                        if (name == "p" && stack.Count > 0)
                        {
                            var main = stack[0];
                            LyricLine? line = LineFrom(main.Words);
                            if (line == null)
                            {
                                var text = Regex.Replace(plain.ToString(), @"\s+", " ").Trim();
                                if (text.Length > 0 && lineStart >= 0)
                                {
                                    line = Karaoke.SpreadWords(text, lineStart, Math.Max(lineEnd, lineStart));
                                    line.Estimated = true;
                                }
                            }
                            if (line != null)
                            {
                                if (lineStart >= 0) line.Start = lineStart;
                                if (lineEnd > line.Start) line.End = lineEnd;
                                line.Voice = voice;
                                if (backing != null) line.Backing = LineFrom(backing.Words);
                                lines.Add(line);
                                keys.Add(key);
                            }
                            stack.Clear();
                            break;
                        }
                        if (name == "text" && reading != 0)
                        {
                            var byLang = reading == 1 ? translations : pronunciations;
                            if (!byLang.TryGetValue(language ?? "", out var map)) byLang[language ?? ""] = map = new();
                            map[key ?? ""] = new HeadText
                            {
                                Words = stack.Count > 0 ? stack[0].Words : new(),
                                Backing = backing?.Words ?? new(),
                                Plain = Regex.Replace(plain.ToString(), @"\s+", " ").Trim(),
                            };
                            stack.Clear();
                            reading = 0;
                            break;
                        }
                        if (name is "translation" or "transliteration") { part = 0; break; }
                        if (name != "span" || stack.Count == 0) break;
                        if (word != null && spanDepth == wordDepth)
                        {
                            var text = word.ToString().Trim();
                            var into = stack[^1];
                            if (text.Length > 0)
                            {
                                into.Words.Add(new LyricWord { Text = text, Start = wordStart, End = wordEnd, Joined = into.Words.Count > 0 && !into.Spaced });
                                into.Spaced = false;
                            }
                            word = null;
                            wordDepth = 0;
                        }
                        if (bgDepth > 0 && spanDepth == bgDepth) { stack.RemoveAt(stack.Count - 1); bgDepth = 0; }
                        if (spanDepth > 0) spanDepth--;
                        break;
                    }
                }
            }
        }
        catch (XmlException ex)
        {
            Log.Info($"ttml: unreadable ({ex.Message})");
            if (lines.Count == 0) return null;
        }
        if (lines.Count == 0) return null;

        // Duets: the first voice keeps the leading side, any other voice takes the trailing side.
        string? firstVoice = lines.Select(l => l.Voice).FirstOrDefault(v => v != null);
        foreach (var l in lines)
        {
            if (l.Voice != null && firstVoice != null && l.Voice != firstVoice && l.Voice != "v1000") l.Align = LineAlign.Trailing;
            if (l.Backing != null) l.Backing.Align = l.Align;
        }

        // Pronunciations: the first transliteration in the document.
        var pron = pronunciations.Values.FirstOrDefault();
        // Translations: the language asked for (any region), else the first.
        Dictionary<string, HeadText>? trans = null;
        if (!string.IsNullOrEmpty(translationLanguage))
        {
            var lang = translationOrder.FirstOrDefault(l => l.Equals(translationLanguage, StringComparison.OrdinalIgnoreCase)
                || l.StartsWith(translationLanguage + "-", StringComparison.OrdinalIgnoreCase));
            if (lang != null) trans = translations[lang];
        }
        else if (translationOrder.Count > 0) trans = translations[translationOrder[0]];

        for (int i = 0; i < lines.Count; i++)
        {
            var k = keys[i];
            if (k == null) continue;
            if (pron != null && pron.TryGetValue(k, out var p))
            {
                lines[i].Pronunciation = Karaoke.Pronunciation(p.Words.Count > 0 ? p.Words : null, p.Plain, lines[i]);
                if (lines[i].Backing != null && p.Backing.Count > 0)
                    lines[i].Backing!.Pronunciation = Karaoke.Pronunciation(p.Backing, null, lines[i].Backing!);
            }
            if (trans != null && trans.TryGetValue(k, out var t) && t.Plain.Length > 0
                && Karaoke.BareText(t.Plain) != Karaoke.BareText(lines[i].Text))
                lines[i].Translation = t.Plain;
        }
        return lines;
    }

    static LyricLine? LineFrom(List<LyricWord> words)
    {
        if (words.Count == 0) return null;
        return new LyricLine { Words = words, Start = words[0].Start, End = Math.Max(words[^1].End, words[0].Start) };
    }

    /// <summary>Plain, untimed lyrics.</summary>
    public static List<LyricLine> ParsePlain(string text) =>
        Karaoke.StaticLines(text.Replace("\r", "").Split('\n'));
}
