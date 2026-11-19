using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LycRomanise.Core;
using LycRomanise.Romanize;
using LycRomanise.Sources;
using LycRomanise.UI;

namespace LycRomanise;

/// <summary>`--selftest &lt;dir&gt; deep`: hundreds of generated cases per area (seeded, so a failure can be
/// reproduced), plus performance and memory measurements.</summary>
static class SelfTestDeep
{
    static readonly StringBuilder R = new();
    static readonly Dictionary<string, (int pass, int fail)> Areas = new();
    static string _area = "";
    static int _shownFails;
    static string _out = "";

    static void Area(string name) { _area = name; _shownFails = 0; Areas.TryAdd(name, (0, 0)); R.AppendLine().AppendLine("== " + name); }
    static void Check(string name, bool ok, string detail = "")
    {
        var (p, f) = Areas[_area];
        Areas[_area] = ok ? (p + 1, f) : (p, f + 1);
        if (!ok && _shownFails++ < 25) R.AppendLine($"FAIL  {name}  -> {detail}");
    }
    static void Info(string s) => R.AppendLine("      " + s);
    static void Try(string name, Action a)
    {
        try { a(); } catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
    }

    public static Task RunAsync(string outDir)
    {
        _out = outDir; Directory.CreateDirectory(outDir);
        var total = Stopwatch.StartNew();
        RomanizeKorean(); RomanizeJapanese(); RomanizeChinese(); RomanizeOther();
        LrcFuzz(); TtmlFuzz(); PiecesFuzz();
        Matching();
        EverySetting();
        StripRendering();
        PageRendering();
        Performance();
        var head = new StringBuilder($"LycRomanise deep self-test {DateTime.Now:yyyy-MM-dd HH:mm}  ({total.Elapsed.TotalSeconds:0} s)\n");
        foreach (var (a, (p, f)) in Areas) head.AppendLine($"  {a,-34} {p + f,5} tests  {p,5} pass  {f,4} fail");
        head.AppendLine($"  {"TOTAL",-34} {Areas.Values.Sum(x => x.pass + x.fail),5} tests  {Areas.Values.Sum(x => x.pass),5} pass  {Areas.Values.Sum(x => x.fail),4} fail");
        File.WriteAllText(Path.Combine(outDir, "deep.txt"), head.ToString() + R);
        return Task.CompletedTask;
    }

    static string Clean(string s) => s;
    static bool Latinish(string s) => !Romanizer.Needs(s) && !s.Contains('�');

    // ------------------------------------------------------------------ romanization
    static void RomanizeKorean()
    {
        Area("Romanization: Korean");
        var rng = new Random(1);
        for (int i = 0; i < 600; i++)
        {
            int len = rng.Next(1, 9);
            var sb = new StringBuilder();
            for (int k = 0; k < len; k++) { sb.Append((char)(0xAC00 + rng.Next(11172))); if (rng.Next(6) == 0) sb.Append(' '); }
            if (rng.Next(5) == 0) sb.Append(" baby");
            var text = sb.ToString().Trim();
            Try($"ko {text}", () =>
            {
                var r = Romanizer.Line(text, "ko");
                bool ok = Latinish(r) && r.Length >= text.Replace(" ", "").Length && r.Length <= text.Length * 7 + 6
                          && (!text.Contains("baby") || r.Contains("baby"))
                          && r.All(c => c < 128 || char.IsLetter(c));
                Check($"ko {text}", ok, $"\"{r}\"");
            });
        }
        // Every syllable on its own reads to something.
        int bad = 0; string? example = null;
        for (int c = 0xAC00; c <= 0xD7A3; c++)
        {
            var r = Romanizer.Line(((char)c).ToString(), "ko");
            if (r.Length == 0 || !Latinish(r)) { bad++; example ??= $"{(char)c}->\"{r}\""; }
        }
        Check("all 11,172 Hangul syllables romanize", bad == 0, $"{bad} bad, e.g. {example}");
    }

    static readonly string Kana = "あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをんがぎぐげござじずぜぞだぢづでどばびぶべぼぱぴぷぺぽゃゅょっアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヲンガギグゲゴヴァィゥェォャュョッー";
    static readonly string Kanji = "夢見空心愛君僕私夜星光花風雨涙声歌恋時間世界明日今日未来永遠笑顔名前手目耳口足道街海山川月雪春夏秋冬朝昼晩会話思想言行来帰走歩飛泣怒嬉楽悲";

    static void RomanizeJapanese()
    {
        Area("Romanization: Japanese");
        var rng = new Random(2);
        for (int i = 0; i < 600; i++)
        {
            int len = rng.Next(1, 14);
            var sb = new StringBuilder();
            for (int k = 0; k < len; k++) sb.Append(rng.Next(3) == 0 ? Kanji[rng.Next(Kanji.Length)] : Kana[rng.Next(Kana.Length)]);
            if (rng.Next(6) == 0) sb.Append(" love");
            if (rng.Next(8) == 0) sb.Insert(0, "「").Append("」");
            var text = sb.ToString();
            Try($"ja {text}", () =>
            {
                // Half the cases on a thread-pool thread, as the app prepares lyrics in the background.
                var r = i % 2 == 0 ? Romanizer.Line(text, "ja") : Task.Run(() => Romanizer.Line(text, "ja")).Result;
                bool ok = Latinish(r) && r.Length > 0 && r.Length <= text.Length * 8 + 6 && (!text.Contains("love") || r.Contains("love"));
                Check($"ja {text}", ok, $"\"{r}\"");
            });
        }
    }

    static void RomanizeChinese()
    {
        Area("Romanization: Chinese");
        var rng = new Random(3);
        var vowels = "aeiouüāáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜ";
        for (int i = 0; i < 600; i++)
        {
            int len = rng.Next(1, 12);
            var sb = new StringBuilder();
            const string common = "的一是不了人我在有他这为之大来以个中上们到说国和地也子时道出而要于就下得可你年生自会那后能对着事其里所去行过家十用发天如然作方成者多日都三小军二无同么经法当起与好看学进种将还分此心前面又定见只主没公从爱情梦想月光风花雪夜星海天空回忆眼泪微笑等待永远离开相信世界未来时间";
            for (int k = 0; k < len; k++) sb.Append(common[rng.Next(common.Length)]);
            var text = sb.ToString();
            Try($"zh {text}", () =>
            {
                var r = Romanizer.Line(text, "zh");
                var sylls = r.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool ok = Latinish(r) && sylls.Length >= 1 && sylls.All(s => s.Any(c => vowels.Contains(char.ToLowerInvariant(c)) || "mn".Contains(s)) || s.Length > 0);
                Check($"zh {text}", ok, $"\"{r}\"");
            });
        }
    }

    static void RomanizeOther()
    {
        Area("Romanization: other scripts & mixed");
        var rng = new Random(4);
        string[] pools = { "абвгдеёжзийклмнопрстуфхцчшщъыьэюяАБВГД", "αβγδεζηθικλμνξοπρστυφχψωάέήίόύώ", "ابتثجحخدذرزسشصضطظعغفقكلمنهوي", "אבגדהוזחטיכלמנסעפצקרשת", "कखगघचछजझटठडढणतथदधनपफबभमयरलवशसह" , "사랑夢を你好love123!?…♪💕 " };
        for (int i = 0; i < 600; i++)
        {
            var pool = pools[i % pools.Length];
            var sb = new StringBuilder();
            int len = rng.Next(1, 16);
            for (int k = 0; k < len; k++) { sb.Append(pool[rng.Next(pool.Length)]); if (rng.Next(5) == 0) sb.Append(' '); }
            var text = sb.ToString();
            Try($"other {text}", () =>
            {
                var hint = Romanizer.DetectScript(text);
                var r = Romanizer.Line(text, hint);
                bool ok = r != null && !r.Contains('�') && r.Length <= text.Length * 8 + 8 && (i % pools.Length == 5 || Latinish(r) || r.Trim().Length == 0 || r.All(c => !char.IsLetter(c) || c < 0x0250 || char.IsSymbol(c)));
                Check($"other {text}", ok, $"\"{r}\"");
            });
        }
    }

    // ------------------------------------------------------------------ parsers
    static string Stamp(Random rng, long ms) => rng.Next(4) switch
    {
        0 => $"[{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000 / 10:00}]",
        1 => $"[{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}]",
        2 => $"[{ms / 60000}:{ms / 1000 % 60:00}]",
        _ => $"[{ms / 60000:00}:{ms / 1000 % 60:00}:{ms % 1000 / 10:00}]",
    };

    static void LrcFuzz()
    {
        Area("Parser: LRC (fuzzed)");
        var rng = new Random(5);
        string[] junk = { "", "[ar:x]", "[offset:-250]", "[ti:y]", "<00:01.00>", "﻿", "\r", "[]", "[xx:yy]", "作词 : 某人", "   " };
        for (int i = 0; i < 600; i++)
        {
            var sb = new StringBuilder();
            if (rng.Next(4) == 0) sb.Append('﻿');
            int n = rng.Next(0, 40);
            long t = rng.Next(0, 5000);
            for (int k = 0; k < n; k++)
            {
                if (rng.Next(7) == 0) sb.Append(junk[rng.Next(junk.Length)]).Append('\n');
                t += rng.Next(0, 6000);
                var stamp = Stamp(rng, t);
                if (rng.Next(9) == 0) stamp += Stamp(rng, t + 30000);
                var words = string.Join(' ', Enumerable.Range(0, rng.Next(0, 6)).Select(_ => "w" + rng.Next(100)));
                if (rng.Next(4) == 0) words = string.Join(" ", words.Split(' ').Select((w, j) => $"<{Stamp(rng, t + j * 300)[1..^1]}>{w}"));
                sb.Append(stamp).Append(words).Append(rng.Next(3) == 0 ? "\r\n" : "\n");
            }
            if (rng.Next(10) == 0) sb.Remove(sb.Length / 2, sb.Length / 4);
            var text = sb.ToString();
            Try($"lrc #{i}", () =>
            {
                var lines = Formats.ParseLrc(text);
                bool ok = lines == null || (lines.All(l => l.End >= l.Start && l.Start >= 0 && l.Words.Count > 0 && l.Words.All(w => w.End >= w.Start))
                                            && lines.Zip(lines.Skip(1)).All(p => p.First.Start <= p.Second.Start));
                Check($"lrc #{i}", ok, lines == null ? "" : string.Join(" | ", lines.Take(4).Select(l => $"{l.Start}-{l.End} {l.Text}")));
            });
        }
    }

    static void TtmlFuzz()
    {
        Area("Parser: TTML (fuzzed)");
        var rng = new Random(6);
        for (int i = 0; i < 600; i++)
        {
            var sb = new StringBuilder("<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"><body><div>");
            double t = rng.NextDouble() * 5;
            int n = rng.Next(0, 25);
            for (int k = 0; k < n; k++)
            {
                double b = t, e = t + rng.NextDouble() * 4;
                t = rng.Next(5) == 0 ? t - 1 : e;
                string agent = rng.Next(3) == 0 ? $" ttm:agent=\"v{rng.Next(1, 3)}\"" : "";
                sb.Append($"<p begin=\"{b:0.000}\" end=\"{e:0.000}\"{agent}>");
                int words = rng.Next(0, 5);
                double wt = b;
                for (int w = 0; w < words; w++)
                {
                    double we = wt + rng.NextDouble();
                    string bg = rng.Next(6) == 0 ? " ttm:role=\"x-bg\"" : "";
                    sb.Append($"<span begin=\"{wt:0.000}\" end=\"{we:0.000}\"{bg}>{(rng.Next(4) == 0 ? "&amp;" : "")}w{rng.Next(99)}</span>");
                    if (rng.Next(2) == 0) sb.Append(' ');
                    wt = we;
                }
                if (words == 0) sb.Append("text " + k);
                sb.Append("</p>");
            }
            sb.Append("</div></body></tt>");
            var xml = sb.ToString();
            if (rng.Next(8) == 0) xml = xml[..rng.Next(xml.Length)];
            Try($"ttml #{i}", () =>
            {
                var lines = Formats.ParseTtml(xml);
                bool ok = lines == null || lines.All(l => l.End >= l.Start && l.Start >= 0 && l.Words.All(w => w.End >= w.Start));
                Check($"ttml #{i}", ok, lines == null ? "" : string.Join(" | ", lines.Take(3).Select(l => $"{l.Start}-{l.End} {l.Text}")));
            });
        }
    }

    static void PiecesFuzz()
    {
        Area("Parser: yrc/krc (fuzzed)");
        var rng = new Random(7);
        for (int i = 0; i < 600; i++)
        {
            bool krc = i % 2 == 1;
            var sb = new StringBuilder();
            long t = 0;
            for (int k = 0; k < rng.Next(0, 20); k++)
            {
                long len = rng.Next(0, 5000);
                sb.Append($"[{t},{len}]");
                long wt = 0;
                for (int w = 0; w < rng.Next(0, 6); w++)
                {
                    long wl = rng.Next(0, 800);
                    sb.Append(krc ? $"<{wt},{wl},0>w{w}" : $"({t + wt},{wl},0)w{w}");
                    wt += wl;
                }
                sb.Append('\n');
                t += len + rng.Next(-500, 1500);
            }
            var body = sb.ToString();
            if (rng.Next(6) == 0) body = body.Replace("]", "").Replace(",", ";");
            Try($"pieces #{i}", () =>
            {
                var lines = Formats.ParsePieces(body, krc);
                bool ok = lines == null || lines.All(l => l.End >= l.Start && l.Words.All(w => w.End >= w.Start));
                Check($"pieces #{i}", ok, lines == null ? "" : string.Join(" | ", lines.Take(3).Select(l => $"{l.Start}-{l.End} {l.Text}")));
            });
        }
    }

    // ------------------------------------------------------------------ matching
    static void Matching()
    {
        Area("Title & artist matching");
        var rng = new Random(8);
        string[] titles = { "YOLO", "Whiplash", "Hype Boy", "Love Dive", "Dynamite", "Anti-Hero", "Lemon", "Pretender", "Spicy", "Drama", "Ditto", "Butter", "Next Level", "Savage", "Black Mamba", "Super Shy", "OMG", "After LIKE", "Kitsch", "Antifragile" };
        string[] suffixes = { "", " - Remastered 2011", " (feat. Someone)", " [feat. A & B]", " - Live", " - Radio Edit", " (with X)", " - 2020 Remaster" };
        for (int i = 0; i < 500; i++)
        {
            var t = titles[rng.Next(titles.Length)];
            var v = (rng.Next(3) switch { 0 => t.ToUpperInvariant(), 1 => t.ToLowerInvariant(), _ => t }) + suffixes[rng.Next(suffixes.Length)];
            Check($"same title \"{v}\" ~ \"{t}\"", Normalize.SameTitle(v, t), "");
        }
        for (int i = 0; i < 500; i++)
        {
            var a = titles[rng.Next(titles.Length)]; var b = titles[rng.Next(titles.Length)];
            if (a == b) continue;
            Check($"different titles \"{a}\" vs \"{b}\"", !Normalize.SameTitle(a, b), "");
        }
        string[] artists = { "aespa", "NewJeans", "IVE", "LE SSERAFIM", "BTS", "TWICE", "IU", "V", "Jung Kook", "Stray Kids", "YOASOBI", "Ado", "Taylor Swift" };
        for (int i = 0; i < 300; i++)
        {
            var a = artists[rng.Next(artists.Length)];
            var credited = rng.Next(3) switch { 0 => a.ToUpperInvariant(), 1 => a + " feat. Other", _ => a };
            Check($"artist \"{credited}\" ~ \"{a}\"", Normalize.SameArtist(a, credited) || Normalize.SameArtist(credited, a), "");
            var other = artists[rng.Next(artists.Length)];
            if (other != a && !a.Contains(other) && !other.Contains(a)) Check($"artist \"{other}\" !~ \"{a}\"", !Normalize.SameArtist(other, a), "");
        }
    }

    // ------------------------------------------------------------------ every setting
    static IEnumerable<object?> ValuesFor(PropertyInfo p, Random rng)
    {
        var t = p.PropertyType;
        if (t == typeof(bool)) { yield return true; yield return false; }
        else if (t.IsEnum) { foreach (var v in Enum.GetValues(t)) yield return v; }
        else if (t == typeof(int)) { foreach (var v in new[] { int.MinValue, -1, 0, 1, 50, 9000, int.MaxValue }) yield return v; }
        else if (t == typeof(double)) { foreach (var v in new[] { double.NaN, double.NegativeInfinity, -5.0, 0.0, 0.5, 1.0, 40.0, 99999.0 }) yield return v; }
        else if (t == typeof(string)) { foreach (var v in new[] { "", "#FFFFFFFF", "#12", "red", "notacolour", new string('x', 300), "Segoe UI", "Malgun Gothic" }) yield return v; }
        else if (t == typeof(double?)) { yield return null; yield return 100.0; yield return -99999.0; }
    }

    static void EverySetting()
    {
        Area("Every setting, every value");
        var rng = new Random(9);
        var props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.CanRead).ToList();
        var sample = Sample();
        int count = 0;
        foreach (var p in props)
            foreach (var v in ValuesFor(p, rng))
            {
                Try($"{p.Name}={Short(v)}", () =>
                {
                    var s = new AppSettings();
                    p.SetValue(s, v);
                    var copy = s.Copy();                                   // JSON round trip
                    var style = OverlayWindow.StyleFor(copy);
                    var d = AppController.Prepare(sample, copy);
                    var json = System.Text.Json.JsonSerializer.Serialize(copy);
                    bool ok = d.Count == sample.Lines.Count && style.FontSize > 0 && !double.IsNaN(style.FontSize) && json.Length > 100;
                    Check($"{p.Name}={Short(v)}", ok, $"font={style.FontSize} lines={d.Count}");
                });
                count++;
            }
        // Random combinations of many settings at once.
        for (int i = 0; i < 300; i++)
        {
            var s = new AppSettings();
            var changed = new List<string>();
            foreach (var p in props.OrderBy(_ => rng.Next()).Take(12))
            {
                var vals = ValuesFor(p, rng).ToList();
                if (vals.Count == 0) continue;
                var v = vals[rng.Next(vals.Count)];
                try { p.SetValue(s, v); changed.Add($"{p.Name}={Short(v)}"); } catch { }
            }
            Try($"combo #{i}", () =>
            {
                var c = s.Copy();
                var d = AppController.Prepare(sample, c);
                var st = OverlayWindow.StyleFor(c);
                Check($"combo #{i}", d.Count == sample.Lines.Count && st.FontSize > 0, string.Join(", ", changed));
            });
        }
        Info($"{props.Count} settings, {count} single-value cases, 300 random combinations");
    }

    static string Short(object? v) { var s = v?.ToString() ?? "null"; return s.Length > 20 ? s[..20] + "…" : s; }

    static LyricsResult Sample()
    {
        LyricLine L(long s, long e, params string[] words)
        {
            var l = new LyricLine { Start = s, End = e };
            long step = (e - s) / Math.Max(1, words.Length);
            for (int i = 0; i < words.Length; i++) l.Words.Add(new LyricWord { Text = words[i], Start = s + i * step, End = s + (i + 1) * step });
            return l;
        }
        var lines = new List<LyricLine>
        {
            L(1000, 4000, "별빛", "아래", "우리", "춤을", "춰"),
            L(4000, 7000, "Can't", "touch", "that", "baby"),
            L(7000, 10000, "夢を", "見た", "夜空に"),
            L(10000, 13000, "我", "爱", "你"),
            L(13000, 16000, "이건", "정말", "아주", "아주", "아주", "긴", "가사", "줄이라서", "화면을", "넘어갈지도", "몰라", "어떻게", "보일까", "궁금해", "정말로"),
            L(16000, 19000, "💕", "✨", "사랑해", "🎵"),
            L(19000, 22000, "Ágħ", "Ñoño", "Ýjqgp"),          // accents and descenders, for clipping checks
        };
        lines[1].Align = LineAlign.Trailing;
        lines[2].Backing = L(8000, 10000, "(Oh", "oh)");
        lines[3].Translation = "I love you";
        return new LyricsResult { Provider = "test", SourceKey = "test", Lines = lines, Timing = Core.Timing.Word };
    }

    // ------------------------------------------------------------------ rendering
    static RenderTargetBitmap Draw(FrameworkElement v, int w, int h)
    {
        v.Measure(new Size(w, h)); v.Arrange(new Rect(0, 0, w, h)); v.UpdateLayout();
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        return bmp;
    }

    /// <summary>Whether any drawn pixel touches the given edge rows/columns (text cut by the bounds).</summary>
    static (bool top, bool bottom, bool left, bool right, int ink) Edges(RenderTargetBitmap bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        bool Ink(int x, int y) => px[(y * w + x) * 4 + 3] > 40;
        bool Faint(int i) => px[i] > 12;
        bool top = false, bottom = false, left = false, right = false; int ink = 0;
        for (int x = 0; x < w; x++) { top |= Ink(x, 0); bottom |= Ink(x, h - 1); }
        for (int y = 0; y < h; y++) { left |= Ink(0, y); right |= Ink(w - 1, y); }
        for (int i = 3; i < px.Length; i += 16) if (Faint(i)) ink++;
        return (top, bottom, left, right, ink);
    }

    static void Save(RenderTargetBitmap bmp, string file)
    {
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(_out, file)); enc.Save(f);
    }

    static void StripRendering()
    {
        Area("Strip rendering (no clipping, every combination)");
        var sample = Sample();
        var rng = new Random(10);
        long[] times = { 2500, 5500, 8500, 11500, 14500, 17500, 20500, 26000 };
        int saved = 0;
        for (int i = 0; i < 560; i++)
        {
            var preset = LookPresets.All[i % LookPresets.All.Length];
            var s = new AppSettings(); preset.Apply(s);
            s.Display = (DisplayMode)(i / 9 % 3);
            s.StripStyle = (StripStyle)(i / 27 % 3);
            s.Align = (StripAlign)(i / 81 % 3);
            s.FontSize = new[] { 18, 26, 34, 48, 72 }[rng.Next(5)];
            s.ShowTranslation = rng.Next(2) == 0; s.ShowBackingVocals = rng.Next(3) > 0;
            s.WordLift = rng.Next(0, 8); s.WordGlow = rng.Next(2) == 0;
            int width = new[] { 420, 700, 900, 1400 }[rng.Next(4)];
            long t = times[rng.Next(times.Length)];
            string name = $"{preset.Name}/{s.Display}/{s.StripStyle}/{s.Align}/{s.FontSize}px/w{width}/t{t}";
            Try(name, () =>
            {
                var view = new KaraokeView { Width = width };
                view.SetStyle(OverlayWindow.StyleFor(s));
                long now = t; view.SetClock(() => now);
                view.SetLyrics(AppController.Prepare(sample, s), true, 25000, null);
                int h = (int)Math.Ceiling(view.DesiredStripHeight());
                Draw(view, width, h);
                // Skip the line's entrance animation (as if it started a while ago), then check the settled frame.
                typeof(KaraokeView).GetField("_transitionAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, DateTime.UtcNow.AddSeconds(-5));
                view.InvalidateVisual();
                var bmp = Draw(view, width, h);
                var e = Edges(bmp);
                bool cut = e.top || e.bottom || e.left || e.right;
                Check(name, !cut && e.ink > 0, $"touches edge top={e.top} bottom={e.bottom} left={e.left} right={e.right} ink={e.ink}");
                if (cut && saved++ < 12) Save(bmp, $"cut_{saved}_{preset.Name.Replace(" ", "")}_{s.StripStyle}_{s.FontSize}_{width}.png");
            });
        }
    }

    static void PageRendering()
    {
        Area("Full lyrics page (motion × size × time)");
        var sample = Sample();
        var rng = new Random(11);
        int saved = 0;
        for (int i = 0; i < 520; i++)
        {
            var motion = (LyricsMotion)(i % 5);
            var s = new AppSettings { Display = (DisplayMode)(i / 5 % 3), ShowTranslation = i % 2 == 0 };
            int w = new[] { 520, 900, 1200, 1900 }[rng.Next(4)], h = new[] { 420, 700, 1000 }[rng.Next(3)];
            double font = new[] { 22, 36, 48, 70, 90 }[rng.Next(5)];
            long t = new long[] { 0, 2500, 5000, 9000, 11500, 14500, 17500, 20500, 30000 }[rng.Next(9)];
            string name = $"{motion}/{s.Display}/{w}x{h}/{font}px/t{t}";
            Try(name, () =>
            {
                var page = new LyricsPageView { Width = w, Height = h, FontSize = font, Motion = LookPresets.Motion(motion), ShowTranslation = s.ShowTranslation };
                long now = t; page.SetClock(() => now);
                page.SetLyrics(AppController.Prepare(sample, s), true, 25000, null);
                Draw(page, w, h);
                page.InvalidateVisual();
                var bmp = Draw(page, w, h);
                var e = Edges(bmp);
                bool sideCut = e.left || e.right;      // top and bottom fade off by design
                Check(name, e.ink > 0 && !sideCut, $"ink={e.ink} left={e.left} right={e.right}");
                if ((sideCut || e.ink == 0) && saved++ < 8) Save(bmp, $"pagecut_{saved}_{motion}_{w}x{h}_{font}.png");
                if (i < 5) Save(bmp, $"page_{motion}.png");
            });
        }
    }

    // ------------------------------------------------------------------ performance
    static void Performance()
    {
        Area("Performance");
        var sample = Sample();
        // A long song: 120 lines with word timing.
        var big = new LyricsResult { Timing = Core.Timing.Word };
        var rng = new Random(12);
        for (int i = 0; i < 120; i++)
        {
            var l = new LyricLine { Start = i * 2500, End = i * 2500 + 2400 };
            for (int w = 0; w < 8; w++) l.Words.Add(new LyricWord { Text = rng.Next(3) == 0 ? "사랑해" : rng.Next(2) == 0 ? "夢を見た" : "word" + w, Start = l.Start + w * 300, End = l.Start + w * 300 + 300 });
            big.Lines.Add(l);
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) AppController.Prepare(big, new AppSettings { Display = DisplayMode.OriginalAndRomanized });
        double prep = sw.Elapsed.TotalMilliseconds / 20;
        Info($"prepare a 120-line song (romanize all): {prep:0.0} ms");
        Check("prepare a 120-line song under 400 ms", prep < 400, $"{prep:0} ms");

        sw.Restart();
        int n = 0;
        for (int i = 0; i < 300; i++) { Romanizer.Line("별빛 아래 우리 춤을 춰 너와 함께 이 밤을", "ko"); n++; }
        double ko = sw.Elapsed.TotalMilliseconds / n;
        Info($"romanize a Korean line: {ko:0.000} ms");
        Check("Korean line under 2 ms", ko < 2, $"{ko:0.00} ms");
        sw.Restart();
        for (int i = 0; i < 100; i++) Romanizer.Line("夜空に光る夢を見た君と歌うこの歌" + i, "ja");
        double ja = sw.Elapsed.TotalMilliseconds / 100;
        Info($"romanize a Japanese line: {ja:0.00} ms");
        Check("Japanese line under 25 ms", ja < 25, $"{ja:0.0} ms");
        sw.Restart();
        for (int i = 0; i < 300; i++) Romanizer.Line("你問我愛你有多深我愛你有幾分", "zh");
        double zh = sw.Elapsed.TotalMilliseconds / 300;
        Info($"romanize a Chinese line: {zh:0.000} ms");
        Check("Chinese line under 3 ms", zh < 3, $"{zh:0.00} ms");

        var lrc = string.Join("\n", big.Lines.Select(l => $"[{l.Start / 60000:00}:{l.Start / 1000 % 60:00}.{l.Start % 1000 / 10:00}]" + l.Text));
        sw.Restart();
        for (int i = 0; i < 200; i++) Formats.ParseLrc(lrc);
        double parse = sw.Elapsed.TotalMilliseconds / 200;
        Info($"parse a 120-line LRC: {parse:0.00} ms");
        Check("parse LRC under 10 ms", parse < 10, $"{parse:0.0} ms");

        // Strip: time per frame across looks, mid-song.
        foreach (var preset in LookPresets.All)
        {
            var s = new AppSettings(); preset.Apply(s); s.WordLift = 4; s.WordGlow = true;
            var view = new KaraokeView { Width = 1000 };
            view.SetStyle(OverlayWindow.StyleFor(s));
            long now = 5000; view.SetClock(() => now);
            view.SetLyrics(AppController.Prepare(big, s), true, 300000, null);
            int h = (int)view.DesiredStripHeight();
            view.Measure(new Size(1000, h)); view.Arrange(new Rect(0, 0, 1000, h));
            var bmp = new RenderTargetBitmap(1000, h, 96, 96, PixelFormats.Pbgra32);
            for (int i = 0; i < 10; i++) { now += 16; view.InvalidateVisual(); bmp.Render(view); }
            KaraokeView.RenderMs = 0; KaraokeView.RenderCount = 0;
            int frames = 120;
            for (int i = 0; i < frames; i++) { now += 16; view.InvalidateVisual(); view.Measure(new Size(1000, h)); view.Arrange(new Rect(0, 0, 1000, h)); view.UpdateLayout(); bmp.Clear(); bmp.Render(view); }
            double ms = KaraokeView.RenderMs / Math.Max(1, KaraokeView.RenderCount);
            Info($"strip frame ({preset.Name}): {ms:0.000} ms of drawing work per frame");
            Check($"strip frame {preset.Name} under 4 ms", ms < 4, $"{ms:0.00} ms");
        }

        // Full lyrics page: time per frame per motion.
        foreach (LyricsMotion motion in Enum.GetValues<LyricsMotion>())
        {
            var page = new LyricsPageView { Width = 1200, Height = 800, FontSize = 46, Motion = LookPresets.Motion(motion) };
            long now = 40000; page.SetClock(() => now);
            page.SetLyrics(AppController.Prepare(big, new AppSettings()), true, 300000, null);
            page.Measure(new Size(1200, 800)); page.Arrange(new Rect(0, 0, 1200, 800));
            var bmp = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
            for (int i = 0; i < 10; i++) { now += 16; page.InvalidateVisual(); bmp.Render(page); }
            LyricsPageView.RenderMs = 0; LyricsPageView.RenderCount = 0;
            int frames = 90;
            for (int i = 0; i < frames; i++) { now += 25; page.InvalidateVisual(); page.Measure(new Size(1200, 800)); page.Arrange(new Rect(0, 0, 1200, 800)); page.UpdateLayout(); bmp.Clear(); bmp.Render(page); }
            double ms = LyricsPageView.RenderMs / Math.Max(1, LyricsPageView.RenderCount);
            Info($"lyrics page frame ({motion}): {ms:0.000} ms of drawing work per frame");
            Check($"lyrics page frame {motion} under 6 ms", ms < 6, $"{ms:0.00} ms");
        }

        // Memory: thousands of redraws and song changes should not grow without bound.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        {
            var view = new KaraokeView { Width = 900 };
            var s = new AppSettings();
            view.SetStyle(OverlayWindow.StyleFor(s));
            long now = 0; view.SetClock(() => now);
            var bmp = new RenderTargetBitmap(900, 200, 96, 96, PixelFormats.Pbgra32);
            view.Measure(new Size(900, 200)); view.Arrange(new Rect(0, 0, 900, 200));
            for (int song = 0; song < 40; song++)
            {
                view.SetLyrics(AppController.Prepare(big, s), true, 300000, null);
                for (int f = 0; f < 60; f++) { now = f * 4000; view.InvalidateVisual(); bmp.Clear(); bmp.Render(view); }
            }
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long after = GC.GetTotalMemory(true);
        double grew = (after - before) / 1048576.0;
        Info($"managed memory after 40 songs × 60 frames: {grew:+0.0;-0.0} MB");
        Check("no memory growth over 2,400 frames and 40 songs (< 15 MB)", grew < 15, $"{grew:0.0} MB");
    }
}
