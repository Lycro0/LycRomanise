using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using LycRomanise.Core;

namespace LycRomanise.Sources;

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

    // Enhanced LRC word stamps (<mm:ss.xx>) give word timing; null when no line is timed.
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
        // Stable, so rows sharing a stamp (original then translation) keep the file's order.
        stamped = stamped.OrderBy(s => s.ms).ToList();

        var starts = stamped.Select(s => s.ms).ToList();
        var lines = new List<LyricLine>();
        bool anyWordStamps = stamped.Any(s => LrcWordStamp().IsMatch(s.text));
        if (!anyWordStamps)
        {
            // Some files write each line twice at the same time: the original and its romanization.
            var merged = new List<(long ms, string text, string? pron, string? trans)>();
            foreach (var (ms, text) in stamped)
            {
                if (merged.Count > 0 && merged[^1].ms == ms && text.Length > 0 && merged[^1].text.Length > 0)
                {
                    var prev = merged[^1];
                    bool prevLatin = !Romanize.Romanizer.Needs(prev.text), curLatin = !Romanize.Romanizer.Needs(text);
                    if (prevLatin != curLatin && prev.pron == null)
                    {
                        merged[^1] = prevLatin ? (ms, text, prev.text, prev.trans) : (ms, prev.text, text, prev.trans);
                        continue;
                    }
                    // Any other row at the same time is a translation; as its own line it would never be current.
                    if (Karaoke.BareText(text) != Karaoke.BareText(prev.text) && prev.trans == null) merged[^1] = prev with { trans = text };
                    continue;
                }
                merged.Add((ms, text, null, null));
            }
            var est = Karaoke.EstimatedLines(merged.Select(m => m.ms).ToList(), merged.Select(m => m.text).ToList());
            // EstimatedLines skips empty rows; pair the rest back up with their romanization.
            var withText = merged.Where(m => m.text.Trim().Length > 0 && m.text.Trim() != "♪").ToList();
            for (int i = 0; i < est.Count && i < withText.Count; i++)
            {
                if (withText[i].pron != null) est[i].Pronunciation = Karaoke.Pronunciation(null, withText[i].pron, est[i]);
                if (withText[i].trans != null) est[i].Translation = withText[i].trans;
            }
            return est.Count > 0 ? est : null;
        }

        for (int i = 0; i < stamped.Count; i++)
        {
            var (ms, text) = stamped[i];
            // The next later stamp: a row at the same time would give this line no length at all.
            long next = ms + 5000;
            for (int j = i + 1; j < stamped.Count; j++) if (stamped[j].ms > ms) { next = stamped[j].ms; break; }
            var marks = LrcWordStamp().Matches(text);
            if (marks.Count == 0)
            {
                var plain = text.Trim();
                if (plain.Length == 0 || plain == "♪") continue;   // the stamp still ends the row before
                // A second row at a line's time is its translation, not a line of its own.
                if (lines.Count > 0 && lines[^1].Start == ms)
                {
                    if (lines[^1].Translation == null && Karaoke.BareText(plain) != Karaoke.BareText(lines[^1].Text)) lines[^1].Translation = plain;
                    continue;
                }
                // Capped like EstimatedLines: a long gap after a line is a pause, not a slow sweep.
                long pEnd = Math.Min(next, ms + Math.Max(2500, plain.Length * 400L));
                var est = Karaoke.SpreadWords(plain, ms, pEnd);
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
                long segStart = cursor, segEnd;
                if (k < marks.Count) segEnd = StampMs(marks[k]) - offset;
                else
                {
                    // No closing stamp: the last word is sung for about its length, not until the next line.
                    int tl = seg.Trim().Length;
                    segEnd = tl == 0 ? next : Math.Max(segStart, Math.Min(next, segStart + Math.Max(600, tl * 400L)));
                }
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

    // yrc "[start,len](t,d,0)word" or krc "[start,len]<t,d,0>word", whose piece times count from the line start.
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
            // A number too big for a long is a broken row, not a reason to lose the whole file.
            if (!long.TryParse(head.Groups[1].Value, out var lineStart) || !long.TryParse(head.Groups[2].Value, out var lineLen)) continue;
            var pieces = pieceRx.Matches(row, head.Length);
            var words = new List<LyricWord>();
            LyricWord? open = null;
            bool spaced = true;
            for (int i = 0; i < pieces.Count; i++)
            {
                int from = pieces[i].Index + pieces[i].Length, to = i + 1 < pieces.Count ? pieces[i + 1].Index : row.Length;
                var rawText = row[from..to];
                var text = rawText.Trim();
                if (!long.TryParse(pieces[i].Groups[1].Value, out var ps) || !long.TryParse(pieces[i].Groups[2].Value, out var pd)) continue;
                long start = ps + (krc ? lineStart : 0);
                long end = start + Math.Max(pd, 1);
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
    // "1.241", "0:01.241", "1:02:03.456", "1.5s", "500ms"; -1 when unreadable.
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
                                if (backing != null) line.Backing = LineFrom(backing.Words);
                                // The <p> times may widen the line but never cut off words still being sung.
                                bool timed = !line.Estimated && line.Words.Count > 0;
                                if (lineStart >= 0) line.Start = timed ? Math.Min(lineStart, line.Words[0].Start) : lineStart;
                                long end = line.End;
                                if (lineEnd > line.Start) end = timed ? Math.Max(end, lineEnd) : lineEnd;
                                if (line.Backing != null) end = Math.Max(end, line.Backing.End);
                                line.End = Math.Max(end, line.Start);
                                line.Voice = voice;
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

        var pron = pronunciations.Values.FirstOrDefault();
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

    public static List<LyricLine> ParsePlain(string text) =>
        Karaoke.StaticLines(text.Replace("\r", "").Split('\n'));
}
