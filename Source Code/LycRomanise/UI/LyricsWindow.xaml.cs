using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LycRomanise.Core;

namespace LycRomanise.UI;

/// <summary>The full lyrics, Apple Music style: the cover blurred behind, the player on the left, and
/// every line on the right, following the song.</summary>
public partial class LyricsWindow : Window
{
    readonly AppController _app;
    readonly System.Windows.Threading.DispatcherTimer _tick;
    bool _panelHidden;

    public LyricsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        var s = app.Settings;
        // Never larger than the screen it opens on.
        Width = Math.Min(s.LyricsWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(s.LyricsHeight, SystemParameters.WorkArea.Height);
        if (s.LyricsLeft is double l && s.LyricsTop is double t) { WindowStartupLocation = WindowStartupLocation.Manual; Left = l; Top = t; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = s.LyricsAlwaysOnTop;
        PinBtn.Opacity = Topmost ? 1 : 0.6;
        Page.FontSize = s.LyricsFontSize;
        Page.Motion = LookPresets.Motion(s.LyricsMotion);
        Page.SetClock(app.Clock);
        Page.Seek = app.SeekToLyric;

        app.LyricsChanged += OnLyrics;
        app.TrackChanged += OnTrack;
        app.Media.Changed += OnMediaChanged;
        _tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => UpdateProgress();
        _tick.Start();

        // A slow drift of the blurred cover, as Apple Music's background moves.
        DriftRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-6, 6, TimeSpan.FromSeconds(40)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        DriftScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.5, 1.75, TimeSpan.FromSeconds(28)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        DriftScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.5, 1.75, TimeSpan.FromSeconds(28)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });

        SizeChanged += (_, _) => UpdateLayoutMode();
        StateChanged += (_, _) => UpdateAnimating();
        Closed += (_, _) =>
        {
            app.LyricsChanged -= OnLyrics;
            app.TrackChanged -= OnTrack;
            app.Media.Changed -= OnMediaChanged;
            _tick.Stop();
            Page.SetAnimating(false);
            SaveBounds();
        };
        KeyDown += OnKey;
        OnTrack();
        OnLyrics();
    }

    public void ApplySettings()
    {
        Page.Motion = LookPresets.Motion(_app.Settings.LyricsMotion);
        Page.InvalidateVisual();
    }

    void OnMediaChanged(Media.MediaState? _) => Dispatcher.BeginInvoke(() => { UpdateAnimating(); UpdateProgress(); });

    void OnTrack()
    {
        var np = _app.NowPlaying;
        TitleText.Text = np?.Title ?? "Nothing playing";
        ArtistText.Text = np?.Artist ?? "Play a song in Spotify or another player";
        var cover = _app.Cover;
        CoverBrush.ImageSource = cover;
        Backdrop.Source = cover;
        Fallback.BeginAnimation(OpacityProperty, new DoubleAnimation(cover == null ? 1 : 0, TimeSpan.FromMilliseconds(600)));
        Title = np == null ? "Lyrics — LycRomanise" : $"{np.Title} · {np.Artist} — Lyrics";
    }

    void OnLyrics()
    {
        var r = _app.CurrentResult;
        string? empty = _app.NowPlaying == null ? "Play a song to see its lyrics here."
            : r == null ? "Looking for lyrics…"
            : r.Instrumental && !r.HasLines ? "♪  Instrumental"
            : !r.HasLines ? "No lyrics found for this song. Use \"Choose lyrics\" at the top right to try every source, or import a file." : null;
        if (r == null && _app.CurrentQuery != null && _app.CurrentLines.Count == 0) empty = "Looking for lyrics…";
        Page.ShowTranslation = _app.Settings.ShowTranslation;
        Page.SetLyrics(_app.CurrentLines, r?.Synced ?? true, _app.NowPlaying?.DurationMs ?? 0, empty);
        CreditText.Text = r is { HasLines: true } ? $"Lyrics: {r.Provider}" + (r.Timing == Timing.Word ? " · word synced" : r.Timing == Timing.Line ? " · line synced" : " · not synced") : "";
        UpdateAnimating();
    }

    void UpdateAnimating()
    {
        bool visible = WindowState != WindowState.Minimized && IsVisible;
        Page.SetAnimating(visible && _app.NowPlaying is { Playing: true });
        Page.InvalidateVisual();
        PlayBtn.Content = _app.NowPlaying is { Playing: true } ? "" : "";
    }

    void UpdateProgress()
    {
        var np = _app.NowPlaying;
        if (np == null || np.DurationMs <= 0) { Fill.Width = 0; ElapsedText.Text = RemainText.Text = ""; return; }
        long pos = np.PositionNow();
        double f = Math.Clamp(pos / (double)np.DurationMs, 0, 1);
        Fill.Width = Track.ActualWidth * f;
        ElapsedText.Text = Clock(pos);
        RemainText.Text = "-" + Clock(np.DurationMs - pos);
        if (!Page.IsMouseOver && _app.NowPlaying is { Playing: false }) Page.InvalidateVisual();
    }

    static string Clock(long ms) { var t = TimeSpan.FromMilliseconds(Math.Max(0, ms)); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }

    /// <summary>Narrow: lyrics alone. Wide: the player beside them.</summary>
    void UpdateLayoutMode()
    {
        bool narrow = ActualWidth < 900 || _panelHidden;
        NowPanel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        LeftCol.Width = narrow ? new GridLength(0) : new GridLength(0.82, GridUnitType.Star);
        double size = Math.Clamp(ActualHeight * 0.42, 220, 420);
        CoverCard.Width = CoverCard.Height = size;
        foreach (var fe in new FrameworkElement[] { TitleText, ArtistText, CreditText }) fe.Width = size;
        ((FrameworkElement)Track.Parent).Width = size;
        ((FrameworkElement)ElapsedText.Parent).Width = size;
    }

    void SaveBounds()
    {
        var s = _app.Settings;
        s.LyricsMaximized = WindowState == WindowState.Maximized;
        var b = RestoreBounds;
        if (!b.IsEmpty) { s.LyricsLeft = b.Left; s.LyricsTop = b.Top; s.LyricsWidth = b.Width; s.LyricsHeight = b.Height; }
        s.LyricsFontSize = Page.FontSize;
        _app.SaveSettings();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: _ = _app.Media.TogglePlayAsync(); e.Handled = true; break;
            case Key.Escape: if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; else Close(); break;
            case Key.F11: WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
            case Key.OemPlus or Key.Add when Keyboard.Modifiers == ModifierKeys.Control: Page.FontSize += 2; break;
            case Key.OemMinus or Key.Subtract when Keyboard.Modifiers == ModifierKeys.Control: Page.FontSize -= 2; break;
        }
    }

    void Track_Click(object sender, MouseButtonEventArgs e)
    {
        var np = _app.NowPlaying;
        if (np == null || np.DurationMs <= 0) return;
        double f = e.GetPosition(Track).X / Math.Max(1, Track.ActualWidth);
        _ = _app.Media.SeekAsync((long)(np.DurationMs * Math.Clamp(f, 0, 1)));
    }

    void Prev_Click(object sender, RoutedEventArgs e) => _ = _app.Media.SkipAsync(false);
    void Next_Click(object sender, RoutedEventArgs e) => _ = _app.Media.SkipAsync(true);
    void Play_Click(object sender, RoutedEventArgs e) => _ = _app.Media.TogglePlayAsync();
    void Smaller_Click(object sender, RoutedEventArgs e) => Page.FontSize -= 3;
    void Bigger_Click(object sender, RoutedEventArgs e) => Page.FontSize += 3;
    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var lines = _app.CurrentResult?.Lines;
        if (lines == null || lines.Count == 0) return;
        var np = _app.NowPlaying;
        var text = (np != null ? $"{np.Artist} - {np.Title}\n\n" : "") + string.Join("\n", lines.Select(l => l.Text));
        try { Clipboard.SetText(text); _app.Tray?.Toast("Lyrics copied"); } catch { }
    }

    void Choose_Click(object sender, RoutedEventArgs e) => _app.ShowPicker();
    void Panel_Click(object sender, RoutedEventArgs e) { _panelHidden = !_panelHidden; UpdateLayoutMode(); }
    void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _app.Settings.LyricsAlwaysOnTop = Topmost;
        PinBtn.Opacity = Topmost ? 1 : 0.6;
        _app.SaveSettings();
    }
}
