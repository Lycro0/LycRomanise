using System.Windows;
using LycRomanise.Core;

namespace LycRomanise;

public partial class App : Application
{
    Mutex? _single;
    AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 3 && e.Args[0] == "--dump-lyrics")
        {
            // For videos: a cached lyrics file as the strip would show it, written as JSON.
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(e.Args[1]).TrimStart((char)0xFEFF))!;
                var r = System.Text.Json.JsonSerializer.Deserialize<LyricsResult>(node["Result"]!.ToJsonString(), new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
                var lines = AppController.Prepare(r, new AppSettings { Display = DisplayMode.OriginalAndRomanized });
                File.WriteAllText(e.Args[2], System.Text.Json.JsonSerializer.Serialize(lines));
            }
            catch (Exception ex) { File.WriteAllText(e.Args[2] + ".err", ex.ToString()); }
            Shutdown();
            return;
        }
        if (e.Args.Length >= 4 && e.Args[0] == "--probe")
        {
            // Diagnostic: what every source returns for one song.
            _ = Probe(e.Args[1], e.Args[2], int.Parse(e.Args[3]), e.Args.Length > 4 ? e.Args[4] : "probe.txt");
            return;
        }
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest" && e.Args[2] == "app")
        {
            // The whole app with fake playback (see SelfTestApp).
            _single = new Mutex(true, "LycRomanise.Modern.SingleInstance", out _);
            DispatcherUnhandledException += (_, ex) => { Log.Error("ui", ex.Exception); ex.Handled = true; };
            TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };
            _controller = new AppController();
            _controller.Start(openSettings: false);
            var outDir = e.Args[1];
            Dispatcher.InvokeAsync(async () => { await SelfTestApp.RunAsync(_controller, outDir); Shutdown(); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            var mode = e.Args.Length > 2 ? e.Args[2] : "all";
            (mode == "deep" ? SelfTestDeep.RunAsync(e.Args[1]) : mode == "fuzz" ? SelfTestFuzz.RunAsync(e.Args[1]) : SelfTest.RunAsync(e.Args[1], mode)).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }
        _single = new Mutex(true, "LycRomanise.Modern.SingleInstance", out bool first);
        if (!first)
        {
            UI.IpcSignal.Send(e.Args.Contains("--lyrics") ? UI.IpcSignal.Lyrics : UI.IpcSignal.Settings);
            Shutdown();
            return;
        }
        // Made before anything slow, so a second launch during start-up is not lost.
        var ipcSettings = UI.IpcSignal.Create(UI.IpcSignal.Settings);
        var ipcLyrics = UI.IpcSignal.Create(UI.IpcSignal.Lyrics);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Info("FATAL " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };
        Log.Info($"--- LycRomanise {typeof(App).Assembly.GetName().Version} starting");
        try
        {
            _controller = new AppController { IpcSettingsHandle = ipcSettings, IpcLyricsHandle = ipcLyrics };
            _controller.Start(openSettings: e.Args.Contains("--settings"));
        }
        catch (Exception ex)
        {
            // Without this the error is swallowed and an invisible copy keeps the single-instance lock forever.
            Log.Info("FATAL startup " + ex);
            MessageBox.Show("LycRomanise could not start:\n" + ex.Message, "LycRomanise", MessageBoxButton.OK, MessageBoxImage.Error);
            try { _controller?.Dispose(); } catch { }
            _controller = null;
            try { _single?.ReleaseMutex(); } catch { }
            _single?.Dispose();
            _single = null;
            Shutdown(1);
            return;
        }
        // Only once start-up worked: later UI errors are logged and the app keeps going.
        DispatcherUnhandledException += (_, ex) => { Log.Error("ui", ex.Exception); ex.Handled = true; };
        // Windows does not close windows on logoff/shutdown before ending the process: save now.
        SessionEnding += (_, _) => _controller?.FlushForSessionEnd();
        if (e.Args.Contains("--lyrics")) _controller.ShowLyricsWindow();
        int demo = Array.IndexOf(e.Args, "--demo-looks");
        if (demo >= 0) _ = _controller.DemoLooksAsync(demo + 1 < e.Args.Length && double.TryParse(e.Args[demo + 1], out var sec) ? sec : 8);
    }

    async Task Probe(string artist, string title, int seconds, string outPath)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var settings = AppSettings.Load();
            var chain = new Sources.LyricsChain(() => settings);
            var q = new TrackQuery { Artist = artist, Title = title, Seconds = seconds };
            await chain.Musixmatch.Identify(q, default);
            sb.AppendLine($"{artist} - {title} ({seconds}s) spotify={q.SpotifyId} isrc={q.Isrc}");
            var script = (string?)null;
            foreach (var src in chain.All.Values)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                LyricsResult? r = null;
                try { r = await src.AskAsync(q, new CancellationTokenSource(10000).Token); }
                catch (Exception ex) { sb.AppendLine($"[{src.Key}] EXCEPTION {ex.Message}"); continue; }
                sb.AppendLine($"[{src.Key}] {(r == null ? "nothing" : $"{r.Provider}: {r.Lines.Count} lines, {r.Timing}, pron={r.Lines.Count(l => l.Pronunciation != null)}, bg={r.Lines.Count(l => l.Backing != null)}, duet={r.Lines.Count(l => l.Align == LineAlign.Trailing)}")} {sw.ElapsedMilliseconds}ms");
                if (r is { HasLines: true })
                {
                    script ??= Romanize.Romanizer.SongScript(r.Lines);
                    foreach (var l in r.Lines.Skip(2).Take(2))
                    {
                        sb.AppendLine($"    {l.Start,7} {l.Text}");
                        var rom = l.Pronunciation ?? Romanize.Romanizer.ForLine(l, script);
                        if (rom != null) sb.AppendLine($"            {rom.Text}{(l.Pronunciation != null ? "  (source)" : "")}");
                    }
                }
            }
        }
        catch (Exception ex) { sb.AppendLine("FAILED " + ex); }
        File.WriteAllText(outPath, sb.ToString());
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
