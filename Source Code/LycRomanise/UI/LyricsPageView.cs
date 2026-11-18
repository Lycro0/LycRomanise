using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LycRomanise.Core;

namespace LycRomanise.UI;

/// <summary>Every line of the song in a scrolling column, in the manner of Apple Music: the line being
/// sung is bright and fills word by word, the rest are dimmed, and the column glides to keep the
/// current line in view with each line following a beat behind the one before. Scrolling takes over
/// for a few seconds; clicking a line jumps the song to it.</summary>
public sealed class LyricsPageView : FrameworkElement
{
    sealed class Block
    {
        public DisplayLine? Line;           // null for an instrumental break
        public long BreakFrom, BreakTo;
        public double Top, Height;          // in the column
        public double Y;                    // where it is drawn now (animated)
        public double Scale = 0.96, Opacity = 0.35;
        public Text? Main, Sub, Backing, Translation;
    }

    sealed class Text
    {
        public Geometry Geometry = Geometry.Empty;
        public double Height, Width;
        public List<(Rect rect, long start, long end)> Words = new();
    }

    List<Block> _blocks = new();
    List<DisplayLine> _lines = new();
    bool _synced = true;
    long _durationMs;
    Func<long>? _clock;
    double _fontSize = 36;
    double _layoutWidth = -1;
    double _scroll, _userScroll;
    DateTime _userUntil = DateTime.MinValue;
    int _hover = -1, _active = -1;
    DateTime _lastFrame = DateTime.UtcNow;
    bool _rendering;
    double _dpi = 1;
    string? _empty;
    readonly Typeface _bold = new(new FontFamily("Segoe UI Variable Display, Segoe UI, Yu Gothic UI, Malgun Gothic, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    readonly Typeface _semi = new(new FontFamily("Segoe UI Variable Display, Segoe UI, Yu Gothic UI, Malgun Gothic, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public Action<long>? Seek { get; set; }
    public bool ShowTranslation { get; set; } = true;

    public LyricsPageView()
    {
        ClipToBounds = true;
        Focusable = true;
        Loaded += (_, _) => _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // Fade the column out at the top and bottom edges.
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
        _lines = lines.ToList();
        _synced = synced;
        _durationMs = durationMs;
        _empty = emptyMessage;
        _layoutWidth = -1;
        _active = -1;
        _userUntil = DateTime.MinValue;
        InvalidateVisual();
    }

    public void SetAnimating(bool on)
    {
        if (on == _rendering) return;
        _rendering = on;
        if (on) { _lastFrame = DateTime.UtcNow; CompositionTarget.Rendering += OnFrame; }
        else CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    // ------------------------------------------------------------------ layout
    double Pad => Math.Max(28, ActualWidth * 0.06);

    Text Build(LyricLine line, double size, Typeface face, double maxWidth, TextAlignment align)
    {
        // The words with a space between them unless a word runs on from the one before.
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
        var t = new Text { Geometry = ft.BuildGeometry(new Point(0, 0)), Height = ft.Height, Width = ft.Width };
        t.Geometry.Freeze();
        for (int i = 0; i < ranges.Count; i++)
        {
            var g = ft.BuildHighlightGeometry(new Point(0, 0), ranges[i].from, ranges[i].len);
            if (g != null) t.Words.Add((g.Bounds, line.Words[i].Start, line.Words[i].End));
        }
        return t;
    }

    Text BuildPlain(string text, double size, Typeface face, double maxWidth, TextAlignment align) =>
        Build(Karaoke.SpreadWords(text, 0, 0), size, face, maxWidth, align);

    void Layout()
    {
        if (Math.Abs(_layoutWidth - ActualWidth) < 0.5 && _blocks.Count > 0) return;
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
        // Past the end of a line and well before the next: the break row (if any) is current.
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
            _userUntil = DateTime.MinValue;   // follow the song again
        }
    }

    // ------------------------------------------------------------------ drawing
    static readonly SolidColorBrush Dim = Frozen(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF));
    static readonly SolidColorBrush Bright = Frozen(Colors.White);
    static readonly SolidColorBrush SubDim = Frozen(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF));
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
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

        // Where the column should sit: the current line about a third of the way down.
        bool user = now < _userUntil;
        double anchor = ActualHeight * 0.32;
        double target = user ? _userScroll : _active >= 0 ? _blocks[_active].Top - anchor : -anchor;
        _scroll += (target - _scroll) * (1 - Math.Exp(-dt * 9));

        for (int i = 0; i < _blocks.Count; i++)
        {
            var b = _blocks[i];
            // Each line follows a little behind the one above it, for the wave Apple Music has.
            int dist = _active < 0 ? 0 : Math.Abs(i - _active);
            double rate = user ? 14 : 7.5 / (1 + Math.Min(dist, 8) * 0.18);
            double goalY = b.Top - _scroll;
            b.Y += (goalY - b.Y) * (1 - Math.Exp(-dt * rate));
            if (dt == 0) b.Y = goalY;
            bool isActive = i == _active;
            double goalOpacity = !_synced ? 0.85 : isActive ? 1 : i == _hover ? 0.75 : user ? 0.6 : 0.42;
            double goalScale = isActive || !_synced ? 1 : 0.965;
            b.Opacity += (goalOpacity - b.Opacity) * (1 - Math.Exp(-dt * 10));
            b.Scale += (goalScale - b.Scale) * (1 - Math.Exp(-dt * 10));
            if (b.Y + b.Height < -_fontSize * 2 || b.Y > ActualHeight + _fontSize) continue;
            DrawBlock(dc, b, isActive, t);
        }
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

    /// <summary>Dim text, with the sung part of each word drawn bright over it while the line is current.</summary>
    double DrawText(DrawingContext dc, Text text, double x, double y, bool active, long t, Brush dim, Brush bright)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        if (!active || !_synced)
        {
            dc.DrawGeometry(active || !_synced ? bright : dim, null, text.Geometry);
        }
        else
        {
            dc.DrawGeometry(dim, null, text.Geometry);
            foreach (var (rect, start, end) in text.Words)
            {
                if (t <= start) continue;
                double f = t >= end ? 1 : (t - start) / (double)Math.Max(1, end - start);
                var r = new Rect(rect.X - 2, rect.Y - 4, rect.Width * f + (f >= 1 ? 4 : 2), rect.Height + 8);
                if (f >= 1)
                {
                    dc.PushClip(new RectangleGeometry(r));
                    dc.DrawGeometry(bright, null, text.Geometry);
                    dc.Pop();
                }
                else
                {
                    // A soft leading edge on the word being sung.
                    double edge = Math.Min(14, rect.Width * 0.4);
                    var mask = new LinearGradientBrush
                    {
                        MappingMode = BrushMappingMode.Absolute,
                        StartPoint = new Point(rect.X + rect.Width * f - edge, 0), EndPoint = new Point(rect.X + rect.Width * f + 0.1, 0),
                        GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) },
                    };
                    dc.PushClip(new RectangleGeometry(new Rect(rect.X - 2, rect.Y - 4, rect.Width + 4, rect.Height + 8)));
                    dc.PushOpacityMask(mask);
                    dc.DrawGeometry(bright, null, text.Geometry);
                    dc.Pop();
                    dc.Pop();
                }
            }
        }
        dc.Pop();
        return text.Height;
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
            var brush = new SolidColorBrush(Color.FromArgb((byte)(90 + 165 * fill), 255, 255, 255));
            dc.DrawEllipse(brush, null, new Point(x + r + i * r * 3.2, y + _fontSize * 0.55), r * breathe, r * breathe);
        }
    }
}
