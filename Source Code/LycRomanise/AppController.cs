using System.Windows;
using System.Windows.Threading;
using LycRomanise.Core;
using LycRomanise.Media;
using LycRomanise.Romanize;
using LycRomanise.Sources;
using LycRomanise.UI;
using LycRomanise.Web;

namespace LycRomanise;

/// <summary>Ties it together: what is playing, the lyrics for it, and every place they are shown.</summary>
public sealed class AppController : IDisposable
{
    public AppSettings Settings { get; private set; }
    public LyricsChain Chain { get; }
    public MediaWatcher Media { get; }
    public TrayIcon? Tray { get; private set; }
    public OverlayWindow Overlay { get; private set; } = null!;
    public WebServer Web { get; }
    public bool CreditRequired { get; private set; }
    public SpotifyClient Spotify { get; }
    /// <summary>The cover of what is playing: Spotify's 640 px image when logged in, else the player's thumbnail.</summary>
    public System.Windows.Media.Imaging.BitmapSource? Cover { get; private set; }
    public event Action? LyricsChanged;
    public event Action? TrackChanged;
    public UpdateInfo? Update { get; private set; }
    public event Action? UpdateChanged;
    Hotkeys? _hotkeys;
    DispatcherTimer? _updateTimer;

    readonly Dispatcher _ui;
    TrackQuery? _query;
    string? _trackKey;
    CancellationTokenSource? _fetch;
    LyricsResult? _result;
    List<DisplayLine> _display = new();
    SettingsWindow? _settingsWindow;
    PickerWindow? _picker;
    LyricsWindow? _lyricsWindow;
    CancellationTokenSource? _prefetch;
    string? _prefetchedId;
    string _nowPlayingText = "";

    public AppController()
    {
        _ui = Dispatcher.CurrentDispatcher;
        Settings = AppSettings.Load();
        Chain = new LyricsChain(() => Settings);
        Media = new MediaWatcher(() => Settings.OnlyMusicApps);
        Web = new WebServer(this);
        Spotify = new SpotifyClient(() => Settings, SaveSettings);
    }

    public void Start(bool openSettings)
    {
        Theme.Apply(Settings.Theme);
        Overlay = new OverlayWindow(this);
        Overlay.ApplySettings(Settings);
        Overlay.PlaceInitially(Settings);
        Overlay.Karaoke.SetClock(Clock);
        Overlay.Karaoke.ShowTitleCard("LycRomanise — play a song");
        if (!Settings.HideDesktopStrip) Overlay.Show();
        Tray = new TrayIcon(this);
        Media.Changed += s => _ui.BeginInvoke(() => OnMedia(s));
        _ = Media.StartAsync();
        if (Settings.WebEnabled) Web.Start(Settings.WebPort);
        IpcSignal.Listen(() => _ui.BeginInvoke(ShowSettings));
        IpcSignal.Listen(() => _ui.BeginInvoke(ShowLyricsWindow), IpcSignal.Lyrics);
        // Windows' own Run entry is the truth (the installer may have set it), not the saved setting.
        Settings.StartWithWindows = Autostart.IsOn();
        if (openSettings) ShowSettings();
        if (Spotify.LoggedIn) _ = Spotify.LoadProfileAsync(CancellationToken.None);
        ApplyHotkeys();
        // A first look for updates shortly after starting, then every six hours.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += (_, _) => { _updateTimer.Interval = TimeSpan.FromHours(6); _ = CheckForUpdateAsync(false); };
        _updateTimer.Start();
    }

    void ApplyHotkeys()
    {
        _hotkeys ??= new Hotkeys();
        _hotkeys.Clear();
        if (!Settings.GlobalHotkeys) return;
        _hotkeys.Register(0x4C /* L */, ToggleLyricsWindow);
        _hotkeys.Register(0x4B /* K */, () => SetLocked(!Settings.Locked));
        _hotkeys.Register(0x48 /* H */, () => { var c = Settings.Copy(); c.HideDesktopStrip = !c.HideDesktopStrip; ApplySettings(c, false); });
        _hotkeys.Register(0x25 /* Left */, () => NudgeOffset(250));
        _hotkeys.Register(0x27 /* Right */, () => NudgeOffset(-250));
        _hotkeys.Register(0x20 /* Space */, () => _ = Media.TogglePlayAsync());
    }

    /// <summary>Looks for a newer release on GitHub. manual: say so even when there is none.</summary>
    public async Task CheckForUpdateAsync(bool manual)
    {
        if (!manual && !Settings.CheckUpdates) return;
        var info = await Updater.CheckAsync(CancellationToken.None);
        Update = info;
        UpdateChanged?.Invoke();
        if (info == null) { if (manual) Tray?.Toast($"You're on the latest version ({Updater.CurrentText})."); return; }
        if (!manual && Settings.SkippedVersion == info.Tag) return;
        Tray?.Toast($"LycRomanise {info.Tag} is available. Open the tray menu or Settings → General to update.");
    }

    public async Task<string?> InstallUpdateAsync(IProgress<double>? progress)
    {
        if (Update == null) return "No update to install.";
        var err = await Updater.InstallAsync(Update, progress, CancellationToken.None);
        if (err == null) Quit();
        return err;
    }

    public void ToggleLyricsWindow()
    {
        if (_lyricsWindow != null && _lyricsWindow.IsActive) _lyricsWindow.Close();
        else ShowLyricsWindow();
    }

    // ------------------------------------------------------------------ clock
    /// <summary>Where the song is, shifted by the offsets (positive shows lyrics earlier).</summary>
    public long Clock()
    {
        var m = Media.Current;
        if (m == null) return 0;
        int song = _query != null && Settings.SongOffsets.TryGetValue(_query.Key, out var o) ? o : 0;
        return m.PositionNow() + Settings.GlobalOffsetMs + song;
    }

    public int SongOffset => _query != null && Settings.SongOffsets.TryGetValue(_query.Key, out var o) ? o : 0;

    public void NudgeOffset(int ms)
    {
        if (_query == null) return;
        var v = Math.Clamp(SongOffset + ms, -30000, 30000);
        if (v == 0) Settings.SongOffsets.Remove(_query.Key); else Settings.SongOffsets[_query.Key] = v;
        SaveSettings();
        Tray?.Toast($"This song: lyrics {(v >= 0 ? "+" : "")}{v / 1000.0:0.00}s");
        _settingsWindow?.RefreshSongInfo();
    }

    // ------------------------------------------------------------------ media
    public MediaState? NowPlaying => Media.Current;
    public TrackQuery? CurrentQuery => _query;
    public LyricsResult? CurrentResult => _result;
    public IReadOnlyList<DisplayLine> CurrentLines => _display;

    void OnMedia(MediaState? s)
    {
        bool hasSong = s != null && !MediaWatcher.IsAdOrDj(s);
        var key = hasSong ? s!.TrackKey : null;
        if (key != _trackKey)
        {
            _trackKey = key;
            if (hasSong) BeginTrack(s!);
            else ClearTrack();
        }
        UpdateVisibility();
        Web.Notify();
    }

    void ClearTrack()
    {
        _fetch?.Cancel();
        _query = null;
        _result = null;
        _display = new();
        Overlay.Karaoke.SetLyrics(_display, true, 0, null);
        Overlay.Karaoke.ShowTitleCard(null);
        Overlay.SetCredit(null);
        WriteNowPlaying("");
        Cover = null;
        TrackChanged?.Invoke();
        LyricsChanged?.Invoke();
    }

    void BeginTrack(MediaState s)
    {
        _fetch?.Cancel();
        _fetch = new CancellationTokenSource();
        var ct = _fetch.Token;
        _query = new TrackQuery
        {
            Title = s.Title, Artist = s.Artist, Album = s.Album,
            Seconds = s.DurationMs > 0 ? (int)Math.Round(s.DurationMs / 1000.0) : 0,
        };
        _result = null;
        _display = new();
        CreditRequired = false;
        Overlay.Karaoke.SetLyrics(_display, true, s.DurationMs, null);
        Overlay.Karaoke.ShowTitleCard(Settings.ShowTitleCard ? $"{s.Artist} — {s.Title}" : null);
        Overlay.SetCredit(null);
        WriteNowPlaying(Settings.NowPlayingFormat switch
        {
            NowPlayingFormat.TitleArtist => $"{s.Title} - {s.Artist}",
            NowPlayingFormat.Title => s.Title,
            _ => $"{s.Artist} - {s.Title}",
        });
        _settingsWindow?.RefreshSongInfo();
        Cover = null;
        TrackChanged?.Invoke();
        LyricsChanged?.Invoke();
        _ = LoadCoverAsync(_query, null, ct);
        _ = LoadAsync(_query, ct, useCache: true);
    }

    /// <summary>The player's own thumbnail first, as it is there at once; Spotify's larger cover replaces it.</summary>
    async Task LoadCoverAsync(TrackQuery q, string? url, CancellationToken ct)
    {
        try
        {
            byte[]? bytes = null;
            if (url != null)
            {
                using var resp = await Http.Client.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode) bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            }
            else
            {
                await Task.Delay(400, ct);   // the player sets the cover a moment after the title
                bytes = await Media.ThumbnailAsync();
            }
            if (bytes == null || ct.IsCancellationRequested || q != _query) return;
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            if (url == null && Cover != null) return;   // Spotify's cover already arrived
            Cover = img;
            TrackChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("cover", ex); }
    }

    /// <summary>With a Spotify login, the exact track: its id and ISRC for the sources that look up by
    /// them, and the large cover. Only taken when Spotify is playing the same song the player reports.</summary>
    async Task MatchSpotifyAsync(TrackQuery q, CancellationToken ct)
    {
        if (!Spotify.LoggedIn) return;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(2500);
            var now = await Spotify.CurrentlyPlayingAsync(limit.Token);
            var t = now?.Track;
            if (t == null) return;
            string a = Normalize.MatchKey(Normalize.CleanTitle(t.Title)), b = Normalize.MatchKey(Normalize.CleanTitle(q.Title));
            if (a.Length == 0 || !(a == b || a.StartsWith(b) || b.StartsWith(a))) return;
            q.SpotifyId = t.Id;
            q.Isrc ??= t.Isrc;
            if (q.Seconds <= 0) q.Seconds = (int)Math.Round(t.DurationMs / 1000.0);
            if (q.Album.Length == 0) q.Album = t.Album;
            Log.Info($"spotify: matched {t.Id} ({t.Isrc})");
            if (t.ImageUrl != null) _ = LoadCoverAsync(q, t.ImageUrl, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>Looks up the lyrics of the next song in the Spotify queue, so they are there the moment it starts.</summary>
    async Task PrefetchNextAsync()
    {
        if (!Spotify.LoggedIn || !Settings.PrefetchNext) return;
        _prefetch?.Cancel();
        _prefetch = new CancellationTokenSource();
        var ct = _prefetch.Token;
        try
        {
            await Task.Delay(1500, ct);
            var queue = await Spotify.QueueAsync(ct);
            var next = queue.FirstOrDefault();
            if (next == null || next.Id == _prefetchedId || next.Id == _query?.SpotifyId) return;
            _prefetchedId = next.Id;
            var q = next.ToQuery();
            Log.Info($"prefetch: {q.Artist} - {q.Title}");
            await Task.Run(() => Chain.FetchAsync(q, null, ct, useCache: true), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("prefetch", ex); }
    }

    async Task LoadAsync(TrackQuery q, CancellationToken ct, bool useCache)
    {
        try
        {
            // Spotify reports the length a moment after the title; wait briefly for it.
            for (int i = 0; i < 6 && q.Seconds <= 0 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(250, ct);
                if (Media.Current is { DurationMs: > 0 } m && m.TrackKey == _trackKey) q.Seconds = (int)Math.Round(m.DurationMs / 1000.0);
            }
            await MatchSpotifyAsync(q, ct);
            var result = await Task.Run(() => Chain.FetchAsync(q, p => _ui.BeginInvoke(() => Tray?.SetStatus(p)), ct, useCache), ct);
            if (ct.IsCancellationRequested) return;
            await ShowResultAsync(q, result, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("load", ex); }
    }

    async Task ShowResultAsync(TrackQuery q, LyricsResult? result, CancellationToken ct)
    {
        var settings = Settings;
        List<DisplayLine> display = new();
        if (result is { HasLines: true })
        {
            if (settings.ShowTranslation && result.Lines.All(l => l.Translation == null) && !string.IsNullOrEmpty(settings.TranslationLanguage))
            {
                try
                {
                    var tr = await Chain.Musixmatch.Translations(q, settings.TranslationLanguage, ct);
                    if (tr != null)
                        foreach (var l in result.Lines)
                            if (tr.TryGetValue(MusixmatchSource.LineKey(l.Text), out var t)) l.Translation = t;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error("translations", ex); }
            }
            display = await Task.Run(() => Prepare(result, settings), ct);
        }
        if (ct.IsCancellationRequested || q != _query) return;
        _result = result;
        _display = display;
        CreditRequired = result?.CreditRequired == true;
        var credit = result is { HasLines: true } && (settings.ShowSourceCredit || result.CreditRequired) ? "Lyrics: " + result.Provider : null;
        Overlay.Karaoke.SetLyrics(display, result?.Synced ?? true, Media.Current?.DurationMs ?? q.Seconds * 1000L, credit);
        Overlay.SetCredit(credit);
        if (result == null) Tray?.SetStatus("No lyrics found");
        else if (result.Instrumental && !result.HasLines) { Tray?.SetStatus("Instrumental"); Overlay.Karaoke.ShowTitleCard($"{q.Artist} — {q.Title}  ·  instrumental"); }
        else Tray?.SetStatus($"{result.Provider} · {result.Timing} sync");
        if (result == null && Settings.ShowTitleCard) Overlay.Karaoke.ShowTitleCard($"{q.Artist} — {q.Title}  ·  no lyrics found");
        Web.Notify(lyricsChanged: true);
        _settingsWindow?.RefreshSongInfo();
        LyricsChanged?.Invoke();
        _ = PrefetchNextAsync();
    }

    /// <summary>Romanizes the lines and sets out what each one shows, by the display mode.</summary>
    static List<DisplayLine> Prepare(LyricsResult r, AppSettings s)
    {
        var script = Romanizer.SongScript(r.Lines);
        var list = new List<DisplayLine>(r.Lines.Count);
        foreach (var line in r.Lines)
        {
            LyricLine? roman = null, backingRoman = null;
            if (s.Display != DisplayMode.OriginalOnly && Romanizer.Needs(line.Text))
                roman = line.Pronunciation ?? Romanizer.ForLine(line, script);
            if (line.Backing != null && s.Display == DisplayMode.RomanizedOnly && Romanizer.Needs(line.Backing.Text))
                backingRoman = line.Backing.Pronunciation ?? Romanizer.ForLine(line.Backing, script);
            var main = s.Display == DisplayMode.RomanizedOnly && roman != null ? roman : line;
            list.Add(new DisplayLine
            {
                Main = main,
                Sub = s.Display == DisplayMode.OriginalAndRomanized ? roman : null,
                Translation = line.Translation,
                Backing = backingRoman ?? line.Backing,
                Align = line.Align,
            });
        }
        return list;
    }

    /// <summary>Re-prepares the lines already fetched, after a display setting changed.</summary>
    public void Reprepare()
    {
        if (_query == null) return;
        var q = _query;
        var ct = _fetch?.Token ?? CancellationToken.None;
        _ = ShowResultAsync(q, _result, ct);
    }

    public void Refetch(bool skipCache)
    {
        if (_query == null) return;
        _fetch?.Cancel();
        _fetch = new CancellationTokenSource();
        if (skipCache) Chain.Forget(_query);
        _ = LoadAsync(_query, _fetch.Token, useCache: !skipCache);
    }

    public void UseResult(LyricsResult r, bool rememberForSong)
    {
        if (_query == null) return;
        if (rememberForSong)
        {
            Settings.SongSourceOverride[_query.Key] = r.SourceKey;
            SaveSettings();
        }
        Chain.Remember(_query, r);
        _ = ShowResultAsync(_query, r, _fetch?.Token ?? CancellationToken.None);
    }

    // ------------------------------------------------------------------ visibility
    void UpdateVisibility()
    {
        var m = Media.Current;
        bool playing = m is { Playing: true };
        bool show = !Settings.HideDesktopStrip && (playing || !Settings.HideWhenPaused || _settingsWindow != null);
        if (show && !Overlay.IsVisible) Overlay.Show();
        Overlay.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(250)));
        if (!show) Overlay.IsHitTestVisible = false; else Overlay.IsHitTestVisible = true;
        Overlay.Karaoke.SetAnimating(show && (playing || _settingsWindow != null));
    }

    // ------------------------------------------------------------------ settings
    public void SaveSettings() => Settings.Save();

    public void ApplySettings(AppSettings updated, bool lyricsAffected)
    {
        bool webChanged = updated.WebEnabled != Settings.WebEnabled || updated.WebPort != Settings.WebPort;
        bool sourcesChanged = string.Join(",", updated.Sources.Select(e => e.Key + e.Enabled)) != string.Join(",", Settings.Sources.Select(e => e.Key + e.Enabled))
                              || updated.PreferWordSync != Settings.PreferWordSync || updated.SpicyKeyProtected != Settings.SpicyKeyProtected
                              || updated.ImportedFolder != Settings.ImportedFolder;
        bool themeChanged = updated.Theme != Settings.Theme;
        bool autostartChanged = updated.StartWithWindows != Settings.StartWithWindows;
        bool hotkeysChanged = updated.GlobalHotkeys != Settings.GlobalHotkeys;
        // Keep the strip's live position and lock, which the strip itself changes.
        updated.Left = Settings.Left;
        updated.Top = Settings.Top;
        updated.Locked = Settings.Locked;
        updated.SongOffsets = Settings.SongOffsets;
        updated.SongSourceOverride = Settings.SongSourceOverride;
        updated.SpotifyRefreshProtected = Settings.SpotifyRefreshProtected;
        updated.LyricsAlwaysOnTop = Settings.LyricsAlwaysOnTop;
        updated.LyricsFontSize = Settings.LyricsFontSize;
        updated.LyricsLeft = Settings.LyricsLeft; updated.LyricsTop = Settings.LyricsTop;
        updated.LyricsWidth = Settings.LyricsWidth; updated.LyricsHeight = Settings.LyricsHeight;
        updated.LyricsMaximized = Settings.LyricsMaximized;
        Settings = updated;
        SaveSettings();
        if (themeChanged) Theme.Apply(Settings.Theme);
        if (autostartChanged) Autostart.Apply(Settings.StartWithWindows);
        if (hotkeysChanged) ApplyHotkeys();
        Overlay.ApplySettings(Settings);
        if (webChanged)
        {
            Web.Stop();
            if (Settings.WebEnabled) Web.Start(Settings.WebPort);
        }
        if (sourcesChanged) Refetch(skipCache: false);
        else if (lyricsAffected) Reprepare();
        UpdateVisibility();
        Tray?.Refresh();
    }

    public void SetLocked(bool locked)
    {
        Settings.Locked = locked;
        SaveSettings();
        Overlay.ApplyLock();
        Tray?.Refresh();
        if (locked) Tray?.Toast("Strip locked — unlock it from the tray icon");
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; UpdateVisibility(); };
            _settingsWindow.Show();
            OverlayWindow.ShowWindow(new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Handle, 9 /* SW_RESTORE */);
            UpdateVisibility();
        }
        _settingsWindow.Activate();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
    }

    public void ShowLyricsWindow()
    {
        if (_lyricsWindow == null)
        {
            _lyricsWindow = new LyricsWindow(this);
            _lyricsWindow.Closed += (_, _) => _lyricsWindow = null;
            _lyricsWindow.Show();
            OverlayWindow.ShowWindow(new System.Windows.Interop.WindowInteropHelper(_lyricsWindow).Handle, Settings.LyricsMaximized ? 3 : 9);
        }
        _lyricsWindow.Activate();
    }

    /// <summary>Jumps the song to where a lyric line starts (the offsets taken back out).</summary>
    public void SeekToLyric(long lyricMs)
    {
        long target = lyricMs - Settings.GlobalOffsetMs - SongOffset;
        _ = Media.SeekAsync(Math.Max(0, target - 150));
    }

    public void ShowPicker()
    {
        if (_query == null) { Tray?.Toast("Play a song first"); return; }
        if (_picker != null) { _picker.Activate(); return; }
        _picker = new PickerWindow(this, _query);
        _picker.Closed += (_, _) => _picker = null;
        _picker.Show();
    }

    // ------------------------------------------------------------------ OBS text file
    void WriteNowPlaying(string text)
    {
        if (!Settings.NowPlayingFile || text == _nowPlayingText) return;
        _nowPlayingText = text;
        try { File.WriteAllText(Path.Combine(Paths.Data, "LycRomanise.txt"), text); }
        catch (Exception ex) { Log.Error("now playing file", ex); }
    }

    public void Quit()
    {
        Settings.Save();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _fetch?.Cancel();
        Media.Dispose();
        Web.Stop();
        Tray?.Dispose();
        _hotkeys?.Dispose();
    }
}

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("LycRomanise Modern") != null;
        }
        catch { return false; }
    }

    public static void Apply(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (on) key.SetValue("LycRomanise Modern", $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue("LycRomanise Modern") != null) key.DeleteValue("LycRomanise Modern");
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }
}
