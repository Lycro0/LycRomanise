namespace LycRomanise.Core;

/// <summary>A whole look for the strip in one click: layout, colours, outline, glow, background and
/// how the words move. Classic, the way LycRomanise 1.x looked, comes first and is the default.</summary>
public sealed record LookPreset(string Name, string Description, Action<AppSettings> Apply);

public static class LookPresets
{
    static void Colours(AppSettings s, string unsung, string sung, string next, string sub, string outline)
    {
        s.UnsungColor = unsung; s.SungColor = sung; s.NextLineColor = next; s.SubColor = sub; s.OutlineColor = outline;
    }

    public static readonly LookPreset[] All =
    {
        new("Classic", "The original LycRomanise: the line being sung, the next one straight under it, yellow turning pink.", s =>
        {
            s.StripStyle = StripStyle.Classic; s.Display = DisplayMode.RomanizedOnly;
            Colours(s, "#FFE14D", "#FF3D9A", "#FFE14D", "#E6FFFFFF", "#000000");
            s.OutlineWidth = 1.5; s.Glow = true; s.WordLift = 0; s.WordGlow = false; s.Bold = true;
            s.BackgroundOpacity = 0; s.FontSize = 34; s.FontFamily = "Segoe UI";
        }),
        new("Modern", "Original and romanized together, a bolder outline and a soft glow.", s =>
        {
            s.StripStyle = StripStyle.Modern; s.Display = DisplayMode.OriginalAndRomanized;
            Colours(s, "#FFE14D", "#FF3D9A", "#FFE14D", "#E6FFFFFF", "#000000");
            s.OutlineWidth = 3; s.Glow = true; s.WordLift = 0; s.WordGlow = false; s.Bold = true;
            s.BackgroundOpacity = 0; s.FontSize = 34; s.FontFamily = "Segoe UI Variable Display";
        }),
        new("Apple Music", "White words that light up and lift as they're sung, on frosted glass.", s =>
        {
            s.StripStyle = StripStyle.Modern; s.Display = DisplayMode.OriginalAndRomanized;
            Colours(s, "#59FFFFFF", "#FFFFFF", "#73FFFFFF", "#B3FFFFFF", "#000000");
            s.OutlineWidth = 0; s.Glow = true; s.WordLift = 3; s.WordGlow = true; s.Bold = true;
            s.BackgroundColor = "#1C1C24"; s.BackgroundOpacity = 0.55; s.CornerRadius = 22;
            s.FontSize = 34; s.FontFamily = "Segoe UI Variable Display";
        }),
        new("Spotify", "Grey turning white on a dark card, like Spotify's lyrics.", s =>
        {
            s.StripStyle = StripStyle.Modern; s.Display = DisplayMode.OriginalAndRomanized;
            Colours(s, "#7A7A7A", "#FFFFFF", "#9A9A9A", "#B3B3B3", "#000000");
            s.OutlineWidth = 0; s.Glow = false; s.WordLift = 0; s.WordGlow = false; s.Bold = true;
            s.BackgroundColor = "#121212"; s.BackgroundOpacity = 0.9; s.CornerRadius = 14;
            s.FontSize = 32; s.FontFamily = "Segoe UI Variable Display";
        }),
        new("Neon", "Cyan into magenta with a glow around the sung words.", s =>
        {
            s.StripStyle = StripStyle.Modern; s.Display = DisplayMode.OriginalAndRomanized;
            Colours(s, "#7CF5FF", "#FF3DF2", "#7CF5FF", "#E6FFFFFF", "#1A0030");
            s.OutlineWidth = 2; s.Glow = true; s.WordLift = 1.5; s.WordGlow = true; s.Bold = true;
            s.BackgroundOpacity = 0; s.FontSize = 34; s.FontFamily = "Segoe UI Variable Display";
        }),
        new("Karaoke", "Big white words filling blue with a thick outline, like a karaoke screen.", s =>
        {
            s.StripStyle = StripStyle.Classic; s.Display = DisplayMode.RomanizedOnly;
            Colours(s, "#FFFFFF", "#2E7DFF", "#FFFFFF", "#E6FFFFFF", "#000000");
            s.OutlineWidth = 4; s.Glow = false; s.WordLift = 0; s.WordGlow = false; s.Bold = true;
            s.BackgroundOpacity = 0; s.FontSize = 40; s.FontFamily = "Segoe UI";
        }),
        new("Minimal", "Just the line being sung, small and quiet.", s =>
        {
            s.StripStyle = StripStyle.Minimal; s.Display = DisplayMode.RomanizedOnly;
            Colours(s, "#B3FFFFFF", "#FFFFFF", "#80FFFFFF", "#B3FFFFFF", "#000000");
            s.OutlineWidth = 1.5; s.Glow = true; s.WordLift = 0; s.WordGlow = false; s.Bold = false;
            s.BackgroundOpacity = 0; s.FontSize = 28; s.FontFamily = "Segoe UI Variable Display";
        }),
        new("Sunset", "Peach turning coral.", s =>
        {
            s.StripStyle = StripStyle.Modern; s.Display = DisplayMode.OriginalAndRomanized;
            Colours(s, "#FFD1A9", "#FF6B6B", "#FFD1A9", "#E6FFFFFF", "#2A0A0A");
            s.OutlineWidth = 2.5; s.Glow = true; s.WordLift = 0; s.WordGlow = false; s.Bold = true;
            s.BackgroundOpacity = 0; s.FontSize = 34; s.FontFamily = "Segoe UI Variable Display";
        }),
        new("Mint", "Pale green turning bright green.", s =>
        {
            s.StripStyle = StripStyle.Modern; s.Display = DisplayMode.OriginalAndRomanized;
            Colours(s, "#C3F0CA", "#2ED573", "#C3F0CA", "#E6FFFFFF", "#002010");
            s.OutlineWidth = 2.5; s.Glow = true; s.WordLift = 0; s.WordGlow = false; s.Bold = true;
            s.BackgroundOpacity = 0; s.FontSize = 34; s.FontFamily = "Segoe UI Variable Display";
        }),
    };

    public static LookPreset? Find(string? name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>The full lyrics window's motion styles, after Taurus's: how far words lift, how much they
    /// glow, how far each line lags the one above, and how quickly the column moves.</summary>
    public static (double lift, double glow, double stagger, double scroll, double dim) Motion(LyricsMotion m) => m switch
    {
        LyricsMotion.Glow => (1.5, 1.0, 0.18, 9, 0.42),
        LyricsMotion.Float => (7, 0.5, 0.28, 7, 0.42),
        LyricsMotion.Calm => (0, 0, 0, 6, 0.55),
        LyricsMotion.Snappy => (2, 0.3, 0.05, 18, 0.4),
        _ => (2.5, 0.5, 0.18, 9, 0.42),
    };

    public static readonly (LyricsMotion value, string name)[] MotionNames =
    {
        (LyricsMotion.AppleMusic, "Apple Music"), (LyricsMotion.Glow, "Glow"), (LyricsMotion.Float, "Float"),
        (LyricsMotion.Calm, "Calm"), (LyricsMotion.Snappy, "Snappy"),
    };
}
