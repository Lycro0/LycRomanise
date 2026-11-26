using System.Windows.Documents;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LycRomanise.Core;
using LycRomanise.Sources;

namespace LycRomanise.UI;

public partial class SettingsWindow : Window
{
    readonly AppController _app;
    AppSettings _s;
    readonly DispatcherTimer _debounce;
    bool _pendingLyrics;
    readonly Dictionary<string, Func<UIElement>> _pages = new();
    string _page = "nowplaying";
    TextBlock? _songTitle, _songInfo, _songOffset;

    static FontFamily Icons => (FontFamily)Application.Current.Resources["Icons"];

    public SettingsWindow(AppController app)
    {
        _app = app;
        _s = app.Settings.Copy();
        _base = app.Settings.Copy();
        InitializeComponent();
        Theme.Paint(this);
        VersionText.Text = "v" + typeof(App).Assembly.GetName().Version?.ToString(3);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Commit(); };

        AddPage("nowplaying", "Now playing", "", NowPlayingPage);
        AddPage("look", "Appearance", "", LookPage);
        AddPage("lyrics", "Lyrics & romanization", "", LyricsPage);
        AddPage("sources", "Lyric sources", "", SourcesPage);
        AddPage("spotify", "Spotify account", "", SpotifyPage);
        AddPage("window", "Strip & window", "", WindowPage);
        AddPage("timing", "Timing", "", TimingPage);
        AddPage("web", "Web view & OBS", "", WebPage);
        AddPage("about", "General & about", "", AboutPage);
        Show("nowplaying");
        _app.UpdateChanged += RefreshUpdateBar;
        _app.TrackChanged += RefreshLyricsBar;
        Closed += (_, _) => { _app.UpdateChanged -= RefreshUpdateBar; _app.TrackChanged -= RefreshLyricsBar; };
        RefreshUpdateBar();
        Loaded += (_, _) => RefreshLyricsBar();
    }

    void RefreshLyricsBar()
    {
        var np = _app.NowPlaying;
        LyricsBar.ApplyTemplate();
        if (LyricsBar.Template.FindName("LyricsBarSong", LyricsBar) is TextBlock t)
            t.Text = np == null ? "Open the lyrics screen" : $"{np.Title} · {np.Artist}";
    }

    void RefreshUpdateBar()
    {
        UpdateBar.Visibility = _app.Update == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateBarText.Text = _app.Update == null ? "" : $"Update to {_app.Update.Tag} available";
    }

    void LyricsBar_Click(object sender, RoutedEventArgs e) => _app.ShowLyricsWindow();
    void UpdateBar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Show("about");

    void AddPage(string key, string title, string glyph, Func<UIElement> build)
    {
        _pages[key] = build;
        var rb = new RadioButton
        {
            Style = (Style)FindResource("NavButton"), GroupName = "nav", Tag = key,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 16, Width = 30, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        rb.Checked += (_, _) => Show(key);
        Nav.Children.Add(rb);
    }

    // Pages are built once and kept; rebuild only when a change elsewhere makes the shown values stale.
    readonly Dictionary<string, UIElement> _built = new();

    void Show(string key, bool rebuild = false)
    {
        bool same = key == _page && PageHost.Content != null;
        _page = key;
        foreach (RadioButton rb in Nav.Children) rb.IsChecked = (string)rb.Tag == key;
        if (rebuild || !_built.TryGetValue(key, out var page)) _built[key] = page = _pages[key]();
        double y = Scroller.VerticalOffset;
        PageHost.Content = page;
        if (same) Scroller.ScrollToVerticalOffset(y); else Scroller.ScrollToTop();
    }

    void Changed(bool lyricsAffected = false)
    {
        _pendingLyrics |= lyricsAffected;
        // A throttle, not a restarting debounce: while a slider is dragged the strip still follows every 120 ms.
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    // Settings as this window last took or saved them: tells its own edits apart from changes made elsewhere.
    AppSettings _base;
    AppSettings? _beforePreset;

    void Commit()
    {
        _app.ApplySettings(_s.Copy(), _pendingLyrics, fromSettingsWindow: true);
        _base = _app.Settings.Copy();
        _pendingLyrics = false;
        // Other pages may show the setting just changed (Display is on two pages): build them fresh next time.
        foreach (var key in _built.Keys.Where(k => k != _page).ToList()) _built.Remove(key);
        SavedText.Text = "Saved · " + DateTime.Now.ToString("HH:mm:ss");
    }

    // Settings changed elsewhere (tray, hotkey): take them, saving any edit still waiting first.
    public void Resync()
    {
        if (_debounce.IsEnabled) { _debounce.Stop(); var mine = _s; _s = _app.Settings.Copy(); MergePending(mine); Commit(); }
        else { _s = _app.Settings.Copy(); _base = _app.Settings.Copy(); }
        _built.Clear();
        Show(_page, rebuild: true);
    }

    static readonly System.Text.Json.JsonSerializerOptions MergeJson = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    // Three-way merge: settings changed here since _base come from the pending edit, the rest from outside (_s).
    void MergePending(AppSettings mine)
    {
        var baseNode = System.Text.Json.JsonSerializer.SerializeToNode(_base, MergeJson)!.AsObject();
        var mineNode = System.Text.Json.JsonSerializer.SerializeToNode(mine, MergeJson)!.AsObject();
        var fresh = System.Text.Json.JsonSerializer.SerializeToNode(_s, MergeJson)!.AsObject();
        foreach (var (name, value) in mineNode.ToList())
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(value, baseNode[name])) fresh[name] = value?.DeepClone();
        _s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(fresh.ToJsonString(), MergeJson)!.Copy();
    }

    static System.Windows.Media.Imaging.BitmapImage QrImage(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var img = new System.Windows.Media.Imaging.BitmapImage();
        img.BeginInit();
        img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(png);
        img.EndInit();
        img.Freeze();
        return img;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing does not move logical focus, so a box that saves on LostFocus (port, client id, typed font)
        // would lose its text: clear focus first, which raises LostFocus and queues the save.
        if (System.Windows.Input.FocusManager.GetFocusedElement(this) != null) System.Windows.Input.FocusManager.SetFocusedElement(this, null);
        if (_debounce.IsEnabled) { _debounce.Stop(); Commit(); }
        base.OnClosed(e);
    }

    static StackPanel PageRoot(string title, string? subtitle = null)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("PageTitle"), Margin = new Thickness(0, 0, 0, subtitle == null ? 18 : 4) });
        if (subtitle != null) sp.Children.Add(new TextBlock { Text = subtitle, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        return sp;
    }

    static TextBlock Group(string text) => new() { Text = text, Style = (Style)Application.Current.FindResource("GroupTitle") };

    static Border Card(string title, string? hint, UIElement? control, string? glyph = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph != null)
        {
            var g = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 18, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(g);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (hint != null) text.Children.Add(new TextBlock { Text = hint, Style = (Style)Application.Current.FindResource("CardHint") });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control != null)
        {
            if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }
        return new Border { Style = (Style)Application.Current.FindResource("Card"), Child = grid, MinHeight = 64 };
    }

    static Border Wide(UIElement content) => new() { Style = (Style)Application.Current.FindResource("Card"), Child = content };

    CheckBox Toggle(bool value, Action<bool> set, bool lyrics = false)
    {
        var cb = new CheckBox { IsChecked = value, Content = value ? "On" : "Off", MinWidth = 70 };
        cb.Checked += (_, _) => { cb.Content = "On"; set(true); Changed(lyrics); };
        cb.Unchecked += (_, _) => { cb.Content = "Off"; set(false); Changed(lyrics); };
        return cb;
    }

    FrameworkElement SliderBox(double value, double min, double max, double step, string format, Action<double> set, bool lyrics = false)
    {
        var label = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = string.Format(format, value) };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 220, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { label.Text = string.Format(format, e.NewValue); set(e.NewValue); Changed(lyrics); };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, label } };
    }

    ComboBox Combo<T>(T value, (T val, string label)[] options, Action<T> set, bool lyrics = false)
    {
        var cb = new ComboBox { MinWidth = 220 };
        foreach (var (v, l) in options) cb.Items.Add(new ComboBoxItem { Content = l, Tag = v });
        cb.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.val, value)));
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: T v }) { set(v); Changed(lyrics); } };
        return cb;
    }

    static readonly Lazy<string[]> FontNames = new(() => Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(x => x).ToArray());

    static readonly string[] Palette =
    {
        "#FFE14D", "#FF3D9A", "#FFFFFF", "#000000", "#7CF5FF", "#5B8CFF", "#A66BFF", "#FF6B6B", "#FF9F43", "#2ED573",
        "#1E1E24", "#F8C8DC", "#C3F0CA", "#FFD1A9", "#B5DEFF", "#E0C3FC", "#FFB3C1", "#9AE6B4", "#FBD38D", "#90CDF4",
    };

    FrameworkElement ColorBox(string value, Action<string> set)
    {
        var swatch = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), Background = new SolidColorBrush(Theme.ParseColor(value, Colors.White)) };
        var hex = new TextBox { Text = value, Width = 96, Margin = new Thickness(10, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        var popup = new Popup { PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var grid = new UniformGrid { Columns = 10, Margin = new Thickness(10) };
        string current = value.ToUpperInvariant();
        void Apply(string h)
        {
            h = h.Trim();
            if (h.Length == 0) return;
            if (!h.StartsWith('#')) h = "#" + h;
            var c = Theme.ParseColor(h, Colors.Transparent);
            if (c == Colors.Transparent && h != "#00000000") return;
            if (h.Length is not (4 or 7 or 9)) return;
            swatch.Background = new SolidColorBrush(c);
            h = h.ToUpperInvariant();
            if (h == current) return;
            current = h;
            set(h);
            Changed();
        }
        popup.Opened += (_, _) => { if (grid.Children.Count == 0) FillSwatches(); };
        void FillSwatches()
        {
        foreach (var p in Palette)
        {
            var b = new Button { Width = 24, Height = 24, Margin = new Thickness(3), Padding = new Thickness(0), Background = new SolidColorBrush(Theme.ParseColor(p, Colors.White)), ToolTip = p, MinWidth = 0, MinHeight = 0 };
            b.Template = SwatchTemplate();
            b.Click += (_, _) => { hex.Text = p; Apply(p); popup.IsOpen = false; };
            grid.Children.Add(b);
        }
        }
        var popBorder = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = grid };
        // References, not copies, so a theme change on this page repaints them.
        popBorder.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        popBorder.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        popup.Child = popBorder;
        swatch.MouseLeftButtonUp += (_, _) => popup.IsOpen = true;
        swatch.Cursor = System.Windows.Input.Cursors.Hand;
        hex.TextChanged += (_, _) => { if (hex.IsLoaded) Apply(hex.Text); };
        hex.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Apply(hex.Text); };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, hex, popup } };
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        f.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)));
        f.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        t.VisualTree = f;
        return t;
    }

    static Button Btn(string text, Action click, bool accent = false, string? glyph = null)
    {
        var content = glyph == null ? (object)text : new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new TextBlock { Text = glyph, FontFamily = Icons, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text } },
        };
        var b = new Button { Content = content, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
        if (accent) b.Style = (Style)Application.Current.FindResource("AccentButtonStyle");
        b.Click += (_, _) => click();
        return b;
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("open " + target, ex); }
    }

    UIElement NowPlayingPage()
    {
        var p = PageRoot("Now playing");
        var hero = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(24, 20, 24, 20), Margin = new Thickness(0, 0, 0, 8),
            Background = new LinearGradientBrush(Color.FromRgb(0x0E, 0x0E, 0x10), Color.FromRgb(0x6E, 0x0B, 0x18), 20),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        TextElement.SetForeground(hero, Brushes.White);
        var info = new StackPanel();
        _songTitle = new TextBlock { FontSize = 24, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _songInfo = new TextBlock { Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        _songOffset = new TextBlock { Opacity = 0.8, Margin = new Thickness(0, 4, 0, 0) };
        info.Children.Add(_songTitle);
        info.Children.Add(_songInfo);
        info.Children.Add(_songOffset);
        var actions = new WrapPanel { Margin = new Thickness(-6, 14, 0, 0) };
        actions.Children.Add(Btn("Full lyrics", _app.ShowLyricsWindow, true, ""));
        actions.Children.Add(Btn("Choose lyrics…", _app.ShowPicker, false, ""));
        actions.Children.Add(Btn("Search again", () => _app.Refetch(true), false, ""));
        actions.Children.Add(Btn("Earlier", () => _app.NudgeOffset(250), false, ""));
        actions.Children.Add(Btn("Later", () => _app.NudgeOffset(-250), false, ""));
        actions.Children.Add(Btn("Reset timing", () => _app.NudgeOffset(-_app.SongOffset), false, ""));
        actions.Children.Add(Btn("Import lyrics file…", ImportForSong, false, ""));
        info.Children.Add(actions);
        hero.Child = info;
        p.Children.Add(hero);
        RefreshSongInfo();

        p.Children.Add(Group("Quick settings"));
        p.Children.Add(Card("Lock the strip", "Unlock from the tray icon.", Toggle(_app.Settings.Locked, v => _app.SetLocked(v)), ""));
        p.Children.Add(Card("What the strip shows", null, Combo(_s.Display, new[]
        {
            (DisplayMode.OriginalAndRomanized, "Original + romanized"), (DisplayMode.RomanizedOnly, "Romanized only"), (DisplayMode.OriginalOnly, "Original only"),
        }, v => _s.Display = v, true), ""));
        p.Children.Add(Card("Show translation", null, Toggle(_s.ShowTranslation, v => _s.ShowTranslation = v, true), ""));
        return p;
    }

    public void RefreshSongInfo()
    {
        if (_songTitle == null) return;
        var np = _app.NowPlaying;
        var r = _app.CurrentResult;
        var q = _app.CurrentQuery;
        _songTitle.Text = np == null ? "Nothing playing" : np.Title;
        _songInfo!.Text = np == null ? "Play something in Spotify, Apple Music or another player." :
            $"{np.Artist}{(np.Album.Length > 0 ? " · " + np.Album : "")}\n" +
            (r == null ? "Lyrics: searching or none found" : r.Instrumental && !r.HasLines ? "Instrumental" :
                $"Lyrics: {r.Provider} · {(r.Timing == Timing.Word ? "word synced" : r.Timing == Timing.Line ? "line synced" : "not synced")}") +
            (q?.SpotifyId != null ? $"\nSpotify id {q.SpotifyId}" : "");
        _songOffset!.Text = $"Timing for this song: {(_app.SongOffset >= 0 ? "+" : "")}{_app.SongOffset / 1000.0:0.00} s";
    }

    void ImportForSong()
    {
        var q = _app.CurrentQuery;
        if (q == null) { MessageBox.Show(this, "Play the song first, then import its lyrics.", "LycRomanise"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Lyrics (*.lrc;*.ttml;*.txt)|*.lrc;*.ttml;*.txt|All files|*.*", Title = $"Lyrics for {q.Artist} - {q.Title}" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(_s.ImportedFolder);
            var safe = string.Concat($"{q.Artist} - {q.Title}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var dest = Path.Combine(_s.ImportedFolder, safe + Path.GetExtension(dlg.FileName).ToLowerInvariant());
            File.Copy(dlg.FileName, dest, true);
            var r = _app.Chain.Imported.Load(dest);
            if (r == null) { MessageBox.Show(this, "That file has no lyrics LycRomanise can read.", "LycRomanise"); return; }
            _app.UseResult(q, r, rememberForSong: true);   // not applied if the song changed meanwhile
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not import"); }
    }

    UIElement LookPage()
    {
        var p = PageRoot("Appearance", "Pick a look, then fine-tune its colours, font and effects.");
        p.Children.Add(Group("Looks"));
        var gallery = new WrapPanel { Margin = new Thickness(-4, 0, 0, 0) };
        // Which card is chosen comes from the values, not the stored name: edits after picking one make it Custom.
        var matching = LookPresets.Matching(_s);
        if (matching == null)
            p.Children.Add(new TextBlock { Text = "Your look is custom. Picking a look below replaces its colours, font and effects; you can undo that here.",
                Style = (Style)Application.Current.FindResource("CardHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        if (_beforePreset != null && !LookPresets.SameLook(_beforePreset, _s))
        {
            var undo = Btn("Put my own look back", () =>
            {
                LookPresets.CopyLook(_beforePreset!, _s);
                _beforePreset = null;
                Changed(lyricsAffected: true);
                Show("look", rebuild: true);
            });
            undo.HorizontalAlignment = HorizontalAlignment.Left;
            undo.Margin = new Thickness(0, 0, 0, 8);
            p.Children.Add(undo);
        }
        foreach (var preset in LookPresets.All)
        {
            var trial = _s.Copy();
            preset.Apply(trial);
            bool chosen = ReferenceEquals(matching, preset);
            var sample = new TextBlock { FontSize = 17, FontWeight = trial.Bold ? FontWeights.Bold : FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
            sample.Inlines.Add(new System.Windows.Documents.Run("Sing ") { Foreground = new SolidColorBrush(Theme.ParseColor(trial.SungColor, Colors.White)) });
            sample.Inlines.Add(new System.Windows.Documents.Run("along") { Foreground = new SolidColorBrush(Theme.ParseColor(trial.UnsungColor, Colors.White)) });
            var nextSample = new TextBlock { Text = trial.StripStyle == StripStyle.Minimal ? " " : "the next line", FontSize = 11, Foreground = new SolidColorBrush(Theme.ParseColor(trial.NextLineColor, Colors.White)), Opacity = 0.8 };
            var bgColor = Theme.ParseColor(trial.BackgroundColor, Colors.Black);
            var face = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 2, 12, 10), Margin = new Thickness(0, 8, 0, 0),
                Background = new SolidColorBrush(trial.BackgroundOpacity > 0.05 ? Color.FromArgb((byte)(Math.Max(0.6, trial.BackgroundOpacity) * 255), bgColor.R, bgColor.G, bgColor.B) : Color.FromRgb(0x0E, 0x0E, 0x12)),
                Child = new StackPanel { Children = { sample, nextSample } },
            };
            var body = new StackPanel { Width = 196 };
            body.Children.Add(new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            body.Children.Add(new TextBlock { Text = preset.Description, Style = (Style)Application.Current.FindResource("CardHint"), Height = 34, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(face);
            var card = new Button
            {
                Content = body, Margin = new Thickness(4), Padding = new Thickness(12), HorizontalContentAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(chosen ? 2 : 1),
            };
            card.SetResourceReference(Control.BorderBrushProperty, chosen ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush");
            card.Click += (_, _) =>
            {
                // A custom look matches no card: keep it, so one click does not lose it for good.
                if (LookPresets.Matching(_s) == null) _beforePreset = _s.Copy();
                preset.Apply(_s);
                _s.LookPreset = preset.Name;
                Changed(lyricsAffected: true);
                Show("look", rebuild: true);
            };
            gallery.Children.Add(card);
        }
        p.Children.Add(gallery);

        p.Children.Add(Group("Layout"));
        p.Children.Add(Card("Strip layout", "Classic: the line and the next one straight under it. Modern: romanization under the line, next line at the bottom. Minimal: one line.",
            Combo(_s.StripStyle, new[] { (StripStyle.Classic, "Classic"), (StripStyle.Modern, "Modern"), (StripStyle.Minimal, "Minimal") }, v => _s.StripStyle = v), "\uE8A1"));
        p.Children.Add(Card("Words lift as they're sung", "The Apple Music touch.", SliderBox(_s.WordLift, 0, 8, 0.5, "{0:0.0} px", v => _s.WordLift = v), "\uE74A"));
        p.Children.Add(Card("Glow on sung words", null, Toggle(_s.WordGlow, v => _s.WordGlow = v), "\uE706"));

        p.Children.Add(Group("Full lyrics window"));
        p.Children.Add(Card("Motion", "How words lift and glow and how the lines move.",
            Combo(_s.LyricsMotion, LookPresets.MotionNames, v => _s.LyricsMotion = v), "\uE8BC"));

        p.Children.Add(Group("Colours"));
        p.Children.Add(Card("Not yet sung", null, ColorBox(_s.UnsungColor, v => _s.UnsungColor = v), ""));
        p.Children.Add(Card("Sung", "The colour sweeps across each word as it is sung.", ColorBox(_s.SungColor, v => _s.SungColor = v), ""));
        p.Children.Add(Card("Next line", null, ColorBox(_s.NextLineColor, v => _s.NextLineColor = v), ""));
        p.Children.Add(Card("Romanization and translation", null, ColorBox(_s.SubColor, v => _s.SubColor = v), ""));
        p.Children.Add(Card("Outline", null, ColorBox(_s.OutlineColor, v => _s.OutlineColor = v), ""));

        p.Children.Add(Group("Text"));
        var fonts = new ComboBox { MinWidth = 240, IsEditable = true, Text = _s.FontFamily };
        fonts.DropDownOpened += (_, _) => { if (fonts.Items.Count == 0) foreach (var f in FontNames.Value) fonts.Items.Add(f); };
        fonts.SelectionChanged += (_, _) => { if (fonts.SelectedItem is string f) { _s.FontFamily = f; Changed(); } };
        fonts.LostFocus += (_, _) => { if (fonts.Text.Length > 0) { _s.FontFamily = fonts.Text; Changed(); } };
        p.Children.Add(Card("Font", "Japanese, Korean and Chinese fall back to Yu Gothic, Malgun Gothic and YaHei.", fonts, ""));
        p.Children.Add(Card("Size", null, SliderBox(_s.FontSize, 16, 96, 1, "{0:0} px", v => _s.FontSize = v), ""));
        p.Children.Add(Card("Bold", null, Toggle(_s.Bold, v => _s.Bold = v), ""));
        p.Children.Add(Card("Outline width", null, SliderBox(_s.OutlineWidth, 0, 8, 0.5, "{0:0.0} px", v => _s.OutlineWidth = v), ""));
        p.Children.Add(Card("Glow", null, Toggle(_s.Glow, v => _s.Glow = v), ""));
        p.Children.Add(Card("Smooth sweep", null, Toggle(_s.SmoothSweep, v => _s.SmoothSweep = v), ""));

        p.Children.Add(Group("Background"));
        p.Children.Add(Card("Background colour", null, ColorBox(_s.BackgroundColor, v => _s.BackgroundColor = v), ""));
        p.Children.Add(Card("Background opacity", "0 % shows only the text.", SliderBox(_s.BackgroundOpacity * 100, 0, 100, 5, "{0:0} %", v => _s.BackgroundOpacity = v / 100), ""));
        p.Children.Add(Card("Corner radius", null, SliderBox(_s.CornerRadius, 0, 40, 1, "{0:0} px", v => _s.CornerRadius = v), ""));

        p.Children.Add(Group("Settings window"));
        p.Children.Add(Card("Theme", null, Combo(_s.Theme, new[] { (ThemeChoice.System, "Use Windows setting"), (ThemeChoice.Dark, "Dark"), (ThemeChoice.Light, "Light") }, v => _s.Theme = v), ""));
        return p;
    }

    UIElement LyricsPage()
    {
        var p = PageRoot("Lyrics & romanization", "Korean, Japanese, Chinese, Cyrillic, Greek, Arabic, Hebrew and Devanagari lyrics can be shown in Latin letters.");
        p.Children.Add(Card("What the strip shows", "Where the source has its own romanization (Apple Music, Spicy Lyrics) it is used, timed to the words.",
            Combo(_s.Display, new[] { (DisplayMode.OriginalAndRomanized, "Original + romanized"), (DisplayMode.RomanizedOnly, "Romanized only"), (DisplayMode.OriginalOnly, "Original only") }, v => _s.Display = v, true), ""));
        p.Children.Add(Card("Show translation", null, Toggle(_s.ShowTranslation, v => _s.ShowTranslation = v, true), ""));
        p.Children.Add(Card("Translation language", "Used when the source has none: Musixmatch's community translations.", Combo(_s.TranslationLanguage, new[]
        {
            ("en", "English"), ("es", "Spanish"), ("pt", "Portuguese"), ("fr", "French"), ("de", "German"), ("it", "Italian"),
            ("ja", "Japanese"), ("ko", "Korean"), ("zh-Hans", "Chinese (Simplified)"), ("zh-Hant", "Chinese (Traditional)"),
            ("ru", "Russian"), ("tr", "Turkish"), ("id", "Indonesian"), ("vi", "Vietnamese"), ("th", "Thai"), ("ar", "Arabic"), ("hi", "Hindi"),
        }, v => _s.TranslationLanguage = v, true), ""));
        p.Children.Add(Card("Backing vocals", "The (oh, yeah) sung under a line, where the source has them.", Toggle(_s.ShowBackingVocals, v => _s.ShowBackingVocals = v, true), ""));
        p.Children.Add(Card("Next line preview", null, Toggle(_s.ShowNextLine, v => _s.ShowNextLine = v), ""));
        p.Children.Add(Card("Song title card", null, Toggle(_s.ShowTitleCard, v => _s.ShowTitleCard = v), ""));
        return p;
    }

    UIElement SourcesPage()
    {
        var p = PageRoot("Lyric sources", "Asked from the top down. The first word-synced answer wins; a line-synced one is kept while the rest are asked for word sync.");
        var list = new StackPanel();
        void Rebuild()
        {
            list.Children.Clear();
            for (int i = 0; i < _s.Sources.Count; i++)
            {
                var entry = _s.Sources[i];
                if (!_app.Chain.All.TryGetValue(entry.Key, out var src)) continue;
                int index = i;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = (i + 1).ToString(), FontSize = 18, FontWeight = FontWeights.SemiBold, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center });
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = src.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = src.Detail, Style = (Style)Application.Current.FindResource("CardHint") });
                if (entry.Key == "spicy")
                {
                    var status = string.IsNullOrEmpty(_s.SpicyKey) ? "Needs a key (add it below)" : _app.Chain.Spicy.Status ?? "Key set";
                    text.Children.Add(new TextBlock { Text = status, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush") });
                }
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var up = new Button { Content = new TextBlock { Text = "", FontFamily = Icons }, Padding = new Thickness(10, 6, 10, 6), IsEnabled = i > 0, ToolTip = "Ask earlier" };
                var down = new Button { Content = new TextBlock { Text = "", FontFamily = Icons }, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(4, 0, 10, 0), IsEnabled = i < _s.Sources.Count - 1, ToolTip = "Ask later" };
                up.Click += (_, _) => { (_s.Sources[index - 1], _s.Sources[index]) = (_s.Sources[index], _s.Sources[index - 1]); Changed(); Rebuild(); };
                down.Click += (_, _) => { (_s.Sources[index + 1], _s.Sources[index]) = (_s.Sources[index], _s.Sources[index + 1]); Changed(); Rebuild(); };
                controls.Children.Add(up);
                controls.Children.Add(down);
                controls.Children.Add(Toggle(entry.Enabled, v => entry.Enabled = v));
                Grid.SetColumn(controls, 2);
                row.Children.Add(controls);
                list.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Child = row, Opacity = entry.Enabled ? 1 : 0.75 });
            }
        }
        Rebuild();
        p.Children.Add(list);
        var reset = Btn("Restore default order", () => { _s.Sources = AppSettings.DefaultSources(); Changed(); Show("sources", rebuild: true); });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(reset);

        p.Children.Add(Group("Spicy Lyrics key"));
        var keyBox = new PasswordBox { Width = 260, Password = _s.SpicyKey ?? "" };
        var keyMsg = new TextBlock { Style = (Style)Application.Current.FindResource("CardHint"),
            Text = "Sign up at Spicy Lyrics for Developers, create an application, allow \"No origin header\", and paste its publishable key (sl_pk_…). It is stored encrypted for your Windows account." };
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(keyBox);
        keyRow.Children.Add(Btn("Save", () =>
        {
            var k = keyBox.Password.Trim();
            if (k.Length > 0 && SpicyLyricsSource.KeyProblem(k) is { } problem) { keyMsg.Text = problem; return; }
            _s.SpicyKey = k.Length > 0 ? k : null;
            _app.Chain.Spicy.ResetRefusal();
            keyMsg.Text = k.Length > 0 ? "Saved. Spicy Lyrics will be asked first." : "Key removed.";
            Changed();
        }, true));
        keyRow.Children.Add(Btn("Get a key", () => Open("https://developers.spicylyrics.org/dashboard")));
        var keyPanel = new StackPanel { Children = { keyRow, keyMsg } };
        p.Children.Add(Wide(keyPanel));

        p.Children.Add(Group("Imported lyrics"));
        var folder = new TextBox { Text = _s.ImportedFolder, Width = 360, IsReadOnly = true };
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        folderRow.Children.Add(folder);
        folderRow.Children.Add(Btn("Change…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = _s.ImportedFolder, Title = "Folder with your lyric files" };
            if (dlg.ShowDialog(this) == true) { _s.ImportedFolder = dlg.FolderName; folder.Text = dlg.FolderName; Changed(); }
        }));
        folderRow.Children.Add(Btn("Open", () => { Directory.CreateDirectory(_s.ImportedFolder); Open(_s.ImportedFolder); }));
        p.Children.Add(Wide(new StackPanel
        {
            Children =
            {
                folderRow,
                new TextBlock { Style = (Style)Application.Current.FindResource("CardHint"),
                    Text = "Put .lrc (including enhanced word-timed LRC), .ttml or .txt files here, named \"Artist - Title\". Subfolders are searched too." },
            },
        }));

        p.Children.Add(Group("Behaviour"));
        p.Children.Add(Card("Keep looking for word sync", "Off: the first source with synced lyrics wins, which is faster.", Toggle(_s.PreferWordSync, v => _s.PreferWordSync = v), ""));
        p.Children.Add(Card("Remembered lyrics", "Answers are kept so a song you play again shows its lyrics instantly.", Btn("Clear", () =>
        {
            LyricsChain.ClearCache();
            _app.Settings.SongSourceOverride.Clear();
            Changed();
            MessageBox.Show(this, "Cleared. Lyrics will be searched again.", "LycRomanise");
        }), ""));
        return p;
    }

    UIElement SpotifyPage()
    {
        var p = PageRoot("Spotify account", "Optional, and not needed for lyrics or the full lyrics window. It only adds prefetching of the next song in your queue. Spotify requires the account that creates the developer app to have Premium; without one, skip this page. Track IDs come from Musixmatch and the cover from Windows anyway.");
        var sp = _app.Spotify;
        var status = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        void Refresh() => status.Text = sp.LoggedIn ? $"Logged in{(sp.UserName != null ? " as " + sp.UserName : "")}" : "Not logged in";
        Refresh();

        p.Children.Add(Group("What it adds"));
        p.Children.Add(Wide(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, LineHeight = 21,
            Text = "• The exact Spotify track ID and ISRC, so Spicy Lyrics and BiniLyrics always match the right recording.\n" +
                   "• The lyrics of the next song in your queue are looked up ahead of time, so they appear the moment it starts.\n" +
                   "• The full-size cover for the lyrics window.",
        }));

        p.Children.Add(Group("Set up (once)"));
        var steps = new StackPanel();
        steps.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21, Text =
            "1. Open the Spotify Developer Dashboard and create an app (any name).\n" +
            "2. Under Redirect URIs add exactly the address below, tick \"Web API\", and save.\n" +
            "3. Copy the app's Client ID, paste it here, and press Log in." });
        var uriRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        uriRow.Children.Add(new TextBox { Text = SpotifyClient.RedirectUri, IsReadOnly = true, Width = 300 });
        uriRow.Children.Add(Btn("Copy", () => Clipboard.SetText(SpotifyClient.RedirectUri)));
        uriRow.Children.Add(Btn("Open dashboard", () => Open("https://developer.spotify.com/dashboard")));
        steps.Children.Add(uriRow);
        p.Children.Add(Wide(steps));

        var idBox = new TextBox { Text = _s.SpotifyClientId ?? "", Width = 330 };
        idBox.LostFocus += (_, _) => { _s.SpotifyClientId = idBox.Text.Trim(); Changed(); };
        p.Children.Add(Card("Client ID", null, idBox, ""));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button? login = null;
        login = Btn(sp.LoggedIn ? "Log in again" : "Log in with Spotify", async () =>
        {
            _s.SpotifyClientId = idBox.Text.Trim();
            _debounce.Stop();
            Commit();
            login!.IsEnabled = false;
            status.Text = "Waiting for you to log in in your browser…";
            var err = await sp.LoginAsync(CancellationToken.None);
            login.IsEnabled = true;
            if (err != null) status.Text = "Login failed: " + err; else { Refresh(); _app.Refetch(false); }
        }, true, "");
        buttons.Children.Add(login);
        buttons.Children.Add(Btn("Log out", () => { sp.Logout(); Refresh(); }));
        p.Children.Add(Card("Account", null, buttons, ""));
        p.Children.Add(Wide(status));
        p.Children.Add(Card("Prefetch the next song's lyrics", "Uses your queue; needs the login.", Toggle(_s.PrefetchNext, v => _s.PrefetchNext = v), ""));
        return p;
    }

    UIElement WindowPage()
    {
        var p = PageRoot("Strip & window");
        p.Children.Add(Card("Lock the strip", null, Toggle(_app.Settings.Locked, v => _app.SetLocked(v)), ""));
        p.Children.Add(Card("Hide the lock button", null, Toggle(_s.HideLockIcon, v => _s.HideLockIcon = v), ""));
        p.Children.Add(Card("Width", null, SliderBox(_s.StripWidth, 400, 2400, 10, "{0:0} px", v => _s.StripWidth = v), ""));
        p.Children.Add(Card("Text alignment", null, Combo(_s.Align, new[] { (StripAlign.Center, "Centre"), (StripAlign.Left, "Left"), (StripAlign.Right, "Right") }, v => _s.Align = v), ""));
        p.Children.Add(Card("Hide while paused", null, Toggle(_s.HideWhenPaused, v => _s.HideWhenPaused = v), ""));
        p.Children.Add(Card("Hide while the full lyrics window is open", "The strip comes back when you close or minimize it.", Toggle(_s.HideStripWithLyricsWindow, v => _s.HideStripWithLyricsWindow = v), "\uE8A7"));
        p.Children.Add(Card("Taskbar button", "Lets OBS window capture and other tools find the strip.", Toggle(_s.ShowInTaskbar, v => _s.ShowInTaskbar = v), ""));
        p.Children.Add(Card("Reset position", "Back to the top centre of the main screen.", Btn("Reset", () => _app.Overlay.ResetPosition()), ""));
        return p;
    }

    UIElement TimingPage()
    {
        var p = PageRoot("Timing", "Positive shows lyrics earlier. Per-song timing can also be nudged from the strip with Ctrl + mouse wheel.");
        p.Children.Add(Card("All songs", null, SliderBox(_s.GlobalOffsetMs, -3000, 3000, 50, "{0:+0;-0;0} ms", v => _s.GlobalOffsetMs = (int)v), ""));
        p.Children.Add(Card("This song", _app.CurrentQuery == null ? "Nothing playing" : $"{_app.CurrentQuery.Artist} - {_app.CurrentQuery.Title}",
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { Btn("−0.25 s", () => _app.NudgeOffset(-250)), Btn("+0.25 s", () => _app.NudgeOffset(250)), Btn("Reset", () => _app.NudgeOffset(-_app.SongOffset)) },
            }, ""));
        p.Children.Add(Card("Songs with their own timing", $"{_app.Settings.SongOffsets.Count} saved", Btn("Clear all", () =>
        {
            _s.SongOffsets.Clear();
            _app.Settings.SongOffsets.Clear();
            Changed();
            Show("timing", rebuild: true);
        }), ""));
        return p;
    }

    UIElement WebPage()
    {
        var p = PageRoot("Web view & OBS", "Show the lyrics on a phone, tablet or as an OBS browser source on the same network.");
        p.Children.Add(Card("Web view", null, Toggle(_s.WebEnabled, v => _s.WebEnabled = v), ""));
        var port = new TextBox { Text = _s.WebPort.ToString(), Width = 90 };
        port.LostFocus += (_, _) => { if (int.TryParse(port.Text, out var v) && v is >= 1024 and <= 65535) { _s.WebPort = v; Changed(); } };
        p.Children.Add(Card("Port", null, port, ""));
        var url = $"http://{Web.WebServer.LanAddress()}:{_s.WebPort}/";
        p.Children.Add(Card("Link for phones", url, new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { Btn("Copy", () => Clipboard.SetText(url)), Btn("Open", () => Open(url)) },
        }, ""));
        var qr = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Image { Source = QrImage(url), Width = 160, Height = 160, SnapsToDevicePixels = true },
        };
        RenderOptions.SetBitmapScalingMode(qr.Child, BitmapScalingMode.NearestNeighbor);
        p.Children.Add(Card("Scan with your phone", _s.WebEnabled
            ? "Your phone has to be on the same Wi-Fi."
            : "Turn on the web view first, then scan the code.", qr, ""));
        var obs = url + "obs";
        p.Children.Add(Card("Link for OBS (transparent)", obs, Btn("Copy", () => Clipboard.SetText(obs)), ""));
        p.Children.Add(Card("Hide lyrics on this PC", "Only the web view shows them.", Toggle(_s.HideDesktopStrip, v => _s.HideDesktopStrip = v), ""));
        p.Children.Add(Group("Song title file"));
        p.Children.Add(Card("Keep LycRomanise.txt up to date", "For an OBS Text (GDI+) source with \"Read from file\".", Toggle(_s.NowPlayingFile, v => _s.NowPlayingFile = v), ""));
        p.Children.Add(Card("What it says", null, Combo(_s.NowPlayingFormat, new[]
        {
            (NowPlayingFormat.ArtistTitle, "Artist - Song"), (NowPlayingFormat.TitleArtist, "Song - Artist"), (NowPlayingFormat.Title, "Song"),
        }, v => _s.NowPlayingFormat = v), ""));
        p.Children.Add(Card("File location", Path.Combine(Paths.Data, "LycRomanise.txt"), Btn("Open folder", () => Open(Paths.Data)), ""));
        return p;
    }

    UIElement AboutPage()
    {
        var p = PageRoot("General & about");
        p.Children.Add(Card("Start with Windows", null, Toggle(_s.StartWithWindows, v => _s.StartWithWindows = v), ""));
        p.Children.Add(Card("Only follow music apps", "Off: browsers and video players count too.", Toggle(_s.OnlyMusicApps, v => _s.OnlyMusicApps = v), ""));
        p.Children.Add(Card("Logs and data", Paths.Data, Btn("Open folder", () => Open(Paths.Data)), "\uE838"));

        p.Children.Add(Group("Updates"));
        var upStatus = new TextBlock { Style = (Style)Application.Current.FindResource("CardHint") };
        var bar = new ProgressBar { Height = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, Maximum = 1 };
        var upButtons = new WrapPanel { Margin = new Thickness(-6, 10, 0, 0) };
        void RefreshUpdate()
        {
            upButtons.Children.Clear();
            var up = _app.Update;
            upStatus.Text = up == null ? $"You're on {Updater.CurrentText}. " + (Updater.Installed ? "Installed copy." : "Portable copy.")
                : $"{up.Tag} is available (you have {Updater.CurrentText}).";
            if (up == null)
                upButtons.Children.Add(Btn("Check now", async () => { upStatus.Text = "Checking…"; await _app.CheckForUpdateAsync(true); RefreshUpdate(); }, false, "\uE72C"));
            else
            {
                upButtons.Children.Add(Btn(Updater.CanSelfUpdate(up) ? $"Install {up.Tag}" : "Open download page", async () =>
                {
                    if (!Updater.CanSelfUpdate(up)) { Open(up.PageUrl); return; }
                    bar.Visibility = Visibility.Visible;
                    upStatus.Text = "Downloading…";
                    var err = await _app.InstallUpdateAsync(new Progress<double>(v => bar.Value = v));
                    if (err != null) { upStatus.Text = "Update failed: " + err; bar.Visibility = Visibility.Collapsed; }
                }, true, "\uE896"));
                upButtons.Children.Add(Btn("What's new", () => Open(up.PageUrl)));
                upButtons.Children.Add(Btn("Skip this version", () => { _app.Settings.SkippedVersion = up.Tag; _app.SaveSettings(); }));
            }
        }
        RefreshUpdate();
        p.Children.Add(Wide(new StackPanel { Children = { new TextBlock { Text = "LycRomanise " + Updater.CurrentText, FontSize = 14 }, upStatus, bar, upButtons } }));
        p.Children.Add(Card("Check for updates automatically", null, Toggle(_s.CheckUpdates, v => _s.CheckUpdates = v), "\uE895"));

        p.Children.Add(Group("Keyboard shortcuts"));
        p.Children.Add(Card("Global shortcuts", null,Toggle(_s.GlobalHotkeys, v => _s.GlobalHotkeys = v), "\uE765"));
        var keys = new Grid();
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        keys.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        var failedKeys = _app.FailedHotkeys;
        foreach (var (k, what) in Hotkeys.Defaults)
        {
            keys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            bool taken = _s.GlobalHotkeys && row < Hotkeys.DefaultKeys.Length && Hotkeys.DefaultKeys[row].Any(failedKeys.Contains);
            var kt = new TextBlock { Text = k, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Opacity = taken ? 0.5 : 1 };
            var wt = new TextBlock { Text = taken ? what + "  (in use by another app)" : what, Opacity = taken ? 0.5 : 0.75, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(kt, row); Grid.SetRow(wt, row); Grid.SetColumn(wt, 1);
            keys.Children.Add(kt); keys.Children.Add(wt);
            row++;
        }
        p.Children.Add(Wide(keys));
        p.Children.Add(Group("About"));
        p.Children.Add(Wide(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, LineHeight = 20,
            Text = "LycRomanise shows synced, romanized lyrics on your desktop for whatever Spotify, Apple Music or any other player is playing.\n\n" +
                   "Lyrics come from Spicy Lyrics, BiniLyrics, your imported files, Musixmatch, Unison, NetEase Cloud Music, LRCLIB, QQ Music, KuGou and Genius. " +
                   "Not affiliated with any of them, or with Spotify, Apple or Lyricify.\n\nBuilt with .NET and WPF; renders on the GPU and sleeps when nothing is playing.",
        }));
        return p;
    }
}
