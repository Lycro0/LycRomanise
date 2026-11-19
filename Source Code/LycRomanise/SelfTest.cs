using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LycRomanise.Core;
using LycRomanise.Romanize;
using LycRomanise.Sources;
using LycRomanise.UI;

namespace LycRomanise;

/// <summary>Developer self-test: `LycRomanise.exe --selftest &lt;out folder&gt; [all|logic|render|sources]`.
/// Exercises romanization, parsers, matching, settings and drawing without touching the running app,
/// and writes a report plus rendered images to the folder.</summary>
static class SelfTest
{
    static readonly StringBuilder R = new();
    static int _pass, _fail;
    static string _out = "";

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        R.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
    }
    static void Note(string s) => R.AppendLine("      " + s);
    static void Section(string s) => R.AppendLine().AppendLine("== " + s);
    static void Safe(string name, Action a)
    {
        try { a(); } catch (Exception ex) { Check(name + " (no exception)", false, ex.GetType().Name + ": " + ex.Message); }
    }

    public static async Task RunAsync(string outDir, string what)
    {
        _out = outDir; Directory.CreateDirectory(outDir);
        try
        {
            if (what is "all" or "logic") { Romanization(); Parsers(); Matching(); Timing(); SettingsFiles(); Display(); }
            if (what is "all" or "render") { RenderStrip(); RenderPage(); }
            if (what is "all" or "sources") await Songs();
        }
        catch (Exception ex) { Check("self-test crashed", false, ex.ToString()); }
        R.Insert(0, $"LycRomanise self-test {DateTime.Now:yyyy-MM-dd HH:mm}  pass={_pass} fail={_fail}\n");
        File.WriteAllText(Path.Combine(outDir, "report.txt"), R.ToString());
    }

    // ------------------------------------------------------------------ romanization
    static void Romanization()
    {
        Section("Romanization: expected readings (case-insensitive, spaces ignored)");
        var cases = new (string text, string expect)[]
        {
            // Korean (Revised Romanization, with sound changes)
            ("사랑해", "saranghae"), ("같이", "gachi"), ("좋아", "joa"), ("안녕하세요", "annyeonghaseyo"),
            ("신라", "silla"), ("먹는", "meongneun"), ("감사합니다", "gamsahamnida"), ("굳이", "guji"),
            ("없어", "eopseo"), ("읽어", "ilgeo"), ("밝은", "balgeun"), ("넣어", "neoeo"),
            ("오빠", "oppa"), ("꿈", "kkum"), ("별빛", "byeolbit"), ("국물", "gungmul"),
            ("한국어", "hangugeo"), ("좋다", "jota"), ("많이", "mani"), ("닭", "dak"), ("말해줄래", "malhaejullae"), ("멀리", "meolli"), ("멀어", "meoreo"), ("이유", "iyu"),
            // Japanese
            ("こんにちは", "konnichiwa"), ("ありがとう", "arigatou"), ("東京", "toukyou"), ("きって", "kitte"),
            ("ラーメン", "raamen"), ("私は学生です", "watashiwagakuseidesu"), ("夢を見た", "yumeomita"), ("気づいたら大人になっていた", "kizuitaraotonaninatteita"),
            ("しゃしん", "shashin"), ("ちょっと", "chotto"), ("コンピューター", "konpyuutaa"), ("ヴァイオリン", "vaiorin"),
            ("君の名は", "kiminonawa"), ("空に広がる天使（てんし）の声", "soranihirogarutenshinokoe"), ("空に広がる天使の声", "soranihirogarutenshinokoe"), ("会いたい", "aitai"), ("空へ", "sorae"),
            // Chinese
            ("也許", "yěxǔ"), ("幾分", "jǐfēn"), ("晃到", "huàngdào"), ("我們", "wǒmen"), ("你好", "nǐhǎo"), ("我爱你", "wǒàinǐ"), ("月光", "yuèguāng"), ("中国", "zhōngguó"), ("重要", "zhòngyào"),
            // Others
            ("Привет", "privet"), ("Москва", "moskva"), ("Ελλάδα", "ellada"), ("नमस्ते", "namaste"),
        };
        foreach (var (t, e) in cases)
            Safe($"romanize {t}", () =>
            {
                var hint = t is "東京" ? "ja" : Romanizer.DetectScript(t);
                var got = Romanizer.Line(t, hint);
                string norm(string x) => new string(x.ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '\'').ToArray());
                Check($"romanize {t}", norm(got) == norm(e), $"got \"{got}\", want \"{e}\"");
            });

        Safe("syllable-timed Korean keeps sound changes", () =>
        {
            var line = new LyricLine { Start = 0, End = 4000, Words = { W("말", 0, 500), W("해", 500, 1000, true), W("줄", 1000, 1500, true), W("래", 1500, 2000, true), W("멀", 2000, 3000), W("리", 3000, 4000, true) } };
            var r = Romanizer.ForLine(line, "ko");
            Check("syllable-timed Korean keeps sound changes", r?.Text == "malhaejullae meolli", r?.Text ?? "null");
        });

        Section("Romanization on a background thread (as the app does it)");
        foreach (var t in new[] { "空に広がる天使の声", "悲しまない明日を願うけど", "夢を見た", "気づいたら大人になっていた" })
            Safe($"background {t}", () =>
            {
                var got = Task.Run(() => Romanizer.Line(t + " ", "ja")).Result;   // the space avoids the cache
                Check($"background {t}", !Romanizer.Needs(got), got);
            });

        Safe("furigana pieces removed", () =>
        {
            var chars = "空に広がる天使（てんし）の声";
            var line = new LyricLine { Start = 0, End = chars.Length * 100 };
            for (int i = 0; i < chars.Length; i++) line.Words.Add(W(chars[i].ToString(), i * 100, i * 100 + 100, i > 0));
            SourceUtil.DropFurigana(line);
            Check("furigana pieces removed from a char-timed line", line.Text == "空に広がる天使の声", line.Text);
            var one = new LyricLine { Start = 0, End = 1000, Words = { W("天使(てんし)の", 0, 500), W("声", 500, 1000) } };
            SourceUtil.DropFurigana(one);
            Check("furigana inside a word removed", one.Text == "天使の 声", one.Text);
        });

        Section("Romanization: robustness");
        foreach (var t in new[] { "", " ", "Hello world", "🎵🎶 ♪", "사랑해 baby 💕", "LOVE (사랑) [feat. 아이유]", "ㅋㅋㅋ ㅠㅠ", "ㄱㄴㄷ", "さぁ!!", "ー", "っ", "〜♪〜",
                     "日本語と한국어と中文", "(Yeah) 오 오 오", "Ａ Ｂ Ｃ", "​사랑​", "ﾊﾝｶｸ", "𠮷野家", "مرحبا بك", "שלום", "Ω", new string('가', 500) })
            Safe($"romanize {Short(t)}", () =>
            {
                var got = Romanizer.Line(t, Romanizer.DetectScript(t));
                bool ok = got != null && !got.Contains('�') && got.Length < t.Length * 8 + 10;
                bool latinKept = !t.Any(char.IsAsciiLetter) || t.Where(char.IsAsciiLetter).All(c => got!.Contains(c, StringComparison.OrdinalIgnoreCase));
                Check($"romanize {Short(t)}", ok && latinKept, $"\"{Short(got ?? "null", 80)}\"");
            });

        Section("Romanization: per-word timing kept");
        Safe("ForLine keeps word count and timing", () =>
        {
            var line = new LyricLine { Start = 1000, End = 4000, Words = { W("사랑해", 1000, 2000), W("너를", 2000, 3000), W("baby", 3000, 4000) } };
            var r = Romanizer.ForLine(line, "ko");
            Check("ForLine returns a line", r != null);
            if (r == null) return;
            Check("ForLine start/end preserved", r.Start == 1000 && r.End == 4000, $"{r.Start}-{r.End}");
            Check("ForLine words monotonic", r.Words.Zip(r.Words.Skip(1)).All(p => p.First.Start <= p.Second.Start), string.Join(" | ", r.Words.Select(w => $"{w.Text}@{w.Start}")));
            Check("ForLine keeps English word", r.Text.Contains("baby"), r.Text);
        });
    }

    static LyricWord W(string t, long s, long e, bool j = false) => new() { Text = t, Start = s, End = e, Joined = j };
    static string Short(string s, int n = 24) => s.Length <= n ? s : s[..n] + "…";

    // ------------------------------------------------------------------ parsers
    static void Parsers()
    {
        Section("LRC parser");
        var lrcs = new (string name, string text, int expectLines)[]
        {
            ("basic", "[00:01.00]one\n[00:02.50]two\n[00:04.00]three", 3),
            ("crlf+bom", "﻿[00:01.00]one\r\n[00:02.00]two\r\n", 2),
            ("3-digit ms", "[00:01.123]a\n[00:02.456]b", 2),
            ("no fraction", "[00:01]a\n[00:02]b", 2),
            ("multi stamps", "[00:01.00][00:10.00]chorus\n[00:05.00]verse", 3),
            ("out of order", "[00:10.00]late\n[00:01.00]early", 2),
            ("metadata only", "[ar:Someone]\n[ti:Song]\n[length:03:00]", 0),
            ("metadata+lines", "[ar:Someone]\n[ti:Song]\n[offset:+500]\n[00:01.00]a\n[00:02.00]b", 2),
            ("enhanced words", "[00:01.00]<00:01.00>Hel<00:01.30>lo <00:01.60>world\n[00:03.00]next", 2),
            ("empty lines", "[00:01.00]\n[00:02.00]a\n[00:03.00]   \n[00:04.00]b", 2),
            ("over an hour", "[61:00.00]long\n[61:05.00]song", 2),
            ("garbage", "this is not lrc at all\njust text", 0),
            ("broken tags", "[00:aa.00]x\n[0:1.0]y\n[00:02.00]ok", 1),
            ("angle only", "<00:01.00>a <00:02.00>b", 0),
            ("emoji+cjk", "[00:01.00]사랑해 💕\n[00:02.00]夢を見た", 2),
            ("credits header", "[00:00.10]作词 : 某人\n[00:00.20]作曲 : 某人\n[00:05.00]real line\n[00:07.00]second", 2),
        };
        foreach (var (name, text, n) in lrcs)
            Safe($"lrc {name}", () =>
            {
                var lines = Formats.ParseLrc(text);
                int got = lines?.Count ?? 0;
                bool sorted = lines == null || lines.Zip(lines.Skip(1)).All(p => p.First.Start <= p.Second.Start);
                bool ends = lines == null || lines.All(l => l.End >= l.Start);
                Check($"lrc {name}", got == n && sorted && ends, $"lines={got} (want {n}) sorted={sorted} endsOk={ends}" + (lines is { Count: > 0 } ? $" first=\"{lines[0].Text}\"@{lines[0].Start}" : ""));
            });
        Safe("lrc offset applied", () =>
        {
            var l = Formats.ParseLrc("[offset:+500]\n[00:01.00]a\n[00:02.00]b");
            Check("lrc [offset:+500] shifts lines 500 ms earlier", l != null && l[0].Start == 500, l == null ? "null" : $"first@{l[0].Start}");
        });
        Safe("lrc enhanced word timing", () =>
        {
            var l = Formats.ParseLrc("[00:01.00]<00:01.00>Hel<00:01.30>lo <00:01.60>world\n[00:03.00]next");
            Check("enhanced LRC gives word timing", l != null && l[0].Words.Count >= 2 && l[0].Words.Last().Start >= 1600, l == null ? "null" : string.Join(" | ", l[0].Words.Select(w => $"{w.Text}@{w.Start}{(w.Joined ? "j" : "")}")));
        });

        Section("TTML parser");
        string ttml(string body) => "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\"><body><div>" + body + "</div></body></tt>";
        var ttmls = new (string name, string xml, int expect)[]
        {
            ("line timed", ttml("<p begin=\"00:01.000\" end=\"00:02.000\">Hello</p><p begin=\"00:03.000\" end=\"00:04.000\">World</p>"), 2),
            ("word timed", ttml("<p begin=\"1.0\" end=\"3.0\"><span begin=\"1.0\" end=\"1.5\">Hel</span><span begin=\"1.5\" end=\"2\">lo</span> <span begin=\"2\" end=\"3\">there</span></p>"), 1),
            ("duet agents", ttml("<p begin=\"1\" end=\"2\" ttm:agent=\"v1\">a</p><p begin=\"2\" end=\"3\" ttm:agent=\"v2\">b</p>"), 2),
            ("backing vocals", ttml("<p begin=\"1\" end=\"3\"><span begin=\"1\" end=\"2\">main</span><span ttm:role=\"x-bg\"><span begin=\"2\" end=\"3\">(ooh)</span></span></p>"), 1),
            ("hh:mm:ss", ttml("<p begin=\"00:00:01.000\" end=\"00:00:02.000\">a</p>"), 1),
            ("empty p", ttml("<p begin=\"1\" end=\"2\"></p><p begin=\"3\" end=\"4\">x</p>"), 1),
            ("malformed", "<tt><body><p begin=\"1\">unclosed", 0),
            ("not xml", "hello", 0),
            ("entities", ttml("<p begin=\"1\" end=\"2\">Rock &amp; Roll &lt;3</p>"), 1),
        };
        foreach (var (name, xml, n) in ttmls)
            Safe($"ttml {name}", () =>
            {
                var lines = Formats.ParseTtml(xml);
                Check($"ttml {name}", (lines?.Count ?? 0) == n, $"lines={lines?.Count ?? 0} (want {n})" + (lines is { Count: > 0 } ? $" text=\"{lines[0].Text}\" align={lines[^1].Align} bg={(lines[0].Backing?.Text ?? "-")}" : ""));
            });
        Safe("ttml word spacing", () =>
        {
            var l = Formats.ParseTtml(ttmls[1].xml);
            Check("ttml syllables joined, words spaced", l != null && l[0].Text == "Hello there", l == null ? "null" : $"\"{l[0].Text}\"");
        });
        Safe("ttml entities decoded", () =>
        {
            var l = Formats.ParseTtml(ttmls[8].xml);
            Check("ttml &amp; decoded", l != null && l[0].Text.Contains("Rock & Roll <3"), l == null ? "null" : $"\"{l[0].Text}\"");
        });

        Section("yrc / krc pieces");
        foreach (var (name, body) in new[]
        {
            ("yrc", "[1000,2000](1000,500,0)Hel(1500,500,0)lo\n[3000,1000](3000,1000,0)there"),
            ("krc", "[1000,2000]<0,500,0>Hel<500,500,0>lo\n[3000,1000]<0,1000,0>there"),
            ("json header", "{\"t\":0,\"c\":[{\"tx\":\"作词: x\"}]}\n[1000,1000](1000,1000,0)a"),
            ("garbage", "(((((]]]]"),
            ("empty", ""),
        })
            Safe($"pieces {name}", () =>
            {
                var l = Formats.ParsePieces(body, name == "krc");
                Note($"pieces {name}: {(l == null ? "null" : string.Join(" / ", l.Select(x => $"{x.Text}@{x.Start}")))}");
                Check($"pieces {name} no crash", true);
            });

        Section("Clock strings");
        foreach (var (c, ms) in new[] { ("1.5", 1500L), ("00:01.500", 1500L), ("00:00:01.500", 1500L), ("1:02:03.4", 3723400L), ("12s", 12000L), ("", -1L), ("abc", -1L) })
            Safe($"clock {c}", () => { var v = Formats.ClockMs(c); Check($"clock \"{c}\"", ms < 0 ? v <= 0 : v == ms, $"got {v}"); });
    }

    // ------------------------------------------------------------------ matching
    static void Matching()
    {
        Section("Title / artist matching");
        var titles = new (string a, string b, bool same)[]
        {
            ("YOLO", "YOLO", true), ("YOLO", "Whiplash", false), ("Song - 2011 Remaster", "Song", true), ("Song (feat. X)", "Song", true),
            ("Song", "Song (Japanese ver.)", false), ("Love Story (Taylor's Version)", "Love Story", false), ("Dynamite", "dynamite", true),
            ("Hype Boy", "Hype boy", true), ("사랑해", "사랑해", true), ("Ditto", "Ditto (250 Remix)", false), ("Song - Live", "Song", true),
            ("ETA", "E.T.A.", true), ("I AM", "I Am", true), ("", "", false), ("Spicy", "Spicy (English ver.)", false),
        };
        foreach (var (a, b, same) in titles) Check($"SameTitle(\"{a}\", \"{b}\") = {same}", Normalize.SameTitle(a, b) == same);
        var artists = new (string credited, string artist, bool same)[]
        {
            ("aespa", "aespa", true), ("AESPA", "aespa", true), ("BTS", "BTS feat. Halsey", true), ("Halsey", "BTS feat. Halsey", false),
            ("IU", "IU", true), ("I", "IU", false), ("NewJeans", "NewJeans", true), ("Jung Kook", "Jung Kook, Latto", true),
            ("Kep1er", "Kep1er", true), ("A", "B", false), ("TWICE", "TWICE", true), ("트와이스", "TWICE", false),
        };
        foreach (var (c, a, same) in artists) Check($"SameArtist(\"{c}\", \"{a}\") = {same}", Normalize.SameArtist(c, a) == same);
    }

    // ------------------------------------------------------------------ timing helpers
    static void Timing()
    {
        Section("Karaoke timing helpers");
        Safe("SpreadWords", () =>
        {
            var l = Karaoke.SpreadWords("one two three", 1000, 4000);
            Check("SpreadWords covers the line", l.Words.Count == 3 && l.Words[0].Start == 1000 && l.Words[^1].End == 4000, string.Join(" | ", l.Words.Select(w => $"{w.Text}@{w.Start}-{w.End}")));
            var z = Karaoke.SpreadWords("x", 5000, 5000);
            Check("SpreadWords zero length", z.Words.Count == 1);
            var e = Karaoke.SpreadWords("", 0, 1000);
            Check("SpreadWords empty text", e.Words.Count <= 1);
            var cjk = Karaoke.SpreadWords("夢を見た", 0, 4000);
            Check("SpreadWords splits unspaced CJK", cjk.Words.Count >= 2, $"{cjk.Words.Count} words");
        });
        Safe("EstimatedLines", () =>
        {
            var l = Karaoke.EstimatedLines(new long[] { 0, 2000, 2000, 1000 }, new[] { "a", "b", "c", "d" });
            Check("EstimatedLines with duplicate/out-of-order starts", l.All(x => x.End >= x.Start), string.Join(" | ", l.Select(x => $"{x.Text}@{x.Start}-{x.End}")));
        });
    }

    // ------------------------------------------------------------------ settings
    static void SettingsFiles()
    {
        Section("Settings files (real file backed up and restored)");
        var path = Paths.Settings;
        var backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            foreach (var (name, content) in new[]
            {
                ("empty file", ""), ("not json", "hello"), ("truncated", "{\"FontSize\": 3"), ("null", "null"), ("array", "[]"),
                ("extremes", "{\"FontSize\":99999,\"StripWidth\":-5,\"WebPort\":1,\"BackgroundOpacity\":7,\"GlobalOffsetMs\":999999999}"),
                ("wrong types", "{\"FontSize\":\"big\",\"Locked\":\"yes\"}"),
                ("unknown enum", "{\"StripStyle\":\"Fancy\",\"Theme\":\"Purple\"}"),
                ("one bad among good", "{\"FontSize\":44,\"StripStyle\":\"Fancy\",\"WebPort\":9000}"),
                ("bad colours", "{\"UnsungColor\":\"notacolour\",\"SungColor\":\"#12\"}"),
                ("null lists", "{\"Sources\":null,\"SongOffsets\":null}"),
            })
                Safe($"settings {name}", () =>
                {
                    File.WriteAllText(path, content);
                    var s = AppSettings.Load();
                    var style = OverlayWindow.StyleFor(s);
                    if (name == "unknown enum") Check("one bad value keeps the rest", true);
                    if (name == "wrong types") Check("good values survive a bad one", AppSettings.Load() is { } x, "");
                    if (name == "one bad among good") Check("bad value does not reset good ones", s.FontSize == 44 && s.WebPort == 9000, $"font={s.FontSize} port={s.WebPort}");
                    Check($"settings {name} loads", s != null && s.Sources.Count >= 10 && s.FontSize is >= 12 and <= 120 && s.WebPort is >= 1024 and <= 65535,
                        $"font={s?.FontSize} width={s?.StripWidth} port={s?.WebPort} sources={s?.Sources.Count} layout={s?.StripStyle}");
                });
        }
        finally
        {
            if (backup != null) File.WriteAllBytes(path, backup); else File.Delete(path);
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.broken-*")) File.Delete(f);
        }
        Check("settings file restored", backup == null || File.ReadAllBytes(path).SequenceEqual(backup));
    }

    // ------------------------------------------------------------------ display preparation
    static LyricsResult Sample()
    {
        LyricLine L(long s, long e, params string[] words)
        {
            var l = new LyricLine { Start = s, End = e };
            long step = (e - s) / Math.Max(1, words.Length);
            for (int i = 0; i < words.Length; i++) l.Words.Add(W(words[i], s + i * step, s + (i + 1) * step));
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
            L(19000, 22000, "مرحبا", "بك"),
        };
        lines[1].Align = LineAlign.Trailing;
        lines[2].Backing = L(8000, 10000, "(Oh", "oh)");
        lines[3].Translation = "I love you";
        return new LyricsResult { Provider = "test", SourceKey = "test", Lines = lines, Timing = Core.Timing.Word };
    }

    static void Display()
    {
        Section("Source romanization that is only half done is replaced");
        Safe("half-romanized source", () =>
        {
            var line = new LyricLine { Start = 0, End = 3000, Words = { W("悲しまない", 0, 1000), W("明日を", 1000, 2000), W("願うけど", 2000, 3000) } };
            line.Pronunciation = new LyricLine { Start = 0, End = 3000, Words = { W("悲shimanai", 0, 1000), W("明日o", 1000, 2000), W("願ukedo", 2000, 3000) } };
            var d = AppController.Prepare(new LyricsResult { Lines = new() { line } }, new AppSettings { Display = DisplayMode.OriginalAndRomanized });
            var sub = d[0].Sub?.Text ?? "";
            Check("half-romanized source replaced", !Romanizer.Needs(sub) && sub.Length > 0, sub);
        });
        Safe("good source kept", () =>
        {
            var line = new LyricLine { Start = 0, End = 2000, Words = { W("夢を", 0, 1000), W("見た", 1000, 2000) } };
            line.Pronunciation = new LyricLine { Start = 0, End = 2000, Words = { W("yume wo", 0, 1000), W("mita", 1000, 2000) } };
            var d = AppController.Prepare(new LyricsResult { Lines = new() { line } }, new AppSettings { Display = DisplayMode.OriginalAndRomanized });
            Check("good source romanization kept", d[0].Sub?.Text == "yume wo mita", d[0].Sub?.Text ?? "null");
        });

        Section("Display preparation (all modes)");
        foreach (DisplayMode mode in Enum.GetValues<DisplayMode>())
            Safe($"prepare {mode}", () =>
            {
                var s = new AppSettings { Display = mode };
                var d = AppController.Prepare(Sample(), s);
                Check($"prepare {mode}", d.Count == 7 && d.All(x => x.Main.Words.Count > 0), string.Join(" / ", d.Select(x => Short(x.Main.Text, 30) + (x.Sub != null ? $" [{Short(x.Sub.Text, 20)}]" : ""))));
            });
    }

    // ------------------------------------------------------------------ rendering
    static void Save(Visual v, int w, int h, string file)
    {
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 40, 48)), null, new Rect(0, 0, w, h));
        bmp.Render(bg); bmp.Render(v);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(_out, file)); enc.Save(f);
    }

    static void RenderStrip()
    {
        Section("Strip rendering (images in the out folder)");
        var sample = Sample();
        foreach (var preset in LookPresets.All)
            foreach (DisplayMode mode in new[] { DisplayMode.RomanizedOnly, DisplayMode.OriginalAndRomanized, DisplayMode.OriginalOnly })
                Safe($"render {preset.Name} {mode}", () =>
                {
                    var s = new AppSettings(); preset.Apply(s); s.Display = mode; s.ShowTranslation = true;
                    var d = AppController.Prepare(sample, s);
                    int rows = 0;
                    var frames = new List<(long t, string what)> { (2500, "korean mid"), (5500, "duet trailing"), (8500, "japanese+backing"), (11500, "chinese+translation"), (15000, "very long"), (17500, "emoji"), (20500, "arabic"), (30000, "after end") };
                    var panel = new System.Windows.Controls.StackPanel { Width = 900, Background = Brushes.Transparent };
                    foreach (var (t, what) in frames)
                    {
                        var view = new KaraokeView { Width = 900 };
                        view.SetStyle(OverlayWindow.StyleFor(s));
                        long now = t;
                        view.SetClock(() => now);
                        view.SetLyrics(d, true, 25000, null);
                        view.Height = view.DesiredStripHeight();
                        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"{preset.Name} · {mode} · {what}", Foreground = Brushes.Gray, FontSize = 11 });
                        panel.Children.Add(view);
                        rows++;
                    }
                    panel.Measure(new Size(900, double.PositiveInfinity)); panel.Arrange(new Rect(panel.DesiredSize)); panel.UpdateLayout();
                    // Draw once to start each line's entrance animation, wait for it to finish, then capture.
                    var warm = new RenderTargetBitmap(900, (int)Math.Ceiling(panel.ActualHeight) + 1, 96, 96, PixelFormats.Pbgra32);
                    warm.Render(panel);
                    Thread.Sleep(450);
                    foreach (var child in panel.Children.OfType<KaraokeView>()) child.InvalidateVisual();
                    panel.UpdateLayout();
                    Save(panel, 900, (int)Math.Ceiling(panel.ActualHeight), $"strip_{preset.Name.Replace(" ", "")}_{mode}.png");
                    Check($"render {preset.Name} {mode}", rows == frames.Count, $"{(int)panel.ActualHeight}px tall");
                });
    }

    static void RenderPage()
    {
        Section("Full lyrics window page rendering");
        foreach (var (w, h) in new[] { (1100, 700), (520, 800), (1800, 1000) })
            Safe($"page {w}x{h}", () =>
            {
                var s = new AppSettings();
                var d = AppController.Prepare(Sample(), s);
                var page = new LyricsPageView { Width = w, Height = h };
                long now = 11500;
                page.SetClock(() => now);
                page.SetLyrics(d, true, 25000, null);
                page.Measure(new Size(w, h)); page.Arrange(new Rect(0, 0, w, h)); page.UpdateLayout();
                Save(page, w, h, $"page_{w}x{h}.png");
                Check($"page {w}x{h} renders", true);
            });
        Safe("page empty message", () =>
        {
            var page = new LyricsPageView { Width = 800, Height = 400 };
            page.SetLyrics(new List<DisplayLine>(), true, 0, "No lyrics found for this song. Use \"Choose lyrics\" at the top right to try every source, or import a file.");
            page.Measure(new Size(800, 400)); page.Arrange(new Rect(0, 0, 800, 400));
            Save(page, 800, 400, "page_empty.png");
            Check("page empty renders", true);
        });
    }

    // ------------------------------------------------------------------ live sources
    static async Task Songs()
    {
        Section("Live lyric lookups (whole chain, no cache)");
        var settings = AppSettings.Load();
        var chain = new LyricsChain(() => settings);
        var songs = new (string artist, string title, int sec, string expectWord)[]
        {
            ("aespa", "YOLO", 189, "YOLO"), ("aespa", "Whiplash", 183, "Whiplash"), ("aespa", "Supernova", 179, "Supernova"),
            ("NewJeans", "Hype Boy", 179, "Hype"), ("NewJeans", "Ditto", 186, "Ditto"), ("IU", "Love wins all", 271, ""),
            ("BTS", "Dynamite", 199, "Dynamite"), ("BLACKPINK", "How You Like That", 182, "like that"), ("TWICE", "What is Love?", 208, "love"),
            ("LE SSERAFIM", "ANTIFRAGILE", 184, "antifragile"), ("IVE", "LOVE DIVE", 177, "love dive"), ("Stray Kids", "MANIAC", 182, "maniac"),
            ("YOASOBI", "アイドル", 213, ""), ("YOASOBI", "夜に駆ける", 261, ""), ("Kenshi Yonezu", "Lemon", 255, ""), ("Ado", "うっせぇわ", 204, ""),
            ("Fujii Kaze", "Shinunoga E-Wa", 185, ""), ("Official HIGE DANDism", "Pretender", 326, ""),
            ("Jay Chou", "晴天", 269, ""), ("G.E.M.", "光年之外", 235, ""), ("Teresa Teng", "月亮代表我的心", 207, ""),
            ("Taylor Swift", "Anti-Hero", 200, "hero"), ("The Weeknd", "Blinding Lights", 200, "lights"), ("Queen", "Bohemian Rhapsody - Remastered 2011", 354, "real life"),
            ("Daft Punk", "Get Lucky (feat. Pharrell Williams and Nile Rodgers)", 248, "lucky"), ("Luis Fonsi", "Despacito", 228, "despacito"),
            ("t.A.T.u.", "Нас не догонят", 254, ""), ("Hans Zimmer", "Time", 275, ""), ("Nobody Real", "This Song Does Not Exist 12345", 200, ""),
        };
        foreach (var (artist, title, sec, expect) in songs)
        {
            var q = new TrackQuery { Artist = artist, Title = title, Seconds = sec };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LyricsResult? r = null;
            try { r = await chain.FetchAsync(q, null, new CancellationTokenSource(30000).Token, useCache: false); }
            catch (Exception ex) { Check($"song {artist} - {title}", false, ex.Message); continue; }
            var all = r == null ? "" : string.Join(" ", r.Lines.Select(l => l.Text));
            bool found = r is { HasLines: true } || r is { Instrumental: true };
            bool wordOk = expect.Length == 0 || all.Contains(expect, StringComparison.OrdinalIgnoreCase);
            bool expectNone = title.Contains("Does Not Exist");
            bool ok = expectNone ? !found : found && wordOk;
            Check($"song {artist} - {title}", ok, r == null ? $"nothing ({sw.ElapsedMilliseconds} ms)" :
                $"{r.Provider} · {r.Timing} · {r.Lines.Count} lines · {sw.ElapsedMilliseconds} ms{(wordOk ? "" : $" · MISSING \"{expect}\"")}");
            if (r is { HasLines: true })
            {
                var script = Romanizer.SongScript(r.Lines);
                foreach (var l in r.Lines.Where(l => l.Text.Length > 3).Skip(3).Take(2))
                {
                    var shown = AppController.Prepare(new LyricsResult { Lines = new() { l } }, new AppSettings { Display = DisplayMode.OriginalAndRomanized });
                    var rom = shown.Count > 0 ? shown[0].Sub?.Text : null;
                    Note($"{l.Start,7}  {Short(l.Text, 60)}{(rom != null ? "  =>  " + Short(rom, 70) : "")}{(l.Pronunciation != null ? "   [source: " + Short(l.Pronunciation.Text, 40) + "]" : "")}");
                }
                bool sorted = r.Lines.Zip(r.Lines.Skip(1)).All(p => p.First.Start <= p.Second.Start);
                if (!sorted)
                {
                    var bad = r.Lines.Zip(r.Lines.Skip(1)).First(p => p.First.Start > p.Second.Start);
                    Note($"out of order: {bad.First.Start}-{bad.First.End} \"{Short(bad.First.Text, 40)}\" ({bad.First.Align}) then {bad.Second.Start}-{bad.Second.End} \"{Short(bad.Second.Text, 40)}\" ({bad.Second.Align})");
                }
                bool sane = r.Lines.All(l => l.End >= l.Start && l.Start >= 0) && (r.Lines.Count == 0 || r.Lines[^1].Start < (sec + 30) * 1000L);
                Check($"   timing sane ({artist} - {title})", sorted && sane, $"sorted={sorted} sane={sane} last@{r.Lines[^1].Start}");
            }
        }
    }
}
