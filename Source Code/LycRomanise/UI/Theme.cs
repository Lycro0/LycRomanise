using System.Windows;
using System.Windows.Media;
using LycRomanise.Core;

namespace LycRomanise.UI;

public static class Theme
{
    public static Color ParseColor(string? text, Color fallback)
    {
        try { return text == null ? fallback : (Color)ColorConverter.ConvertFromString(text); }
        catch { return fallback; }
    }

    public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static void Apply(ThemeChoice choice)
    {
#pragma warning disable WPF0001
        Application.Current.ThemeMode = choice switch
        {
            ThemeChoice.Dark => ThemeMode.Dark,
            ThemeChoice.Light => ThemeMode.Light,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        Brand();
    }

    // Red in place of the Windows accent: the Fluent theme bakes the accent into its own brushes, so they are recoloured in place.
    static void Brand()
    {
        var app = Application.Current.Resources;
        var red = Color.FromRgb(0xE5, 0x26, 0x3B);
        bool dark = Application.Current.ThemeMode == ThemeMode.Dark || (Application.Current.ThemeMode == ThemeMode.System && IsSystemDark());
        IsDark = dark;
        if (dark) { app["LayerFillColorDefaultBrush"] = Frozen(Color.FromRgb(0x10, 0x10, 0x12)); }
        var shades = new (string key, Color to)[]
        {
            ("SystemAccentColor", red),
            ("SystemAccentColorLight1", Color.FromRgb(0xF0, 0x4A, 0x5C)), ("SystemAccentColorLight2", Color.FromRgb(0xFF, 0x6B, 0x7A)), ("SystemAccentColorLight3", Color.FromRgb(0xFF, 0x9A, 0xA4)),
            ("SystemAccentColorDark1", Color.FromRgb(0xC2, 0x1A, 0x2E)), ("SystemAccentColorDark2", Color.FromRgb(0x9A, 0x12, 0x23)), ("SystemAccentColorDark3", Color.FromRgb(0x6E, 0x0B, 0x18)),
        };
        var map = new Dictionary<Color, Color>();
        // .NET 9+ exposes the Windows accent as SystemColors keys; the Fluent theme looks those up.
        var sys = new (object colorKey, object brushKey, Color from, Color to)[]
        {
            (SystemColors.AccentColorKey, SystemColors.AccentColorBrushKey, SystemColors.AccentColor, shades[0].to),
            (SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight1BrushKey, SystemColors.AccentColorLight1, shades[1].to),
            (SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight2BrushKey, SystemColors.AccentColorLight2, shades[2].to),
            (SystemColors.AccentColorLight3Key, SystemColors.AccentColorLight3BrushKey, SystemColors.AccentColorLight3, shades[3].to),
            (SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark1BrushKey, SystemColors.AccentColorDark1, shades[4].to),
            (SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark2BrushKey, SystemColors.AccentColorDark2, shades[5].to),
            (SystemColors.AccentColorDark3Key, SystemColors.AccentColorDark3BrushKey, SystemColors.AccentColorDark3, shades[6].to),
        };
        foreach (var (ck, bk, from, to) in sys) { map[Opaque(from)] = to; app[ck] = to; app[bk] = Frozen(to); }
        foreach (var (key, to) in shades)
            if (app.Contains(key) && app[key] is Color from) map[from] = to;
        var bgFrom = new HashSet<Color>();
        if (dark) foreach (var k in new[] { "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush", "SolidBackgroundFillColorBase" })
            if (app.Contains(k)) { if (app[k] is SolidColorBrush sb) bgFrom.Add(sb.Color); else if (app[k] is Color c) bgFrom.Add(c); }
        var black = Color.FromRgb(0x0A, 0x0A, 0x0B);
        foreach (var d in Walk(app))
            foreach (var key in d.Keys.Cast<object>().ToList())
            {
                switch (d[key])
                {
                    case Color c when map.TryGetValue(c, out var n): d[key] = n; break;
                    case Color c when bgFrom.Contains(c): d[key] = black; break;
                    case SolidColorBrush b when map.TryGetValue(Opaque(b.Color), out var n):
                        d[key] = Frozen(Color.FromArgb(b.Color.A, n.R, n.G, n.B)); break;
                    case SolidColorBrush b when b.Color.A == 255 && bgFrom.Contains(b.Color): d[key] = Frozen(black); break;
                }
            }
        foreach (var (key, to) in shades) app[key] = to;
        LycRomanise.Core.Log.Info($"theme: {Application.Current.ThemeMode.Value}, dicts={Walk(app).Count()}, accents={map.Count}, bg={bgFrom.Count}, keys={string.Join(',', Walk(app).Select(x => x.Source?.ToString() ?? x.Count.ToString()))}");
    }

    public static bool IsDark { get; private set; }
    // Near-black instead of Windows' grey material in dark mode.
    public static void Paint(Window w)
    {
        if (IsDark) w.Background = Frozen(Color.FromRgb(0x05, 0x05, 0x06));
        else w.ClearValue(Window.BackgroundProperty);
    }

    static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static IEnumerable<ResourceDictionary> Walk(ResourceDictionary d)
    {
        yield return d;
        foreach (var m in d.MergedDictionaries) foreach (var x in Walk(m)) yield return x;
    }

    static bool IsSystemDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}

// Lets a second launch tell the running copy to open its settings or lyrics window.
public static class IpcSignal
{
    public const string Settings = "LycRomanise.Modern.OpenSettings";
    public const string Lyrics = "LycRomanise.Modern.OpenLyrics";

    public static void Send(string name = Settings)
    {
        // The running copy may still be starting: keep trying for a few seconds before giving up.
        for (int i = 0; i < 30; i++)
        {
            if (EventWaitHandle.TryOpenExisting(name, out var ev)) { using (ev) ev.Set(); return; }
            Thread.Sleep(100);
        }
        LycRomanise.Core.Log.Info($"ipc: {name} not answered");
    }

    // Created early so a signal sent before Listen runs is kept (auto-reset holds it).
    public static EventWaitHandle Create(string name) => new(false, EventResetMode.AutoReset, name);

    public static void Listen(Action onSignal, string name = Settings, EventWaitHandle? existing = null)
    {
        var ev = existing ?? Create(name);
        var thread = new Thread(() =>
        {
            while (true) { ev.WaitOne(); onSignal(); }
        }) { IsBackground = true, Name = "ipc" };
        thread.Start();
    }
}
