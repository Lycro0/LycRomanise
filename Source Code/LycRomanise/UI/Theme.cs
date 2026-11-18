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
    }
}

/// <summary>Lets a second launch tell the running copy to open its settings.</summary>
public static class IpcSignal
{
    public const string Settings = "LycRomanise.Modern.OpenSettings";
    public const string Lyrics = "LycRomanise.Modern.OpenLyrics";

    public static void Send(string name = Settings)
    {
        try { using var ev = EventWaitHandle.OpenExisting(name); ev.Set(); } catch { }
    }

    public static void Listen(Action onSignal, string name = Settings)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        var thread = new Thread(() =>
        {
            while (true) { ev.WaitOne(); onSignal(); }
        }) { IsBackground = true, Name = "ipc" };
        thread.Start();
    }
}
