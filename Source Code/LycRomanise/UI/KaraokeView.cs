using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LycRomanise.Core;

namespace LycRomanise.UI;

/// <summary>A line as the strip shows it: the main text (original or romanized), what goes under it,
/// and the backing vocals.</summary>
public sealed class DisplayLine
{
    public required LyricLine Main { get; init; }
    public LyricLine? Sub { get; init; }
    public string? Translation { get; init; }
    public LyricLine? Backing { get; init; }
    public LineAlign Align { get; init; }
    public long Start => Main.Start;
    public long End => Math.Max(Main.End, Backing?.End ?? 0);
}

public sealed class KaraokeStyle
{
    public StripStyle Layout = StripStyle.Modern;
    /// <summary>How far a word rises as it is sung, in pixels at the main size (Apple Music style).</summary>
    public double WordLift;
    /// <summary>A soft halo around the sung part of the line.</summary>
    public bool WordGlow;
    public Color Unsung, Sung, Next, Outline, Sub;
    public Typeface Typeface = new("Segoe UI");
    public double FontSize = 34, OutlineWidth = 3;
    public bool ShowNext = true, ShowTranslation, ShowBacking = true, Smooth = true;
    public TextAlignment Align = TextAlignment.Center;
}

/// <summary>Draws the karaoke strip. Text geometry is built once per line and size; each frame only
/// moves the sweep and the transition, so it stays cheap on the GPU.</summary>
public sealed class KaraokeView : FrameworkElement
{
    public KaraokeStyle Style { get; private set; } = new();
    List<DisplayLine> _lines = new();
    bool _synced = true;
    long _durationMs;
    Func<long>? _clock;
    string? _titleCard, _notice, _credit;
    DateTime _titleCardAt;
    int _shownIndex = -2;
    DateTime _transitionAt = DateTime.MinValue;
    int _fromIndex = -2;
    bool _rendering;
    bool _hasDuet;
    readonly Dictionary<(LyricLine, double, double), RowLayout[]> _layoutCache = new();
    double _dpi = 1;

    const double TransitionMs = 320;

    public KaraokeView()
    {
        IsHitTestVisible = true;
        Loaded += (_, _) => { _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; };
    }

    public void SetStyle(KaraokeStyle style)
    {
        Style = style;
        _layoutCache.Clear();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetLyrics(List<DisplayLine> lines, bool synced, long durationMs, string? credit)
    {
        _lines = lines;
        _synced = synced;
        _durationMs = durationMs;
        _credit = credit;
        _hasDuet = lines.Any(l => l.Align == LineAlign.Trailing);
        _shownIndex = -2;
        _fromIndex = -2;
        _layoutCache.Clear();
        InvalidateVisual();
    }

    public void SetClock(Func<long>? clock) => _clock = clock;

    public void ShowTitleCard(string? text)
    {
        _titleCard = text;
        _titleCardAt = DateTime.UtcNow;
        InvalidateVisual();
    }

    public void SetNotice(string? text) { _notice = text; InvalidateVisual(); }

    /// <summary>Redraws every frame while playing; stops when nothing moves, so an idle strip uses no CPU.</summary>
    public void SetAnimating(bool on)
    {
        if (on == _rendering) return;
        _rendering = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
        InvalidateVisual();
    }

    void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    // ------------------------------------------------------------- sizes
    double MainSize => Style.FontSize;
    double SubSize => Style.FontSize * 0.58;
    double TransSize => Style.FontSize * 0.5;
    double NextSize => Style.FontSize * (Style.Layout == StripStyle.Classic ? 0.76 : 0.6);
    double BackingSize => Style.FontSize * 0.5;
    double LineH(double size) => size * 1.32;

    /// <summary>The height the strip needs: two rows of the main line, two of romanization, then the
    /// translation, backing vocals and the next line.</summary>
    public double DesiredStripHeight()
    {
        if (Style.Layout == StripStyle.Classic)
        {
            // v1's strip: the line being sung and the next one straight under it.
            double c = 18 + LineH(MainSize) * 2 + LineH(NextSize) * 1.2;
            if (Style.ShowBacking) c += LineH(BackingSize) * 0.6;
            if (Style.ShowTranslation) c += LineH(TransSize);
            return Math.Ceiling(c + LineH(SubSize));
        }
        if (Style.Layout == StripStyle.Minimal)
            return Math.Ceiling(24 + LineH(MainSize) * 2 + LineH(SubSize) + (Style.ShowTranslation ? LineH(TransSize) : 0));
        double h = 18 + LineH(MainSize) * 2 + LineH(SubSize) * 2;
        if (Style.ShowBacking) h += LineH(BackingSize);
        if (Style.ShowTranslation) h += LineH(TransSize);
        if (Style.ShowNext) h += LineH(NextSize) + 6;
        return Math.Ceiling(h + 14);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width, DesiredStripHeight());

    // ------------------------------------------------------------- layout
    sealed class RowLayout
    {
        public Geometry Geometry = Geometry.Empty;
        public double Width, Height;
        public List<(double x0, double x1, long start, long end)> Words = new();
    }

    FormattedText Format(string text, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Style.Typeface, size, Brushes.White, _dpi);

    /// <summary>Splits a line into rows that fit the width (shrinking the font a little first), and
    /// records where each word sits so the sweep can follow the singing.</summary>
    RowLayout[] Layout(LyricLine line, double size, double maxWidth)
    {
        var key = (line, size, maxWidth);
        if (_layoutCache.TryGetValue(key, out var cached)) return cached;
        if (_layoutCache.Count > 64) _layoutCache.Clear();

        var words = line.Words;
        string Joined(int from, int to)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = from; i < to; i++) { if (i > from && !words[i].Joined) sb.Append(' '); sb.Append(words[i].Text); }
            return sb.ToString();
        }
        double full = Format(Joined(0, words.Count), size).WidthIncludingTrailingWhitespace;
        // Shrink to fit before wrapping, down to 80 %.
        if (full > maxWidth && full * 0.8 <= maxWidth) { size *= maxWidth / full; full = maxWidth; }
        var breaks = new List<(int from, int to)>();
        if (full <= maxWidth || words.Count <= 1) breaks.Add((0, words.Count));
        else
        {
            // Balanced: aim for rows of about equal width, breaking only between words.
            int rows = (int)Math.Ceiling(full / maxWidth);
            double target = full / rows;
            int from = 0;
            while (from < words.Count)
            {
                int to = from + 1;
                while (to < words.Count)
                {
                    if (words[to].Joined) { to++; continue; }
                    double w = Format(Joined(from, to + 1), size).WidthIncludingTrailingWhitespace;
                    if (w > maxWidth || (w > target * 1.08 && breaks.Count < rows - 1)) break;
                    to++;
                }
                while (to < words.Count && words[to].Joined) to++;
                breaks.Add((from, to));
                from = to;
            }
        }

        var result = new RowLayout[breaks.Count];
        for (int r = 0; r < breaks.Count; r++)
        {
            var (from, to) = breaks[r];
            var text = Joined(from, to);
            var ft = Format(text, size);
            var row = new RowLayout { Geometry = ft.BuildGeometry(new Point(0, 0)), Width = ft.WidthIncludingTrailingWhitespace, Height = ft.Height };
            row.Geometry.Freeze();
            int pos = 0;
            for (int i = from; i < to; i++)
            {
                if (i > from && !words[i].Joined) pos++;
                int len = words[i].Text.Length;
                var hb = ft.BuildHighlightGeometry(new Point(0, 0), pos, len)?.Bounds ?? Rect.Empty;
                double x0 = hb.IsEmpty ? 0 : hb.Left, x1 = hb.IsEmpty ? row.Width : hb.Right;
                row.Words.Add((x0, x1, words[i].Start, words[i].End));
                pos += len;
            }
            result[r] = row;
        }
        _layoutCache[key] = result;
        return result;
    }

    /// <summary>How far across the row the singing has got, in pixels.</summary>
    static double SweepX(RowLayout row, long t)
    {
        if (row.Words.Count == 0) return 0;
        if (t <= row.Words[0].start) return row.Words[0].x0;
        foreach (var (x0, x1, start, end) in row.Words)
        {
            if (t < start) return x0;
            if (t < end) return x0 + (x1 - x0) * (t - start) / Math.Max(1, end - start);
        }
        return row.Width;
    }

    // ------------------------------------------------------------- drawing
    static Color Fade(Color c, double opacity) => Color.FromArgb((byte)Math.Clamp(c.A * opacity, 0, 255), c.R, c.G, c.B);

    readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    SolidColorBrush Brush(Color c)
    {
        if (_brushes.TryGetValue(c, out var b)) return b;
        if (_brushes.Count > 256) _brushes.Clear();
        b = new SolidColorBrush(c);
        b.Freeze();
        return _brushes[c] = b;
    }

    readonly Dictionary<(Color, double), Pen> _pens = new();
    Pen? Outline(double opacity, double scale)
    {
        if (Style.OutlineWidth <= 0) return null;
        var c = Fade(Style.Outline, opacity);
        var key = (c, Math.Round(Style.OutlineWidth * scale, 1));
        if (_pens.TryGetValue(key, out var p)) return p;
        if (_pens.Count > 128) _pens.Clear();
        p = new Pen(Brush(c), key.Item2 * 2) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return _pens[key] = p;
    }

    double RowX(double width, LineAlign align)
    {
        double pad = 16, avail = ActualWidth - pad * 2;
        if (align == LineAlign.Trailing && Style.Align == TextAlignment.Center)
            return pad + Math.Max(0, avail - width) * (_hasDuet ? 0.86 : 0.5);   // a duet's second voice leans right
        if (align == LineAlign.Trailing) return pad + Math.Max(0, avail - width);
        return Style.Align switch
        {
            TextAlignment.Left => pad,
            TextAlignment.Right => pad + Math.Max(0, avail - width),
            _ => pad + (avail - width) / 2,
        } - (Style.Align == TextAlignment.Center && _hasDuet && align == LineAlign.Leading
            ? Math.Max(0, avail - width) * 0.36 : 0);
    }

    /// <summary>Draws a timed line in rows from y, returns the height used.</summary>
    double DrawSwept(DrawingContext dc, LyricLine line, double size, double y, long t, LineAlign align,
        Color unsung, Color sung, double opacity, bool sweep, int maxRows = 2)
    {
        var rows = Layout(line, size, ActualWidth - 32);
        double used = 0;
        var pen = Outline(opacity, size / MainSize);
        for (int r = 0; r < rows.Length && r < maxRows; r++)
        {
            var row = rows[r];
            double x = RowX(row.Width, align);
            dc.PushTransform(new TranslateTransform(x, y + used));
            if (sweep && (Style.WordLift > 0 || Style.WordGlow))
            {
                DrawRowLifted(dc, row, size, t, unsung, sung, opacity, pen);
                dc.Pop();
                used += LineH(size);
                continue;
            }
            if (pen != null) dc.DrawGeometry(null, pen, row.Geometry);
            dc.DrawGeometry(Brush(Fade(unsung, opacity)), null, row.Geometry);
            if (sweep)
            {
                double sx = SweepX(row, t);
                if (sx > 0)
                {
                    double soft = Style.Smooth ? Math.Max(6, size * 0.35) : 0.5;
                    var mask = new LinearGradientBrush
                    {
                        MappingMode = BrushMappingMode.Absolute,
                        StartPoint = new Point(sx - soft, 0), EndPoint = new Point(sx + 0.01, 0),
                        GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) },
                    };
                    if (sx >= row.Width) { dc.DrawGeometry(Brush(Fade(sung, opacity)), null, row.Geometry); }
                    else
                    {
                        dc.PushOpacityMask(mask);
                        dc.DrawGeometry(Brush(Fade(sung, opacity)), null, row.Geometry);
                        dc.Pop();
                    }
                }
            }
            dc.Pop();
            used += LineH(size);
        }
        return used;
    }

    /// <summary>A row drawn word by word, each word rising a little as it is sung and the sung part
    /// glowing: the Apple Music look.</summary>
    void DrawRowLifted(DrawingContext dc, RowLayout row, double size, long t, Color unsung, Color sung, double opacity, Pen? pen)
    {
        double sx = SweepX(row, t);
        double soft = Style.Smooth ? Math.Max(6, size * 0.35) : 0.5;
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(sx - soft, 0), EndPoint = new Point(sx + 0.01, 0),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) },
        };
        double lift = Style.WordLift * size / MainSize;
        Pen? glowWide = null, glowNarrow = null;
        if (Style.WordGlow)
        {
            glowWide = new Pen(Brush(Fade(sung, 0.14 * opacity)), size * 0.22) { LineJoin = PenLineJoin.Round };
            glowNarrow = new Pen(Brush(Fade(sung, 0.22 * opacity)), size * 0.1) { LineJoin = PenLineJoin.Round };
        }
        for (int i = 0; i < row.Words.Count; i++)
        {
            var (x0, x1, start, end) = row.Words[i];
            // Cut between words halfway across the gap, so no glyph is clipped.
            double left = i == 0 ? -size : (row.Words[i - 1].x1 + x0) / 2;
            double right = i == row.Words.Count - 1 ? row.Width + size : (x1 + row.Words[i + 1].x0) / 2;
            double f = t <= start ? 0 : t >= end ? 1 : (t - start) / (double)Math.Max(1, end - start);
            double rise = lift * (1 - Math.Pow(1 - f, 3));
            dc.PushClip(new RectangleGeometry(new Rect(left, -size, right - left, row.Height + size * 2)));
            dc.PushTransform(new TranslateTransform(0, -rise));
            if (pen != null) dc.DrawGeometry(null, pen, row.Geometry);
            dc.DrawGeometry(Brush(Fade(unsung, opacity)), null, row.Geometry);
            if (f > 0 && sx > 0)
            {
                dc.PushOpacityMask(mask);
                if (glowWide != null) { dc.DrawGeometry(null, glowWide, row.Geometry); dc.DrawGeometry(null, glowNarrow, row.Geometry); }
                dc.DrawGeometry(Brush(Fade(sung, opacity)), null, row.Geometry);
                dc.Pop();
            }
            dc.Pop();
            dc.Pop();
        }
    }

    double DrawPlain(DrawingContext dc, string text, double size, double y, Color color, double opacity, LineAlign align = LineAlign.Leading, int maxRows = 1)
    {
        var line = Karaoke.SpreadWords(text, 0, 0);
        return DrawSwept(dc, line, size, y, 0, align, color, color, opacity, false, maxRows);
    }

    int IndexAt(long t)
    {
        if (!_synced)
        {
            if (_lines.Count == 0 || _durationMs <= 0) return 0;
            return (int)Math.Clamp(t * _lines.Count / _durationMs, 0, _lines.Count - 1);
        }
        int idx = -1;
        for (int i = 0; i < _lines.Count; i++) { if (_lines[i].Start <= t) idx = i; else break; }
        return idx;
    }

    static double Ease(double x) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), 3);

    protected override void OnRender(DrawingContext dc)
    {
        // A hit-testable surface across the whole strip, so it can be dragged anywhere.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double top = 10;

        if (_notice != null)
        {
            DrawPlain(dc, _notice, SubSize, top + LineH(MainSize) * 0.5, Style.Next, 0.9, LineAlign.Leading, 2);
            return;
        }
        long t = _clock?.Invoke() ?? 0;

        // Title card: "Artist — Song" for a few seconds, or until the first line.
        bool titleShowing = _titleCard != null && (DateTime.UtcNow - _titleCardAt).TotalSeconds < 7
            && (_lines.Count == 0 || !_synced || t < _lines[0].Start - 300);
        if (_lines.Count == 0)
        {
            if (_titleCard != null && titleShowing) DrawPlain(dc, _titleCard, MainSize * 0.8, top + LineH(MainSize) * 0.3, Style.Unsung, 1, LineAlign.Leading, 2);
            return;
        }

        int idx = IndexAt(t);
        if (idx != _shownIndex)
        {
            _fromIndex = _shownIndex;
            _shownIndex = idx;
            _transitionAt = DateTime.UtcNow;
        }
        double p = Ease((DateTime.UtcNow - _transitionAt).TotalMilliseconds / TransitionMs);

        if (idx < 0)
        {
            // Before the first line: title card, then a countdown as the first line nears.
            if (titleShowing) DrawPlain(dc, _titleCard!, MainSize * 0.8, top + LineH(MainSize) * 0.3, Style.Unsung, 1, LineAlign.Leading, 2);
            else DrawCountdown(dc, top + LineH(MainSize) * 0.5, t, 0, _lines[0].Start);
            if (Style.ShowNext) DrawNext(dc, 0, 1);
            return;
        }

        var cur = _lines[idx];
        var next = idx + 1 < _lines.Count ? _lines[idx + 1] : null;
        if (Style.Layout == StripStyle.Classic && _synced && !(t > cur.End + 1200 && (next == null || next.Start - t > 2200)))
        {
            RenderClassic(dc, top, t, idx, p);
            return;
        }
        // A long instrumental break after the line: dots that fill as the next line comes.
        if (_synced && t > cur.End + 1200 && (next == null || next.Start - t > 2200))
        {
            if (next != null) DrawCountdown(dc, top + LineH(MainSize) * 0.5, t, cur.End, next.Start);
            else DrawPlain(dc, "♪", MainSize, top, Style.Unsung, 0.8);
            if (next != null && Style.ShowNext) DrawNext(dc, idx + 1, 1);
            return;
        }

        // The outgoing line slides up and fades; the new one rises into place.
        double slide = (1 - p) * LineH(MainSize) * 0.6;
        if (p < 1 && _fromIndex >= 0 && _fromIndex < _lines.Count && _fromIndex != idx)
        {
            // Gone by 45 % of the way, so it never sits on top of the new line.
            double leave = Math.Clamp(p / 0.45, 0, 1);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            dc.PushOpacity(Math.Pow(1 - leave, 2));
            DrawBlock(dc, _lines[_fromIndex], top - leave * LineH(MainSize) * 1.2, t, 1);
            dc.Pop();
            dc.Pop();
        }
        dc.PushOpacity(p < 1 ? Math.Clamp((p - 0.2) / 0.8, 0, 1) : 1);
        double h = DrawBlock(dc, cur, top + slide, t, 1);
        dc.Pop();
        if (Style.ShowNext && next != null) DrawNext(dc, idx + 1, p);
        if (!_synced) DrawPlain(dc, "Unsynced lyrics", TransSize * 0.85, ActualHeight - LineH(TransSize), Style.Sub, 0.55);
    }

    /// <summary>The current line, what goes under it and its backing vocals. Returns its height.</summary>
    double DrawBlock(DrawingContext dc, DisplayLine line, double y, long t, double opacity)
    {
        double used = DrawSwept(dc, line.Main, MainSize, y, t, line.Align, Style.Unsung, Style.Sung, opacity, _synced);
        if (line.Sub != null)
            used += DrawSwept(dc, line.Sub, SubSize, y + used, t, line.Align, Fade(Style.Sub, 0.85), Style.Sung, opacity, _synced);
        if (Style.ShowBacking && line.Backing != null)
            used += DrawSwept(dc, line.Backing, BackingSize, y + used, t, line.Align, Fade(Style.Sub, 0.7), Fade(Style.Sung, 0.85), opacity, _synced, 1);
        if (Style.ShowTranslation && !string.IsNullOrEmpty(line.Translation))
            used += DrawPlain(dc, line.Translation!, TransSize, y + used, Fade(Style.Sub, 0.8), opacity, line.Align);
        return used;
    }

    /// <summary>v1's strip. The line being sung sits at the top and the next waits straight under it;
    /// when the line changes, the next one rises into the top slot and grows to full size while the
    /// one after it fades up underneath.</summary>
    void RenderClassic(DrawingContext dc, double top, long t, int idx, double p)
    {
        var cur = _lines[idx];
        var next = idx + 1 < _lines.Count ? _lines[idx + 1] : null;
        // Where the next line goes: under the current line's rows (one or two) and its romanization.
        double CurHeight(DisplayLine l)
        {
            double h = Layout(l.Main, MainSize, ActualWidth - 32).Length > 1 ? LineH(MainSize) * 2 : LineH(MainSize);
            if (l.Sub != null) h += LineH(SubSize);
            if (Style.ShowBacking && l.Backing != null) h += LineH(BackingSize);
            if (Style.ShowTranslation && !string.IsNullOrEmpty(l.Translation)) h += LineH(TransSize);
            return h;
        }
        double nextY = top + CurHeight(cur) + 2;

        if (p < 1 && _fromIndex >= 0 && _fromIndex < _lines.Count && _fromIndex == idx - 1)
        {
            // The old line leaves upward and fades.
            double leave = Math.Clamp(p / 0.5, 0, 1);
            dc.PushOpacity(Math.Pow(1 - leave, 2));
            DrawBlock(dc, _lines[_fromIndex], top - leave * LineH(MainSize) * 0.8, t, 1);
            dc.Pop();
            // The new line rises from where it waited and grows from the next line's size to full.
            double fromY = top + CurHeight(_lines[_fromIndex]) + 2;
            double y = fromY + (top - fromY) * p;
            double s = NextSize / MainSize + (1 - NextSize / MainSize) * p;
            double cx = ActualWidth / 2;
            dc.PushTransform(new ScaleTransform(s, s, cx, y));
            DrawBlock(dc, cur, y, t, 1);
            dc.Pop();
            if (Style.ShowNext && next != null)
            {
                dc.PushOpacity(p);
                DrawSwept(dc, next.Main, NextSize, nextY + (1 - p) * LineH(NextSize) * 0.5, 0, next.Align, Style.Next, Style.Next, 1, false, 1);
                dc.Pop();
            }
            return;
        }
        if (p < 1)
        {
            dc.PushOpacity(Math.Clamp(p * 1.5, 0, 1));
            DrawBlock(dc, cur, top + (1 - p) * LineH(MainSize) * 0.4, t, 1);
            dc.Pop();
        }
        else DrawBlock(dc, cur, top, t, 1);
        if (Style.ShowNext && next != null)
            DrawSwept(dc, next.Main, NextSize, nextY, 0, next.Align, Style.Next, Style.Next, 1, false, 1);
    }

    void DrawNext(DrawingContext dc, int index, double appear)
    {
        if (Style.Layout == StripStyle.Minimal) return;
        var next = _lines[index];
        double y = ActualHeight - LineH(NextSize) - 8;
        dc.PushOpacity(0.55 * appear + 0.0);
        DrawSwept(dc, next.Main, NextSize, y, 0, next.Align, Style.Next, Style.Next, 1, false, 1);
        dc.Pop();
    }

    void DrawCountdown(DrawingContext dc, double y, long t, long from, long to)
    {
        double span = Math.Max(1, to - from);
        double prog = Math.Clamp((t - from) / span, 0, 1);
        double r = MainSize * 0.16, gap = r * 3.2;
        double cx = ActualWidth / 2 - gap;
        // Breathing, the way Apple Music's dots do.
        double breathe = 1 + 0.12 * Math.Sin((DateTime.UtcNow.TimeOfDay.TotalMilliseconds / 1000.0) * Math.PI * 1.6);
        for (int i = 0; i < 3; i++)
        {
            double fill = Math.Clamp(prog * 3 - i, 0, 1);
            var c = Color.FromArgb(255,
                (byte)(Style.Unsung.R + (Style.Sung.R - Style.Unsung.R) * fill),
                (byte)(Style.Unsung.G + (Style.Sung.G - Style.Unsung.G) * fill),
                (byte)(Style.Unsung.B + (Style.Sung.B - Style.Unsung.B) * fill));
            var center = new Point(cx + gap * i, y + MainSize * 0.4);
            var pen = Outline(1, 0.5);
            dc.DrawEllipse(Brush(Fade(c, 0.45 + 0.55 * fill)), pen, center, r * breathe, r * breathe);
        }
    }
}
