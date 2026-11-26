using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using LycRomanise.Core;

namespace LycRomanise.UI;

public partial class LyricsWindow : Window
{
    readonly AppController _app;
    readonly System.Windows.Threading.DispatcherTimer _tick;
    bool _panelHidden;
    readonly List<AnimationClock> _drift = new();
    readonly System.Windows.Threading.DispatcherTimer _saveSoon;

    public LyricsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        var s = app.Settings;
        if (s.LyricsLeft is double l && s.LyricsTop is double t)
        {
            // Checked against the monitor it lands on once the window exists (see FitToMonitor).
            Width = s.LyricsWidth; Height = s.LyricsHeight;
            WindowStartupLocation = WindowStartupLocation.Manual; Left = l; Top = t;
        }
        else
        {
            Width = Math.Min(s.LyricsWidth, SystemParameters.WorkArea.Width);
            Height = Math.Min(s.LyricsHeight, SystemParameters.WorkArea.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        SourceInitialized += (_, _) => FitToMonitor();
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

        // 20 fps is plenty for a blurred image moving under a pixel per frame.
        Drift(DriftRotate, RotateTransform.AngleProperty, new DoubleAnimation(-6, 6, TimeSpan.FromSeconds(40)));
        Drift(DriftScale, ScaleTransform.ScaleXProperty, new DoubleAnimation(1.5, 1.75, TimeSpan.FromSeconds(28)));
        Drift(DriftScale, ScaleTransform.ScaleYProperty, new DoubleAnimation(1.5, 1.75, TimeSpan.FromSeconds(28)));

        // Saved soon after a change, not only on close, so a logoff or crash keeps them.
        _saveSoon = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveSoon.Tick += (_, _) => { _saveSoon.Stop(); SaveBounds(); };
        LocationChanged += (_, _) => SaveSoon();
        SizeChanged += (_, _) => SaveSoon();
        StateChanged += (_, _) => SaveSoon();

        SizeChanged += (_, _) => UpdateLayoutMode();
        StateChanged += (_, _) => UpdateAnimating();
        // Opened while a song is already playing: start moving as soon as the window is on screen.
        IsVisibleChanged += (_, _) => UpdateAnimating();
        Closed += (_, _) =>
        {
            app.LyricsChanged -= OnLyrics;
            app.TrackChanged -= OnTrack;
            app.Media.Changed -= OnMediaChanged;
            _tick.Stop();
            _saveSoon.Stop();
            Page.StopAll();
            foreach (var c in _drift) c.Controller?.Stop();
            SaveBounds();
        };
        KeyDown += OnKey;
        _panelHidden = s.LyricsPanelHidden;
        ApplyBackground();
        _idle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idle.Tick += (_, _) => { _idle.Stop(); if (_app.Settings.LyricsAutoHideTools && SettingsPanel.Visibility != Visibility.Visible && !Tools.IsMouseOver) FadeTools(0); };
        MouseMove += (_, _) => { FadeTools(1); _idle.Stop(); _idle.Start(); };
        _idle.Start();
        OnTrack();
        OnLyrics();
    }

    public void ApplySettings()
    {
        Page.Motion = LookPresets.Motion(_app.Settings.LyricsMotion);
        Page.ShowTranslation = _app.Settings.ShowTranslation;
        ApplyBackground();
        Page.InvalidateVisual();
        if (SettingsPanel.Visibility == Visibility.Visible) BuildPanel();
    }

    // ------------------------------------------------------------------ toolbar and settings panel
    readonly System.Windows.Threading.DispatcherTimer _idle;
    double _toolsShown = 1;

    void FadeTools(double to)
    {
        if (Math.Abs(_toolsShown - to) < 0.01) return;
        _toolsShown = to;
        Tools.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 150 : 600)));
        Cursor = to > 0 ? null : Cursors.None;
    }

    void ApplyBackground()
    {
        bool black = _app.Settings.LyricsBackground == LyricsBackground.Black;
        BlackBg.Visibility = black ? Visibility.Visible : Visibility.Collapsed;
        BackdropHost.Visibility = black ? Visibility.Collapsed : Visibility.Visible;
        UpdateAnimating();
    }

    void Drift(Animatable target, DependencyProperty dp, DoubleAnimation a)
    {
        a.AutoReverse = true; a.RepeatBehavior = RepeatBehavior.Forever; a.EasingFunction = new SineEase();
        Timeline.SetDesiredFrameRate(a, 20);
        var clock = a.CreateClock();
        target.ApplyAnimationClock(dp, clock);
        _drift.Add(clock);
    }

    void SaveSoon() { _saveSoon.Stop(); _saveSoon.Start(); }

    void SetFont(double size) { Page.FontSize = size; SaveSoon(); }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RectPx r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct RectPx { public int L, T, R, B; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MonitorInfo { public int Size; public RectPx Monitor, Work; public uint Flags; }

    // A saved place on a monitor that is gone (or between monitors of different heights) would open off
    // screen, and a size from a big monitor would hang off a small one.
    void FitToMonitor()
    {
        if (WindowState == WindowState.Maximized) return;   // Windows maximizes it onto a real monitor itself
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var mon = MonitorFromWindow(hwnd, 0 /* NULL */);
            bool offScreen = mon == IntPtr.Zero;
            if (offScreen) mon = MonitorFromWindow(hwnd, 2 /* NEAREST */);
            var mi = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(hwnd, out var r)) return;
            var w = mi.Work;
            int ww = Math.Min(r.R - r.L, w.R - w.L), wh = Math.Min(r.B - r.T, w.B - w.T);
            int x = offScreen ? w.L + (w.R - w.L - ww) / 2 : Math.Clamp(r.L, w.L, w.R - ww);
            int y = offScreen ? w.T + (w.B - w.T - wh) / 2 : Math.Clamp(r.T, w.T, w.B - wh);
            if (x != r.L || y != r.T || ww != r.R - r.L || wh != r.B - r.T)
                SetWindowPos(hwnd, IntPtr.Zero, x, y, ww, wh, 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);
        }
        catch (Exception ex) { Log.Error("lyrics window place", ex); }
    }

    void Gear_Click(object sender, RoutedEventArgs e) => TogglePanel();

    void TogglePanel()
    {
        bool open = SettingsPanel.Visibility != Visibility.Visible;
        if (open)
        {
            BuildPanel();
            SettingsPanel.Visibility = Visibility.Visible;
            PanelSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            SettingsPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            FadeTools(1);
        }
        else SettingsPanel.Visibility = Visibility.Collapsed;
    }

    void Change(Action<AppSettings> edit, bool lyrics = false)
    {
        var c = _app.Settings.Copy();
        edit(c);
        _app.ApplySettings(c, lyrics);
    }

    static readonly SolidColorBrush PanelText = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF3)), PanelHint = Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
        Red = Frozen(Color.FromRgb(0xFF, 0x4A, 0x5C)), Chip = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), ChipOn = Frozen(Color.FromRgb(0xE5, 0x26, 0x3B));
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    void BuildPanel()
    {
        var s = _app.Settings;
        PanelBody.Children.Clear();
        PanelBody.Children.Add(new TextBlock { Text = "Lyrics window", FontSize = 20, FontWeight = FontWeights.Bold, Foreground = PanelText, Margin = new Thickness(0, 0, 0, 2) });
        PanelBody.Children.Add(new TextBlock { Text = "Changes apply instantly. Press S or Esc to close.", FontSize = 12, Foreground = PanelHint, Margin = new Thickness(0, 0, 0, 4) });

        Group("Motion");
        Chips(new[] { ("Apple Music", LyricsMotion.AppleMusic), ("Glow", LyricsMotion.Glow), ("Float", LyricsMotion.Float), ("Calm", LyricsMotion.Calm), ("Snappy", LyricsMotion.Snappy) },
            s.LyricsMotion, v => Change(c => c.LyricsMotion = v));
        Group("Background");
        Chips(new[] { ("Blurred cover", LyricsBackground.Cover), ("Black", LyricsBackground.Black) }, s.LyricsBackground, v => Change(c => c.LyricsBackground = v));
        Group("Show");
        Chips(new[] { ("Romanized", DisplayMode.RomanizedOnly), ("Both", DisplayMode.OriginalAndRomanized), ("Original", DisplayMode.OriginalOnly) }, s.Display, v => Change(c => c.Display = v, true));
        Toggle("Translations", s.ShowTranslation, v => Change(c => c.ShowTranslation = v, true));
        Toggle("Backing vocals", s.ShowBackingVocals, v => Change(c => c.ShowBackingVocals = v, true));

        Group("Text size");
        var size = new Slider { Minimum = 22, Maximum = 90, Value = Page.FontSize, Margin = new Thickness(0, 2, 0, 4) };
        size.ValueChanged += (_, e) => SetFont(e.NewValue);
        PanelBody.Children.Add(size);

        Group("Timing for this song");
        var timing = new TextBlock { Foreground = PanelHint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        void ShowOffset() => timing.Text = _app.SongOffset == 0 ? "in sync" : $"{(_app.SongOffset > 0 ? "+" : "")}{_app.SongOffset / 1000.0:0.00} s";
        ShowOffset();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(SmallButton("Earlier", () => { _app.NudgeOffset(250); ShowOffset(); }));
        row.Children.Add(SmallButton("Later", () => { _app.NudgeOffset(-250); ShowOffset(); }));
        row.Children.Add(SmallButton("Reset", () => { _app.NudgeOffset(-_app.SongOffset); ShowOffset(); }));
        row.Children.Add(timing);
        PanelBody.Children.Add(row);

        Group("Window");
        Toggle("Keep on top", Topmost, v => { Topmost = v; PinBtn.Opacity = v ? 1 : 0.6; _app.Settings.LyricsAlwaysOnTop = v; _app.SaveSettings(); });
        Toggle("Show the player", !_panelHidden, v => { _panelHidden = !v; _app.Settings.LyricsPanelHidden = _panelHidden; UpdateLayoutMode(); _app.SaveSettings(); });
        Toggle("Hide buttons while watching", s.LyricsAutoHideTools, v => Change(c => c.LyricsAutoHideTools = v));
        Toggle("Hide the desktop strip while open", s.HideStripWithLyricsWindow, v => Change(c => c.HideStripWithLyricsWindow = v));

        var all = SmallButton("All settings…", () => _app.ShowSettings());
        all.Margin = new Thickness(0, 18, 0, 0);
        all.HorizontalAlignment = HorizontalAlignment.Stretch;
        PanelBody.Children.Add(all);
    }

    void Group(string title) => PanelBody.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Red, Margin = new Thickness(0, 14, 0, 6) });

    void Chips<T>((string label, T value)[] items, T current, Action<T> pick) where T : struct
    {
        var wrap = new WrapPanel();
        foreach (var (label, value) in items)
        {
            bool on = EqualityComparer<T>.Default.Equals(value, current);
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
                Background = on ? ChipOn : Chip,
                Child = new TextBlock { Text = label, FontSize = 13, Foreground = PanelText, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal },
            };
            var v = value;
            b.MouseLeftButtonUp += (_, _) => { pick(v); BuildPanel(); };
            wrap.Children.Add(b);
        }
        PanelBody.Children.Add(wrap);
    }

    void Toggle(string label, bool on, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = on, Foreground = PanelText, Margin = new Thickness(0, 2, 0, 2), FontSize = 13 };
        cb.Checked += (_, _) => set(true);
        cb.Unchecked += (_, _) => set(false);
        PanelBody.Children.Add(cb);
    }

    static Button SmallButton(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 12 };
        b.Click += (_, _) => click();
        return b;
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
        Title = np == null ? "Lyrics - LycRomanise" : $"{np.Title} · {np.Artist} - Lyrics";
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
        CreditText.Text = r is { HasLines: true } && (_app.Settings.ShowSourceCredit || r.CreditRequired) ? $"Lyrics: {r.Provider}" + (r.Timing == Timing.Word ? " · word synced" : r.Timing == Timing.Line ? " · line synced" : " · not synced") : "";
        UpdateAnimating();
    }

    void UpdateAnimating()
    {
        bool visible = WindowState != WindowState.Minimized && IsVisible;
        bool playing = _app.NowPlaying is { Playing: true };
        Page.SetAnimating(visible && playing);
        if (!visible) Page.StopAll();
        // The drifting backdrop redraws the whole window.
        bool drift = visible && playing && _app.Settings.LyricsBackground != LyricsBackground.Black;
        foreach (var c in _drift)
        {
            if (c.Controller == null) continue;
            if (drift && c.IsPaused) c.Controller.Resume();
            else if (!drift && !c.IsPaused) c.Controller.Pause();
        }
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

    internal void SaveBounds()
    {
        var s = _app.Settings;
        // Minimized keeps whatever it was before, so a maximized window comes back maximized.
        if (WindowState != WindowState.Minimized) s.LyricsMaximized = WindowState == WindowState.Maximized;
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
            case Key.Escape:
                if (SettingsPanel.Visibility == Visibility.Visible) TogglePanel();
                else if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; else Close();
                break;
            case Key.S when Keyboard.Modifiers == ModifierKeys.None: TogglePanel(); e.Handled = true; break;
            case Key.F11: WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
            case Key.OemPlus or Key.Add when Keyboard.Modifiers == ModifierKeys.Control: SetFont(Page.FontSize + 2); break;
            case Key.OemMinus or Key.Subtract when Keyboard.Modifiers == ModifierKeys.Control: SetFont(Page.FontSize - 2); break;
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
    void Smaller_Click(object sender, RoutedEventArgs e) => SetFont(Page.FontSize - 3);
    void Bigger_Click(object sender, RoutedEventArgs e) => SetFont(Page.FontSize + 3);
    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var lines = _app.CurrentResult?.Lines;
        if (lines == null || lines.Count == 0) return;
        var np = _app.NowPlaying;
        var text = (np != null ? $"{np.Artist} - {np.Title}\n\n" : "") + string.Join("\n", lines.Select(l => l.Text));
        try { Clipboard.SetText(text); _app.Tray?.Toast("Lyrics copied"); } catch { }
    }

    void Choose_Click(object sender, RoutedEventArgs e) => _app.ShowPicker();
    void Panel_Click(object sender, RoutedEventArgs e) { _panelHidden = !_panelHidden; _app.Settings.LyricsPanelHidden = _panelHidden; _app.SaveSettings(); UpdateLayoutMode(); }
    void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _app.Settings.LyricsAlwaysOnTop = Topmost;
        PinBtn.Opacity = Topmost ? 1 : 0.6;
        _app.SaveSettings();
    }
}
