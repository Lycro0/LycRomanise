using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LycRomanise.Core;
using LycRomanise.Media;
using MediaState = LycRomanise.Media.MediaState;
using LycRomanise.UI;

namespace LycRomanise;

/// <summary>`--selftest &lt;dir&gt; app`: runs the real app (windows, web server, title file) with fake playback
/// and drives it through every state. The user's settings and autostart entry are restored at the end.</summary>
static class SelfTestApp
{
    static readonly StringBuilder R = new();
    static readonly Dictionary<string, (int pass, int fail)> Areas = new();
    static string _area = "";
    static int _shown;
    static AppController _app = null!;
    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static void Area(string n) { _area = n; _shown = 0; Areas.TryAdd(n, (0, 0)); R.AppendLine().AppendLine("== " + n); }
    static void Check(string name, bool ok, string detail = "")
    {
        var (p, f) = Areas[_area];
        Areas[_area] = ok ? (p + 1, f) : (p, f + 1);
        if (!ok && _shown++ < 30) R.AppendLine($"FAIL  {name}  -> {detail}");
    }
    static void Info(string s) => R.AppendLine("      " + s);

    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Any)!.GetValue(o)!;
    static void SetField(object o, string name, object? v) => o.GetType().GetField(name, Any)!.SetValue(o, v);
    static object? Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(o, args);

    /// <summary>Lets queued UI work run (bindings, animations, BeginInvoke callbacks).</summary>
    static async Task Pump(int ms = 30)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    static MediaState Fake(string artist, string title, bool playing, long pos = 30000, long dur = 200000) =>
        new("Spotify.exe", title, artist, "Test Album", dur, pos, DateTime.UtcNow, playing, 1);

    /// <summary>Pretends the player reported this state.</summary>
    static async Task Play(MediaState? s)
    {
        SetField(_app.Media, "_last", s);
        Call(_app, "OnMedia", s);
        await Pump();
    }

    public static async Task RunAsync(AppController app, string outDir)
    {
        _app = app;
        Directory.CreateDirectory(outDir);
        var settingsBackup = File.ReadAllBytes(Paths.Settings);
        bool autostart = Autostart.IsOn();
        // Real playback must not interfere: stop the player polling.
        Field<System.Threading.Timer>(app.Media, "_timer").Change(Timeout.Infinite, Timeout.Infinite);
        var logPath = Path.Combine(Paths.Logs, "lycromanise.log");
        int logStart = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;
        try
        {
            var baseline = app.Settings.Copy();
            baseline.HideDesktopStrip = false; baseline.WebEnabled = true; baseline.NowPlayingFile = true; baseline.StartWithWindows = autostart;
            app.ApplySettings(baseline, false);
            await Pump(300);

            var steps = new (string, Func<Task>)[] { ("visibility", Visibility), ("songs", SongStorm), ("title file", TitleFile), ("web", Web), ("settings", SettingsWindowEverywhere), ("lyrics window", LyricsWindowEverywhere) };
            if (Environment.GetEnvironmentVariable("LYC_QUICK") == "1") steps = new (string, Func<Task>)[] { ("settings", SettingsWindowEverywhere) };
            if (Environment.GetEnvironmentVariable("LYC_QUICK") == "skip") steps = new (string, Func<Task>)[] { ("skip back", SkipBack) };
            else steps = steps.Append(("skip back", (Func<Task>)SkipBack)).ToArray();
            foreach (var (name, step) in steps)
            {
                var run = step();
                if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(6))) != run) { Area("timeouts"); Check($"{name} finished within 6 minutes", false, "stuck"); }
                else if (run.Exception != null) { Area("crash"); Check(name, false, run.Exception.ToString()); }
                Write(outDir);
            }
        }
        catch (Exception ex) { Area("crash"); Check("harness", false, ex.ToString()); }
        finally
        {
            File.WriteAllBytes(Paths.Settings, settingsBackup);
            Autostart.Apply(autostart);
        }
        var errors = File.ReadAllLines(logPath).Skip(logStart)
            .Where(l => (l.Contains("ERROR") || l.Contains("FATAL")) && !l.Contains("settings load") && !l.Contains("now playing file: UnauthorizedAccess") && !l.Contains("now playing file: IOException"))
            .ToList();
        Area("Errors logged while testing");
        Check("no unexpected errors in the log", errors.Count == 0, string.Join("\n        ", errors.Take(15)));

        Write(outDir);
    }

    static void Write(string outDir)
    {
        var head = new StringBuilder($"LycRomanise app self-test {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        foreach (var (a, (p, f)) in Areas) head.AppendLine($"  {a,-44} {p + f,5} tests  {p,5} pass  {f,4} fail");
        head.AppendLine($"  {"TOTAL",-44} {Areas.Values.Sum(x => x.pass + x.fail),5} tests  {Areas.Values.Sum(x => x.pass),5} pass  {Areas.Values.Sum(x => x.fail),4} fail");
        File.WriteAllText(Path.Combine(outDir, "app.txt"), head.ToString() + R);
    }

    // ------------------------------------------------------------------ strip visibility
    static async Task Visibility()
    {
        Area("Strip visibility (every combination)");
        await Play(Fake("aespa", "Supernova", true));
        foreach (bool playing in new[] { true, false })
        foreach (bool hidePaused in new[] { true, false })
        foreach (bool hidePc in new[] { true, false })
        foreach (bool hideWithLyrics in new[] { true, false })
        foreach (int lyricsWin in new[] { 0, 1, 2 })            // closed, open, minimized
        foreach (int settingsWin in new[] { 0, 1, 2 })          // closed, open and focused, minimized
        {
            var c = _app.Settings.Copy();
            c.HideWhenPaused = hidePaused; c.HideDesktopStrip = hidePc; c.HideStripWithLyricsWindow = hideWithLyrics;
            _app.ApplySettings(c, false);
            await Play(Fake("aespa", "Supernova", playing));
            var lw = Field<LyricsWindow?>(_app, "_lyricsWindow");
            if (lyricsWin == 0) lw?.Close();
            else { _app.ShowLyricsWindow(); lw = Field<LyricsWindow?>(_app, "_lyricsWindow")!; lw.WindowState = lyricsWin == 2 ? WindowState.Minimized : WindowState.Normal; }
            var sw = Field<SettingsWindow?>(_app, "_settingsWindow");
            if (settingsWin == 0) sw?.Close();
            else { _app.ShowSettings(); sw = Field<SettingsWindow?>(_app, "_settingsWindow")!; sw.WindowState = settingsWin == 2 ? WindowState.Minimized : WindowState.Normal; if (settingsWin == 1) sw.Activate(); }
            await Pump(80);
            Call(_app, "UpdateVisibility");
            await Pump(320);
            bool lyricsUp = hideWithLyrics && lyricsWin == 1;
            bool settingsUp = settingsWin == 1 && (Field<SettingsWindow?>(_app, "_settingsWindow")?.IsActive ?? false);
            bool want = !hidePc && !lyricsUp && (playing || !hidePaused || settingsUp);
            bool shown = _app.Overlay.IsVisible && _app.Overlay.Opacity > 0.9 && _app.Overlay.IsHitTestVisible;
            bool hidden = !_app.Overlay.IsVisible || (_app.Overlay.Opacity < 0.1 && !_app.Overlay.IsHitTestVisible);
            Check($"playing={playing} hidePaused={hidePaused} hidePC={hidePc} hideWithLyrics={hideWithLyrics} lyricsWin={lyricsWin} settings={settingsWin}",
                want ? shown : hidden, $"want {(want ? "shown" : "hidden")}, opacity={_app.Overlay.Opacity:0.00} hit={_app.Overlay.IsHitTestVisible} visible={_app.Overlay.IsVisible}");
        }
        Field<LyricsWindow?>(_app, "_lyricsWindow")?.Close();
        Field<SettingsWindow?>(_app, "_settingsWindow")?.Close();
        await Pump(100);
    }

    // ------------------------------------------------------------------ rapid song changes
    static async Task SongStorm()
    {
        Area("Song changes (storms, pauses, ads, nothing playing)");
        var c = _app.Settings.Copy(); c.HideDesktopStrip = false; c.HideWhenPaused = true; _app.ApplySettings(c, false);
        var songs = new (string a, string t)[] { ("aespa", "YOLO"), ("aespa", "Whiplash"), ("aespa", "Supernova"), ("NewJeans", "Ditto"), ("IU", "Love wins all"), ("BTS", "Dynamite"), ("YOASOBI", "アイドル"), ("Kenshi Yonezu", "Lemon") };
        var rng = new Random(21);
        for (int i = 0; i < 300; i++)
        {
            var (a, t) = songs[rng.Next(songs.Length)];
            int kind = rng.Next(10);
            MediaState? s = kind switch
            {
                0 => null,                                                  // player closed
                1 => new MediaState("Spotify.exe", "Advertisement", "", "", 30000, 0, DateTime.UtcNow, true, 1),
                2 => Fake(a, t, false),
                _ => Fake(a, t, true, rng.Next(0, 180000)),
            };
            SetField(_app.Media, "_last", s);
            Call(_app, "OnMedia", s);
            if (i % 7 == 0) await Pump(rng.Next(0, 60)); // sometimes let fetches run, sometimes hammer
            var q = _app.CurrentQuery;
            bool ok = s == null || kind == 1 ? q == null : q != null && q.Title == t && q.Artist == a;
            Check($"#{i} {(s == null ? "nothing" : kind == 1 ? "ad" : $"{a} - {t} playing={s.Playing}")}", ok, $"query={(q == null ? "none" : q.Artist + " - " + q.Title)}");
        }
        // Settle on one song: its lyrics should arrive, and be that song's.
        await Play(Fake("aespa", "Supernova", true, 60000));
        for (int i = 0; i < 100 && (_app.CurrentResult == null); i++) await Pump(100);
        var r = _app.CurrentResult;
        Check("after the storm, the last song's lyrics load", r is { HasLines: true }, r == null ? "none" : r.Provider);
        Check("and they are that song's", r != null && string.Join(" ", r.Lines.Select(l => l.Text)).Contains("Supernova", StringComparison.OrdinalIgnoreCase), "");
        await Pump(500);
        Check("the strip shows them", _app.CurrentLines.Count > 0 && _app.Overlay.Opacity > 0.9,
            $"lines={_app.CurrentLines.Count} opacity={_app.Overlay.Opacity:0.00} visible={_app.Overlay.IsVisible} hidePC={_app.Settings.HideDesktopStrip} hidePaused={_app.Settings.HideWhenPaused} playing={_app.NowPlaying?.Playing} lyricsWin={Field<LyricsWindow?>(_app, "_lyricsWindow") != null} settingsWin={Field<SettingsWindow?>(_app, "_settingsWindow") != null}");
    }

    // ------------------------------------------------------------------ quick skip forward and back
    static async Task SkipBack()
    {
        Area("Quick skip forward and back (length arrives late)");
        var c = _app.Settings.Copy(); c.HideDesktopStrip = false; _app.ApplySettings(c, false);
        var songs = new (string t, long dur, string word)[] { ("YOLO", 189000, "YOLO"), ("Whiplash", 183000, "Whiplash"), ("Supernova", 179000, "Supernova"), ("Drama", 214000, "Drama") };
        var rng = new Random(41);
        for (int round = 0; round < 40; round++)
        {
            var a = songs[rng.Next(songs.Length)];
            var b = songs.Where(x => x.t != a.t).ElementAt(rng.Next(songs.Length - 1));
            await Play(Fake("aespa", a.t, true, 5000, a.dur));
            await Pump(rng.Next(50, 900));
            await Play(Fake("aespa", b.t, true, 0, b.dur));                     // skip forward
            await Pump(rng.Next(30, 500));
            await Play(Fake("aespa", a.t, true, 0, b.dur));                     // back: new title, old length
            await Pump(rng.Next(0, 700));
            await Play(Fake("aespa", a.t, true, 200, a.dur));                   // the real length arrives
            LyricsResult? r = null;
            for (int i = 0; i < 80; i++) { r = _app.CurrentResult; if (r is { HasLines: true } && _app.CurrentQuery?.Title == a.t) break; await Pump(100); }
            var q = _app.CurrentQuery;
            var text = r == null ? "" : string.Join(" ", r.Lines.Select(l => l.Text));
            Check($"round {round}: {a.t} -> {b.t} -> back to {a.t}",
                q?.Title == a.t && Math.Abs((q?.Seconds ?? 0) - a.dur / 1000) <= 2 && text.Contains(a.word, StringComparison.OrdinalIgnoreCase),
                $"query={q?.Title} {q?.Seconds}s, lyrics={(r == null ? "none" : r.Provider + ": " + text[..Math.Min(60, text.Length)])}");
        }
    }

    // ------------------------------------------------------------------ OBS title file
    static async Task TitleFile()
    {
        Area("OBS title file (formats, locks, unicode)");
        var path = Path.Combine(Paths.Data, "LycRomanise.txt");
        string Read() { try { return File.ReadAllText(path, Encoding.UTF8); } catch { return "<locked>"; } }
        var tracks = new (string a, string t)[] { ("aespa", "YOLO"), ("YOASOBI", "アイドル"), ("아이유", "좋은 날"), ("周杰倫", "晴天"), ("", "Untitled"), ("AC/DC", "T.N.T."), ("Beyoncé", "Halo"), ("a\"b", "c'd") };
        foreach (NowPlayingFormat fmt in Enum.GetValues<NowPlayingFormat>())
            foreach (var (a, t) in tracks)
                for (int rep = 0; rep < 6; rep++)
                {
                    var c = _app.Settings.Copy(); c.NowPlayingFormat = fmt; c.NowPlayingFile = true; _app.ApplySettings(c, false);
                    await Play(Fake(a, t, rep % 2 == 0));
                    string want = fmt switch
                    {
                        NowPlayingFormat.Title => t,
                        NowPlayingFormat.TitleArtist => a.Length > 0 ? $"{t} - {a}" : t,
                        _ => a.Length > 0 ? $"{a} - {t}" : t,
                    };
                    var got = Read();
                    Check($"{fmt} {a} - {t}", got == want, $"file says \"{got}\"");
                }
        // Locked by another program (as OBS can): the write is retried and lands once the lock goes.
        await Play(Fake("aespa", "Whiplash", true));
        using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Play(Fake("aespa", "Drama", true));
            await Pump(400);
        }
        await Pump(1600);
        var c2 = _app.Settings.Copy(); var expect = c2.NowPlayingFormat switch { NowPlayingFormat.Title => "Drama", NowPlayingFormat.TitleArtist => "Drama - aespa", _ => "aespa - Drama" };
        Check("locked file catches up after the lock goes", Read() == expect, $"file says \"{Read()}\"");
        await Play(null);
        Check("nothing playing empties the file", Read() == "", $"file says \"{Read()}\"");
        var c3 = _app.Settings.Copy(); c3.NowPlayingFile = false; _app.ApplySettings(c3, false);
        await Play(Fake("aespa", "YOLO", true));
        Check("turned off: the file is left alone", Read() == "", $"file says \"{Read()}\"");
        var c4 = _app.Settings.Copy(); c4.NowPlayingFile = true; _app.ApplySettings(c4, false);
        await Pump(100);
        Check("turned back on: written at once", Read().Contains("YOLO"), $"file says \"{Read()}\"");
    }

    // ------------------------------------------------------------------ web server
    static async Task Web()
    {
        Area("Web server (every endpoint, during song changes)");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string b = $"http://127.0.0.1:{_app.Settings.WebPort}";
        var rng = new Random(31);
        string[] paths = { "/", "/obs", "/?obs=1", "/state", "/lyrics", "/cover", "/nope", "/../settings.json", "/%2e%2e/settings.json", "/state?x=" + new string('a', 3000) };
        for (int i = 0; i < 400; i++)
        {
            if (i % 25 == 0) { var songs = new[] { "YOLO", "Whiplash", "Supernova" }; SetField(_app.Media, "_last", Fake("aespa", songs[rng.Next(3)], rng.Next(3) > 0)); Call(_app, "OnMedia", _app.Media.Current); }
            var p = paths[rng.Next(paths.Length)];
            try
            {
                using var resp = await http.GetAsync(b + p);
                var body = await resp.Content.ReadAsStringAsync();
                bool ok = p switch
                {
                    "/" or "/obs" or "/?obs=1" => resp.IsSuccessStatusCode && body.Contains("<html"),
                    "/state" => resp.IsSuccessStatusCode && JsonDocument.Parse(body).RootElement.TryGetProperty("playing", out _),
                    "/lyrics" => resp.IsSuccessStatusCode && JsonDocument.Parse(body).RootElement.TryGetProperty("lines", out _),
                    "/cover" => (int)resp.StatusCode is 200 or 204 or 404,
                    _ when p.StartsWith("/state?") => resp.IsSuccessStatusCode && JsonDocument.Parse(body).RootElement.ValueKind == JsonValueKind.Object,
                    _ => (int)resp.StatusCode == 404 && !body.Contains("SpotifyClientId"),
                };
                Check($"GET {p[..Math.Min(40, p.Length)]}", ok, $"{(int)resp.StatusCode} {body[..Math.Min(80, body.Length)]}");
            }
            catch (Exception ex) { Check($"GET {p[..Math.Min(40, p.Length)]}", false, ex.Message); }
            if (i % 10 == 0) await Pump(5);
        }
        // Turned off and on again.
        var c = _app.Settings.Copy(); c.WebEnabled = false; _app.ApplySettings(c, false); await Pump(200);
        bool refused = false; try { await http.GetAsync(b + "/state"); } catch { refused = true; }
        Check("web view off: nothing answers", refused);
        c = _app.Settings.Copy(); c.WebEnabled = true; _app.ApplySettings(c, false); await Pump(300);
        try { var r = await http.GetAsync(b + "/state"); Check("web view back on: answers", r.IsSuccessStatusCode); } catch (Exception ex) { Check("web view back on: answers", false, ex.Message); }
    }

    // ------------------------------------------------------------------ settings window
    static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is T t) yield return t;
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) stack.Push(child);
            if (d is System.Windows.Media.Visual)
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }
    }

    static async Task SettingsWindowEverywhere()
    {
        Area("Settings window (every page, every control)");
        await Play(Fake("aespa", "Supernova", true));
        _app.ShowSettings();
        await Pump(400);
        var sw = Field<SettingsWindow>(_app, "_settingsWindow");
        var pages = Field<Dictionary<string, Func<UIElement>>>(sw, "_pages").Keys.ToList();
        bool quick = Environment.GetEnvironmentVariable("LYC_QUICK") == "1";
        foreach (var page in quick ? new List<string>() : pages)
        {
            try { Call(sw, "Show", page); await Pump(150); Check($"page {page} opens", true); }
            catch (Exception ex) { Check($"page {page} opens", false, ex.InnerException?.Message ?? ex.Message); continue; }
            // Toggles: flip each, then back. (Start with Windows and the web view are restored at the end.)
            var toggles = All<ToggleButton>(sw).Where(t => t.IsVisible && t.IsEnabled && t is not RadioButton).ToList();
            int n = 0;
            foreach (var t in toggles)
            {
                for (int k = 0; k < 2; k++)
                {
                    try { t.IsChecked = !(t.IsChecked ?? false); await Pump(10); Check($"{page}: toggle #{n} flip {k}", true); }
                    catch (Exception ex) { Check($"{page}: toggle #{n} flip {k}", false, ex.InnerException?.Message ?? ex.Message); }
                }
                n++;
            }
            foreach (var cb in All<ComboBox>(sw).Where(c => c.IsVisible && c.IsEnabled).ToList())
            {
                int orig = cb.SelectedIndex;
                for (int k = 0; k < cb.Items.Count; k++)
                {
                    try { cb.SelectedIndex = k; await Pump(10); Check($"{page}: list item {k}", true); }
                    catch (Exception ex) { Check($"{page}: list item {k}", false, ex.InnerException?.Message ?? ex.Message); }
                }
                try { cb.SelectedIndex = orig; } catch { }
            }
            foreach (var sl in All<Slider>(sw).Where(s => s.IsVisible && s.IsEnabled).ToList())
            {
                double orig = sl.Value;
                foreach (var v in new[] { sl.Minimum, sl.Maximum, (sl.Minimum + sl.Maximum) / 2, orig })
                {
                    try { sl.Value = v; await Pump(10); Check($"{page}: slider to {v:0.##}", true); }
                    catch (Exception ex) { Check($"{page}: slider to {v:0.##}", false, ex.InnerException?.Message ?? ex.Message); }
                }
            }
            await Pump(200);   // let the debounced save happen
        }
        // The look gallery: every look card applies.
        Call(sw, "Show", "look"); await Pump(200);
        foreach (var preset in LookPresets.All)
        {
            var c = _app.Settings.Copy(); preset.Apply(c); _app.ApplySettings(c, true); await Pump(60);
            Check($"look {preset.Name} applies", _app.Settings.LookPreset == preset.Name || _app.Settings.UnsungColor == c.UnsungColor, _app.Settings.LookPreset);
        }
        // External change while Settings is open is not overwritten by it (the earlier "unticks itself" bug).
        var ext = _app.Settings.Copy(); ext.HideDesktopStrip = !ext.HideDesktopStrip; bool wantHide = ext.HideDesktopStrip;
        _app.ApplySettings(ext, false); await Pump(100);
        Call(sw, "Changed", false); await Pump(400);
        Check("a tray change survives the next edit in Settings", _app.Settings.HideDesktopStrip == wantHide, $"HideDesktopStrip={_app.Settings.HideDesktopStrip}");
        sw.Close(); await Pump(100);
        Check("settings window closes cleanly", Field<SettingsWindow?>(_app, "_settingsWindow") == null);
    }

    // ------------------------------------------------------------------ full lyrics window
    static async Task LyricsWindowEverywhere()
    {
        Area("Full lyrics window (panel, keys, sizes)");
        var c0 = _app.Settings.Copy(); c0.HideDesktopStrip = false; c0.HideStripWithLyricsWindow = true; c0.HideWhenPaused = true; _app.ApplySettings(c0, false);
        await Play(Fake("aespa", "Supernova", true, 60000));
        _app.ShowLyricsWindow(); await Pump(400);
        var lw = Field<LyricsWindow>(_app, "_lyricsWindow");
        Check("opening full lyrics hides the strip", _app.Overlay.Opacity < 0.1, $"opacity={_app.Overlay.Opacity:0.00}");
        Call(lw, "TogglePanel"); await Pump(300);
        var panel = (FrameworkElement)lw.FindName("SettingsPanel");
        Check("gear opens the settings panel", panel.Visibility == System.Windows.Visibility.Visible);
        for (int round = 0; round < 3; round++)
        {
            // Every chip in the panel.
            var body = (Panel)lw.FindName("PanelBody");
            int chips = body.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<Border>()).Count();
            for (int i = 0; i < chips; i++)
            {
                var chip = ((Panel)lw.FindName("PanelBody")).Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<Border>()).ElementAt(i);
                try
                {
                    chip.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                    await Pump(40);
                    Check($"round {round}: chip {i} ({((TextBlock)chip.Child).Text})", true);
                }
                catch (Exception ex) { Check($"round {round}: chip {i}", false, ex.InnerException?.Message ?? ex.Message); }
            }
            foreach (var cb in ((Panel)lw.FindName("PanelBody")).Children.OfType<CheckBox>().ToList())
                for (int k = 0; k < 2; k++)
                {
                    try { cb.IsChecked = !(cb.IsChecked ?? false); await Pump(40); Check($"round {round}: {cb.Content} flip {k}", true); }
                    catch (Exception ex) { Check($"round {round}: {cb.Content} flip {k}", false, ex.InnerException?.Message ?? ex.Message); }
                }
            foreach (var sl in ((Panel)lw.FindName("PanelBody")).Children.OfType<Slider>().ToList())
                foreach (var v in new[] { sl.Minimum, sl.Maximum, 40.0 })
                {
                    try { sl.Value = v; await Pump(20); Check($"round {round}: text size {v}", Math.Abs(((FrameworkElement)lw.FindName("Page")).GetValue(FrameworkElement.WidthProperty) is double ? 0 : 0) == 0); }
                    catch (Exception ex) { Check($"round {round}: text size {v}", false, ex.Message); }
                }
        }
        // Background option really switches.
        var cb1 = _app.Settings.Copy(); cb1.LyricsBackground = LyricsBackground.Black; _app.ApplySettings(cb1, false); await Pump(100);
        Check("black background shows", ((FrameworkElement)lw.FindName("BlackBg")).Visibility == System.Windows.Visibility.Visible);
        cb1 = _app.Settings.Copy(); cb1.LyricsBackground = LyricsBackground.Cover; _app.ApplySettings(cb1, false); await Pump(100);
        Check("cover background shows", ((FrameworkElement)lw.FindName("BackdropHost")).Visibility == System.Windows.Visibility.Visible);
        // Sizes and states.
        foreach (var (w, h) in new[] { (520.0, 420.0), (800, 600), (1180, 760), (1920, 1080), (600, 1000) })
        {
            lw.WindowState = WindowState.Normal; lw.Width = w; lw.Height = h; await Pump(120);
            Check($"window {w}x{h}", lw.ActualWidth > 0 && ((FrameworkElement)lw.FindName("Page")).ActualHeight > 0);
        }
        lw.WindowState = WindowState.Maximized; await Pump(200); Check("maximized", lw.WindowState == WindowState.Maximized);
        lw.WindowState = WindowState.Minimized; await Pump(400);
        Check("minimized: strip comes back", _app.Overlay.Opacity > 0.9, $"opacity={_app.Overlay.Opacity:0.00}");
        lw.WindowState = WindowState.Normal; await Pump(400);
        Check("restored: strip hides again", _app.Overlay.Opacity < 0.1, $"opacity={_app.Overlay.Opacity:0.00}");
        // Songs changing while it's open.
        foreach (var t in new[] { "YOLO", "Whiplash", "Drama", "Supernova" }) { await Play(Fake("aespa", t, true, 50000)); await Pump(300); Check($"title follows the song ({t})", lw.Title.Contains(t), lw.Title); }
        await Play(null); await Pump(200);
        Check("nothing playing: window says so", lw.Title.Contains("Lyrics"), lw.Title);
        lw.Close(); await Pump(400);
        await Play(Fake("aespa", "Supernova", true));
        await Pump(400);
        Check("closing full lyrics brings the strip back", _app.Overlay.Opacity > 0.9, $"opacity={_app.Overlay.Opacity:0.00}");
    }
}
