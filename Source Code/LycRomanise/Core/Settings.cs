using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LycRomanise.Core;

public enum DisplayMode { RomanizedOnly, OriginalAndRomanized, OriginalOnly }
public enum ThemeChoice { System, Dark, Light }
public enum NowPlayingFormat { ArtistTitle, TitleArtist, Title }
public enum StripAlign { Center, Left, Right }
public enum StripStyle { Classic, Modern, Minimal }
public enum LyricsMotion { AppleMusic, Glow, Float, Calm, Snappy }
public enum LyricsBackground { Cover, Black }

public sealed class SourceEntry
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class AppSettings
{
    // ---- Look ----
    public StripStyle StripStyle { get; set; } = StripStyle.Minimal;
    public double WordLift { get; set; } = 4;
    public bool WordGlow { get; set; } = true;
    public string LookPreset { get; set; } = "Minimal";
    public LyricsMotion LyricsMotion { get; set; } = LyricsMotion.AppleMusic;
    public LyricsBackground LyricsBackground { get; set; } = LyricsBackground.Cover;
    public bool LyricsAutoHideTools { get; set; } = true;
    public bool LyricsPanelHidden { get; set; }
    public string UnsungColor { get; set; } = "#B3FFFFFF";
    public string SungColor { get; set; } = "#FFFFFF";
    public string NextLineColor { get; set; } = "#80FFFFFF";
    public string OutlineColor { get; set; } = "#000000";
    public string SubColor { get; set; } = "#B3FFFFFF";
    public string BackgroundColor { get; set; } = "#1C1C24";
    public double BackgroundOpacity { get; set; } = 0.0;           // 0 = see-through strip, text only
    public double CornerRadius { get; set; } = 22;
    public string FontFamily { get; set; } = "Segoe UI Variable Display";
    public double FontSize { get; set; } = 28;
    public bool Bold { get; set; } = false;
    public double OutlineWidth { get; set; } = 1.5;
    public bool Glow { get; set; } = true;
    public DisplayMode Display { get; set; } = DisplayMode.RomanizedOnly;
    public bool ShowTranslation { get; set; } = false;
    public string TranslationLanguage { get; set; } = "en";
    public bool ShowNextLine { get; set; } = true;
    public bool ShowBackingVocals { get; set; } = true;
    public bool ShowTitleCard { get; set; } = true;
    // Not an option: the source is shown only where a source requires it.
    [JsonIgnore] public bool ShowSourceCredit => false;
    public bool SmoothSweep { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;

    // ---- Window ----
    public double StripWidth { get; set; } = 680;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Locked { get; set; }
    public bool HideLockIcon { get; set; } = true;
    public bool ShowInTaskbar { get; set; }
    public StripAlign Align { get; set; } = StripAlign.Center;
    public bool HideWhenPaused { get; set; } = true;

    // ---- Timing ----
    public int GlobalOffsetMs { get; set; }
    public Dictionary<string, int> SongOffsets { get; set; } = new();

    // ---- Sources ----
    public List<SourceEntry> Sources { get; set; } = DefaultSources();
    public string ImportedFolder { get; set; } = Paths.DefaultImported;
    public bool PreferWordSync { get; set; } = true;
    public Dictionary<string, string> SongSourceOverride { get; set; } = new();
    [JsonPropertyName("SpicyKeyProtected")] public string? SpicyKeyProtected { get; set; }

    // ---- Web / OBS ----
    public bool WebEnabled { get; set; } = true;
    public int WebPort { get; set; } = 8765;
    public bool HideDesktopStrip { get; set; }
    public bool HideStripWithLyricsWindow { get; set; } = true;
    public bool NowPlayingFile { get; set; } = true;
    public NowPlayingFormat NowPlayingFormat { get; set; } = NowPlayingFormat.Title;

    // ---- Spotify ----
    public string? SpotifyClientId { get; set; }
    public string? SpotifyRefreshProtected { get; set; }
    public bool PrefetchNext { get; set; } = true;

    [JsonIgnore]
    public string? SpotifyRefreshToken
    {
        get => Unprotect(SpotifyRefreshProtected);
        set => SpotifyRefreshProtected = Protect(value);
    }

    // ---- Full lyrics window ----
    public double LyricsFontSize { get; set; } = 36;
    public double? LyricsLeft { get; set; }
    public double? LyricsTop { get; set; }
    public double LyricsWidth { get; set; } = 1180;
    public double LyricsHeight { get; set; } = 760;
    public bool LyricsMaximized { get; set; }
    public bool LyricsAlwaysOnTop { get; set; }

    static string? Unprotect(string? p)
    {
        if (string.IsNullOrEmpty(p)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(p), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    static string? Protect(string? v) => string.IsNullOrWhiteSpace(v) ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(v.Trim()), null, DataProtectionScope.CurrentUser));

    // ---- General ----
    public bool StartWithWindows { get; set; } = true;
    public bool GlobalHotkeys { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public bool OnlyMusicApps { get; set; } = true;

    public static readonly string[] SourceOrder =
        { "spicy", "bini", "imported", "musixmatch", "unison", "netease", "lrclib", "qq", "kugou", "genius" };

    public static List<SourceEntry> DefaultSources() => SourceOrder.Select(k => new SourceEntry { Key = k }).ToList();

    [JsonIgnore]
    public string? SpicyKey
    {
        get
        {
            if (string.IsNullOrEmpty(SpicyKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(SpicyKeyProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            SpicyKeyProtected = string.IsNullOrWhiteSpace(value) ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    // ---- persistence ----
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    // Set when settings.json could not be read at all: this session runs on defaults and must not overwrite it.
    static bool _readOnly;

    public static AppSettings Load()
    {
        AppSettings s;
        bool first = !File.Exists(Paths.Settings);
        string? text = null;
        bool unreadable = false;
        if (!first)
        {
            // A backup or sync tool can hold the file briefly at logon; wait a little rather than treat it as broken.
            for (int i = 0; ; i++)
            {
                try { text = File.ReadAllText(Paths.Settings); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (i >= 4) { Log.Error("settings read", ex); unreadable = true; break; }
                    Thread.Sleep(150);
                }
            }
        }
        if (unreadable)
        {
            _readOnly = true;
            s = new();
            s.Sanitize();
            return s;
        }
        _readOnly = false;
        try { s = text == null ? new() : JsonSerializer.Deserialize<AppSettings>(text, Json) ?? new(); }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
            try { File.Copy(Paths.Settings, Paths.Settings + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
            s = Salvage(text!);
        }
        if (first) s.ImportFromV1();
        else if (s.DefaultsVersion < CurrentDefaults)
        {
            s.ApplyCurrentDefaults();
            Log.Info($"settings: updated to the v{CurrentDefaults} default setup");
        }
        // Never lower it: an older build writing its own number back would make the newer one reset the look again.
        s.DefaultsVersion = Math.Max(s.DefaultsVersion, CurrentDefaults);
        s.Sanitize();
        if (!first) s.Save();
        return s;
    }

    // Settings from a newer build, kept so a save does not drop them.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    // Each setting is tried on its own, so one bad value only resets that setting.
    static AppSettings Salvage(string text)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is not System.Text.Json.Nodes.JsonObject obj)
                return new();
            var kept = new System.Text.Json.Nodes.JsonObject();
            foreach (var (name, value) in obj.ToList())
            {
                var one = new System.Text.Json.Nodes.JsonObject { [name] = value?.DeepClone() };
                try { JsonSerializer.Deserialize<AppSettings>(one.ToJsonString(), Json); kept[name] = value?.DeepClone(); }
                catch { Log.Info($"settings: ignored unreadable value for {name}"); }
            }
            return JsonSerializer.Deserialize<AppSettings>(kept.ToJsonString(), Json) ?? new();
        }
        catch { return new(); }
    }

    // Raised when the default setup changes; existing installs then take the new one once.
    public const int CurrentDefaults = 3;
    // 0 in files written before this existed, so those installs take the default setup once.
    public int DefaultsVersion { get; set; }

    // Personal state (positions, lock, per-song choices, logins, keys, folders) is left out on purpose.
    static readonly string[] SetupSettings =
    {
        nameof(StripStyle), nameof(WordLift), nameof(WordGlow), nameof(LookPreset), nameof(LyricsMotion), nameof(LyricsBackground),
        nameof(LyricsAutoHideTools), nameof(UnsungColor), nameof(SungColor), nameof(NextLineColor), nameof(OutlineColor), nameof(SubColor),
        nameof(BackgroundColor), nameof(BackgroundOpacity), nameof(CornerRadius), nameof(FontFamily), nameof(FontSize), nameof(Bold),
        nameof(OutlineWidth), nameof(Glow), nameof(Display), nameof(ShowTranslation), nameof(TranslationLanguage), nameof(ShowNextLine),
        nameof(ShowBackingVocals), nameof(ShowTitleCard), nameof(SmoothSweep), nameof(Theme), nameof(StripWidth), nameof(HideLockIcon),
        nameof(ShowInTaskbar), nameof(Align), nameof(HideWhenPaused), nameof(PreferWordSync), nameof(WebEnabled), nameof(HideDesktopStrip),
        nameof(HideStripWithLyricsWindow), nameof(NowPlayingFile), nameof(NowPlayingFormat), nameof(PrefetchNext), nameof(StartWithWindows),
        nameof(GlobalHotkeys), nameof(CheckUpdates), nameof(OnlyMusicApps),
    };

    void ApplyCurrentDefaults()
    {
        var d = new AppSettings();
        foreach (var name in SetupSettings)
        {
            var p = typeof(AppSettings).GetProperty(name)!;
            p.SetValue(this, p.GetValue(d));
        }
    }

    public void Save()
    {
        if (_readOnly) return;
        try { Paths.WriteAtomic(Paths.Settings, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    public AppSettings Copy() { var c = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!; c.Sanitize(); return c; }

    void ImportFromV1()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LycRomanise", "appearance.json");
            if (!File.Exists(path)) return;
            var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            string? Rgb(string key) => j?[key] is System.Text.Json.Nodes.JsonArray a && a.Count >= 3
                ? $"#{(int)a[0]!:X2}{(int)a[1]!:X2}{(int)a[2]!:X2}" : null;
            UnsungColor = Rgb("karaoke_unsung_rgb") ?? UnsungColor;
            SungColor = Rgb("karaoke_sung_rgb") ?? SungColor;
            NextLineColor = Rgb("next_line_rgb") ?? NextLineColor;
            OutlineColor = Rgb("outline_rgb") ?? OutlineColor;
            if (j?["desktop_width"] is { } w) StripWidth = Math.Max((double)w, 600);
            if (j?["lyric_offset_ms"] is { } o) GlobalOffsetMs = (int)(double)o;
            if (j?["hide_lock_icon"] is { } h) HideLockIcon = (bool)h;
            if (j?["show_title_card"] is { } t) ShowTitleCard = (bool)t;
            if (j?["mobile_enabled"] is { } m) WebEnabled = (bool)m;
            if (j?["mobile_port"] is { } p) WebPort = (int)p;
            if (j?["hide_desktop_strip"] is { } hd) HideDesktopStrip = (bool)hd;
            if (j?["show_in_taskbar"] is { } tb) ShowInTaskbar = (bool)tb;
            Log.Info("imported settings from LycRomanise 1.x");
        }
        catch (Exception ex) { Log.Error("import v1 settings", ex); }
    }

    void Sanitize()
    {
        // A source added in a later version joins the end of the user's list; unknown ones drop out.
        Sources ??= DefaultSources();
        Sources = Sources.Where(e => SourceOrder.Contains(e.Key)).GroupBy(e => e.Key).Select(g => g.First()).ToList();
        foreach (var key in SourceOrder)
            if (Sources.All(e => e.Key != key)) Sources.Add(new SourceEntry { Key = key });
        static double Ok(double v, double fallback) => double.IsFinite(v) ? v : fallback;
        WordLift = Math.Clamp(Ok(WordLift, 0), 0, 20);
        CornerRadius = Math.Clamp(Ok(CornerRadius, 16), 0, 60);
        LyricsFontSize = Math.Clamp(Ok(LyricsFontSize, 36), 18, 110);
        LyricsWidth = Math.Clamp(Ok(LyricsWidth, 1180), 520, 10000);
        LyricsHeight = Math.Clamp(Ok(LyricsHeight, 760), 420, 10000);
        FontSize = Ok(FontSize, 34); StripWidth = Ok(StripWidth, 900); BackgroundOpacity = Ok(BackgroundOpacity, 0); OutlineWidth = Ok(OutlineWidth, 3);
        if (Left is double l && !double.IsFinite(l)) Left = null;
        if (Top is double tp && !double.IsFinite(tp)) Top = null;
        FontSize = Math.Clamp(FontSize, 12, 120);
        StripWidth = Math.Clamp(StripWidth, 300, 4000);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        OutlineWidth = Math.Clamp(OutlineWidth, 0, 10);
        GlobalOffsetMs = Math.Clamp(GlobalOffsetMs, -30000, 30000);
        WebPort = Math.Clamp(WebPort, 1024, 65535);
        SongOffsets ??= new();
        SongSourceOverride ??= new();
        if (string.IsNullOrWhiteSpace(ImportedFolder)) ImportedFolder = Paths.DefaultImported;
        ImportedFolder = Paths.FromOld(ImportedFolder);
    }
}
