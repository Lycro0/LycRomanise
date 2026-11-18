using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LycRomanise.Core;

public enum DisplayMode { RomanizedOnly, OriginalAndRomanized, OriginalOnly }
public enum ThemeChoice { System, Dark, Light }
public enum NowPlayingFormat { ArtistTitle, TitleArtist, Title }
public enum StripAlign { Center, Left, Right }
/// <summary>How the strip lays its lines out.</summary>
public enum StripStyle { Classic, Modern, Minimal }
/// <summary>How the full lyrics window moves.</summary>
public enum LyricsMotion { AppleMusic, Glow, Float, Calm, Snappy }

public sealed class SourceEntry
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class AppSettings
{
    // ---- Look ----
    public StripStyle StripStyle { get; set; } = StripStyle.Classic;
    public double WordLift { get; set; }
    public bool WordGlow { get; set; }
    public string LookPreset { get; set; } = "Classic";
    public LyricsMotion LyricsMotion { get; set; } = LyricsMotion.AppleMusic;
    public string UnsungColor { get; set; } = "#FFE14D";
    public string SungColor { get; set; } = "#FF3D9A";
    public string NextLineColor { get; set; } = "#FFE14D";
    public string OutlineColor { get; set; } = "#000000";
    public string SubColor { get; set; } = "#E6FFFFFF";           // romanization / translation under the line
    public string BackgroundColor { get; set; } = "#101014";
    public double BackgroundOpacity { get; set; } = 0.0;           // 0 = see-through strip, text only
    public double CornerRadius { get; set; } = 16;
    public string FontFamily { get; set; } = "Segoe UI Variable Display";
    public double FontSize { get; set; } = 34;
    public bool Bold { get; set; } = true;
    public double OutlineWidth { get; set; } = 3;
    public bool Glow { get; set; } = true;
    public DisplayMode Display { get; set; } = DisplayMode.RomanizedOnly;
    public bool ShowTranslation { get; set; } = false;
    public string TranslationLanguage { get; set; } = "en";
    public bool ShowNextLine { get; set; } = true;
    public bool ShowBackingVocals { get; set; } = true;
    public bool ShowTitleCard { get; set; } = true;
    public bool ShowSourceCredit { get; set; } = true;
    public bool SmoothSweep { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    // ---- Window ----
    public double StripWidth { get; set; } = 900;
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
    /// <summary>Keep asking down the list for word timing when a source only times lines.</summary>
    public bool PreferWordSync { get; set; } = true;
    public Dictionary<string, string> SongSourceOverride { get; set; } = new();
    [JsonPropertyName("SpicyKeyProtected")] public string? SpicyKeyProtected { get; set; }

    // ---- Web / OBS ----
    public bool WebEnabled { get; set; }
    public int WebPort { get; set; } = 8765;
    public bool HideDesktopStrip { get; set; }
    public bool NowPlayingFile { get; set; } = true;
    public NowPlayingFormat NowPlayingFormat { get; set; } = NowPlayingFormat.ArtistTitle;

    // ---- Spotify ----
    public string? SpotifyClientId { get; set; }
    public string? SpotifyRefreshProtected { get; set; }
    /// <summary>Looks up the lyrics of the next song in the queue ahead of time.</summary>
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
    public bool StartWithWindows { get; set; }
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
    };

    public static AppSettings Load()
    {
        AppSettings s;
        bool first = !File.Exists(Paths.Settings);
        try
        {
            s = File.Exists(Paths.Settings)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings), Json) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Log.Error("settings load", ex);
            s = new();
        }
        if (first) s.ImportFromV1();
        s.Sanitize();
        return s;
    }

    public void Save()
    {
        try { Paths.WriteAtomic(Paths.Settings, JsonSerializer.Serialize(this, Json)); }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    public AppSettings Copy() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>On the first start, the colours, size and toggles from LycRomanise 1.x (appearance.json).</summary>
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
        FontSize = Math.Clamp(FontSize, 12, 120);
        StripWidth = Math.Clamp(StripWidth, 300, 4000);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 1);
        OutlineWidth = Math.Clamp(OutlineWidth, 0, 10);
        GlobalOffsetMs = Math.Clamp(GlobalOffsetMs, -30000, 30000);
        WebPort = Math.Clamp(WebPort, 1024, 65535);
        SongOffsets ??= new();
        SongSourceOverride ??= new();
        if (string.IsNullOrWhiteSpace(ImportedFolder)) ImportedFolder = Paths.DefaultImported;
    }
}
