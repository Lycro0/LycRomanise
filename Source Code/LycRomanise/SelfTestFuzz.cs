using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LycRomanise.Core;
using LycRomanise.Romanize;
using LycRomanise.Sources;
using LycRomanise.UI;
using MediaState = LycRomanise.Media.MediaState;
using ClockSmoother = LycRomanise.Media.ClockSmoother;

namespace LycRomanise;

/// <summary>Stress / property test harness: `LycRomanise.exe --selftest &lt;out&gt; fuzz`. Fixed seeds.</summary>
static class SelfTestFuzz
{
    static readonly Dictionary<string, (int pass, int fail)> Areas = new();
    static readonly List<string> Failures = new();
    static readonly Dictionary<string, int> Shown = new();
    static readonly StringBuilder Notes = new();
    const int MaxShownPerArea = 40;

    static void C(string area, bool ok, Func<string> repro)
    {
        var (p, f) = Areas.GetValueOrDefault(area);
        if (ok) { Areas[area] = (p + 1, f); return; }
        Areas[area] = (p, f + 1);
        int shown = Shown.GetValueOrDefault(area);
        if (shown < MaxShownPerArea) { Failures.Add($"FAIL [{area}] {repro()}"); Shown[area] = shown + 1; }
    }

    static void Safe(string area, Func<string> repro, Action a)
    {
        try { a(); }
        catch (Exception ex) { C(area, false, () => repro() + " -> EXCEPTION " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
    }

    static string Esc(string? s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '\r') sb.Append("\\r"); else if (c == '\n') sb.Append("\\n");
            else if (c < 32 || char.IsSurrogate(c) || c == 0xFEFF) sb.Append($"\\u{(int)c:X4}");
            else sb.Append(c);
        }
        if (sb.Length > 300) { sb.Length = 300; sb.Append("..."); }
        return sb.Append('"').ToString();
    }

    static bool Ascii(string s) => s.All(c => c < 128);

    public static async Task RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir); _reportDir = outDir;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timing = new StringBuilder();
        void Run(string name, Action a)
        {
            var t0 = sw.ElapsedMilliseconds;
            try { a(); } catch (Exception ex) { C(name, false, () => "section crashed: " + ex); }
            timing.AppendLine($"  {name}: {sw.ElapsedMilliseconds - t0} ms");
        }
        Run("a-korean", Korean_);
        Run("b-japanese", Japanese_);
        Run("c-scripts", Scripts);
        Run("d-lrc", Lrc);
        Run("e-ttml-pieces", TtmlPieces);
        Run("f-clock", ClockMsTests);
        Run("g-karaoke-normalize", KaraokeNormalize);
        Run("h-prepare", PrepareTests);
        Run("i-media", MediaTests);
        Run("j-settings", SettingsTests);
        Run("k-strip", StripTests);
        Run("l-page", PageTests);
        Run("m-web", WebTests);
        await Task.Yield();

        int pass = Areas.Values.Sum(v => v.pass), fail = Areas.Values.Sum(v => v.fail);
        var r = new StringBuilder();
        r.AppendLine($"fuzz pass={pass} fail={fail}");
        r.AppendLine($"total={pass + fail} elapsed={sw.ElapsedMilliseconds} ms");
        r.AppendLine("per-area:");
        foreach (var (k, v) in Areas.OrderBy(k => k.Key)) r.AppendLine($"  {k}: pass={v.pass} fail={v.fail}");
        r.AppendLine("section time:"); r.Append(timing);
        if (Notes.Length > 0) { r.AppendLine("notes:"); r.Append(Notes); }
        r.AppendLine("failures (first " + MaxShownPerArea + " per area):");
        foreach (var f in Failures) r.AppendLine(f);
        File.WriteAllText(Path.Combine(outDir, "fuzz-report.txt"), r.ToString());
    }

    // =============================================================== a) Korean
    static readonly string[] RrInit = { "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h" };
    static readonly string[] RrVowel = { "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i" };
    // Word-final (neutralized) consonants, Revised Romanization: ㄱㄲㄳ k, ㄴㄵㄶ n, ㄷ t, ㄹ l, ㄺ k, ㄻ m, ㄼ l, ㄽ l, ㄾ l, ㄿ p, ㅀ l,
    // ㅁ m, ㅂ p, ㅄ p, ㅅ t, ㅆ t, ㅇ ng, ㅈ t, ㅊ t, ㅋ k, ㅌ t, ㅍ p, ㅎ t.
    static readonly string[] RrFinal = { "", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l", "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t" };

    static void Korean_()
    {
        for (int i = 0; i < 11172; i++)
        {
            char ch = (char)(0xAC00 + i);
            string expect = RrInit[i / 588] + RrVowel[i % 588 / 28] + RrFinal[i % 28];
            string got = "";
            Safe("a-korean-syllable", () => $"{ch} U+{(int)ch:X4}", () => { got = Korean.Romanize(ch.ToString()); C("a-korean-syllable", got == expect, () => $"{ch} U+{(int)ch:X4} got {Esc(got)} want {Esc(expect)}"); });
        }
        var rng = new Random(1001);
        for (int n = 0; n < 2500; n++)
        {
            var sb = new StringBuilder();
            int len = rng.Next(1, 12);
            for (int k = 0; k < len; k++)
            {
                int roll = rng.Next(20);
                if (roll == 0) sb.Append(' ');
                else if (roll == 1) sb.Append((char)rng.Next(0x3131, 0x3164));   // compatibility jamo
                else sb.Append((char)(0xAC00 + rng.Next(11172)));
            }
            var s = sb.ToString();
            Safe("a-korean-random", () => Esc(s), () =>
            {
                var a = Korean.Romanize(s); var b = Korean.Romanize(s);
                C("a-korean-random", Ascii(a) && a == b && (s.Trim().Length == 0 || a.Trim().Length > 0), () => $"{Esc(s)} -> {Esc(a)} / {Esc(b)}");
            });
        }
    }

    // =============================================================== b) Japanese
    const string HiraTable =
        "あa いi うu えe おo かka きki くku けke こko がga ぎgi ぐgu げge ごgo さsa しshi すsu せse そso ざza じji ずzu ぜze ぞzo " +
        "たta ちchi つtsu てte とto だda ぢji づzu でde どdo なna にni ぬnu ねne のno はha ひhi ふfu へhe ほho ばba びbi ぶbu べbe ぼbo " +
        "ぱpa ぴpi ぷpu ぺpe ぽpo まma みmi むmu めme もmo やya ゆyu よyo らra りri るru れre ろro わwa ゐi ゑe をo んn ゔvu " +
        "ぁa ぃi ぅu ぇe ぉo ゃya ゅyu ょyo ゎwa ゕka ゖke";

    static void Japanese_()
    {
        var table = new Dictionary<string, string>();
        foreach (var pair in HiraTable.Split(' ', StringSplitOptions.RemoveEmptyEntries)) table[pair[..1]] = pair[1..];
        string Kata(string h) => new(h.Select(c => c >= 0x3041 && c <= 0x3096 ? (char)(c + 0x60) : c).ToArray());
        void Exact(string area, string kana, string want)
        {
            Safe(area, () => kana, () => { var got = Japanese.KanaToRomaji(kana); C(area, got == want, () => $"{kana} got {Esc(got)} want {Esc(want)}"); });
        }
        // Every hiragana / katakana in the table (singles).
        foreach (var (k, v) in table) { Exact("b-kana-single", k, v); Exact("b-kana-single", Kata(k), v); }
        // Every codepoint of the hiragana and katakana blocks: ASCII output.
        for (int c = 0x3041; c <= 0x309F; c++) AsciiOnly("b-kana-block-ascii", ((char)c).ToString());
        for (int c = 0x30A1; c <= 0x30FF; c++) { if (c == 0x30FB) continue; AsciiOnly("b-kana-block-ascii", "カ" + (char)c); }
        for (int c = 0x31F0; c <= 0x31FF; c++) AsciiOnly("b-kana-block-ascii", ((char)c).ToString());
        // Digraphs.
        var iRow = new (string k, string r)[] { ("き", "ky"), ("し", "sh"), ("ち", "ch"), ("に", "ny"), ("ひ", "hy"), ("み", "my"), ("り", "ry"), ("ぎ", "gy"), ("じ", "j"), ("ぢ", "j"), ("び", "by"), ("ぴ", "py") };
        var small = new (string k, string v)[] { ("ゃ", "a"), ("ゅ", "u"), ("ょ", "o") };
        foreach (var (k, r) in iRow) foreach (var (s, v) in small)
        {
            Exact("b-kana-digraph", k + s, r + v); Exact("b-kana-digraph", Kata(k + s), r + v);
            Exact("b-kana-sokuon", "っ" + k + s, (r.StartsWith("ch") ? "t" : r[..1]) + r + v);
            Exact("b-kana-sokuon", Kata("っ" + k + s), (r.StartsWith("ch") ? "t" : r[..1]) + r + v);
        }
        // Sokuon before every consonant kana; ん before vowels and y; long vowel mark.
        foreach (var (k, v) in table)
        {
            if (k[0] is 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' or 'ゃ' or 'ゅ' or 'ょ' or 'ゎ' or 'ゕ' or 'ゖ' or 'ん') continue;
            if ("aiueo".Contains(v[0])) { Exact("b-kana-n-apostrophe", "ん" + k, "n'" + v); Exact("b-kana-long", Kata(k) + "ー", v + v[^1]); continue; }
            string gem = (v.StartsWith("ch") ? "t" : v[..1]) + v;
            Exact("b-kana-sokuon", "っ" + k, gem); Exact("b-kana-sokuon", Kata("っ" + k), gem);
            if (v[0] == 'y') Exact("b-kana-n-apostrophe", "ん" + k, "n'" + v);
            else Exact("b-kana-n", "ん" + k, "n" + v);
            Exact("b-kana-long", Kata(k) + "ー", v + v[^1]);
        }
        // Rarer kana a song can still contain: va/vi/ve/vo katakana and the iteration marks.
        Exact("b-kana-rare", "ヷ", "va"); Exact("b-kana-rare", "ヸ", "vi"); Exact("b-kana-rare", "ヹ", "ve"); Exact("b-kana-rare", "ヺ", "vo");
        Exact("b-kana-rare", "こゝろ", "kokoro"); Exact("b-kana-rare", "いすゞ", "isuzu"); Exact("b-kana-rare", "コヽロ", "kokoro");

        // Random kana strings.
        var rng = new Random(2002);
        var alphabet = new List<char>();
        for (int c = 0x3041; c <= 0x3096; c++) alphabet.Add((char)c);
        for (int c = 0x30A1; c <= 0x30F6; c++) alphabet.Add((char)c);
        alphabet.Add('ー'); alphabet.Add('っ'); alphabet.Add('ッ');
        for (int n = 0; n < 3000; n++)
        {
            var s = new string(Enumerable.Range(0, rng.Next(1, 14)).Select(_ => alphabet[rng.Next(alphabet.Count)]).ToArray());
            Safe("b-kana-random", () => Esc(s), () => { var got = Japanese.KanaToRomaji(s); C("b-kana-random", Ascii(got) && got == Japanese.KanaToRomaji(s), () => $"{Esc(s)} -> {Esc(got)}"); });
        }
        // Through the analyzer (Windows' Japanese reader): random kana and common kanji, plus a chunk boundary in an emoji.
        string kanji = "夢愛君空心花雨星光風涙道今日明月夜手目声歌恋海雪";
        for (int n = 0; n < 150; n++)
        {
            var s = new string(Enumerable.Range(0, rng.Next(2, 16)).Select(_ => rng.Next(4) == 0 ? kanji[rng.Next(kanji.Length)] : alphabet[rng.Next(0x3096 - 0x3041)]).ToArray());
            Safe("b-japanese-analyzer", () => Esc(s), () => { var got = Romanizer.Line(s, "ja"); C("b-japanese-analyzer", Ascii(got) && got.Length > 0, () => $"{Esc(s)} -> {Esc(got)}"); });
        }
        for (int at = 95; at <= 101; at++)
        {
            var s = new string('あ', at) + "😀" + "いう";
            Safe("b-japanese-surrogate", () => $"{at} x あ + emoji", () =>
            {
                var got = Japanese.Romanize(s);
                bool lone = false;
                for (int i = 0; i < got.Length; i++)
                {
                    if (char.IsHighSurrogate(got[i]) && (i + 1 >= got.Length || !char.IsLowSurrogate(got[i + 1]))) lone = true;
                    if (char.IsLowSurrogate(got[i]) && (i == 0 || !char.IsHighSurrogate(got[i - 1]))) lone = true;
                }
                C("b-japanese-surrogate", !lone && got.Contains("😀"), () => $"{at} x あ + 😀 + いう -> emoji {(got.Contains("😀") ? "kept" : "LOST")}, lone surrogate={lone}, tail {Esc(got[Math.Max(0, got.Length - 20)..])}");
            });
        }
    }

    static void AsciiOnly(string area, string s) =>
        Safe(area, () => Esc(s), () => { var got = Japanese.KanaToRomaji(s); C(area, Ascii(got), () => $"{s} U+{(int)s[^1]:X4} -> {Esc(got)} (non-ASCII left in)"); });

    // =============================================================== c) Chinese and other scripts
    static bool LatinOnly(string s) => s.All(c => c < 0x0250 || c == '\'' || char.IsWhiteSpace(c));

    static void Scripts()
    {
        var rng = new Random(3003);
        for (int n = 0; n < 3000; n++)
        {
            char c = (char)rng.Next(0x4E00, 0x9FA6);
            Safe("c-chinese-char", () => $"{c} U+{(int)c:X4}", () => { var got = Chinese.Romanize(c.ToString()); C("c-chinese-char", got.Length > 0 && LatinOnly(got) && got.Any(char.IsLetter), () => $"{c} U+{(int)c:X4} -> {Esc(got)}"); });
        }
        string[] latin = { "baby", "love", "oh", "yeah", "Hello", "fly", "I'm", "don't", "OK", "123" };
        for (int n = 0; n < 600; n++)
        {
            var han = new string(Enumerable.Range(0, rng.Next(1, 6)).Select(_ => (char)rng.Next(0x4E00, 0x9FA6)).ToArray());
            var w = latin[rng.Next(latin.Length)];
            var s = rng.Next(2) == 0 ? han + " " + w : w + " " + han;
            Safe("c-chinese-mixed", () => Esc(s), () => { var got = Romanizer.Line(s, "zh"); C("c-chinese-mixed", got.Contains(w) && LatinOnly(got), () => $"{Esc(s)} -> {Esc(got)}"); });
        }
        var sets = new (string area, string letters, Func<string, string> f)[]
        {
            ("c-cyrillic", "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЫЭЮЯєіїґў", Others.Cyrillic),
            ("c-greek", "αβγδεζηθικλμνξοπρστυφχψωΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩάέήίόύώ", Others.Greek),
            ("c-arabic", "ابتثجحخدذرزسشصضطظعغفقكلمنهوي", Others.Arabic),
            ("c-hebrew", "אבגדהוזחטיכלמנסעפצקרשת", Others.Hebrew),
            ("c-devanagari", "अआइईउऊएऐओऔकखगघचछजझटठडढणतथदधनपफबभमयरलवशषसह", Others.Devanagari),
        };
        foreach (var (area, letters, f) in sets)
            for (int n = 0; n < 600; n++)
            {
                var s = new string(Enumerable.Range(0, rng.Next(1, 9)).Select(_ => letters[rng.Next(letters.Length)]).ToArray());
                if (rng.Next(3) == 0) s += " " + latin[rng.Next(latin.Length)];
                Safe(area, () => Esc(s), () =>
                {
                    var got = f(s);
                    var line = Romanizer.Line(s, null);
                    C(area, got.Trim().Length > 0 && LatinOnly(got) && LatinOnly(line), () => $"{Esc(s)} -> {Esc(got)} / line {Esc(line)}");
                });
            }
        for (int n = 0; n < 800; n++)
        {
            var s = new string(Enumerable.Range(0, rng.Next(0, 40)).Select(_ => (char)rng.Next(32, 0x250)).ToArray());
            Safe("c-latin-passthrough", () => Esc(s), () => { var got = Romanizer.Line(s, rng.Next(2) == 0 ? "ja" : null); C("c-latin-passthrough", got == s, () => $"{Esc(s)} -> {Esc(got)}"); });
        }
        // Random code points in the supported ranges: no exception.
        int[][] ranges = { new[] { 0x0370, 0x03FF }, new[] { 0x0400, 0x052F }, new[] { 0x0590, 0x05FF }, new[] { 0x0600, 0x06FF }, new[] { 0x0900, 0x097F }, new[] { 0x3040, 0x30FF }, new[] { 0x3400, 0x4DBF }, new[] { 0xAC00, 0xD7A3 }, new[] { 0x1100, 0x11FF }, new[] { 0xFB50, 0xFDFF } };
        for (int n = 0; n < 1500; n++)
        {
            var s = new string(Enumerable.Range(0, rng.Next(1, 12)).Select(_ => { var rg = ranges[rng.Next(ranges.Length)]; return (char)rng.Next(rg[0], rg[1] + 1); }).ToArray());
            Safe("c-random-codepoints", () => Esc(s), () => { var got = Romanizer.Line(s, null); C("c-random-codepoints", got != null, () => Esc(s)); });
        }
    }

    // =============================================================== d) LRC
    static readonly string[] LatinWords = { "love", "fly", "you", "baby", "night", "stuff", "off", "jump", "WoW", "yeah", "I'm", "don't", "gulf", "joy", "sky" };
    static readonly string[] CjkTokens = { "君の", "夢を", "見た", "愛してる", "ありがとう", "我爱你", "月亮", "天空", "사랑해", "너를", "보고", "싶어", "다시" };

    static string RandText(Random rng, bool allowEmpty = true)
    {
        if (allowEmpty && rng.Next(12) == 0) return "";
        if (rng.Next(40) == 0) return "♪";
        int n = rng.Next(1, 6);
        return string.Join(' ', Enumerable.Range(0, n).Select(_ => rng.Next(2) == 0 ? LatinWords[rng.Next(LatinWords.Length)] : CjkTokens[rng.Next(CjkTokens.Length)]));
    }

    static string Stamp(long ms, int fmt, char open = '[', char close = ']')
    {
        long m = ms / 60000, s = ms / 1000 % 60, f = ms % 1000;
        return fmt switch
        {
            0 => $"{open}{m:00}:{s:00}.{f / 10:00}{close}",
            1 => $"{open}{m:00}:{s:00}.{f:000}{close}",
            2 => $"{open}{m:00}:{s:00}:{f / 10:00}{close}",
            _ => $"{open}{m:00}:{s:00}{close}",
        };
    }

    static long Quant(long ms, int fmt) => fmt == 3 ? ms / 1000 * 1000 : fmt == 1 ? ms : ms / 10 * 10;
    static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    static void CheckLines(string area, List<LyricLine>? got, List<(long start, string text)> want, Func<string> doc)
    {
        var lines = got ?? new List<LyricLine>();
        bool ok = lines.Count == want.Count;
        string why = ok ? "" : $"count {lines.Count} want {want.Count}";
        for (int i = 0; ok && i < lines.Count; i++)
        {
            var l = lines[i];
            if (Math.Abs(l.Start - want[i].start) > 10) { ok = false; why = $"line {i} start {l.Start} want {want[i].start}"; }
            else if (Norm(l.Text) != Norm(want[i].text)) { ok = false; why = $"line {i} text {Esc(l.Text)} want {Esc(want[i].text)}"; }
            else if (l.End < l.Start) { ok = false; why = $"line {i} end {l.End} < start {l.Start}"; }
            else if (i > 0 && l.Start < lines[i - 1].Start) { ok = false; why = $"line {i} not sorted"; }
            else
                for (int k = 0; k < l.Words.Count; k++)
                {
                    var w = l.Words[k];
                    if (w.End < w.Start || w.Start < l.Start - 10 || w.End > l.End + 10 || (k > 0 && w.Start < l.Words[k - 1].Start))
                    { ok = false; why = $"line {i} word {k} {Esc(w.Text)} {w.Start}-{w.End} outside line {l.Start}-{l.End} or out of order"; break; }
                }
        }
        C(area, ok, () => why + " | doc " + doc());
    }

    static void Lrc()
    {
        var rng = new Random(4004);
        for (int n = 0; n < 3000; n++)
        {
            var sb = new StringBuilder();
            var want = new List<(long, string)>();
            string nl = rng.Next(2) == 0 ? "\r\n" : "\n";
            if (rng.Next(4) == 0) sb.Append('﻿');
            long offset = 0;
            if (rng.Next(3) == 0) sb.Append("[ar:Some Artist]").Append(nl).Append("[ti:Title]").Append(nl).Append("[by:me]").Append(nl);
            if (rng.Next(3) == 0)
            {
                offset = rng.Next(-2000, 2001);
                sb.Append($"[offset:{(offset >= 0 && rng.Next(2) == 0 ? "+" : "")}{offset}]").Append(nl);
            }
            var used = new HashSet<long>();
            int lines = rng.Next(1, 25);
            for (int i = 0; i < lines; i++)
            {
                if (rng.Next(8) == 0) sb.Append(nl);
                int stamps = rng.Next(1, 4);
                var text = RandText(rng);
                var row = new StringBuilder();
                for (int k = 0; k < stamps; k++)
                {
                    int fmt = rng.Next(4);
                    long ms;
                    int guard = 0;
                    do ms = Quant(rng.Next(0, 300000), fmt); while ((used.Any(u => Math.Abs(u - ms) < 20)) && ++guard < 50);
                    used.Add(ms);
                    row.Append(Stamp(ms, fmt));
                    if (text.Trim().Length > 0 && text.Trim() != "♪") want.Add((ms - offset, text));
                }
                row.Append(text);
                sb.Append(row).Append(nl);
            }
            want.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var doc = sb.ToString();
            Safe("d-lrc-model", () => Esc(doc), () => CheckLines("d-lrc-model", Formats.ParseLrc(doc), want, () => Esc(doc)));
        }
        // Enhanced LRC with word stamps.
        for (int n = 0; n < 1200; n++)
        {
            var sb = new StringBuilder();
            var want = new List<(long, string)>();
            var wantWords = new List<List<(long start, string text)>>();
            string nl = rng.Next(2) == 0 ? "\r\n" : "\n";
            long t = rng.Next(0, 20000) / 10 * 10;
            int lines = rng.Next(1, 15);
            for (int i = 0; i < lines; i++)
            {
                var row = new StringBuilder(Stamp(t, 0));
                var words = new List<(long, string)>();
                var text = new StringBuilder();
                int tokens = rng.Next(1, 6);
                for (int k = 0; k < tokens; k++)
                {
                    bool cjk = rng.Next(2) == 0;
                    var tok = cjk ? CjkTokens[rng.Next(CjkTokens.Length)] : LatinWords[rng.Next(LatinWords.Length)];
                    var pieces = cjk ? tok.Select(c => c.ToString()).ToList() : (tok.Length > 3 && rng.Next(3) == 0 ? new List<string> { tok[..2], tok[2..] } : new List<string> { tok });
                    if (text.Length > 0) text.Append(' ');
                    text.Append(tok);
                    for (int p = 0; p < pieces.Count; p++)
                    {
                        row.Append(Stamp(t, 0, '<', '>')).Append(pieces[p]);
                        if (p == pieces.Count - 1 && k < tokens - 1) row.Append(' ');
                        words.Add((t, pieces[p]));
                        t += rng.Next(5, 60) * 10;
                    }
                }
                row.Append(Stamp(t, 0, '<', '>'));
                want.Add((t - (t - words[0].Item1), text.ToString()));
                wantWords.Add(words);
                sb.Append(row).Append(nl);
                t += rng.Next(0, 300) * 10;
            }
            var doc = sb.ToString();
            Safe("d-lrc-enhanced", () => Esc(doc), () =>
            {
                var got = Formats.ParseLrc(doc);
                CheckLines("d-lrc-enhanced", got, want, () => Esc(doc));
                bool ok = got != null && got.Count == wantWords.Count;
                string why = ok ? "" : "count";
                for (int i = 0; ok && i < got!.Count; i++)
                {
                    var gw = got[i].Words;
                    if (gw.Count != wantWords[i].Count) { ok = false; why = $"line {i} words {gw.Count} want {wantWords[i].Count}"; break; }
                    for (int k = 0; k < gw.Count; k++)
                        if (Math.Abs(gw[k].Start - wantWords[i][k].start) > 10 || gw[k].Text != wantWords[i][k].text) { ok = false; why = $"line {i} word {k} {Esc(gw[k].Text)}@{gw[k].Start} want {Esc(wantWords[i][k].text)}@{wantWords[i][k].start}"; break; }
                }
                C("d-lrc-enhanced-words", ok, () => why + " | doc " + Esc(doc));
            });
        }
        // Garbage.
        const string soup = "[]<>():.,+-0123456789 abcxyz君の사\r\n﻿[offset:";
        for (int n = 0; n < 3000; n++)
        {
            var s = new string(Enumerable.Range(0, rng.Next(0, 120)).Select(_ => soup[rng.Next(soup.Length)]).ToArray());
            if (rng.Next(3) == 0) s = "[01:02.03]" + s;
            Safe("d-lrc-garbage", () => Esc(s), () => { Formats.ParseLrc(s); C("d-lrc-garbage", true, () => ""); });
        }
    }

    // =============================================================== e) TTML and pieces
    static string Clock(long ms, Random rng) => rng.Next(3) switch
    {
        0 => (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),
        1 => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}",
        _ => $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}",
    };

    static string XmlEsc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    static void TtmlPieces()
    {
        var rng = new Random(5005);
        var validDocs = new List<string>();
        for (int n = 0; n < 1200; n++)
        {
            var sb = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" itunes:timing=\"Word\"><head><metadata><ttm:agent type=\"person\" xml:id=\"v1\"/><ttm:agent type=\"person\" xml:id=\"v2\"/></metadata></head><body><div>");
            var want = new List<(long, string)>();
            var wantVoice = new List<string>();
            var wantBg = new List<string?>();
            long t = rng.Next(0, 10000);
            int lines = rng.Next(1, 12);
            for (int i = 0; i < lines; i++)
            {
                string voice = rng.Next(4) == 0 ? "v2" : "v1";
                long start = t;
                var body = new StringBuilder();
                var text = new StringBuilder();
                bool lineTimed = rng.Next(5) == 0;
                int tokens = rng.Next(1, 6);
                for (int k = 0; k < tokens; k++)
                {
                    bool cjk = rng.Next(2) == 0;
                    var tok = cjk ? CjkTokens[rng.Next(CjkTokens.Length)] : LatinWords[rng.Next(LatinWords.Length)];
                    if (text.Length > 0) { text.Append(' '); body.Append(' '); }
                    text.Append(tok);
                    if (lineTimed) { body.Append(XmlEsc(tok)); t += rng.Next(100, 600); continue; }
                    var pieces = cjk ? tok.Select(c => c.ToString()).ToList() : (tok.Length > 3 && rng.Next(2) == 0 ? new List<string> { tok[..2], tok[2..] } : new List<string> { tok });
                    foreach (var p in pieces)
                    {
                        long d = rng.Next(80, 600);
                        body.Append($"<span begin=\"{Clock(t, rng)}\" end=\"{Clock(t + d, rng)}\">{XmlEsc(p)}</span>");
                        t += d;
                    }
                }
                string? bg = null;
                if (!lineTimed && rng.Next(4) == 0)
                {
                    bg = "(oh yeah)";
                    body.Append($"<span ttm:role=\"x-bg\"><span begin=\"{Clock(t, rng)}\" end=\"{Clock(t + 300, rng)}\">(oh</span> <span begin=\"{Clock(t + 300, rng)}\" end=\"{Clock(t + 600, rng)}\">yeah)</span></span>");
                    t += 600;
                }
                long end = t + 50;
                sb.Append($"<p begin=\"{Clock(start, rng)}\" end=\"{Clock(end, rng)}\" ttm:agent=\"{voice}\" itunes:key=\"L{i + 1}\">{body}</p>");
                want.Add((start, text.ToString()));
                wantVoice.Add(voice); wantBg.Add(bg);
                t = end + rng.Next(0, 2000);
            }
            sb.Append("</div></body></tt>");
            var doc = sb.ToString();
            validDocs.Add(doc);
            Safe("e-ttml-model", () => Esc(doc), () =>
            {
                var got = Formats.ParseTtml(doc);
                CheckLines("e-ttml-model", got, want, () => Esc(doc));
                if (got == null || got.Count != want.Count) return;
                string first = wantVoice[0];
                bool ok = true; string why = "";
                for (int i = 0; i < got.Count && ok; i++)
                {
                    var align = wantVoice[i] != first ? LineAlign.Trailing : LineAlign.Leading;
                    if (got[i].Align != align) { ok = false; why = $"line {i} voice {wantVoice[i]} align {got[i].Align}"; }
                    if (Norm(got[i].Backing?.Text ?? "") != (wantBg[i] ?? "")) { ok = false; why = $"line {i} backing {Esc(got[i].Backing?.Text)} want {Esc(wantBg[i])}"; }
                }
                C("e-ttml-voice-bg", ok, () => why + " | doc " + Esc(doc));
            });
        }
        for (int n = 0; n < 1500; n++)
        {
            var s = validDocs[rng.Next(validDocs.Count)];
            int muts = rng.Next(1, 5);
            for (int m = 0; m < muts && s.Length > 2; m++)
            {
                int at = rng.Next(s.Length);
                switch (rng.Next(4))
                {
                    case 0: s = s.Remove(at, Math.Min(rng.Next(1, 30), s.Length - at)); break;
                    case 1: s = s.Insert(at, new string(Enumerable.Range(0, rng.Next(1, 6)).Select(_ => "<>/\"= &;#x1:.-span"[rng.Next(18)]).ToArray())); break;
                    case 2: s = s[..at]; break;
                    default: int len = Math.Min(rng.Next(1, 80), s.Length - at); s = s.Insert(rng.Next(s.Length), s.Substring(at, len)); break;
                }
            }
            var doc = s;
            Safe("e-ttml-garbage", () => Esc(doc), () => { Formats.ParseTtml(doc); C("e-ttml-garbage", true, () => ""); });
        }
        // yrc / krc pieces.
        var pieceDocs = new List<string>();
        for (int n = 0; n < 1500; n++)
        {
            bool krc = rng.Next(2) == 0;
            var sb = new StringBuilder();
            var want = new List<(long, string)>();
            var wantWords = new List<List<(long, string)>>();
            long t = rng.Next(0, 20000);
            int lines = rng.Next(1, 12);
            for (int i = 0; i < lines; i++)
            {
                long start = t;
                var row = new StringBuilder();
                var text = new StringBuilder();
                var words = new List<(long, string)>();
                int tokens = rng.Next(1, 6);
                for (int k = 0; k < tokens; k++)
                {
                    bool cjk = rng.Next(2) == 0;
                    var tok = cjk ? CjkTokens[rng.Next(CjkTokens.Length)] : LatinWords[rng.Next(LatinWords.Length)];
                    if (text.Length > 0) text.Append(' ');
                    text.Append(tok);
                    var pieces = cjk ? tok.Select(c => c.ToString()).ToList() : (tok.Length > 3 && rng.Next(2) == 0 ? new List<string> { tok[..2], tok[2..] } : new List<string> { tok });
                    bool korean = tok.Any(c => c >= 0xAC00 && c <= 0xD7A3);
                    long wordStart = t;
                    for (int p = 0; p < pieces.Count; p++)
                    {
                        long d = rng.Next(80, 500);
                        long rel = krc ? t - start : t;
                        string piece = pieces[p] + (p == pieces.Count - 1 && k < tokens - 1 ? " " : "");
                        row.Append(krc ? $"<{rel},{d},0>" : $"({rel},{d},0)").Append(piece);
                        // Latin or Korean syllables of one word are one word (Korean: Karaoke.UnspacedScript is false).
                        if (cjk && !korean) words.Add((t, pieces[p]));
                        t += d;
                    }
                    if (!cjk || korean) words.Add((wordStart, tok));
                }
                sb.Append($"[{start},{t - start}]").Append(row).Append(rng.Next(2) == 0 ? "\r\n" : "\n");
                want.Add((start, text.ToString()));
                wantWords.Add(words);
                t += rng.Next(0, 1500);
            }
            var doc = sb.ToString();
            pieceDocs.Add(doc);
            Safe("e-pieces-model", () => Esc(doc), () =>
            {
                var got = Formats.ParsePieces(doc, krc);
                CheckLines("e-pieces-model", got, want, () => (krc ? "krc " : "yrc ") + Esc(doc));
                bool ok = got != null && got.Count == wantWords.Count; string why = ok ? "" : "count";
                for (int i = 0; ok && i < got!.Count; i++)
                {
                    if (got[i].Words.Count != wantWords[i].Count) { ok = false; why = $"line {i} words {got[i].Words.Count} want {wantWords[i].Count}: {string.Join("|", got[i].Words.Select(w => w.Text))}"; break; }
                    for (int k = 0; k < got[i].Words.Count; k++)
                        if (got[i].Words[k].Start != wantWords[i][k].Item1 || got[i].Words[k].Text != wantWords[i][k].Item2) { ok = false; why = $"line {i} word {k} {Esc(got[i].Words[k].Text)}@{got[i].Words[k].Start} want {Esc(wantWords[i][k].Item2)}@{wantWords[i][k].Item1}"; break; }
                }
                C("e-pieces-words", ok, () => why + " | " + (krc ? "krc " : "yrc ") + Esc(doc));
            });
        }
        const string soup = "[](),<>0123456789-, abc君の\r\n";
        for (int n = 0; n < 1500; n++)
        {
            var s = rng.Next(2) == 0 ? new string(Enumerable.Range(0, rng.Next(0, 100)).Select(_ => soup[rng.Next(soup.Length)]).ToArray())
                : pieceDocs[rng.Next(pieceDocs.Count)].Remove(0, 1).Insert(0, "[");
            if (rng.Next(3) == 0) s = "[1000,99999999999999999999]" + s;
            Safe("e-pieces-garbage", () => Esc(s), () => { Formats.ParsePieces(s, rng.Next(2) == 0); C("e-pieces-garbage", true, () => ""); });
        }
    }

    // =============================================================== f) ClockMs
    static void ClockMsTests()
    {
        var rng = new Random(6006);
        for (int n = 0; n < 2500; n++)
        {
            long h = rng.Next(0, 3), m = rng.Next(0, 60), s = rng.Next(0, 60), f = rng.Next(0, 1000);
            long ms = ((h * 60 + m) * 60 + s) * 1000 + f;
            string text; long want = ms;
            switch (rng.Next(6))
            {
                case 0: text = $"{h}:{m:00}:{s:00}.{f:000}"; break;
                case 1: text = $"{h * 60 + m}:{s:00}.{f:000}"; want = ((h * 60 + m) * 60 + s) * 1000 + f; break;
                case 2: text = ((h * 3600 + m * 60 + s) + f / 1000.0).ToString("0.000", CultureInfo.InvariantCulture); break;
                case 3: text = ((h * 3600 + m * 60 + s) + f / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + "s"; break;
                case 4: text = $"{ms}ms"; break;
                default: text = $"  {m}:{s:00}.{f / 100}  "; want = (m * 60 + s) * 1000 + f / 100 * 100; break;
            }
            Safe("f-clockms", () => Esc(text), () => { var got = Formats.ClockMs(text); C("f-clockms", got == want, () => $"{Esc(text)} got {got} want {want}"); });
        }
        foreach (var bad in new[] { null, "", "  ", "abc", "1:2:3:4", "1:a", "::", "1..2", "--1" })
            Safe("f-clockms-invalid", () => Esc(bad), () => { var got = Formats.ClockMs(bad); C("f-clockms-invalid", got == -1, () => $"{Esc(bad)} got {got} want -1"); });
    }

    // =============================================================== g) Karaoke helpers and Normalize
    static void KaraokeNormalize()
    {
        var rng = new Random(7007);
        for (int n = 0; n < 2500; n++)
        {
            var text = RandText(rng, false);
            if (rng.Next(5) == 0) text = "  " + text + "  ";
            long start = rng.Next(0, 200000), end = start + rng.Next(-500, 8000);
            Safe("g-spreadwords", () => $"{Esc(text)} {start}-{end}", () =>
            {
                var l = Karaoke.SpreadWords(text, start, end);
                bool ok = l.Words.Count > 0 && Norm(l.Text) == Norm(text) && l.Words[0].Start == start;
                for (int k = 0; ok && k < l.Words.Count; k++)
                    if (l.Words[k].End < l.Words[k].Start || (k > 0 && l.Words[k].Start < l.Words[k - 1].Start)) ok = false;
                if (end > start && Math.Abs(l.Words[^1].End - end) > 1) ok = false;
                C("g-spreadwords", ok, () => $"{Esc(text)} {start}-{end} -> {string.Join(" | ", l.Words.Select(w => $"{w.Text}@{w.Start}-{w.End}"))}");
            });
        }
        for (int n = 0; n < 800; n++)
        {
            int cnt = rng.Next(1, 20);
            var starts = new List<long>(); var texts = new List<string>();
            long t = rng.Next(0, 5000);
            for (int i = 0; i < cnt; i++) { starts.Add(t); texts.Add(RandText(rng)); t += rng.Next(0, 9000); }
            Safe("g-estimatedlines", () => string.Join(";", texts), () =>
            {
                var lines = Karaoke.EstimatedLines(starts, texts);
                int want = texts.Count(x => x.Trim().Length > 0 && x.Trim() != "♪");
                bool ok = lines.Count == want && lines.All(l => l.Estimated && l.End >= l.Start && l.Words.All(w => w.Start >= l.Start && w.End <= l.End + 1));
                C("g-estimatedlines", ok, () => $"count {lines.Count} want {want}; {string.Join(";", starts.Zip(texts).Select(p => p.First + ":" + Esc(p.Second)))}");
                var timing = Karaoke.TimingOf(lines);
                C("g-timingof", lines.Count == 0 ? timing == Timing.None : timing == (lines.All(l => l.Start == 0 && l.End == 0) ? Timing.None : Timing.Line), () => $"timing {timing}");
            });
        }
        C("g-timingof", Karaoke.TimingOf(new List<LyricLine> { new() { Start = 5, End = 10 } }) == Timing.Word, () => "non-estimated line should be Word");
        C("g-timingof", Karaoke.TimingOf(Karaoke.StaticLines(new[] { "a", "b" })) == Timing.None, () => "static lines should be None");

        string[] titleWords = { "Love", "Dynamite", "Butter", "夜に駆ける", "사랑", "Ride", "Ça", "Ödland", "İstanbul", "ǅemal", "Straße", "ﬁre", "ＡＢＣ", "Café", "Ⅻ" };
        string[] artists = { "IU", "V", "BTS", "Coldplay", "Ado", "RM", "BoA", "YOASOBI", "XG", "Lisa" };
        for (int n = 0; n < 1500; n++)
        {
            var title = string.Join(' ', Enumerable.Range(0, rng.Next(1, 4)).Select(_ => titleWords[rng.Next(titleWords.Length)]));
            Safe("g-matchkey-idempotent", () => Esc(title), () =>
            {
                var k = Normalize.MatchKey(title);
                C("g-matchkey-idempotent", Normalize.MatchKey(k) == k, () => $"{Esc(title)} -> {Esc(k)} -> {Esc(Normalize.MatchKey(k))}");
                C("g-bartext-idempotent", Karaoke.BareText(Karaoke.BareText(title)) == Karaoke.BareText(title), () => Esc(title));
                C("g-sametitle-reflexive", Normalize.SameTitle(title, title), () => Esc(title));
            });
            string a = artists[rng.Next(artists.Length)];
            string full = rng.Next(3) switch { 0 => a, 1 => $"{a} feat. {artists[rng.Next(artists.Length)]}", _ => $"{a}, {artists[rng.Next(artists.Length)]}" };
            string other = artists[rng.Next(artists.Length)];
            Safe("g-sameartist-credit-list", () => a, () => C("g-sameartist-credit-list", Normalize.SameArtist($"{a}, {other}", a) && Normalize.SameArtist($"{a}、{other}", $"{a}, {other}"), () => $"SameArtist({Esc(a + ", " + other)}, {Esc(a)}) = {Normalize.SameArtist($"{a}, {other}", a)}; SameArtist({Esc(a + "、" + other)}, {Esc(a + ", " + other)}) = {Normalize.SameArtist($"{a}、{other}", $"{a}, {other}")}"));
            Safe("g-sameartist-reflexive", () => Esc(full), () => C("g-sameartist-reflexive", Normalize.SameArtist(full, full), () => $"SameArtist({Esc(full)}, {Esc(full)}) = false"));
            string[] suffixes = { " - Remastered 2011", " - 2011 Remaster", " - Remastered", " (feat. Someone)", " [feat. A & B]", " (with X)", " - Live", " - Radio Edit", " - Single Version", " - Mono", " - 2009 Remastered Version", " (Prod. Z)" };
            var suf = suffixes[rng.Next(suffixes.Length)];
            Safe("g-cleantitle", () => Esc(title + suf), () =>
            {
                var c = Normalize.CleanTitle(title + suf);
                C("g-cleantitle", c == title.Trim(), () => $"{Esc(title + suf)} -> {Esc(c)}");
                C("g-songkey", Normalize.SongKey(full, title) == Normalize.SongKey(full, title) && Normalize.SongKey(full, title + suf) == Normalize.SongKey(full, title)
                    && Normalize.SongKey(a + " feat. Someone", title) == Normalize.SongKey(a, title), () => $"{Esc(full)} / {Esc(title + suf)}");
            });
        }
        foreach (var t in new[] { "?", "!!!", "<3", "#", "♥", "...", "*" })
            C("g-sametitle-symbol-only", Normalize.SameTitle(t, t), () => $"SameTitle({Esc(t)}, {Esc(t)}) = false (symbol-only title can never match)");
    }

    // =============================================================== h) Prepare
    static LyricLine RandLine(Random rng, long start)
    {
        var text = RandText(rng, false);
        if (text == "♪") text = "la la";
        long end = start + rng.Next(800, 6000);
        var l = rng.Next(3) == 0 ? Karaoke.SpreadWords(text, start, end) : Karaoke.SpreadWords(text, start, end);
        l.Estimated = rng.Next(2) == 0;
        return l;
    }

    static void PrepareTests()
    {
        var rng = new Random(8008);
        for (int n = 0; n < 300; n++)
        {
            var r = new LyricsResult { Provider = "fuzz", Timing = Timing.Word };
            long t = rng.Next(0, 10000);
            int cnt = rng.Next(1, 10);
            for (int i = 0; i < cnt; i++)
            {
                var l = RandLine(rng, t);
                if (rng.Next(4) == 0) l.Backing = Karaoke.SpreadWords(rng.Next(2) == 0 ? "(oh)" : "(사랑해)", l.Start, l.End);
                if (rng.Next(4) == 0) l.Pronunciation = Karaoke.Pronunciation(null, rng.Next(3) == 0 ? "yume o 見ta" : "romanized text here", l);
                if (rng.Next(2) == 0) l.Align = LineAlign.Trailing;
                r.Lines.Add(l);
                t = l.End + rng.Next(0, 3000);
            }
            var snapshot = r.Lines.Select(l => (l.Start, l.End, string.Join(",", l.Words.Select(w => w.Start + "-" + w.End)))).ToList();
            foreach (DisplayMode mode in Enum.GetValues<DisplayMode>())
            {
                var s = new AppSettings { Display = mode };
                string Repro() => $"mode {mode} lines {string.Join(" / ", r.Lines.Select(l => Esc(l.Text)))}";
                Safe("h-prepare", Repro, () =>
                {
                    var d = AppController.Prepare(r, s);
                    bool ok = d.Count == r.Lines.Count; string why = ok ? "" : "count";
                    for (int i = 0; ok && i < d.Count; i++)
                    {
                        var src = r.Lines[i];
                        var now = (src.Start, src.End, string.Join(",", src.Words.Select(w => w.Start + "-" + w.End)));
                        if (now != snapshot[i]) { ok = false; why = $"line {i} source times changed"; break; }
                        if (d[i].Main.Start != src.Start || d[i].Main.End != src.End) { ok = false; why = $"line {i} main times {d[i].Main.Start}-{d[i].Main.End} want {src.Start}-{src.End}"; break; }
                        bool needs = Romanizer.Needs(src.Text);
                        if (mode == DisplayMode.OriginalAndRomanized && needs && d[i].Sub == null) { ok = false; why = $"line {i} {Esc(src.Text)} no romanized sub line"; break; }
                        if (mode == DisplayMode.OriginalOnly && (d[i].Sub != null || d[i].Main != src)) { ok = false; why = $"line {i} original-only changed"; break; }
                        if (mode == DisplayMode.RomanizedOnly && Romanizer.Needs(d[i].Main.Text)) { ok = false; why = $"line {i} {Esc(src.Text)} romanized-only still has script: {Esc(d[i].Main.Text)}"; break; }
                        var rom = mode == DisplayMode.RomanizedOnly ? d[i].Main : d[i].Sub;
                        if (rom != null && rom.Words.Any(w => w.Start < src.Start || w.End > src.End + 1 || w.End < w.Start)) { ok = false; why = $"line {i} romanized word outside line {src.Start}-{src.End}: {string.Join(" ", rom.Words.Select(w => $"{w.Text}@{w.Start}-{w.End}"))}"; break; }
                        if (d[i].Align != src.Align) { ok = false; why = $"line {i} align"; break; }
                    }
                    C("h-prepare", ok, () => why + " | " + Repro());
                });
            }
        }
    }

    // =============================================================== i) Media timing
    static void MediaTests()
    {
        var rng = new Random(9009);
        for (int n = 0; n < 600; n++)
        {
            bool playing = rng.Next(3) != 0;
            double rate = new[] { 1.0, 1.0, 0.0, -1.0, 0.5, 2.0 }[rng.Next(6)];
            long pos = rng.Next(0, 300000), dur = rng.Next(4) == 0 ? 0 : rng.Next(1000, 320000);
            double ago = rng.Next(0, 20000);
            var before = DateTime.UtcNow;
            var st = new MediaState("spotify", "t", "a", "al", dur, pos, before.AddMilliseconds(-ago), playing, rate);
            long got = st.PositionNow();
            double el = (DateTime.UtcNow - before).TotalMilliseconds + ago;
            double r = rate <= 0 ? 1 : rate;
            long lo = playing ? pos + (long)(ago * r) : pos, hi = playing ? pos + (long)(el * r) + 1 : pos;
            if (playing && dur > 0) { lo = Math.Min(lo, dur); hi = Math.Min(hi, dur); }
            C("i-positionnow", got >= lo && got <= hi, () => $"playing={playing} rate={rate} pos={pos} dur={dur} ago={ago} -> {got} want {lo}..{hi}");
        }
        for (int run = 0; run < 400; run++) SimulateSmoother(run, new Random(10000 + run));
    }

    /// <summary>A player reporting every 0.3-1.5 s with jitter, read by the watcher every 400 ms, smoothed each 60 fps frame.</summary>
    static void SimulateSmoother(int run, Random rng)
    {
        var sm = new ClockSmoother();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double now = 0, truePos = rng.Next(0, 60000);
        bool playing = true; string track = "track0";
        double nextReport = 0, nextPoll = 0;
        double repPos = truePos, repAt = 0; bool repPlaying = true;   // player's last report
        double wPos = truePos, wAt = 0; bool wPlaying = true; string wTrack = track;   // what the watcher shows
        double lastEvent = -10000;
        double jitter = 150;
        var events = new List<(double at, int kind)>();
        for (int e = 0; e < 4; e++) events.Add((rng.Next(3000, 40000), rng.Next(3)));   // 0 pause, 1 seek, 2 track change
        events.Sort();
        double pauseUntil = -1;
        long prevSmooth = long.MinValue; double prevFrame = 0;
        int back = 0; double worstBack = 0; string backAt = "";
        int seekMiss = 0, pauseMove = 0; string seekWhy = "", pauseWhy = "";
        double maxErrTrue = 0, maxErrRaw = 0; int settleSamples = 0; string errWhy = "";
        bool pendingJump = false; double jumpSeenAt = -1;
        double lastWatcherChange = 0;
        long pausedHold = long.MinValue;
        while (now < 45000)
        {
            double dt = 1000.0 / 60 + rng.NextDouble() * 4 - 2;
            now += dt;
            if (playing) truePos += dt;
            // events
            while (events.Count > 0 && events[0].at <= now)
            {
                var kind = events[0].kind; events.RemoveAt(0);
                if (kind == 0 && playing) { playing = false; pauseUntil = now + rng.Next(800, 3000); }
                else if (kind == 1) { truePos = Math.Max(0, truePos + (rng.Next(2) == 0 ? -1 : 1) * rng.Next(1000, 60000)); }
                else { track = "track" + rng.Next(1000); truePos = rng.Next(0, 3000); }
                lastEvent = now; nextReport = now + rng.Next(0, 120);
            }
            if (!playing && pauseUntil > 0 && now >= pauseUntil) { playing = true; pauseUntil = -1; lastEvent = now; nextReport = now + rng.Next(0, 300); }
            if (now >= nextReport)
            {
                // A paused player keeps reporting the same position.
                if (playing || repPlaying) repPos = truePos + (rng.NextDouble() * 2 - 1) * jitter;
                repAt = now; repPlaying = playing;
                nextReport = now + rng.Next(300, 1500);
            }
            if (now >= nextPoll)
            {
                // The watcher: on pause the "at" is now; otherwise the report's own time.
                double newPos = repPos, newAt = repPlaying ? repAt : now;
                bool jump = wTrack != track || Math.Abs((wPlaying ? wPos + (now - wAt) : wPos) - (repPlaying ? newPos + (now - newAt) : newPos)) > 1200 || wPlaying != repPlaying;
                if (jump) { pendingJump = true; jumpSeenAt = now; }
                if (newPos != wPos || newAt != wAt) lastWatcherChange = now;
                wPos = newPos; wAt = newAt; wPlaying = repPlaying; wTrack = track;
                nextPoll = now + 400;
            }
            long raw = (long)(wPlaying ? wPos + (now - wAt) : wPos);
            long smooth = sm.Next(raw, t0.AddMilliseconds(now), wPlaying, 1, wTrack);
            bool steady = wPlaying && playing && now - lastEvent > 2000;
            if (steady && prevSmooth != long.MinValue && now - prevFrame < 40 && smooth < prevSmooth - 1)
            {
                back++;
                if (prevSmooth - smooth > worstBack) { worstBack = prevSmooth - smooth; backAt = $"t={now:0}ms prev={prevSmooth} now={smooth} raw={raw}"; }
            }
            if (pendingJump)
            {
                var diff = Math.Abs(smooth - raw);
                if (Math.Abs(raw - prevSmooth) > ClockSmoother.JumpMs && diff > 5) { seekMiss++; seekWhy = $"t={now:0} raw={raw} smooth={smooth} prev={prevSmooth}"; }
                pendingJump = false;
            }
            if (!wPlaying)
            {
                if (pausedHold != long.MinValue && smooth != pausedHold) { pauseMove++; pauseWhy = $"t={now:0} {pausedHold}->{smooth}"; }
                pausedHold = smooth;
            }
            else pausedHold = long.MinValue;
            if (steady && now - lastWatcherChange > 600)
            {
                settleSamples++;
                double eT = Math.Abs(smooth - truePos), eR = Math.Abs(smooth - raw);
                if (eR > maxErrRaw) maxErrRaw = eR;
                if (eT > maxErrTrue) { maxErrTrue = eT; errWhy = $"t={now:0} true={truePos:0} raw={raw} smooth={smooth}"; }
            }
            prevSmooth = smooth; prevFrame = now;
        }
        string tag = $"run {run} seed {10000 + run}";
        C("i-smoother-monotonic", back == 0, () => $"{tag}: went backwards {back} times in steady playback, worst {worstBack:0} ms ({backAt})");
        C("i-smoother-seek", seekMiss == 0, () => $"{tag}: jump not followed on the next frame ({seekWhy})");
        C("i-smoother-pause", pauseMove == 0, () => $"{tag}: moved while paused ({pauseWhy})");
        C("i-smoother-settle-raw", maxErrRaw < 60, () => $"{tag}: smoothed vs raw still {maxErrRaw:0} ms 600 ms after a report");
        C("i-smoother-settle-true", settleSamples == 0 || maxErrTrue < 60, () => $"{tag}: smoothed vs true position up to {maxErrTrue:0} ms between reports (jitter +/-{jitter}) ({errWhy})");
    }

    // =============================================================== j) Settings
    static void SettingsTests()
    {
        var jsonOpt = (JsonSerializerOptions)typeof(AppSettings).GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var salvage = typeof(AppSettings).GetMethod("Salvage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sanitize = typeof(AppSettings).GetMethod("Sanitize", BindingFlags.NonPublic | BindingFlags.Instance)!;
        AppSettings LoadLikeApp(string text)
        {
            AppSettings s;
            try { s = JsonSerializer.Deserialize<AppSettings>(text, jsonOpt) ?? new(); }
            catch { s = (AppSettings)salvage.Invoke(null, new object[] { text })!; }
            try { sanitize.Invoke(s, null); }
            catch (TargetInvocationException ex) { throw ex.InnerException!; }
            return s;
        }
        string Within(AppSettings s)
        {
            var bad = new List<string>();
            void R(string n, double v, double lo, double hi) { if (!(v >= lo && v <= hi)) bad.Add($"{n}={v}"); }
            R("FontSize", s.FontSize, 12, 120); R("StripWidth", s.StripWidth, 300, 4000); R("BackgroundOpacity", s.BackgroundOpacity, 0, 1);
            R("OutlineWidth", s.OutlineWidth, 0, 10); R("WordLift", s.WordLift, 0, 20); R("CornerRadius", s.CornerRadius, 0, 60);
            R("LyricsFontSize", s.LyricsFontSize, 18, 110); R("LyricsWidth", s.LyricsWidth, 520, 10000); R("LyricsHeight", s.LyricsHeight, 420, 10000);
            R("GlobalOffsetMs", s.GlobalOffsetMs, -30000, 30000); R("WebPort", s.WebPort, 1024, 65535);
            if (s.Left is double l && !double.IsFinite(l)) bad.Add("Left");
            if (s.Top is double t && !double.IsFinite(t)) bad.Add("Top");
            if (s.LyricsLeft is double ll && !double.IsFinite(ll)) bad.Add("LyricsLeft=" + ll);
            if (s.LyricsTop is double lt && !double.IsFinite(lt)) bad.Add("LyricsTop=" + lt);
            if (s.Sources == null || s.Sources.Count != AppSettings.SourceOrder.Length || s.Sources.Any(e => e == null)) bad.Add("Sources");
            if (s.SongOffsets == null || s.SongSourceOverride == null) bad.Add("dicts");
            foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.PropertyType.IsEnum && p.CanWrite))
                if (!Enum.IsDefined(p.PropertyType, p.GetValue(s)!)) bad.Add(p.Name + "=" + p.GetValue(s));
            return string.Join(",", bad);
        }
        var rng = new Random(11011);
        var props = typeof(AppSettings).GetProperties().Where(p => p.CanWrite && p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() == null).Select(p => p.Name).ToArray();
        string[] values = { "null", "\"NaN\"", "\"Infinity\"", "\"-Infinity\"", "1e308", "-1e308", "1e400", "99999999999", "-99999999999", "\"Purple\"", "\"\"", "true", "[]", "{}", "[null]", "[{}]", "[{\"Key\":null}]", "{\"a\":null}", "{\"a\":\"x\"}", "0", "-1", "3.5", "\"#ZZZ\"", "\"12\"", "[1,2]", "\"Modern\"", "7" };
        var baseJson = JsonSerializer.Serialize(new AppSettings(), jsonOpt);
        for (int n = 0; n < 2500; n++)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(baseJson)!.AsObject();
            int muts = rng.Next(1, 6);
            for (int m = 0; m < muts; m++)
            {
                var name = props[rng.Next(props.Length)];
                if (rng.Next(6) == 0) node.Remove(name);
                else node[name] = System.Text.Json.Nodes.JsonNode.Parse(values[rng.Next(values.Length)]);
            }
            if (rng.Next(8) == 0) node["UnknownSetting" + rng.Next(5)] = 1;
            var text = node.ToJsonString();
            if (rng.Next(6) == 0) text = text[..rng.Next(text.Length)];
            if (rng.Next(10) == 0) text = text.Insert(rng.Next(text.Length), "/* c */ ,");
            Safe("j-settings-corrupt", () => Esc(text), () =>
            {
                var s = LoadLikeApp(text);
                var w = Within(s);
                C("j-settings-corrupt", w.Length == 0, () => $"out of limits {w} | {Esc(text)}");
            });
        }
        for (int n = 0; n < 600; n++)
        {
            var s = new AppSettings
            {
                FontSize = rng.Next(12, 121), StripWidth = rng.Next(300, 4001), BackgroundOpacity = Math.Round(rng.NextDouble(), 3), OutlineWidth = rng.Next(0, 11),
                WordLift = rng.Next(0, 21), CornerRadius = rng.Next(0, 61), LyricsFontSize = rng.Next(18, 111), LyricsWidth = rng.Next(520, 3000), LyricsHeight = rng.Next(420, 2000),
                GlobalOffsetMs = rng.Next(-30000, 30001), WebPort = rng.Next(1024, 65536), Left = rng.Next(-3000, 5000), Top = rng.Next(-2000, 3000),
                StripStyle = (StripStyle)rng.Next(3), LyricsMotion = (LyricsMotion)rng.Next(5), Display = (DisplayMode)rng.Next(3), Bold = rng.Next(2) == 0,
                UnsungColor = $"#{rng.Next(0x1000000):X6}", FontFamily = new[] { "Segoe UI", "Georgia", "Yu Gothic UI" }[rng.Next(3)], LyricsPanelHidden = rng.Next(2) == 0,
                SkippedVersion = rng.Next(2) == 0 ? null : "2.3." + rng.Next(9), DefaultsVersion = AppSettings.CurrentDefaults,
            };
            s.SongOffsets["artist|song" + rng.Next(50)] = rng.Next(-3000, 3000);
            var text = JsonSerializer.Serialize(s, jsonOpt);
            Safe("j-settings-roundtrip", () => Esc(text), () =>
            {
                var back = JsonSerializer.Serialize(LoadLikeApp(text), jsonOpt);
                C("j-settings-roundtrip", back == text, () => $"changed on load: {Esc(text)}");
            });
        }
    }

    // =============================================================== k) strip sweep
    static readonly string[] OverhangWords = { "off", "fly", "jay", "WoW", "stuff", "joy", "jiff", "f", "y", "Jj", "gulf", "Wolf", "staff", "fifty", "愛してる", "사랑해", "君", "你", "Ÿ" };
    static readonly string[] Fonts = { "Segoe UI", "Segoe UI Variable Display", "Georgia", "Times New Roman", "Gabriola", "Segoe Script", "Yu Gothic UI", "Malgun Gothic", "Arial", "Comic Sans MS" };

    sealed class StripCase
    {
        public AppSettings S = null!; public List<DisplayLine> D = null!; public long T; public double W; public string Repro = ""; public KaraokeView V = null!;
    }

    static KaraokeView MakeView(AppSettings s, List<DisplayLine> d, long t, double w)
    {
        var view = new KaraokeView { Width = w };
        view.SetStyle(OverlayWindow.StyleFor(s));
        view.SetClock(() => t);
        view.SetLyrics(d, true, 600000, null);
        view.Height = view.DesiredStripHeight();
        view.Measure(new Size(w, view.Height)); view.Arrange(new Rect(0, 0, w, view.Height)); view.UpdateLayout();
        return view;
    }

    static byte[] Pixels(Visual v, int w, int h)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        return px;
    }

    static void SaveImage(Visual v, int w, int h, string path)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), null, new Rect(0, 0, w, h));
        bmp.Render(bg); bmp.Render(v);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path); enc.Save(f);
    }

    static string _outDir = "";

    static void StripTests()
    {

        var rng = new Random(12012);
        var cases = new List<StripCase>();
        for (int n = 0; n < 480; n++)
        {
            var s = new AppSettings
            {
                StripStyle = (StripStyle)rng.Next(3), UnsungColor = "#FF0000", SungColor = "#00FF00", NextLineColor = "#0000FF", SubColor = "#FF0000",
                OutlineWidth = 0, Glow = false, WordGlow = false, BackgroundOpacity = 0, ShowTranslation = false,
                WordLift = new[] { 0.0, 0.0, 4.0, 8.0 }[rng.Next(4)], FontFamily = Fonts[rng.Next(Fonts.Length)], FontSize = rng.Next(16, 61),
                Bold = rng.Next(2) == 0, SmoothSweep = rng.Next(3) != 0, Align = (StripAlign)rng.Next(3), Display = (DisplayMode)rng.Next(3), ShowBackingVocals = false,
            };
            int words = rng.Next(1, 9);
            var text = string.Join(' ', Enumerable.Range(0, words).Select(i => i == words - 1 || rng.Next(2) == 0 ? OverhangWords[rng.Next(OverhangWords.Length)] : LatinWords[rng.Next(LatinWords.Length)]));
            long start = 1000, end = start + words * rng.Next(250, 700);
            var cur = Karaoke.SpreadWords(text, start, end);
            var next = Karaoke.SpreadWords("next line here", end + 1500, end + 4000);
            var r = new LyricsResult { Lines = { cur, next }, Timing = Timing.Word };
            var d = AppController.Prepare(r, s);
            long lastEnd = Math.Max(d[0].Main.Words[^1].End, d[0].Sub?.Words.LastOrDefault()?.End ?? 0);
            double w = rng.Next(500, 1400);
            foreach (var t in new[] { lastEnd + 40, next.Start - 40 })
                cases.Add(new StripCase { S = s, D = d, T = t, W = w, Repro = $"layout={s.StripStyle} font={Esc(s.FontFamily)} size={s.FontSize} bold={s.Bold} lift={s.WordLift} smooth={s.SmoothSweep} align={s.Align} mode={s.Display} width={w} text={Esc(text)} t={t} (last word ends {lastEnd}, next starts {next.Start})" });
        }
        int saved = 0;
        for (int b = 0; b < cases.Count; b += 120)
        {
            var batch = cases.Skip(b).Take(120).ToList();
            foreach (var c in batch) Safe("k-strip-sweep", () => c.Repro, () => { c.V = MakeView(c.S, c.D, c.T, c.W); Pixels(c.V, 4, 4); });
            Thread.Sleep(420);   // let the line's entrance transition finish
            foreach (var c in batch)
            {
                if (c.V == null) continue;
                Safe("k-strip-sweep", () => c.Repro, () =>
                {
                    c.V.InvalidateVisual(); c.V.UpdateLayout();
                    int w = (int)c.W, h = (int)Math.Ceiling(c.V.Height);
                    var px = Pixels(c.V, w, h);
                    int red = 0, minX = int.MaxValue, maxX = -1;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = (y * w + x) * 4;
                            if (px[i + 2] >= 100 && px[i + 1] <= 10 && px[i] <= 10) { red++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); }
                        }
                    string kind = c.S.WordLift > 0 ? "lifted" : "flat";
                    int blend = 0, bMin = int.MaxValue, bMax = -1;
                    for (int i = 0; i < px.Length; i += 4) if (px[i + 2] >= 100 && px[i + 2] > px[i + 1]) { blend++; int x = i / 4 % w; bMin = Math.Min(bMin, x); bMax = Math.Max(bMax, x); }
                    C("k-strip-sweep-" + kind, red == 0, () => $"{red} unsung (pure red) pixels left at x {minX}..{maxX} | {c.Repro}");
                    C("k-strip-sweep-reddominant-" + kind, blend == 0, () => $"{blend} red-dominant (mostly unsung) pixels at x {bMin}..{bMax} | {c.Repro}");
                    if (red > 0 && saved < 6) { SaveImage(c.V, w, h, Path.Combine(_reportDir, $"strip_fail_{saved++}.png")); }
                });
            }
        }
        // Progress never goes backwards along a line.
        var prog = new List<(string repro, List<(long t, KaraokeView v, int w, int h)> frames)>();
        for (int n = 0; n < 60; n++)
        {
            var s = new AppSettings
            {
                StripStyle = (StripStyle)rng.Next(3), UnsungColor = "#FF0000", SungColor = "#00FF00", NextLineColor = "#0000FF", SubColor = "#FF0000",
                OutlineWidth = 0, Glow = false, WordGlow = false, WordLift = new[] { 0.0, 4.0 }[rng.Next(2)], FontFamily = Fonts[rng.Next(Fonts.Length)],
                FontSize = rng.Next(18, 50), SmoothSweep = rng.Next(2) == 0, Display = (DisplayMode)rng.Next(3), ShowBackingVocals = false,
            };
            int words = rng.Next(2, 10);
            var text = string.Join(' ', Enumerable.Range(0, words).Select(_ => rng.Next(3) == 0 ? CjkTokens[rng.Next(CjkTokens.Length)] : LatinWords[rng.Next(LatinWords.Length)]));
            long start = 1000, end = start + words * 500;
            var r = new LyricsResult { Lines = { Karaoke.SpreadWords(text, start, end), Karaoke.SpreadWords("next", end + 1500, end + 3000) }, Timing = Timing.Word };
            var d = AppController.Prepare(r, s);
            int w = rng.Next(500, 1200);
            var frames = new List<(long, KaraokeView, int, int)>();
            for (int k = 0; k <= 12; k++)
            {
                long t = start + (end - start) * k / 12;
                var v = MakeView(s, d, t, w);
                Pixels(v, 4, 4);
                frames.Add((t, v, w, (int)Math.Ceiling(v.Height)));
            }
            prog.Add(($"layout={s.StripStyle} font={Esc(s.FontFamily)} size={s.FontSize} lift={s.WordLift} mode={s.Display} width={w} text={Esc(text)}", frames));
        }
        Thread.Sleep(420);
        foreach (var (repro, frames) in prog)
            Safe("k-strip-progress", () => repro, () =>
            {
                int prevGreen = -1; long prevT = 0; bool ok = true; string why = "";
                foreach (var (t, v, w, h) in frames)
                {
                    v.InvalidateVisual(); v.UpdateLayout();
                    var px = Pixels(v, w, h);
                    int green = 0;
                    for (int i = 0; i < px.Length; i += 4) if (px[i + 1] >= 100 && px[i + 2] <= 60) green++;
                    if (prevGreen >= 0 && green < prevGreen - Math.Max(20, prevGreen / 100)) { ok = false; why = $"sung pixels fell {prevGreen} -> {green} between t={prevT} and t={t}"; }
                    prevGreen = green; prevT = t;
                }
                C("k-strip-progress", ok, () => why + " | " + repro);
            });
    }

    static string _reportDir = "";

    // =============================================================== l) full lyrics page
    static void PageTests()
    {
        var rng = new Random(13013);
        string[] round = { "ooo", "soc", "cocos", "ộ", "Ộ", "ǒ", "Ǒ", "õ", "Ö", "Ő", "の", "ぬ", "oo", "coco", "Ở", "ỗ", "Ỗ" };
        int saved = 0;
        foreach (var (w, h) in new[] { (2560, 1440), (3840, 2160), (1920, 1080) })
            for (int n = 0; n < 14; n++)
            {
                var motion = new[] { LyricsMotion.Glow, LyricsMotion.AppleMusic, LyricsMotion.Float, LyricsMotion.Snappy }[rng.Next(4)];
                double size = new[] { 80.0, 60, 36, 48 }[rng.Next(4)];
                var lines = new List<DisplayLine>();
                long t = 2000;
                int cnt = rng.Next(4, 12);
                for (int i = 0; i < cnt; i++)
                {
                    var text = string.Join(' ', Enumerable.Range(0, rng.Next(1, 6)).Select(_ => round[rng.Next(round.Length)]));
                    var main = Karaoke.SpreadWords(text, t, t + rng.Next(1500, 4000));
                    main.Estimated = false;
                    var sub = rng.Next(2) == 0 ? Karaoke.SpreadWords(string.Join(' ', Enumerable.Range(0, 3).Select(_ => round[rng.Next(3)])), main.Start, main.End) : null;
                    lines.Add(new DisplayLine { Main = main, Sub = sub, Align = rng.Next(4) == 0 ? LineAlign.Trailing : LineAlign.Leading });
                    t = main.End + (rng.Next(5) == 0 ? rng.Next(6500, 12000) : rng.Next(0, 1500));
                }
                int target = rng.Next(lines.Count);
                long now = rng.Next(3) switch
                {
                    0 => lines[target].Main.Words[^1].End + 30,
                    1 => (lines[target].Main.Start + lines[target].Main.End) / 2,
                    _ => lines[target].Main.Start + 5,
                };
                string repro = $"{w}x{h} motion={motion} size={size} t={now} lines={string.Join(" / ", lines.Select(l => $"{l.Start}-{l.End} {Esc(l.Main.Text)}"))}";
                Safe("l-page", () => repro, () =>
                {
                    var page = new LyricsPageView { Width = w, Height = h };
                    page.FontSize = size;
                    page.Motion = LookPresets.Motion(motion);
                    long tt = now;
                    page.SetClock(() => tt);
                    page.SetLyrics(lines, true, t + 5000, null);
                    page.Measure(new Size(w, h)); page.Arrange(new Rect(0, 0, w, h)); page.UpdateLayout();
                    var px = Pixels(page, w, h);
                    // Active line vs timeline.
                    var active = (int)typeof(LyricsPageView).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
                    var blocks = (System.Collections.IList)typeof(LyricsPageView).GetField("_blocks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
                    int wantLine = -1;
                    for (int i = 0; i < lines.Count; i++) if (lines[i].Start <= now) wantLine = i;
                    object? blk = active >= 0 && active < blocks.Count ? blocks[active] : null;
                    var line = blk?.GetType().GetField("Line")!.GetValue(blk) as DisplayLine;
                    bool inBreak = blk != null && line == null;
                    long prevEnd = wantLine >= 0 ? lines[wantLine].End : 0;
                    bool okActive = line == (wantLine >= 0 ? lines[wantLine] : null) || (inBreak && wantLine >= 0 && wantLine + 1 < lines.Count && lines[wantLine + 1].Start - prevEnd > 6000 && now >= prevEnd + 400);
                    C("l-page-active", okActive, () => $"active block {active} ({(inBreak ? "break" : line == null ? "none" : "line " + lines.IndexOf(line))}) want line {wantLine} | {repro}");
                    // Straight cut edges in the glyph / glow ink.
                    int need = (int)Math.Max(12, size * 0.3);
                    int worst = 0; string where = "";
                    for (int y = 12; y < h - 12; y++)
                    {
                        int runTop = 0, runBot = 0;
                        for (int x = 0; x < w; x++)
                        {
                            int a = px[(y * w + x) * 4 + 3], up = px[((y - 1) * w + x) * 4 + 3], dn = px[((y + 1) * w + x) * 4 + 3];
                            runTop = a >= 12 && up == 0 ? runTop + 1 : 0;
                            runBot = a >= 12 && dn == 0 ? runBot + 1 : 0;
                            int run = Math.Max(runTop, runBot);
                            if (run > worst) { worst = run; where = $"horizontal edge at y={y} ending x={x} (alpha {a} next to 0)"; }
                        }
                    }
                    C("l-page-glow-clip", worst < need, () => $"straight cut {worst}px >= {need}px: {where} | {repro}");
                    if (worst >= need && saved < 4) SaveImage(page, w, h, Path.Combine(_reportDir, $"page_fail_{saved++}.png"));
                });
            }
    }

    // =============================================================== m) web JSON builders
    static void WebTests()
    {
        var rng = new Random(14014);
        var bf = BindingFlags.NonPublic | BindingFlags.Instance;
        for (int n = 0; n < 300; n++)
        {
            var app = (AppController)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AppController));
            var settings = new AppSettings();
            typeof(AppController).GetField("<Settings>k__BackingField", bf)!.SetValue(app, settings);
            typeof(AppController).GetField("_smoother", bf)!.SetValue(app, new ClockSmoother());
            var watcher = (LycRomanise.Media.MediaWatcher)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LycRomanise.Media.MediaWatcher));
            bool playing = rng.Next(2) == 0;
            long pos = rng.Next(0, 200000);
            string title = rng.Next(5) == 0 ? "Bad \uD800 surrogate \"quote\"" : "Song " + rng.Next(100);
            var state = new MediaState("spotify", title, "Artist", "Album", 240000, pos, DateTime.UtcNow, playing, 1);
            typeof(LycRomanise.Media.MediaWatcher).GetField("_last", bf)!.SetValue(watcher, state);
            typeof(AppController).GetField("<Media>k__BackingField", bf)!.SetValue(app, watcher);
            var lines = new List<DisplayLine>();
            int cnt = rng.Next(0, 8);
            for (int i = 0; i < cnt; i++)
            {
                var text = RandText(rng, false) + (rng.Next(6) == 0 ? " \uDC00broken" : "") + (rng.Next(6) == 0 ? " </script>\"\\" : "");
                lines.Add(new DisplayLine { Main = Karaoke.SpreadWords(text, i * 3000, i * 3000 + 2500), Sub = rng.Next(2) == 0 ? Karaoke.SpreadWords("sub", i * 3000, i * 3000 + 2500) : null, Translation = rng.Next(3) == 0 ? "tr" : null });
            }
            typeof(AppController).GetField("_display", bf)!.SetValue(app, lines);
            typeof(AppController).GetField("_result", bf)!.SetValue(app, new LyricsResult { Lines = lines.Select(l => l.Main).ToList(), Timing = Timing.Word, Provider = "p" });
            var web = new LycRomanise.Web.WebServer(app);
            string repro = $"title={Esc(title)} lines={string.Join(" / ", lines.Select(l => Esc(l.Main.Text)))}";
            Safe("m-web-lyrics", () => repro, () =>
            {
                web.Notify(true);
                var json = (string)typeof(LycRomanise.Web.WebServer).GetField("_lyricsJson", bf)!.GetValue(web)!;
                var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.GetProperty("lines");
                bool ok = arr.GetArrayLength() == lines.Count;
                for (int i = 0; ok && i < lines.Count; i++)
                {
                    var m = arr[i].GetProperty("m");
                    if (m.GetProperty("w").GetArrayLength() != lines[i].Main.Words.Count || m.GetProperty("s").GetInt64() != lines[i].Main.Start) ok = false;
                    if ((lines[i].Sub == null) != (arr[i].GetProperty("sub").ValueKind == JsonValueKind.Null)) ok = false;
                }
                C("m-web-lyrics", ok, () => "lyrics JSON does not match lines | " + repro);
            });
            Safe("m-web-state", () => repro, () =>
            {
                var json = (string)typeof(LycRomanise.Web.WebServer).GetMethod("StateJson", bf)!.Invoke(web, null)!;
                var doc = JsonDocument.Parse(json);
                long got = doc.RootElement.GetProperty("pos").GetInt64();
                C("m-web-state", doc.RootElement.GetProperty("playing").GetBoolean() == playing && Math.Abs(got - pos) < 200 && doc.RootElement.GetProperty("dur").GetInt64() == 240000,
                    () => $"state pos {got} want ~{pos} | {repro}");
            });
        }
    }

    static SelfTestFuzz() { }

    public static void SetOut(string dir) => _reportDir = dir;
}
