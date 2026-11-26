using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LycRomanise.Core;

namespace LycRomanise.UI;

public sealed class LyricsPageView : FrameworkElement
{
    sealed class Block
    {
        public DisplayLine? Line;           // null for an instrumental break
        public long BreakFrom, BreakTo;
        public double Top, Height;
        public double Y;                    // where it is drawn now (animated)
        public double Scale = 0.96, Opacity = 0.35;
        public Text? Main, Sub, Backing, Translation;
    }

    // A rectangle per row the word sits on (a word can wrap).
    sealed record Word(List<Rect> Rows, long Start, long End, double Width);

    // Outlines are built lazily (word outlines only for the line being sung), so a resize or new song stays cheap.
    sealed class Text
    {
        public readonly FormattedText Ft;
        readonly List<(int from, int len)> _ranges;
        readonly LyricLine _line;
        public double Height, Width;
        Geometry? _geometry;
        List<Word>? _words;
        List<Geometry>? _wordGeometry;

        public Text(FormattedText ft, List<(int from, int len)> ranges, LyricLine line)
        {
            Ft = ft; _ranges = ranges; _line = line;
            Height = ft.Height; Width = ft.Width;
        }

        public Geometry Geometry
        {
            get
            {
                if (_geometry == null) { _geometry = Ft.BuildGeometry(new Point(0, 0)); _geometry.Freeze(); }
                return _geometry;
            }
        }

        public List<Word> Words
        {
            get
            {
                if (_words != null) return _words;
                _words = new();
                for (int i = 0; i < _ranges.Count; i++)
                {
                    var g = Ft.BuildHighlightGeometry(new Point(0, 0), _ranges[i].from, _ranges[i].len);
                    if (g == null) continue;
                    // One rectangle per row: the union bounds of a wrapped word would cover both rows whole.
                    var rows = new List<Rect>();
                    foreach (var fig in g.GetFlattenedPathGeometry().Figures)
                    {
                        var b = new PathGeometry(new[] { fig }).Bounds;
                        if (b.IsEmpty) continue;
                        int at = rows.FindIndex(r => Math.Abs(r.Y - b.Y) < 2);
                        if (at >= 0) rows[at] = Rect.Union(rows[at], b); else rows.Add(b);
                    }
                    if (rows.Count == 0) rows.Add(g.Bounds);
                    rows.Sort((a, b) => a.Y.CompareTo(b.Y));
                    _words.Add(new Word(rows, _line.Words[i].Start, _line.Words[i].End, rows.Sum(r => r.Width)));
                }
                return _words;
            }
        }

        // Each glyph contour goes whole to the word whose row box holds its centre: nothing is cut, so the
        // glow has no flat edge to stroke and descenders and accents stay with their own row's word.
        public List<Geometry> WordGeometry
        {
            get
            {
                if (_wordGeometry != null) return _wordGeometry;
                var words = Words;
                var per = words.Select(_ => new PathGeometry { FillRule = FillRule.Nonzero }).ToList();
                if (per.Count > 0)
                {
                    var all = PathGeometry.CreateFromGeometry(Geometry);
                    foreach (var fig in all.Figures)
                    {
                        var b = new PathGeometry(new[] { fig.Clone() }).Bounds;
                        if (b.IsEmpty) continue;
                        var c = new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
                        int best = 0; double bestD = double.MaxValue;
                        for (int i = 0; i < words.Count; i++)
                            foreach (var r in words[i].Rows)
                            {
                                // Vertical distance weighs double, so a contour never jumps rows.
                                double dx = c.X < r.Left ? r.Left - c.X : c.X > r.Right ? c.X - r.Right : 0;
                                double dy = c.Y < r.Top ? r.Top - c.Y : c.Y > r.Bottom ? c.Y - r.Bottom : 0;
                                double d = dx + dy * 2;
                                if (d < bestD) { bestD = d; best = i; }
                            }
                        per[best].Figures.Add(fig.Clone());
                    }
                }
                foreach (var p in per) p.Freeze();
                return _wordGeometry = per.Cast<Geometry>().ToList();
            }
        }
    }

    List<Block> _blocks = new();
    List<DisplayLine> _lines = new();
    bool _synced = true;
    long _durationMs;
    Func<long>? _clock;
    double _fontSize = 36;
    double _layoutWidth = -1;
    DateTime _layoutAt = DateTime.MinValue;
    double _scroll, _userScroll;
    DateTime _userUntil = DateTime.MinValue;
    int _hover = -1, _active = -1;
    DateTime _lastFrame = DateTime.UtcNow;
    bool _rendering, _settling, _hooked;
    double _dpi = 1;
    string? _empty;
    System.Windows.Threading.DispatcherTimer? _later;
    readonly Typeface _bold = new(new FontFamily("Segoe UI Variable Display, Segoe UI, Yu Gothic UI, Malgun Gothic, Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    readonly Typeface _semi = new(new FontFamily("Segoe UI Variable Display, Segoe UI, Yu Gothic UI, Malgun Gothic, Microsoft YaHei UI, Segoe UI Symbol, Segoe UI Emoji"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public Action<long>? Seek { get; set; }
    public (double lift, double glow, double stagger, double scroll, double dim) Motion { get; set; } = LookPresets.Motion(LyricsMotion.AppleMusic);
    public bool ShowTranslation { get; set; } = true;

    public LyricsPageView()
    {
        ClipToBounds = true;
        Focusable = true;
        Loaded += (_, _) => _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 0.1),
                new GradientStop(Colors.Black, 0.86), new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    public double FontSize
    {
        get => _fontSize;
        set { _fontSize = Math.Clamp(value, 18, 80); _layoutWidth = -1; InvalidateVisual(); }
    }

    public void SetClock(Func<long> clock) => _clock = clock;

    public void SetLyrics(IReadOnlyList<DisplayLine> lines, bool synced, long durationMs, string? emptyMessage)
    {
        _quietUntil = long.MinValue;
        _lines = lines.ToList();
        _synced = synced;
        _durationMs = durationMs;
        _empty = emptyMessage;
        _layoutWidth = -1;
        _blocks = new();   // a new song: lay out at once, never with the last song's lines
        _active = -1;
        _userUntil = DateTime.MinValue;
        _snap = true;
        InvalidateVisual();
    }

    public void SetAnimating(bool on)
    {
        if (on == _rendering) return;
        _rendering = on;
        if (on) _lastFrame = DateTime.UtcNow;
        Hook();
    }

    // For a closed or minimized window, where OnRender no longer runs to stop the loops itself.
    public void StopAll()
    {
        _rendering = _settling = false;
        _later?.Stop();
        Hook();
    }

    // One frame handler serves both the playing loop and the short settle loop after a scroll or hover.
    void Hook()
    {
        bool want = _rendering || _settling;
        if (want == _hooked) return;
        _hooked = want;
        if (want) CompositionTarget.Rendering += OnFrame; else CompositionTarget.Rendering -= OnFrame;
    }

    void RedrawAt(DateTime when)
    {
        _later ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render, Dispatcher);
        _later.Tick -= Later; _later.Tick += Later;
        _later.Stop();
        _later.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (when - DateTime.UtcNow).TotalMilliseconds));
        _later.Start();
    }

    void Later(object? s, EventArgs e) { _later!.Stop(); InvalidateVisual(); }

    DateTime _lastInvalidate;
    // About 30 redraws a second, the same as the strip.
    void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastInvalidate).TotalMilliseconds < 31) return;
        if (_quietUntil != long.MinValue && !_settling && _clock != null)
        {
            long t = _clock();
            if (t >= _quietFrom && t < _quietUntil) return;
        }
        _lastInvalidate = now;
        InvalidateVisual();
    }

    long _quietFrom, _quietUntil = long.MinValue;

    // Until when the frame just drawn stays the same: the active line is fully sung and the next one has not started.
    long QuietUntil(long t)
    {
        if (!_synced || _active < 0) return long.MinValue;
        var b = _blocks[_active];
        if (b.Line == null) return long.MinValue;
        long end = long.MinValue;
        foreach (var text in new[] { b.Main, b.Sub, b.Backing })
            if (text != null) foreach (var w in text.Words) end = Math.Max(end, w.End);
        if (t < end + 500) return long.MinValue;
        return _active + 1 < _blocks.Count ? (_blocks[_active + 1].Line?.Start ?? _blocks[_active + 1].BreakFrom + 400) : long.MaxValue;
    }

    // ------------------------------------------------------------------ layout
    double Pad => Math.Max(28, ActualWidth * 0.06);

    Text Build(LyricLine line, double size, Typeface face, double maxWidth, TextAlignment align)
    {
        var sb = new System.Text.StringBuilder();
        var ranges = new List<(int from, int len)>();
        foreach (var w in line.Words)
        {
            if (sb.Length > 0 && !w.Joined) sb.Append(' ');
            ranges.Add((sb.Length, w.Text.Length));
            sb.Append(w.Text);
        }
        var ft = new FormattedText(sb.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.White, _dpi)
        {
            MaxTextWidth = Math.Max(50, maxWidth), TextAlignment = align, LineHeight = size * 1.18,
        };
        return new Text(ft, ranges, line);
    }

    Text BuildPlain(string text, double size, Typeface face, double maxWidth, TextAlignment align) =>
        Build(Karaoke.SpreadWords(text, 0, 0), size, face, maxWidth, align);

    void Layout()
    {
        if (Math.Abs(_layoutWidth - ActualWidth) < 0.5 && _blocks.Count > 0) return;
        // While a window edge is dragged, re-wrap at most ten times a second; the last width always lands.
        var nowAt = DateTime.UtcNow;
        if (_layoutWidth > 0 && _blocks.Count > 0 && (nowAt - _layoutAt).TotalMilliseconds < 100)
        {
            RedrawAt(_layoutAt.AddMilliseconds(110));
            return;
        }
        _layoutAt = nowAt;
        _layoutWidth = ActualWidth;
        double width = ActualWidth - Pad * 2;
        double gap = _fontSize * 0.95;
        var blocks = new List<Block>();
        double y = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            var l = _lines[i];
            // A long instrumental gap before the line gets its own row of dots.
            long prevEnd = i == 0 ? 0 : _lines[i - 1].End;
            if (_synced && l.Start - prevEnd > 6000 && (i > 0 || l.Start > 6000))
            {
                var br = new Block { BreakFrom = prevEnd, BreakTo = l.Start, Top = y, Height = _fontSize * 1.1 };
                blocks.Add(br);
                y += br.Height + gap;
            }
            var align = l.Align == LineAlign.Trailing ? TextAlignment.Right : TextAlignment.Left;
            var b = new Block { Line = l, Top = y };
            b.Main = Build(l.Main, _fontSize, _bold, width, align);
            double h = b.Main.Height;
            if (l.Sub != null) { b.Sub = Build(l.Sub, _fontSize * 0.52, _semi, width, align); h += b.Sub.Height + _fontSize * 0.12; }
            if (l.Backing != null) { b.Backing = Build(l.Backing, _fontSize * 0.62, _semi, width, align); h += b.Backing.Height + _fontSize * 0.12; }
            if (!string.IsNullOrEmpty(l.Translation)) { b.Translation = BuildPlain(l.Translation!, _fontSize * 0.5, _semi, width, align); h += b.Translation.Height + _fontSize * 0.12; }
            b.Height = h;
            blocks.Add(b);
            y += h + gap;
        }
        // Keep the animation state of blocks that survive a re-layout (a resize).
        if (_blocks.Count == blocks.Count)
            for (int i = 0; i < blocks.Count; i++) { blocks[i].Y = _blocks[i].Y + (blocks[i].Top - _blocks[i].Top); blocks[i].Opacity = _blocks[i].Opacity; blocks[i].Scale = _blocks[i].Scale; }
        else
            foreach (var b in blocks) b.Y = b.Top - _scroll;
        _blocks = blocks;
    }

    int ActiveBlock(long t)
    {
        if (!_synced)
        {
            return -1;
        }
        int idx = -1;
        for (int i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            long start = b.Line?.Start ?? b.BreakFrom + 400;
            if (start <= t) idx = i; else break;
        }
        return idx;
    }

    // ------------------------------------------------------------------ input
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_userUntil < DateTime.UtcNow) _userScroll = _scroll;
        _userScroll -= e.Delta * 0.9;
        double max = _blocks.Count > 0 ? _blocks[^1].Top + _blocks[^1].Height - ActualHeight * 0.3 : 0;
        _userScroll = Math.Clamp(_userScroll, -ActualHeight * 0.3, Math.Max(0, max));
        _userUntil = DateTime.UtcNow.AddSeconds(_synced ? 4 : 3600);
        if (!_rendering) InvalidateVisual();
        e.Handled = true;
    }

    int HitBlock(Point p)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            if (b.Line != null && p.Y >= b.Y - _fontSize * 0.3 && p.Y <= b.Y + b.Height + _fontSize * 0.3) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = HitBlock(e.GetPosition(this));
        Cursor = _hover >= 0 && _synced ? Cursors.Hand : null;
        if (!_rendering) InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; if (!_rendering) InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        int i = HitBlock(e.GetPosition(this));
        if (i >= 0 && _synced && _blocks[i].Line is { } l)
        {
            Seek?.Invoke(l.Start);
            _userUntil = DateTime.MinValue;
            if (!_rendering) InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ drawing
    static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF));
    static readonly SolidColorBrush Bright = Frozen(Colors.White);
    static readonly SolidColorBrush SubDim = Frozen(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    bool _snap = true;
    // For the self-test's performance numbers.
    public static double RenderMs; public static int RenderCount;

    protected override void OnRender(DrawingContext dc)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { RenderCore(dc); }
        finally { RenderMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; RenderCount++; }
    }

    void RenderCore(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 50) return;
        if (_lines.Count == 0)
        {
            if (_empty != null)
            {
                var ft = new FormattedText(_empty, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _bold, _fontSize * 0.8, Dim, _dpi)
                { MaxTextWidth = ActualWidth - Pad * 2 };
                dc.DrawText(ft, new Point(Pad, ActualHeight * 0.38));
            }
            return;
        }
        Layout();
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;
        long t = _clock?.Invoke() ?? 0;
        _active = ActiveBlock(t);

        bool user = now < _userUntil;
        double anchor = ActualHeight * 0.32;
        double target = user ? _userScroll : _active >= 0 ? _blocks[_active].Top - anchor : -anchor;
        if (_snap && ActualHeight > 0)
        {
            // First frame for these lyrics: start where the song is, with every line already in place.
            _scroll = target;
            foreach (var bl in _blocks) bl.Y = bl.Top - _scroll;
            _snap = false;
            dt = 1;   // settle opacity and size at once too
        }
        _scroll += (target - _scroll) * (1 - Math.Exp(-dt * Motion.scroll));
        bool moving = Math.Abs(target - _scroll) > 0.5;

        for (int i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            // Each line follows a little behind the one above it, for the wave Apple Music has.
            int dist = _active < 0 ? 0 : Math.Abs(i - _active);
            double rate = user ? 14 : Motion.scroll * 0.83 / (1 + Math.Min(dist, 8) * Motion.stagger);
            double goalY = b.Top - _scroll;
            b.Y += (goalY - b.Y) * (1 - Math.Exp(-dt * rate));
            if (dt == 0) b.Y = goalY;
            bool isActive = i == _active;
            double goalOpacity = !_synced ? 0.85 : isActive ? 1 : i == _hover ? 0.75 : user ? 0.6 : Motion.dim;
            double goalScale = isActive || !_synced ? 1 : 0.965;
            b.Opacity += (goalOpacity - b.Opacity) * (1 - Math.Exp(-dt * 10));
            b.Scale += (goalScale - b.Scale) * (1 - Math.Exp(-dt * 10));
            moving |= Math.Abs(goalY - b.Y) > 0.5 || Math.Abs(goalOpacity - b.Opacity) > 0.004 || Math.Abs(goalScale - b.Scale) > 0.0005;
            if (b.Y + b.Height < -_fontSize * 2 || b.Y > ActualHeight + _fontSize) continue;
            DrawBlock(dc, b, isActive, t);
        }

        // Paused, nothing redraws on its own: run frames just until the scroll, hover fade or settling ends,
        // and come back when a scroll pause runs out so the column returns to the current line.
        if (moving != _settling) { _settling = moving; if (moving) _lastFrame = now; Hook(); }
        _quietFrom = t;
        _quietUntil = moving || user ? long.MinValue : QuietUntil(t);
        if (user && !_rendering) RedrawAt(_userUntil.AddMilliseconds(20));
    }

    void DrawBlock(DrawingContext dc, Block b, bool active, long t)
    {
        double x = Pad;
        bool trailing = b.Line?.Align == LineAlign.Trailing;
        double originX = trailing ? ActualWidth - Pad : Pad;
        dc.PushTransform(new ScaleTransform(b.Scale, b.Scale, originX, b.Y));
        dc.PushOpacity(b.Opacity);
        if (b.Line == null)
        {
            DrawDots(dc, x, b.Y, b, t);
        }
        else
        {
            double y = b.Y;
            y += DrawText(dc, b.Main!, x, y, active, t, Dim, Bright) ;
            if (b.Sub != null) y += _fontSize * 0.12 + DrawText(dc, b.Sub, x, y + _fontSize * 0.12, active, t, SubDim, Bright);
            if (b.Backing != null) y += _fontSize * 0.12 + DrawText(dc, b.Backing, x, y + _fontSize * 0.12, active, t, SubDim, Bright);
            if (b.Translation != null && ShowTranslation) DrawText(dc, b.Translation, x, y + _fontSize * 0.12, false, t, SubDim, SubDim);
        }
        dc.Pop();
        dc.Pop();
    }

    static IEnumerable<(Rect row, double f, double top, double bottom)> RowFill(Word w, double f)
    {
        double filled = f * w.Width, before = 0;
        for (int i = 0; i < w.Rows.Count; i++)
        {
            var r = w.Rows[i];
            double fr = r.Width <= 0 ? (f >= 1 ? 1 : 0) : Math.Clamp((filled - before) / r.Width, 0, 1);
            before += r.Width;
            // Each row's clip runs to the next row's top, so rows never share pixels.
            double top = i == 0 ? -1e4 : r.Y, bottom = i + 1 < w.Rows.Count ? w.Rows[i + 1].Y : 1e4;
            yield return (r, fr, top, bottom);
        }
    }

    static LinearGradientBrush Edge(Rect r, double f)
    {
        double edge = Math.Min(14, r.Width * 0.4);
        var g = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(r.X + r.Width * f - edge, 0), EndPoint = new Point(r.X + r.Width * f + 0.1, 0),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) },
        };
        g.Freeze();
        return g;
    }

    double DrawText(DrawingContext dc, Text text, double x, double y, bool active, long t, Brush dim, Brush bright)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        if (!active || !_synced)
        {
            dc.DrawGeometry(active || !_synced ? bright : dim, null, text.Geometry);
        }
        else if (Motion.lift > 0 || Motion.glow > 0)
        {
            DrawLifted(dc, text, t, dim, bright);
        }
        else
        {
            dc.DrawGeometry(dim, null, text.Geometry);
            foreach (var w in text.Words)
            {
                if (t <= w.Start) continue;
                double f = t >= w.End ? 1 : (t - w.Start) / (double)Math.Max(1, w.End - w.Start);
                foreach (var (rect, fr, _, _) in RowFill(w, f))
                {
                    if (fr <= 0) continue;
                    if (fr >= 1)
                    {
                        dc.PushClip(new RectangleGeometry(new Rect(rect.X - 2, rect.Y - 4, rect.Width + 4, rect.Height + 8)));
                        dc.DrawGeometry(bright, null, text.Geometry);
                        dc.Pop();
                    }
                    else
                    {
                        dc.PushClip(new RectangleGeometry(new Rect(rect.X - 2, rect.Y - 4, rect.Width + 4, rect.Height + 8)));
                        dc.PushOpacityMask(Edge(rect, fr));
                        dc.DrawGeometry(bright, null, text.Geometry);
                        dc.Pop();
                        dc.Pop();
                    }
                }
            }
        }
        dc.Pop();
        return text.Height;
    }

    void DrawLifted(DrawingContext dc, Text text, long t, Brush dim, Brush bright)
    {
        double lift = Motion.lift * _fontSize / 36;
        var glow = Motion.glow > 0 ? GlowPen(Motion.glow) : null;
        var words = text.Words;
        var geos = text.WordGeometry;
        for (int i = 0; i < words.Count && i < geos.Count; i++)
        {
            var w = words[i];
            var geo = geos[i];
            double f = t <= w.Start ? 0 : t >= w.End ? 1 : (t - w.Start) / (double)Math.Max(1, w.End - w.Start);
            double rise = lift * (1 - Math.Pow(1 - f, 3));
            bool moved = rise > 0.01;
            if (moved) dc.PushTransform(new TranslateTransform(0, -rise));
            if (f < 1) dc.DrawGeometry(dim, null, geo);
            if (f >= 1)
            {
                if (glow != null) dc.DrawGeometry(null, glow, geo);
                dc.DrawGeometry(bright, null, geo);
            }
            else if (f > 0)
            {
                bool single = w.Rows.Count == 1;
                foreach (var (rect, fr, top, bottom) in RowFill(w, f))
                {
                    if (fr <= 0) continue;
                    if (!single) dc.PushClip(new RectangleGeometry(new Rect(-1e4, top, 2e4, bottom - top)));
                    bool partial = fr < 1;
                    if (partial) dc.PushOpacityMask(Edge(rect, fr));
                    if (glow != null) dc.DrawGeometry(null, glow, geo);
                    dc.DrawGeometry(bright, null, geo);
                    if (partial) dc.Pop();
                    if (!single) dc.Pop();
                }
            }
            if (moved) dc.Pop();
        }
    }

    Pen? _glowPen; double _glowFor = -1, _glowSize = -1;
    Pen GlowPen(double strength)
    {
        if (_glowPen == null || _glowFor != strength || _glowSize != _fontSize)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(40 * strength, 0, 255), 255, 255, 255)); b.Freeze();
            _glowPen = new Pen(b, _fontSize * 0.2) { LineJoin = PenLineJoin.Round }; _glowPen.Freeze();
            _glowFor = strength; _glowSize = _fontSize;
        }
        return _glowPen;
    }

    void DrawDots(DrawingContext dc, double x, double y, Block b, long t)
    {
        double span = Math.Max(1, b.BreakTo - b.BreakFrom);
        double prog = Math.Clamp((t - b.BreakFrom) / span, 0, 1);
        bool live = t >= b.BreakFrom && t < b.BreakTo;
        double r = _fontSize * 0.17;
        double breathe = live ? 1 + 0.15 * Math.Sin(DateTime.UtcNow.TimeOfDay.TotalSeconds * Math.PI * 1.5) : 1;
        for (int i = 0; i < 3; i++)
        {
            double fill = live ? Math.Clamp(prog * 3 - i, 0, 1) : 0;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(90 + 165 * fill), 255, 255, 255)); brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(x + r + i * r * 3.2, y + _fontSize * 0.55), r * breathe, r * breathe);
        }
    }
}
